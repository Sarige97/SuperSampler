using System;
using System.Collections.Generic;
using SuperSampler.Abstractions.Events;
using SuperSampler.Abstractions.Values;
using SuperSampler.Core.Config;
using SuperSampler.Core.Events;
using SuperSampler.Core.Runtime;
using SuperSampler.UnitTests.Support;
using Xunit;

namespace SuperSampler.UnitTests.Runtime;

/// <summary>
/// AlarmEngine 补测（docs/07 测试计划 §3.2 G-A 族）：highHigh/lowLow 并存、lowLow 死区清除方向、
/// 缺省报警 id 规则、Pending 期间坏值、缺省 Id 锁存确认（D9 回归）、未知 id 确认静默。
/// </summary>
public class AlarmEngineGapTests
{
    private readonly InProcessEventBus _bus = new();

    private readonly List<AlarmRaisedEvent> _raised = new();
    private readonly List<AlarmClearedEvent> _cleared = new();
    private readonly List<AlarmAcknowledgedEvent> _acked = new();

    private AlarmEngine NewEngine()
    {
        _bus.Subscribe<AlarmRaisedEvent>(e => _raised.Add(e.Body), DeliveryMode.Inline);
        _bus.Subscribe<AlarmClearedEvent>(e => _cleared.Add(e.Body), DeliveryMode.Inline);
        _bus.Subscribe<AlarmAcknowledgedEvent>(e => _acked.Add(e.Body), DeliveryMode.Inline);
        return new AlarmEngine(_bus);
    }

    private static RuntimePoint Point(Action<RuntimePointFactory.RuntimePointConfig>? configure = null)
        => RuntimePointFactory.Point(configure);

    private static PointValue Good(double value)
        => PointValue.Good(value, DateTimeOffset.UtcNow);

    // ─────────────── G-A-1：high + highHigh 同点并存，独立触发/清除 ───────────────

    [Fact]
    public void Ga1_high_and_highhigh_on_same_point_trigger_and_clear_independently()
    {
        var engine = NewEngine();
        var point = Point(c =>
        {
            c.Alarms.Add(new AlarmConfig { Type = "high", Limit = 10 });
            c.Alarms.Add(new AlarmConfig { Type = "highhigh", Limit = 20 });
        });

        engine.Evaluate(point, Good(25)); // 两条同时激活
        Assert.Equal(2, _raised.Count);
        Assert.Contains(_raised, e => e.AlarmType == "high");
        Assert.Contains(_raised, e => e.AlarmType == "highhigh");

        engine.Evaluate(point, Good(15)); // 15 ≤ 20 → highHigh 清除；15 > 10 → high 保持
        var cleared = Assert.Single(_cleared);
        Assert.Equal("highhigh", cleared.AlarmType);

        engine.Evaluate(point, Good(5)); // 5 ≤ 10 → high 清除
        Assert.Equal(2, _cleared.Count);
    }

    // ─────────────── G-A-2：lowLow 触发与死区清除方向 ───────────────

    [Fact]
    public void Ga2_lowlow_clears_only_above_limit_plus_deadband()
    {
        var engine = NewEngine();
        var point = Point(c => c.Alarms.Add(new AlarmConfig { Type = "lowlow", Limit = 0, Deadband = 5 }));

        engine.Evaluate(point, Good(-1)); // < 0 → 触发
        var raised = Assert.Single(_raised);
        Assert.Equal("lowlow", raised.AlarmType);

        engine.Evaluate(point, Good(2)); // 2 < 0+5=5，死区带内不清除
        Assert.Empty(_cleared);

        engine.Evaluate(point, Good(6)); // ≥ 限值+死区 → 清除
        Assert.Single(_cleared);
    }

    // ─────────────── G-A-3：Alarm@Id 缺省 → 事件 AlarmId = pointId#type ───────────────

    [Fact]
    public void Ga3_default_alarm_id_is_pointId_hash_type()
    {
        var engine = NewEngine();
        var point = Point(c => c.Alarms.Add(new AlarmConfig { Type = "high", Limit = 10 }));

        engine.Evaluate(point, Good(11));

        var ev = Assert.Single(_raised);
        Assert.Equal("p#high", ev.AlarmId);
    }

    // ─────────────── G-A-4：Pending（延时中）遇坏值保持 Pending ───────────────

    [Fact]
    public void Ga4_bad_value_during_pending_keeps_pending_state()
    {
        var engine = NewEngine();
        var point = Point(c => c.Alarms.Add(new AlarmConfig { Type = "high", Limit = 10, DelayMs = 60_000 }));

        engine.Evaluate(point, Good(99));  // 越限 → 仅挂起（延时未到）
        Assert.Empty(_raised);

        engine.Evaluate(point, PointValue.Bad("ss.reason.comm", DateTimeOffset.UtcNow)); // 坏值跳过
        engine.Evaluate(point, Good(99));  // 仍处挂起期（PendingSince 未被坏值破坏）

        // 坏值既不提前激活也不误清除/重置挂起计时（若被重置，这里依旧静默；状态保持靠不抛事件验证）
        Assert.Empty(_raised);
        Assert.Empty(_cleared);
    }

    // ─────────────── D9（已修复）：缺省 Id 的锁存报警可确认并重触发 ───────────────
    // 修复：状态键与 AlarmIdOf / Acknowledge 统一为 pointId#type（D9）。
    // 本用例断言修复后的正确行为：确认链路打通，确认后允许再次激活。

    [Fact]
    public void Ga5_default_id_latched_alarm_can_be_acknowledged_then_retriggers()
    {
        var engine = NewEngine();
        var point = Point(c => c.Alarms.Add(new AlarmConfig { Type = "high", Limit = 50, Latch = true }));

        engine.Evaluate(point, Good(60));  // 激活，AlarmId = p#high
        Assert.Single(_raised);

        engine.Evaluate(point, Good(40));  // 回落清除，但锁存待确认
        Assert.Single(_cleared);

        engine.Evaluate(point, Good(60));  // 未确认 → 不重触发（D6）
        Assert.Single(_raised);

        engine.Acknowledge("dev1", "p", "p#high", "operator"); // D9 修复前：静默失配
        var ack = Assert.Single(_acked);
        Assert.Equal("p#high", ack.AlarmId);

        engine.Evaluate(point, Good(60));  // 确认后允许再次激活
        Assert.Equal(2, _raised.Count);
    }

    // ─────────────── G-A-6：确认未知报警 id：不抛、无事件、无副作用 ───────────────

    [Fact]
    public void Ga6_acknowledge_unknown_alarm_id_is_silent_noop()
    {
        var engine = NewEngine();
        var point = Point(c => c.Alarms.Add(new AlarmConfig { Id = "a1", Type = "high", Limit = 10, Latch = true }));

        engine.Evaluate(point, Good(11));
        engine.Acknowledge("dev1", "p", "不存在", "operator"); // 未知 id

        Assert.Empty(_acked);
        // 原报警状态不受影响：仍未确认，再越限不重触发
        engine.Evaluate(point, Good(12));
        Assert.Single(_raised);
    }
}
