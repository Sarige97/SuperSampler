using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Xml.Linq;
using SuperSampler.Abstractions.Events;
using SuperSampler.Core.Config;
using SuperSampler.Core.Runtime;
using SuperSampler.Drivers.Modbus;
using SuperSampler.UnitTests.Support;
using Xunit;

namespace SuperSampler.UnitTests.Runtime;

/// <summary>
/// PROD-8 / findings D107：运行时时钟（<see cref="IRuntimeClock"/>）的**判定侧单调、时间戳侧墙钟**分离验证。
/// <list type="bullet">
/// <item>排程到期判定、退避到期判定只用 <see cref="IRuntimeClock.NowTicks"/>（单调刻度）；
///     系统墙钟被 NTP 回拨/手动前跳不得影响轮询节奏与超时/退避判定；</item>
/// <item>事件/值的 <see cref="DateTimeOffset"/> 时间戳用 <see cref="IRuntimeClock.UtcNow"/>（墙钟）。</item>
/// </list>
/// 实现（`RuntimeClock.cs`）：默认 <see cref="SystemRuntimeClock"/>（单调侧 = <c>Stopwatch.GetTimestamp</c>），
/// 经 <c>InternalsVisibleTo</c> 由测试注入假时钟，**绝不改真实系统时钟**（长稳实验与同机其它进程都在跑）。
/// </summary>
[Collection("real-polling-threads")]
public sealed class ClockJumpTests : IDisposable
{
    private readonly FakeModbusLink _link = new();
    private SamplerEngine? _engine;

    public void Dispose() => _engine?.Dispose();

    /// <summary>假时钟：单调刻度与墙钟可独立控制——「回拨/前跳」只动墙钟，单调刻度保持连续。</summary>
    private sealed class FakeClock : IRuntimeClock
    {
        private long _ticks;
        private DateTimeOffset _utcNow = new(2026, 9, 17, 10, 0, 0, TimeSpan.FromHours(8));

        public long NowTicks => _ticks;

        public DateTimeOffset UtcNow => _utcNow;

        /// <summary>单调刻度 + 墙钟一起正常前进（模拟真实时间流逝）。</summary>
        public void Advance(TimeSpan delta)
        {
            _ticks += RuntimeClockMath.MsToTicks((int)delta.TotalMilliseconds);
            _utcNow += delta;
        }

        /// <summary>只动墙钟（模拟 NTP 回拨 / 手动校时），单调刻度不动。</summary>
        public void JumpWallClock(TimeSpan delta) => _utcNow += delta;
    }

    private static SamplerConfiguration Load(FakeClock clock, string defaultInterval = "200")
    {
        var xml =
            "<SamplerConfig schemaVersion=\"3.0\">"
            + "<Global>"
            + "<Polling defaultIntervalMs=\"" + defaultInterval + "\" requestTimeoutMs=\"500\" />"
            + "<Quality onCommError=\"bad\" onCommErrorValue=\"null\" />"
            + "</Global>"
            + "<Transports><Transport id=\"tcp1\" host=\"127.0.0.1\" /></Transports>"
            + "<Devices><Device id=\"d1\" transport=\"tcp1\" pointSet=\"ps1\" unitId=\"1\" /></Devices>"
            + "<PointSets><PointSet id=\"ps1\"><Points>"
            + "<Point id=\"p0\" address=\"0\" intervalMs=\"" + defaultInterval + "\" />"
            + "</Points></PointSet></PointSets>"
            + "</SamplerConfig>";
        var config = SamplerConfigLoader.Load(XDocument.Parse(xml), Directory.GetCurrentDirectory());
        config.Clock = clock;
        return config;
    }

    private void Start(FakeClock clock)
    {
        _link.DefaultReadData = new ushort[256]; // 健康链路：读得到，不触发退避
        _engine = new SamplerEngine(Load(clock), new Dictionary<string, IModbusLink> { ["tcp1"] = _link });
        _engine.Start();
    }

    private static void WaitUntil(Func<bool> condition, string because, int timeoutMs = 8000)
        => Assert.True(SpinWait.SpinUntil(condition, timeoutMs), because);

    /// <summary>等待条件成立，期间每 10ms 推进假时钟 20ms（模拟真实时钟连续流逝）。</summary>
    private void AdvanceUntil(FakeClock clock, Func<bool> condition, string because, int timeoutMs = 8000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            clock.Advance(TimeSpan.FromMilliseconds(20));
            Thread.Sleep(10);
        }

        Assert.True(condition(), because);
    }

    [Fact]
    public void Wall_clock_rollback_does_not_freeze_polling_and_new_timestamps_follow_wall_clock()
    {
        var clock = new FakeClock();
        Start(clock);

        // 单调刻度推进：轮询必须真的发出请求并稳定流动（先读到 3 次，确认进入稳态再测回拨）
        AdvanceUntil(clock, () => _link.ReadCalls >= 3, "启动后单调刻度推进应稳定轮询（先读到 3 次）");

        var beforeRollback = clock.UtcNow;

        // 墙钟回拨 1 小时（单调刻度不动）。修复前（Scheduler 用 DateTime.UtcNow 算到期）这里会冻结：
        // 所有已算好的到期时刻都落在「未来 1 小时」，轮询线程要等墙钟追上来才发请求。
        clock.JumpWallClock(TimeSpan.FromHours(-1));

        // 回拨后**以回拨瞬间的计数为基线**再要求新增 3 次读：冻结实现（排程用墙钟）回拨后到期时刻全部
        // 落在未来 1 小时，最多只有 1 笔「在途读」能跨过回拨边界（其时间戳恰好落在回拨后，连时间戳断言都能糊弄过去）；
        // 3 次新读只能来自「排程真的不受墙钟影响」。
        var baselineAfterRollback = _link.ReadCalls;
        AdvanceUntil(clock,
            () => _link.ReadCalls >= baselineAfterRollback + 3,
            $"墙钟回拨后轮询必须继续（排程只看单调刻度）：回拨瞬间已读 {baselineAfterRollback} 次，需要 +3，等待 8s 后只读到 {_link.ReadCalls} 次");

        // 回拨后产生的**新采样**时间戳用墙钟 → 应落在回拨后的时刻（远早于回拨前的墙钟）
        WaitUntil(
            () => _engine!.GetValueDetail("d1", "p0").Timestamp < beforeRollback.AddMinutes(-50),
            "回拨后的新采样时间戳必须用墙钟（UtcNow），落在回拨后的时刻");
    }

    [Fact]
    public void Wall_clock_forward_jump_does_not_trigger_backoff_or_break_cadence()
    {
        var clock = new FakeClock();
        Start(clock);

        AdvanceUntil(clock, () => _link.ReadCalls >= 1, "启动后单调刻度推进即应发出首次请求");

        var entered = new List<BackoffEnteredEvent>();
        _engine!.Bus.Subscribe<BackoffEnteredEvent>(e => entered.Add(e.Body), DeliveryMode.Inline);

        var beforeJump = clock.UtcNow;
        var readsBeforeJump = _link.ReadCalls;

        // 墙钟前跳 1 小时：超时/退避判定只看单调刻度，前跳不得凭空产生超时或退避
        clock.JumpWallClock(TimeSpan.FromHours(1));
        AdvanceUntil(clock,
            () => _link.ReadCalls > readsBeforeJump,
            "墙钟前跳后轮询必须按单调刻度继续");

        Assert.Empty(entered); // 健康链路 + 前跳：不得出现任何退避进入事件
        WaitUntil(() => _engine.GetValueDetail("d1", "p0").Timestamp >= beforeJump.AddMinutes(50),
            "前跳后的新采样时间戳用墙钟（UtcNow），应落在前跳后的时刻");
    }
}
