using System;
using System.Collections.Generic;
using System.Diagnostics;
using SuperSampler.Abstractions.Events;
using SuperSampler.Core.Config;

namespace SuperSampler.Core.Runtime;

/// <summary>一次失败后的退避决策（本次档位、等待毫秒、到期时刻）。</summary>
internal readonly struct BackoffStep
{
    public BackoffStep(int attempt, int delayMs, long untilTicks)
    {
        Attempt = attempt;
        DelayMs = delayMs;
        UntilTicks = untilTicks;
    }

    /// <summary>退避档位：本次是连续第几次失败（1 基）。</summary>
    public int Attempt { get; }

    /// <summary>本档位等待毫秒数。</summary>
    public int DelayMs { get; }

    /// <summary>退避到期时刻（<see cref="Stopwatch"/> 单调刻度）。</summary>
    public long UntilTicks { get; }

    /// <summary>到期时刻换算成 UTC 墙钟（只用于事件字段，不参与判定）。</summary>
    public DateTimeOffset NextRetryAt => DateTimeOffset.UtcNow + TicksToTimeSpan(UntilTicks - BackoffGovernor.Now);

    private static TimeSpan TicksToTimeSpan(long ticks)
        => TimeSpan.FromSeconds((double)ticks / Stopwatch.Frequency);
}

/// <summary>一次退避恢复（成功通讯 → 队列归零）的统计。</summary>
internal readonly struct BackoffRecovery
{
    public BackoffRecovery(int attempts, long durationMs)
    {
        Attempts = attempts;
        DurationMs = durationMs;
    }

    /// <summary>本会话连续失败次数。</summary>
    public int Attempts { get; }

    /// <summary>本会话时长（毫秒）。</summary>
    public long DurationMs { get; }
}

/// <summary>
/// 两层退避状态机（docs/01 §6.3、docs/11 §一.1）。
/// <list type="bullet">
/// <item><b>触发</b>：失败一次即进队列，不设「连续 N 次」门槛；设备回异常码（协议异常）不进队列。</item>
/// <item><b>队列</b>：<c>Reconnect@delays</c> 逐项等待，**用完后一直用最后一个值循环**；**成功一次即归零**。</item>
/// <item><b>两个作用域</b>：设备级（键 = deviceId）与链路级（键 = transportId），各自独立的队列进度。</item>
/// <item><b>健壮性</b>：全部状态在锁内读写；计时用 <see cref="Stopwatch"/> 单调刻度（系统时间跳变不影响）；
/// 调用方只做「查时间戳 + 比较」，等待语义不依赖线程时序；成功即删条目，长期运行不累积状态。</item>
/// </list>
/// 这是一个纯状态机：不发事件、不发通讯，事件由调度器按返回值发出（便于确定性单测）。
/// </summary>
internal sealed class BackoffGovernor
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _devices = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Entry> _links = new(StringComparer.Ordinal);
    private readonly ReconnectOptions _options;

    /// <summary>
    /// 单调时钟（PROD-8 / findings D107）：到期判定只读它的 <see cref="IRuntimeClock.NowTicks"/>，
    /// 墙钟被回拨/前跳不会让退避提前解除或卡住。测试可注入假时钟做确定性验证。
    /// </summary>
    private readonly IRuntimeClock _clock;

    public BackoffGovernor(ReconnectOptions options)
        : this(options, SystemRuntimeClock.Instance)
    {
    }

    internal BackoffGovernor(ReconnectOptions options, IRuntimeClock clock)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>是否启用两层退避；false 时全部判定为空操作（保持旧行为：每拍照常发请求）。</summary>
    public bool Enabled => _options.Enabled;

    /// <summary>手动重试门面是否开放。</summary>
    public bool ManualRetryEnabled => _options.ManualRetry;

    /// <summary>退避期间点位质量（offline | bad）。</summary>
    public string OfflineQuality => _options.OfflineQuality;

    /// <summary>系统单调时钟当前刻度（<see cref="Stopwatch"/>；绝不用墙钟做超时判定）。</summary>
    internal static long Now => SystemRuntimeClock.Instance.NowTicks;

    /// <summary>毫秒 → 单调刻度数。</summary>
    internal static long MsToTicks(int ms) => RuntimeClockMath.MsToTicks(ms);

    /// <summary>刻度数 → 毫秒。</summary>
    internal static long TicksToMs(long ticks) => RuntimeClockMath.TicksToMs(ticks);

    // ─────────────── 查询 ───────────────

    /// <summary>该目标当前是否在退避等待中；true 时 <paramref name="untilTicks"/> 给出到期刻度。</summary>
    public bool IsBlocked(BackoffScope scope, string key, out long untilTicks)
    {
        untilTicks = 0;
        if (!_options.Enabled) return false;

        lock (_gate)
        {
            if (!Map(scope).TryGetValue(key, out var entry) || entry.UntilTicks == 0) return false;
            if (_clock.NowTicks < entry.UntilTicks)
            {
                untilTicks = entry.UntilTicks;
                return true;
            }

            // 到期：等待结束（条目保留，失败次数与档位继续累计，直到成功一次才归零）
            return false;
        }
    }

    /// <summary>设备级退避查询。</summary>
    public bool IsDeviceBlocked(string deviceId, out long untilTicks) => IsBlocked(BackoffScope.Device, deviceId, out untilTicks);

    /// <summary>链路级退避查询。</summary>
    public bool IsLinkBlocked(string transportId, out long untilTicks) => IsBlocked(BackoffScope.Link, transportId, out untilTicks);

    // ─────────────── 状态迁移 ───────────────

    /// <summary>一次失败：进退避（或加深一档），返回本次等待决策。关闭退避时返回零档（调用方不必判空）。</summary>
    public BackoffStep OnFailure(BackoffScope scope, string key)
    {
        if (!_options.Enabled) return new BackoffStep(0, 0, 0);

        lock (_gate)
        {
            var entry = Get(scope, key);
            entry.Failures++;
            var delay = _options.DelayAt(entry.Failures - 1);
            entry.DelayMs = delay;
            entry.EnteredTicks = entry.EnteredTicks == 0 ? _clock.NowTicks : entry.EnteredTicks;
            entry.UntilTicks = _clock.NowTicks + MsToTicks(delay);
            return new BackoffStep(entry.Failures, delay, entry.UntilTicks);
        }
    }

    /// <summary>
    /// 一次成功通讯：队列归零（条目删除，下次失败从第一项重来）。
    /// 返回 null 表示该目标本来就没有失败记录（无事发生，调用方不必发恢复事件）。
    /// </summary>
    public BackoffRecovery? OnSuccess(BackoffScope scope, string key)
    {
        if (!_options.Enabled) return null;

        lock (_gate)
        {
            var map = Map(scope);
            if (!map.TryGetValue(key, out var entry)) return null;
            if (entry.Failures == 0)
            {
                map.Remove(key);
                return null;
            }

            var recovery = new BackoffRecovery(entry.Failures,
                entry.EnteredTicks == 0 ? 0 : TicksToMs(_clock.NowTicks - entry.EnteredTicks));
            map.Remove(key);
            return recovery;
        }
    }

    /// <summary>
    /// 手动重试：立刻打断等待（保留档位——失败会继续加深，成功由 <see cref="OnSuccess"/> 归零）。
    /// 返回是否真的打断了正在进行的退避。
    /// </summary>
    public bool Interrupt(BackoffScope scope, string key)
    {
        lock (_gate)
        {
            if (!Map(scope).TryGetValue(key, out var entry)) return false;

            var interrupted = entry.UntilTicks != 0 && Now < entry.UntilTicks;
            entry.UntilTicks = 0;
            return interrupted;
        }
    }

    /// <summary>清掉一个目标的退避记录（升级为链路级退避时作废各设备的进度）。</summary>
    public void Clear(BackoffScope scope, string key)
    {
        lock (_gate) Map(scope).Remove(key);
    }

    private Dictionary<string, Entry> Map(BackoffScope scope)
        => scope == BackoffScope.Device ? _devices : _links;

    private Entry Get(BackoffScope scope, string key)
    {
        var map = Map(scope);
        if (map.TryGetValue(key, out var entry)) return entry;

        entry = new Entry();
        map[key] = entry;
        return entry;
    }

    /// <summary>一个目标的退避状态：连续失败次数、本次档位与到期刻度。</summary>
    private sealed class Entry
    {
        /// <summary>连续失败次数（成功一次即随条目一起作废）。</summary>
        public int Failures { get; set; }

        /// <summary>本次档位等待毫秒数。</summary>
        public int DelayMs { get; set; }

        /// <summary>退避到期刻度；0 = 不在等待。</summary>
        public long UntilTicks { get; set; }

        /// <summary>本会话首次失败的刻度（恢复事件算时长用）。</summary>
        public long EnteredTicks { get; set; }
    }
}
