using System;
using System.Diagnostics;

namespace SuperSampler.Core.Runtime;

/// <summary>
/// 运行时时钟（findings PROD-8 / D107）：把**单调时间**与**墙钟时间**分成两个显式入口，
/// 让「什么该跟墙钟、什么绝不能跟墙钟」在类型层面就写死。
/// <list type="bullet">
/// <item><see cref="NowTicks"/>（单调，默认 <see cref="Stopwatch.GetTimestamp"/>）：
/// <b>只它</b>参与排程到期判定、退避到期判定与一切超时判定——系统时间被 NTP 回拨/手动前跳都不影响这些判定；</item>
/// <item><see cref="UtcNow"/>（墙钟 UTC）：只用于事件/值的时间戳（宿主显示、上账、按时间检索），绝不参与判定。</item>
/// </list>
/// 为什么需要这个抽象：修复前 <c>Scheduler.DeviceLoop</c> 用 <see cref="DateTime.UtcNow"/> 算「还有多久到期」，
/// 系统时间被回拨 1 小时后，所有已算好的到期时刻都落在「未来 1 小时」→ 轮询线程虽然每 50ms 醒一次，
/// 却要等墙钟追上才发请求（冻结式卡死；见 <c>ClockJumpTests</c> 的墙钟回拨用例）。
/// <para>
/// 该类型是 <c>internal</c>：宿主拿不到也改不了，只有测试程序集经 <c>InternalsVisibleTo</c> 注入假时钟，
/// 从而在**不改真实系统时钟**的前提下模拟回拨/前跳（改系统时钟会影响同机其它进程与长稳实验）。
/// </para>
/// </summary>
internal interface IRuntimeClock
{
    /// <summary>单调刻度（<see cref="Stopwatch"/> 频度）：排程/退避/超时判定唯一依据。</summary>
    long NowTicks { get; }

    /// <summary>墙钟 UTC：仅用于时间戳（事件、值、审计），不参与任何判定。</summary>
    DateTimeOffset UtcNow { get; }
}

/// <summary>系统时钟：单调侧 = <see cref="Stopwatch.GetTimestamp"/>，墙钟侧 = <see cref="DateTimeOffset.UtcNow"/>。</summary>
internal sealed class SystemRuntimeClock : IRuntimeClock
{
    /// <summary>进程内唯一实例（无状态，线程安全）。</summary>
    public static readonly SystemRuntimeClock Instance = new SystemRuntimeClock();

    private SystemRuntimeClock()
    {
    }

    /// <inheritdoc />
    public long NowTicks => Stopwatch.GetTimestamp();

    /// <inheritdoc />
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

/// <summary>
/// 单调刻度 ↔ 毫秒的换算（<see cref="Stopwatch.Frequency"/> 不是 1000，必须按频度换算）。
/// 单一实现：Scheduler 排程与 BackoffGovernor 退避共用，避免两处各写一份换算。
/// </summary>
internal static class RuntimeClockMath
{
    /// <summary>毫秒 → 单调刻度数。</summary>
    internal static long MsToTicks(int ms) => (long)(ms * (double)Stopwatch.Frequency / 1000.0);

    /// <summary>单调刻度数 → 毫秒（向下取整）。</summary>
    internal static long TicksToMs(long ticks) => (long)(ticks * 1000.0 / Stopwatch.Frequency);

    /// <summary>单调刻度数 → <see cref="TimeSpan"/>。</summary>
    internal static TimeSpan TicksToTimeSpan(long ticks) => TimeSpan.FromSeconds((double)ticks / Stopwatch.Frequency);
}
