using System;
using System.Collections.Concurrent;
using System.Globalization;
using SuperSampler.Abstractions.Events;
using SuperSampler.Abstractions.Values;
using SuperSampler.Core.Config;
using SuperSampler.Core.Events;

namespace SuperSampler.Core.Runtime;

/// <summary>
/// 报警评估状态机：按点位配置的 Alarm 列表，对新值逐条判定。
/// 状态流转：Normal →（越限）Pending →（持续 delayMs）Active →（回落出死区）Cleared。
/// latch/ackRequired 时 Active 清除后仍需人工确认（Acknowledge）才回 Normal。
/// 坏值不参与评估（保持原状态，避免通讯抖动触发误报）；
/// 死区用于消除限值附近抖动（docs/02 现场坑：不设死区会在限值附近疯狂刷报警）。
/// </summary>
public sealed class AlarmEngine
{
    private readonly InProcessEventBus _bus;
    private readonly ConcurrentDictionary<string, AlarmState> _states = new(StringComparer.Ordinal);

    /// <summary>构造报警引擎。</summary>
    public AlarmEngine(InProcessEventBus bus)
    {
        _bus = bus;
    }

    private sealed class AlarmState
    {
        public bool Active;
        public bool AckPending;
        public DateTime? PendingSince;
    }

    /// <summary>用新值评估该点位的全部报警。值质量非 Good 时跳过（保持原状态）。</summary>
    public void Evaluate(RuntimePoint point, PointValue value)
    {
        if (!value.IsGood) return;
        if (point.Source.Alarms.Count == 0) return;

        var now = DateTime.UtcNow;

        foreach (var config in point.Source.Alarms)
        {
            // 状态键与事件/AlarmIdOf/Acknowledge 用同一套 id（findings D9：
            // 曾用「Id 或索引」，缺省 Id 时与「pointId#type」失配，导致锁存报警永远无法确认）
            var key = point.Key + "#" + AlarmIdOf(point, config);
            var state = _states.GetOrAdd(key, _ => new AlarmState());

            lock (state)
            {
                var crossed = IsCrossed(config, value.Value);
                if (crossed == null) continue; // 报警类型与当前值类型不匹配

                if (!state.Active)
                {
                    EvaluateRaise(state, config, point, value, crossed.Value, now);
                }
                else
                {
                    EvaluateClear(state, config, point, value, crossed.Value);
                }
            }
        }
    }

    /// <summary>
    /// 人工确认。锁存报警在条件清除后须确认才回 Normal；未清除的报警确认后仅记录事件。
    /// alarmId 与 AlarmRaisedEvent 的 AlarmId 一致。
    /// </summary>
    public void Acknowledge(string deviceId, string pointId, string alarmId, string user)
    {
        var key = PointKey.Of(deviceId, pointId) + "#" + alarmId;
        if (!_states.TryGetValue(key, out var state)) return;

        lock (state)
        {
            state.AckPending = false;
        }

        _bus.Emit(new AlarmAcknowledgedEvent(alarmId, deviceId, pointId, user));
    }

    // ─────────────── 状态流转 ───────────────

    private void EvaluateRaise(
        AlarmState state, AlarmConfig config, RuntimePoint point,
        PointValue value, bool crossed, DateTime now)
    {
        // 锁存语义：清除后未确认前，不允许重新触发（findings D6）
        if (state.AckPending) return;

        if (!crossed)
        {
            state.PendingSince = null;
            return;
        }

        if (config.DelayMs > 0)
        {
            state.PendingSince ??= now;
            if ((now - state.PendingSince.Value).TotalMilliseconds < config.DelayMs) return;
        }

        state.Active = true;
        state.AckPending = config.Latch || config.AckRequired;
        _bus.Emit(new AlarmRaisedEvent(
            AlarmIdOf(point, config),
            point.DeviceId,
            point.PointId,
            config.Type,
            config.Limit,
            value.Value,
            config.Priority,
            config.Message));
    }

    private void EvaluateClear(
        AlarmState state, AlarmConfig config, RuntimePoint point,
        PointValue value, bool crossed)
    {
        // 回差方向（findings D4）：high 报警必须回落到限值之下（含死区）才清除，
        // 「仍在限值上方但进了死区带」不能清，否则限值附近照样抖动。
        // digital 报警以值翻转回正常为准。
        var cleared = config.Type == "digital"
            ? !crossed
            : WithinClearBand(config, value.Value);
        if (!cleared) return;

        state.Active = false;
        state.PendingSince = null;
        _bus.Emit(new AlarmClearedEvent(
            AlarmIdOf(point, config),
            point.DeviceId,
            point.PointId,
            config.Type));
    }

    /// <summary>清除条件：high 回落到 限值−死区 之下；low 回升到 限值+死区 之上。</summary>
    private static bool WithinClearBand(AlarmConfig config, object? value)
    {
        if (!config.Limit.HasValue) return false;
        if (!TryDouble(value, out var number)) return false;

        return config.Type switch
        {
            "high" or "highhigh" => number <= config.Limit.Value - config.Deadband,
            "low" or "lowlow" => number >= config.Limit.Value + config.Deadband,
            _ => false,
        };
    }

    // ─────────────── 判定 ───────────────

    /// <summary>判断是否越限。返回 null 表示该报警类型无法用当前值判定。</summary>
    private static bool? IsCrossed(AlarmConfig config, object? value)
    {
        if (config.Type == "digital")
        {
            if (value is bool b)
            {
                return config.Limit.HasValue
                    ? b == (config.Limit.Value != 0)
                    : b;
            }

            if (TryDouble(value, out var dv))
            {
                return config.Limit.HasValue ? (dv != 0) == (config.Limit.Value != 0) : dv != 0;
            }

            return null;
        }

        if (!TryDouble(value, out var number) || !config.Limit.HasValue) return null;

        return config.Type switch
        {
            "high" => number > config.Limit.Value,
            "highhigh" => number > config.Limit.Value,
            "low" => number < config.Limit.Value,
            "lowlow" => number < config.Limit.Value,
            _ => null,
        };
    }

    private static string AlarmIdOf(RuntimePoint point, AlarmConfig config)
        => config.Id.Length > 0 ? config.Id : point.PointId + "#" + config.Type;

    private static bool TryDouble(object? value, out double number)
    {
        if (value is bool b)
        {
            number = b ? 1 : 0;
            return true;
        }

        if (value is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal)
        {
            number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            return true;
        }

        number = 0;
        return false;
    }
}
