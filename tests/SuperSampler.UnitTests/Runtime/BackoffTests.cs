using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using SuperSampler.Abstractions.Errors;
using SuperSampler.Abstractions.Events;
using SuperSampler.Abstractions.Facade;
using SuperSampler.Abstractions.Values;
using SuperSampler.Core.Config;
using SuperSampler.Core.Runtime;
using SuperSampler.Drivers.Modbus;
using SuperSampler.Drivers.Modbus.Wire;
using SuperSampler.UnitTests.Support;
using Xunit;

namespace SuperSampler.UnitTests.Runtime;

/// <summary>
/// 两层退避 + 手动重试 + GetValueAge（docs/11 §一.1/§一.6，PROD-17..20 的单测侧）：
/// <list type="bullet">
/// <item>队列语义：逐项等待、用完后用最后一个值循环、成功一次归零；</item>
/// <item>分层判定：读超时 → 设备级（其它从站照常、退避期不发请求）；IO 失败 → 链路级（关连接）；协议异常不计入；</item>
/// <item>防假死第二道防线：同链路全部从站都在设备级退避 → 升级为链路级（重建连接）；</item>
/// <item>手动重试两粒度：打断等待、立即重试、成功归零；<c>manualRetry=false</c> → NotSupported；</item>
/// <item>退避期间质量按 <c>offlineQuality</c> 置位；反复进退避不泄漏句柄/线程；</item>
/// <item><c>GetValueAge</c>：从未采集 / 刚采集 / 久未采集。</item>
/// </list>
/// 全部经 <see cref="SamplerEngine"/> 注入 <see cref="FakeModbusLink"/> 驱动真实调度线程（findings B1 的接缝）。
/// </summary>
/// <remarks>
/// 加入命名集合：B16 断言**进程级**句柄/线程数，必须与其它「起真实轮询线程」的用例串行采样
/// （并行执行时别的用例开的线程会被误判成本用例的泄漏）。
/// </remarks>
[Collection("real-polling-threads")]
public sealed class BackoffTests : IDisposable
{
    private readonly FakeModbusLink _link = new();

    // 事件收集一律走线程安全收集器：轮询线程在追加、测试线程在读取/清空，
    // 裸 List<T> 的并发 Add + Clear/索引在并行负载下会读到半截状态（这是本文件此前偶发失败的根因之一）
    private readonly Collector<BackoffEnteredEvent> _entered = new();
    private readonly Collector<BackoffRecoveredEvent> _recovered = new();
    private readonly Collector<IErrorEvent> _errors = new();
    private SamplerEngine? _engine;

    /// <summary>线程安全的事件收集器（追加/读取/清空都加锁）。</summary>
    private sealed class Collector<T>
    {
        private readonly List<T> _items = new();

        public void Add(T item)
        {
            lock (_items)
            {
                _items.Add(item);
            }
        }

        public void Clear()
        {
            lock (_items)
            {
                _items.Clear();
            }
        }

        public IReadOnlyList<T> Items
        {
            get
            {
                lock (_items)
                {
                    return _items.ToArray();
                }
            }
        }

        public int Count
        {
            get
            {
                lock (_items)
                {
                    return _items.Count;
                }
            }
        }
    }

    public void Dispose() => _engine?.Dispose();

    // ─────────────── 装配 ───────────────

    /// <summary>
    /// 两台从站（d1/unit1、d2/unit2）共用一条链路：设备级退避的用例必须有两个从站，
    /// 否则「同链路全部从站都退避 → 升级链路级」会立刻触发（那是 B6 的用例）。
    /// </summary>
    private const string TwoDevices =
        "<Device id=\"d1\" transport=\"tcp1\" pointSet=\"ps1\" unitId=\"1\" />"
        + "<Device id=\"d2\" transport=\"tcp1\" pointSet=\"ps2\" unitId=\"2\" />";

    private const string OneDevice =
        "<Device id=\"d1\" transport=\"tcp1\" pointSet=\"ps1\" unitId=\"1\" />";

    private const string TwoPointSets =
        "<PointSet id=\"ps1\"><Points><Point id=\"p1\" address=\"0\" intervalMs=\"100\" /></Points></PointSet>"
        + "<PointSet id=\"ps2\"><Points><Point id=\"p2\" address=\"0\" intervalMs=\"100\" /></Points></PointSet>";

    private const string OnePointSet =
        "<PointSet id=\"ps1\"><Points><Point id=\"p1\" address=\"0\" intervalMs=\"100\" /></Points></PointSet>";

    /// <summary>启动引擎。reconnectAttrs 为 <c>&lt;Reconnect&gt;</c> 的附加属性（delays/manualRetry/offlineQuality）。</summary>
    private SamplerEngine Start(string reconnectAttrs = "", string devices = TwoDevices, string pointSets = TwoPointSets,
        string quality = "onCommError=\"bad\" onCommErrorValue=\"null\"", bool healthy = true)
    {
        if (healthy) _link.DefaultReadData = new ushort[512];

        var xml = "<SamplerConfig schemaVersion=\"3.0\">" +
                  "<Global><Polling defaultIntervalMs=\"100\" requestTimeoutMs=\"150\" />" +
                  "<Quality " + quality + " />" +
                  "<Reconnect " + reconnectAttrs + " /></Global>" +
                  "<Transports><Transport id=\"tcp1\" host=\"127.0.0.1\" /></Transports>" +
                  "<Devices>" + devices + "</Devices>" +
                  "<PointSets>" + pointSets + "</PointSets>" +
                  "</SamplerConfig>";

        var config = SamplerConfigLoader.Load(XDocument.Parse(xml), Directory.GetCurrentDirectory());
        _engine = new SamplerEngine(config, new Dictionary<string, IModbusLink> { ["tcp1"] = _link });
        _engine.Bus.Subscribe<BackoffEnteredEvent>(e => _entered.Add(e.Body), DeliveryMode.Inline);
        _engine.Bus.Subscribe<BackoffRecoveredEvent>(e => _recovered.Add(e.Body), DeliveryMode.Inline);
        _engine.Bus.Subscribe<IErrorEvent>(e => _errors.Add(e.Body), DeliveryMode.Inline);
        _engine.Start();
        return _engine;
    }

    private static void WaitUntil(Func<bool> condition, int timeoutMs, string because)
        => Assert.True(SpinWait.SpinUntil(condition, timeoutMs), because);

    private List<BackoffEnteredEvent> DeviceEntries(string deviceId)
        => _entered.Items.Where(e => e.Scope == BackoffScope.Device && e.DeviceId == deviceId).ToList();

    private List<BackoffEnteredEvent> LinkEntries()
        => _entered.Items.Where(e => e.Scope == BackoffScope.Link).ToList();

    private int ReadsOf(byte unitId) => _link.Calls.Count(c => c.IsRead && c.UnitId == unitId);

    // ─────────────── 队列语义 ───────────────

    [Fact]
    public void B1_delay_queue_waits_in_order_and_then_repeats_last_value()
    {
        // d1 一直读超时（设备级），队列 120,300 → 观察 120 → 300 → 300（用完后一直用最后一个值）
        _link.FaultRules.Add(new FakeFaultRule { UnitId = 1, Kind = ModbusFailureKind.Timeout });
        Start("delays=\"120,300\"");

        WaitUntil(() => DeviceEntries("d1").Count >= 3, 8_000, "d1 应连续三次失败并三次进入退避");
        var entries = DeviceEntries("d1");

        Assert.Equal(new[] { 1, 2, 3 }, entries.Take(3).Select(e => e.Attempt).ToArray());
        Assert.Equal(new[] { 120, 300, 300 }, entries.Take(3).Select(e => e.DelayMs).ToArray());
        Assert.All(entries.Take(3), e => Assert.Equal("MODBUS.TIMEOUT", e.ReasonCode));

        // 下次重试时刻逐档推后（宿主可直接显示「下次重试时间」）；不断言绝对时刻，避免与调度节奏赛跑
        var retries = entries.Take(3).Select(e => e.NextRetryAt).ToList();
        Assert.All(retries, r => Assert.NotEqual(default, r));
        Assert.True(retries[0] < retries[1] && retries[1] < retries[2], "每档的 NextRetryAt 必须推后");

        // 其它从站不受影响（设备级不是链路级）
        Assert.True(ReadsOf(2) > 3, "同链路的 d2 照常轮询（设备级退避只排该从站）");
        Assert.Empty(LinkEntries());
    }

    [Fact]
    public void B2_success_once_resets_the_queue()
    {
        // 前两次失败（120/300），第三次成功 → 恢复；再失败一次 → 档位必须从第一档重来
        _link.FaultRules.Add(new FakeFaultRule { UnitId = 1, Kind = ModbusFailureKind.Timeout, RemainingCalls = 2 });
        Start("delays=\"120,300\"");

        WaitUntil(() => _recovered.Items.Any(r => r.Scope == BackoffScope.Device && r.DeviceId == "d1"), 8_000,
            "成功一次即恢复（发 BackoffRecoveredEvent）");

        var recovery = _recovered.Items.First(r => r.Scope == BackoffScope.Device && r.DeviceId == "d1");
        Assert.Equal(2, recovery.Attempts); // 连续失败 2 次后恢复
        WaitUntil(() => _engine!.GetValueDetail("d1", "p1").IsGood, 5_000, "恢复后质量回 Good");

        // 再注入一次失败：档位从 1 重新开始（队列已归零）。
        // 断言口径改为「等到出现第一档的条目」而不是「等到有条目」——后者在慢负载下可能先看到旧档位。
        _entered.Clear();
        _link.FaultRules.Add(new FakeFaultRule { UnitId = 1, Kind = ModbusFailureKind.Timeout, RemainingCalls = 1 });

        WaitUntil(() => DeviceEntries("d1").Any(e => e.Attempt == 1), 8_000, "再次失败应从第一档重新开始");
        var afterReset = DeviceEntries("d1")[0];
        Assert.Equal(1, afterReset.Attempt);
        Assert.Equal(120, afterReset.DelayMs);
    }

    // ─────────────── 分层判定 ───────────────

    [Fact]
    public void B3_timeout_is_device_scope_and_sends_nothing_while_waiting()
    {
        _link.FaultRules.Add(new FakeFaultRule { UnitId = 1, Kind = ModbusFailureKind.Timeout });
        Start("delays=\"600\"");

        WaitUntil(() => DeviceEntries("d1").Count >= 1, 5_000, "读超时必须判为设备级退避");

        // 退避期间（600ms）该设备一个请求都不发
        var frozen = _link.Calls.Count(c => c.IsRead && c.UnitId == 1);
        Thread.Sleep(300);
        Assert.Equal(frozen, _link.Calls.Count(c => c.IsRead && c.UnitId == 1));

        // 同链路另一个从站照常轮询，且从未进退避
        var otherBefore = ReadsOf(2);
        Thread.Sleep(200);
        Assert.True(ReadsOf(2) > otherBefore, "同链路的 d2 必须照常轮询");
        Assert.Empty(DeviceEntries("d2"));
        Assert.Empty(LinkEntries());

        // 退避期间质量按 offlineQuality（默认 offline）
        Assert.Equal(PointQuality.Offline, _engine!.GetValueDetail("d1", "p1").Quality);
    }

    [Fact]
    public void B4_io_failure_is_link_scope_closes_connection_and_holds_both_slaves()
    {
        _link.FaultRules.Add(new FakeFaultRule { UnitId = 1, Kind = ModbusFailureKind.LinkDown });
        Start("delays=\"600\"");

        WaitUntil(() => LinkEntries().Count >= 1, 5_000, "IO 失败必须升级为链路级退避");

        var linkEntry = LinkEntries()[0];
        Assert.Equal("MODBUS.LINK", linkEntry.ReasonCode);
        Assert.Null(linkEntry.DeviceId);

        // 「关闭连接 → 等待 → 重连」：链路级退避必须真的关掉连接
        Assert.True(_link.CloseCount >= 1, "链路级退避必须关闭连接");
        Assert.False(_link.IsOpen);

        // 整条链路一起等：两个从站在退避期间都不发请求
        var frozen = _link.Calls.Count(c => c.IsRead);
        Thread.Sleep(300);
        Assert.Equal(frozen, _link.Calls.Count(c => c.IsRead));
        Assert.Equal(PointQuality.Offline, _engine!.GetValueDetail("d2", "p2").Quality);
    }

    [Fact]
    public void B5_protocol_exception_is_not_counted_as_failure()
    {
        // 设备明确回异常码 0x02：链路通、设备在回话 → 不进队列，也不中断轮询节奏
        _link.FaultRules.Add(new FakeFaultRule
        {
            Kind = ModbusFailureKind.Protocol,
            ExceptionCode = 0x02,
            RemainingCalls = 3,
        });
        Start("delays=\"1500\"");

        WaitUntil(() => _errors.Items.OfType<DeviceExceptionError>().Any(), 5_000, "协议异常必须照常上报错误事件");
        Thread.Sleep(400); // 1500ms 的第一档退避若被触发，这 400ms 内不会再有请求

        Assert.Empty(_entered.Items);
        Assert.Empty(_recovered.Items);
        Assert.True(ReadsOf(1) >= 3, $"协议异常不进队列：轮询节奏不受影响（实测 {ReadsOf(1)} 次）");
        Assert.True(_link.CloseCount == 0, "协议异常不该关连接");
    }

    [Fact]
    public void B6_all_slaves_in_device_backoff_escalate_to_link_backoff()
    {
        // 一条链路只有一台从站：它一进设备级退避，「全部从站都在退避」成立 → 升级链路级（重建连接）
        _link.FaultRules.Add(new FakeFaultRule { UnitId = 1, Kind = ModbusFailureKind.Timeout });
        Start("delays=\"300,300\"", devices: OneDevice, pointSets: OnePointSet);

        WaitUntil(() => LinkEntries().Any(e => e.ReasonCode == "SS.BACKOFF.ALL_DEVICES"), 5_000,
            "同链路全部从站退避必须升级为链路级退避");

        Assert.NotEmpty(DeviceEntries("d1"));                 // 先有设备级
        Assert.True(_link.CloseCount >= 1, "升级链路级必须重建连接（关连接）");
    }

    [Fact]
    public void B7_offline_quality_governs_quality_while_backing_off()
    {
        _link.FaultRules.Add(new FakeFaultRule { UnitId = 1, Kind = ModbusFailureKind.Timeout });
        Start("delays=\"800\" offlineQuality=\"bad\"");

        WaitUntil(() => DeviceEntries("d1").Count >= 1, 5_000, "应进入退避");
        WaitUntil(() => _engine!.GetValueDetail("d1", "p1").Quality == PointQuality.Bad, 5_000,
            "offlineQuality=bad：退避期间质量应为 Bad（而不是 onCommError 的值）");

        // 退避置位不改值策略：onCommErrorValue=null → 值置空
        Assert.Null(_engine!.GetValueDetail("d1", "p1").Value);
    }

    [Fact]
    public void B7b_keep_last_value_is_kept_while_backing_off()
    {
        _link.DefaultReadData = new ushort[] { 42 };
        Start("delays=\"800\"", devices: OneDevice, pointSets: OnePointSet,
            quality: "onCommError=\"bad\" onCommErrorValue=\"keepLast\"", healthy: false);

        WaitUntil(() => _engine!.GetValueDetail("d1", "p1").IsGood, 5_000, "先采到一个好值");
        _link.DefaultReadData = null;                       // 之后读不到 → 失败
        _link.FaultRules.Add(new FakeFaultRule { UnitId = 1, Kind = ModbusFailureKind.Timeout });

        WaitUntil(() => _engine!.GetValueDetail("d1", "p1").Quality == PointQuality.Offline, 5_000,
            "退避期间质量按 offlineQuality 置位");
        Assert.Equal((ushort)42, Assert.IsType<ushort>(_engine!.GetValueDetail("d1", "p1").Value)); // 值保留
    }

    [Fact]
    public void B8_backoff_disabled_keeps_polling_and_emits_no_backoff_events()
    {
        _link.FaultRules.Add(new FakeFaultRule { UnitId = 1, Kind = ModbusFailureKind.Timeout });
        Start("enabled=\"false\"", devices: OneDevice, pointSets: OnePointSet);

        Thread.Sleep(500);

        Assert.Empty(_entered.Items);
        Assert.True(ReadsOf(1) >= 3, $"退避关闭 = 每拍照常发请求（实测 {ReadsOf(1)} 次）");
    }

    // ─────────────── 手动重试 ───────────────

    [Fact]
    public async Task B9_device_retry_interrupts_the_wait_and_succeeds()
    {
        _link.FaultRules.Add(new FakeFaultRule { UnitId = 1, Kind = ModbusFailureKind.Timeout, RemainingCalls = 1 });
        Start("delays=\"5000\""); // 第一档就 5s：不打断就必须干等

        WaitUntil(() => DeviceEntries("d1").Count >= 1, 5_000, "先进入退避");

        var started = DateTime.UtcNow;
        var result = await _engine!.RetryDeviceAsync("d1");

        Assert.Equal(ManualRetryOutcome.Succeeded, result.Outcome);
        Assert.True(result.InterruptedBackoff, "RetryDeviceAsync 必须打断正在进行的等待");
        Assert.Null(result.Error);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(4), "必须立刻重试，而不是等完 5s（容差放宽到 4s，避免慢负载下假失败）");
        Assert.True(_engine.GetValueDetail("d1", "p1").IsGood, "重试成功后质量回 Good");
        Assert.Contains(_recovered.Items, r => r.Scope == BackoffScope.Device && r.DeviceId == "d1");
    }

    [Fact]
    public async Task B10_link_retry_closes_connection_reconnects_and_clears_device_backoff()
    {
        // d1 读超时进设备级退避（连接还开着）：RetryLinkAsync 必须关连接 → 重连 → 两台从站都刷一遍
        _link.FaultRules.Add(new FakeFaultRule { UnitId = 1, Kind = ModbusFailureKind.Timeout, RemainingCalls = 1 });
        Start("delays=\"5000\"");

        WaitUntil(() => DeviceEntries("d1").Count >= 1, 5_000, "d1 先进入设备级退避");
        var closesBefore = _link.CloseCount;

        var result = await _engine!.RetryLinkAsync("tcp1");

        Assert.Equal(ManualRetryOutcome.Succeeded, result.Outcome);
        Assert.True(result.InterruptedBackoff, "链路重试必须打断链路上（含设备级）的退避等待");
        Assert.True(_link.CloseCount > closesBefore, "RetryLinkAsync 必须关闭旧连接");
        Assert.True(ReadsOf(1) > 0 && ReadsOf(2) > 0, "链路上两台从站各尝试一次");
        Assert.True(_engine.GetValueDetail("d1", "p1").IsGood);
        Assert.True(_engine.GetValueDetail("d2", "p2").IsGood);
        Assert.True(_engine.GetValueAge("d1", "p1") < TimeSpan.FromSeconds(1), "手动重试的采集同样刷新 age");
    }

    [Fact]
    public async Task B11_manual_retry_disabled_returns_not_supported_without_traffic()
    {
        _link.FaultRules.Add(new FakeFaultRule { UnitId = 1, Kind = ModbusFailureKind.Timeout });
        Start("delays=\"5000\" manualRetry=\"false\"");

        WaitUntil(() => DeviceEntries("d1").Count >= 1, 5_000, "先进入退避");
        var readsBefore = ReadsOf(1);

        var device = await _engine!.RetryDeviceAsync("d1");
        var link = await _engine.RetryLinkAsync("tcp1");

        Assert.Equal(ManualRetryOutcome.NotSupported, device.Outcome);
        Assert.False(device.InterruptedBackoff);
        Assert.Equal(ManualRetryOutcome.NotSupported, link.Outcome);
        Assert.Equal(readsBefore, ReadsOf(1)); // 未发出任何通讯
        Assert.Equal(PointQuality.Offline, _engine.GetValueDetail("d1", "p1").Quality); // 退避未被改动
    }

    [Fact]
    public async Task B12_manual_retry_on_unknown_ids_throws()
    {
        Start();

        await Assert.ThrowsAsync<KeyNotFoundException>(() => _engine!.RetryDeviceAsync("nope"));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => _engine!.RetryLinkAsync("nope"));
    }

    // ─────────────── GetValueAge（ADR D40） ───────────────

    [Fact]
    public void B13_get_value_age_null_when_never_acquired()
    {
        // onDemand 点不参与轮询 → 从未成功采集
        Start(devices: OneDevice,
            pointSets: "<PointSet id=\"ps1\"><Points><Point id=\"od\" address=\"0\" mode=\"onDemand\" /></Points></PointSet>");

        Thread.Sleep(200);

        Assert.Null(_engine!.GetValueAge("d1", "od"));
    }

    [Fact]
    public void B14_get_value_age_is_small_right_after_acquisition_and_grows()
    {
        Start(devices: OneDevice, pointSets: OnePointSet);

        WaitUntil(() => _engine!.GetValueAge("d1", "p1") != null, 5_000, "成功采集后必须有 age");
        var fresh = _engine!.GetValueAge("d1", "p1")!.Value;

        Assert.True(fresh >= TimeSpan.Zero && fresh < TimeSpan.FromSeconds(3), $"刚采集的 age 应很小，实际 {fresh}");

        // 久未采集：停掉数据（读失败不再刷新「上次成功采集」），age 随时间增长且成功采集时刻不推进
        _link.DefaultReadData = null;
        _link.FaultRules.Add(new FakeFaultRule { UnitId = 1, Kind = ModbusFailureKind.Timeout });

        Thread.Sleep(250);
        var stale = _engine.GetValueAge("d1", "p1")!.Value;
        Assert.True(stale > fresh, $"久未采集的 age 必须增长（fresh={fresh} stale={stale}）");
        Assert.True(stale >= TimeSpan.FromMilliseconds(150), $"实测 {stale}");
    }

    [Fact]
    public void B15_get_value_age_on_unknown_point_throws()
    {
        Start(devices: OneDevice, pointSets: OnePointSet);

        Assert.Throws<KeyNotFoundException>(() => _engine!.GetValueAge("d1", "nope"));
        Assert.Throws<KeyNotFoundException>(() => _engine!.GetValueAge("nope", "p1"));
    }

    // ─────────────── 健壮性：反复进退避不泄漏 ───────────────

    [Fact]
    public async Task B16_repeated_backoff_cycles_do_not_leak_handles_threads_or_connections()
    {
        _link.DefaultReadData = new ushort[] { 7 };
        _link.ReopenOnRead = true;   // 读前懒重连：这样才能用 OpenCount/CloseCount 成对断言
        Start("delays=\"60\"", devices: OneDevice, pointSets: OnePointSet, healthy: false);

        WaitUntil(() => _engine!.GetValueDetail("d1", "p1").IsGood, 5_000, "先采到好值");
        var process = Process.GetCurrentProcess();
        var handlesBefore = process.HandleCount;
        var threadsBefore = process.Threads.Count;

        // 20 轮「链路级退避 → 手动重试恢复」：每轮关一次连接、重建一次，退避条目成功即删
        for (var i = 0; i < 20; i++)
        {
            _link.FaultRules.Add(new FakeFaultRule { UnitId = 1, Kind = ModbusFailureKind.LinkDown, RemainingCalls = 1 });

            WaitUntil(() => LinkEntries().Count > i, 5_000, $"第 {i + 1} 轮应进入链路级退避");
            var result = await _engine!.RetryLinkAsync("tcp1");
            Assert.NotEqual(ManualRetryOutcome.Failed, result.Outcome);
        }

        // 关连接次数 == 链路级退避次数（每轮一次，不多不少）；重连次数 == 之后的请求轮次（懒重连）
        Assert.Equal(_entered.Items.Count(e => e.Scope == BackoffScope.Link), _link.CloseCount);
        Assert.True(_link.OpenCount >= 20, $"每轮退避后必须重连（实测 {_link.OpenCount} 次）");
        Assert.True(_recovered.Count >= 20, $"每轮都该有恢复事件（实测 {_recovered.Count}）");

        process.Refresh();
        var handlesAfter = process.HandleCount;
        var threadsAfter = process.Threads.Count;

        Assert.True(handlesAfter <= handlesBefore + 32, $"20 轮反复进退避后句柄数不该明显增长（{handlesBefore} → {handlesAfter}）");
        Assert.True(threadsAfter <= threadsBefore + 4, $"线程数不该随退避增长（{threadsBefore} → {threadsAfter}）");
    }
}
