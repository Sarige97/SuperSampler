using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using SuperSampler.Abstractions.Errors;
using SuperSampler.Abstractions.Events;
using SuperSampler.Abstractions.Values;
using SuperSampler.Core.Config;
using SuperSampler.Core.Runtime;
using SuperSampler.Drivers.Modbus;
using SuperSampler.Drivers.Modbus.Wire;
using SuperSampler.UnitTests.Support;
using Xunit;

namespace SuperSampler.UnitTests.Runtime;

/// <summary>
/// 「调度与采集链路」模块的功能级 + 异常级重测（docs/11 §一.1–§一.7、§二、§五.2）：
/// <list type="bullet">
/// <item><b>块</b>：块自己的 intervalMs 分桶、块 mode（onDemand/once）、块内连续性容差（CGV-16 的 ignoreGap 边界）；</item>
/// <item><b>自动分组</b>：不同数据区不混组、位区 2000/2001 切分、ignoreGap 逐洞容差、拆分不漏点；</item>
/// <item><b>间隔</b>：50ms 最小节拍可用、极大间隔只读一次、同间隔同节拍背靠背；</item>
/// <item><b>mode</b>：once 失败重试（退避 / 协议异常 / 退避关闭三条路径）与退避期零请求、onDemand 整批触发；</item>
/// <item><b>退避</b>：到期自动恢复（线程不死）、链路级到期懒重连、Stop 在长退避中立即返回、重复 Start 不翻倍；</item>
/// <item><b>并发</b>：同链路多从站读写不串台、Dispose 与重负载轮询并发；</item>
/// <item><b>边界</b>：地址 65535、位区 2000/2001、禁用链路上的 onDemand 触发；</item>
/// <item><b>Slices</b>：并入自动分组后窗口地址序列与邻居解码、跨度等于上限的边界；</item>
/// <item><b>计算点</b>：依赖链、跨点表限定引用、依赖坏值传播；</item>
/// <item><b>门面</b>：非 Good 一律返回全局 nullText、GetValueAge 语义。</item>
/// </list>
/// 全部经 <see cref="SamplerEngine"/> 注入 <see cref="FakeModbusLink"/> 驱动真实调度线程（findings B1 的接缝）；
/// 加入 <c>real-polling-threads</c> 集合与其它起真实轮询线程的用例串行采样。
/// </summary>
[Collection("real-polling-threads")]
public sealed class SchedulerFunctionTests : IDisposable
{
    private readonly FakeModbusLink _link = new();
    private readonly Collector<BackoffEnteredEvent> _entered = new();
    private readonly Collector<BackoffRecoveredEvent> _recovered = new();
    private readonly Collector<IErrorEvent> _errors = new();
    private SamplerEngine? _engine;

    public void Dispose() => _engine?.Dispose();

    // ─────────────── 线程安全事件收集器（轮询线程追加、测试线程读取）───────────────

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

    // ─────────────── 装配 ───────────────

    private static string Xml(string devices, string pointSets, string globalExtra = "", string globalAttrs = "",
        string defaultInterval = "2000", string transports = "<Transport id=\"tcp1\" host=\"127.0.0.1\" />")
        => "<SamplerConfig schemaVersion=\"3.0\">"
           + "<Global" + globalAttrs + ">"
           + "<Polling defaultIntervalMs=\"" + defaultInterval + "\" requestTimeoutMs=\"500\" />"
           + "<Quality onCommError=\"bad\" onCommErrorValue=\"null\" />"
           + globalExtra
           + "</Global>"
           + "<Transports>" + transports + "</Transports>"
           + "<Devices>" + devices + "</Devices>"
           + "<PointSets>" + pointSets + "</PointSets>"
           + "</SamplerConfig>";

    private static SamplerConfiguration Load(string devices, string pointSets, string globalExtra = "",
        string globalAttrs = "", string defaultInterval = "2000", string transports = "<Transport id=\"tcp1\" host=\"127.0.0.1\" />")
        => SamplerConfigLoader.Load(
            XDocument.Parse(Xml(devices, pointSets, globalExtra, globalAttrs, defaultInterval, transports)),
            Directory.GetCurrentDirectory());

    private SamplerEngine Start(string devices, string pointSets, string globalExtra = "", string globalAttrs = "",
        string defaultInterval = "2000", string transports = "<Transport id=\"tcp1\" host=\"127.0.0.1\" />")
    {
        var config = Load(devices, pointSets, globalExtra, globalAttrs, defaultInterval, transports);
        _engine = new SamplerEngine(config, new Dictionary<string, IModbusLink> { ["tcp1"] = _link });
        _engine.Bus.Subscribe<BackoffEnteredEvent>(e => _entered.Add(e.Body), DeliveryMode.Inline);
        _engine.Bus.Subscribe<BackoffRecoveredEvent>(e => _recovered.Add(e.Body), DeliveryMode.Inline);
        _engine.Bus.Subscribe<IErrorEvent>(e => _errors.Add(e.Body), DeliveryMode.Inline);
        _engine.Start();
        return _engine;
    }

    /// <summary>链路有数据（读得到）：只关心「怎么读」的用例不该被失败/退避影响。</summary>
    private void Healthy(ushort fill = 0) => _link.DefaultReadData = new ushort[4096].Select(_ => fill).ToArray();

    private static void WaitUntil(Func<bool> condition, int timeoutMs, string because)
        => Assert.True(SpinWait.SpinUntil(condition, timeoutMs), because);

    /// <summary>
    /// 轮询直到条件成立（或超时），返回**观测到的墙钟时刻**——用来替代「睡固定时长再断言」。
    /// 观测只会迟到、不会提前：拿它算间隔，判定方向是保守的，满负载下不会假失败
    /// （旧写法「睡 350ms 断言请求数不变」在测试线程被调度延迟时会误判退避已到期）。
    /// </summary>
    private static DateTime? WaitForObservation(Func<bool> condition, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return DateTime.UtcNow;
            Thread.Sleep(5);
        }

        return condition() ? DateTime.UtcNow : null;
    }

    private List<FakeLinkCall> Reads(byte unitId) => _link.Calls.Where(c => c.IsRead && c.UnitId == unitId).ToList();

    private IReadOnlyList<FakeLinkCall> AllReads => _link.Calls.Where(c => c.IsRead).ToArray();

    private const string DevD1 = "<Device id=\"d1\" transport=\"tcp1\" pointSet=\"ps1\" unitId=\"1\" />";
    private const string DevD2 = "<Device id=\"d2\" transport=\"tcp1\" pointSet=\"ps2\" unitId=\"2\" />";

    /// <summary>第二台从站（unit2）：两层退避用例必须有两条从站，否则「全部从站退避 → 升级链路级」会立刻触发。</summary>
    private const string Ps2One = "<PointSet id=\"ps2\"><Points><Point id=\"q\" address=\"0\" intervalMs=\"500\" /></Points></PointSet>";

    // ═══════════════ 一、块（Block） ═══════════════

    [Fact]
    public void Cgv16_block_hole_beyond_the_configured_ignore_gap_is_rejected()
    {
        // ignoreGap=1（容差 1 个地址）：点位 0 与 3 之间空 2 个地址 → 超过容差，加载期必须报错
        var ex = Assert.Throws<ConfigValidationException>(() => Load(DevD1,
            "<PointSet id=\"ps1\"><Blocks><Block id=\"b1\" start=\"0\" count=\"8\">"
            + "<Point id=\"p0\" address=\"0\" /><Point id=\"p3\" address=\"3\" />"
            + "</Block></Blocks><Points /></PointSet>",
            globalExtra: "<Scheduler ignoreGap=\"1\" />"));

        Assert.Contains(ex.Errors, e => e.Contains("块内点位地址不连续") && e.Contains("Block b1") && e.Contains("p3"));
        Assert.Contains("超过忽略间隔数 1", ex.Message);
    }

    [Fact]
    public void Cgv16_block_hole_within_the_configured_ignore_gap_is_accepted_and_read_in_one_request()
    {
        // 同样的 0/3 两点，容差放到 2 → 通过，且运行期就是一次请求读回（count=4）
        Healthy();
        Start(DevD1,
            "<PointSet id=\"ps1\"><Blocks><Block id=\"b1\" start=\"0\" count=\"8\" intervalMs=\"5000\">"
            + "<Point id=\"p0\" address=\"0\" /><Point id=\"p3\" address=\"3\" />"
            + "</Block></Blocks><Points /></PointSet>",
            globalExtra: "<Scheduler ignoreGap=\"2\" />");

        Thread.Sleep(300);

        // 块 = 显式声明的一段窗口：一次请求读的就是块声明的 [start, start+count)，与块内点位无关
        var read = Assert.Single(Reads(1));
        Assert.Equal(0, read.Address);
        Assert.Equal(8, read.Count);
        Assert.True(_engine!.GetValueDetail("d1", "p0").IsGood);
        Assert.True(_engine.GetValueDetail("d1", "p3").IsGood);
    }

    [Fact]
    public void Block_interval_gives_each_block_its_own_cadence()
    {
        // 两个块：b1 每 100ms、b2 每 600ms。块间隔必须各自生效（不被全局默认 3000ms 绑架，也不互相牵连）。
        // 断言用「相对时间」：等 100ms 的块攒够 5 次读数的那一刻做快照——此刻 600ms 的块只可能读 1~2 次。
        // 不绑墙钟窗口（旧的 sleep(900) + 计数上界在满负载下会假失败）。
        Healthy();
        Start(DevD1,
            "<PointSet id=\"ps1\">"
            + "<Blocks>"
            + "<Block id=\"b1\" start=\"0\" count=\"1\" intervalMs=\"100\"><Point id=\"fast\" address=\"0\" /></Block>"
            + "<Block id=\"b2\" start=\"100\" count=\"1\" intervalMs=\"600\"><Point id=\"slow\" address=\"100\" /></Block>"
            + "</Blocks><Points /></PointSet>",
            defaultInterval: "3000");

        var fast = 0;
        var slow = 0;
        WaitUntil(
            () =>
            {
                fast = Reads(1).Count(r => r.Address == 0);
                slow = Reads(1).Count(r => r.Address == 100);
                return fast >= 5;
            },
            5_000,
            "100ms 的块必须攒够 5 次读数");

        Assert.True(slow is >= 1 and <= 3, $"600ms 的块在 100ms 块读 5 次时只该读 1~2 次，实际 {slow}");
        Assert.True(fast > slow, "两个块必须各走自己的节拍");
    }

    [Fact]
    public async Task On_demand_block_is_not_polled_and_is_updated_by_the_batch_trigger()
    {
        // 块 mode=onDemand：轮询窗内零请求；门面整批触发后块内两点都刷新
        Healthy();
        _link.SetReadData(1, DataArea.HoldingRegister, 200, 11, 22);
        Start(DevD1,
            "<PointSet id=\"ps1\">"
            + "<Blocks><Block id=\"od\" start=\"200\" count=\"2\" mode=\"onDemand\">"
            + "<Point id=\"a\" address=\"200\" /><Point id=\"b\" address=\"201\" />"
            + "</Block></Blocks>"
            + "<Points><Point id=\"auto\" address=\"0\" intervalMs=\"100\" /></Points>"
            + "</PointSet>");

        Thread.Sleep(300);

        Assert.Empty(Reads(1).Where(r => r.Address == 200));   // onDemand 块不进周期计划
        Assert.NotEmpty(Reads(1).Where(r => r.Address == 0));  // auto 点照常轮询

        var updated = await _engine!.TriggerOnDemandReadAsync("d1");

        Assert.Equal(2, updated);
        Assert.Equal((ushort)11, Assert.IsType<ushort>(_engine.GetValueDetail("d1", "a").Value));
        Assert.Equal((ushort)22, Assert.IsType<ushort>(_engine.GetValueDetail("d1", "b").Value));
    }

    [Fact]
    public void Once_block_failure_is_retried_until_the_first_success()
    {
        // once 块：前两次读超时 → 必须按退避重试到成功一次；之后不再读
        Healthy();
        _link.SetReadData(1, DataArea.HoldingRegister, 0, 5, 6);
        _link.FaultRules.Add(new FakeFaultRule { UnitId = 1, Kind = ModbusFailureKind.Timeout, RemainingCalls = 2 });

        Start(DevD1 + DevD2,
            "<PointSet id=\"ps1\"><Blocks><Block id=\"b1\" start=\"0\" count=\"2\" mode=\"once\">"
            + "<Point id=\"p0\" address=\"0\" /><Point id=\"p1\" address=\"1\" />"
            + "</Block></Blocks><Points /></PointSet>" + Ps2One,
            globalExtra: "<Reconnect delays=\"150\" />");

        WaitUntil(() => _engine!.GetValueDetail("d1", "p0").IsGood, 5_000, "once 块必须重试到成功一次");
        Thread.Sleep(300);

        Assert.Equal(3, Reads(1).Count);   // 两次失败 + 一次成功，成功之后不再读
        Assert.Equal((ushort)6, Assert.IsType<ushort>(_engine!.GetValueDetail("d1", "p1").Value));
    }

    // ═══════════════ 二、自动分组 ═══════════════

    [Fact]
    public void Points_in_different_areas_never_share_a_request()
    {
        // 同址（address=0）四个区的点位：必须拆成四次请求，各带自己的数据区与从站号
        Healthy();
        Start(DevD1,
            "<PointSet id=\"ps1\"><Points>"
            + "<Point id=\"h\" address=\"0\" />"
            + "<Point id=\"i\" address=\"0\" area=\"input\" />"
            + "<Point id=\"c\" address=\"0\" area=\"coil\" />"
            + "<Point id=\"d\" address=\"0\" area=\"discrete\" />"
            + "</Points></PointSet>");

        Thread.Sleep(300);

        var reads = Reads(1);
        Assert.Equal(4, reads.Count);
        Assert.All(reads, r =>
        {
            Assert.Equal(0, r.Address);
            Assert.Equal(1, r.Count);
            Assert.Equal(1, r.UnitId);
        });

        Assert.Equal(new[] { DataArea.Coil, DataArea.DiscreteInput, DataArea.InputRegister, DataArea.HoldingRegister },
            reads.Select(r => r.Area).OrderBy(a => (int)a).ToArray());
    }

    [Fact]
    public void Bit_area_group_splits_at_the_two_thousand_bit_limit_without_losing_points()
    {
        // 线圈 0..1999：一次请求 2000 位（位区上限，不受寄存器上限 125 影响）
        Healthy();
        Start(DevD1, CoilPoints(2000), defaultInterval: "5000");

        Thread.Sleep(400);

        var reads = Assert.Single(Reads(1));
        Assert.Equal(0, reads.Address);
        Assert.Equal(2000, reads.Count);
    }

    [Fact]
    public void Bit_area_group_of_2001_bits_splits_into_two_requests_in_one_tick()
    {
        // 线圈 0..2000（2001 位，严格相邻）→ 2000 + 1 两次请求，同一节拍读完，不漏点位
        Healthy();
        Start(DevD1, CoilPoints(2001), defaultInterval: "5000");

        Thread.Sleep(400);

        var reads = Reads(1);
        Assert.Equal(2, reads.Count);
        Assert.Equal(new[] { 2000, 1 }, reads.Select(r => r.Count).ToArray());
        Assert.Equal(new[] { 0, 2000 }, reads.Select(r => r.Address).ToArray());

        Assert.True(_engine!.GetValueDetail("d1", "c0").IsGood);
        Assert.True(_engine.GetValueDetail("d1", "c1999").IsGood);
        Assert.True(_engine.GetValueDetail("d1", "c2000").IsGood);   // 切分后最后一个点位仍在缓存里
    }

    [Fact]
    public void Ignore_gap_tolerance_merges_every_hole_within_gap()
    {
        // ignoreGap=1：0/2/4 三点的两个空洞都在容差内 → 一次请求 [0,5) count=5
        Healthy();
        Start(DevD1,
            "<PointSet id=\"ps1\"><Points>"
            + "<Point id=\"a\" address=\"0\" /><Point id=\"b\" address=\"2\" /><Point id=\"c\" address=\"4\" />"
            + "</Points></PointSet>",
            globalExtra: "<Scheduler ignoreGap=\"1\" />");

        Thread.Sleep(300);

        var read = Assert.Single(Reads(1));
        Assert.Equal(0, read.Address);
        Assert.Equal(5, read.Count);
    }

    // ═══════════════ 三、点位间隔与分桶 ═══════════════

    [Fact]
    public void Min_interval_50ms_polls_at_the_tick_cadence()
    {
        // 最小间隔 50ms = 调度节拍：必须真的按 50ms 跑（而不是被夹到更大值），也不能失控。
        // 相对时间口径：5 次读数应在 ~250ms 量级完成（上限 2s，容满负载），随后一段观察窗内的读数受速率上界约束。
        Healthy();
        Start(DevD1, "<PointSet id=\"ps1\"><Points><Point id=\"p\" address=\"0\" intervalMs=\"50\" /></Points></PointSet>");

        var startCount = Reads(1).Count;
        var started = DateTime.UtcNow;
        WaitUntil(() => Reads(1).Count >= startCount + 5, 5_000, "50ms 间隔必须持续出读数");
        var span = DateTime.UtcNow - started;

        Assert.True(span < TimeSpan.FromSeconds(2),
            $"50ms 间隔下 5 次读数不该超过 2s（实测 {span.TotalMilliseconds:F0}ms）");

        Thread.Sleep(200);
        var burst = Reads(1).Count - startCount;
        Assert.True(burst <= 25, $"50ms 间隔不应失控（实测 {burst} 次）");
    }

    [Fact]
    public void Huge_interval_reads_once_and_does_not_spin()
    {
        // 一小时的点位：Start 后先读一次（首拍），之后不再读——不能因为「到期时刻很大」而忙等
        Healthy();
        Start(DevD1, "<PointSet id=\"ps1\"><Points><Point id=\"p\" address=\"0\" intervalMs=\"3600000\" /></Points></PointSet>");

        WaitUntil(() => Reads(1).Count >= 1, 5_000, "首拍必须读一次");
        Thread.Sleep(200);

        Assert.Single(Reads(1));
        Assert.True(_engine!.GetValueDetail("d1", "p").IsGood);
    }

    [Fact]
    public void Same_interval_points_run_back_to_back_in_one_tick()
    {
        // 同间隔（200ms）的两个点位走同一节拍：每拍两个地址各一次，序列交替
        Healthy();
        Start(DevD1,
            "<PointSet id=\"ps1\"><Points>"
            + "<Point id=\"a\" address=\"0\" intervalMs=\"200\" /><Point id=\"b\" address=\"10\" intervalMs=\"200\" />"
            + "</Points></PointSet>");

        WaitUntil(() => Reads(1).Count >= 4, 5_000, "200ms 间隔下 4 次读数应在 ~400ms 内出现");

        var addresses = Reads(1).Select(r => r.Address).Take(4).ToArray();
        Assert.Equal(new[] { 0, 10, 0, 10 }, addresses);
    }

    // ═══════════════ 四、mode ═══════════════

    [Fact]
    public void Once_point_sends_nothing_while_backing_off()
    {
        // once 第一次失败 → 进退避（delays=1200）：等待期间一个请求都不发，到期才重试。
        // 时序判定改为「观测到进入退避之后，下一条新请求何时出现」（观测只会迟到 → 方向保守，不假失败）。
        Healthy();
        _link.SetReadData(1, DataArea.HoldingRegister, 0, 7);
        _link.FaultRules.Add(new FakeFaultRule { UnitId = 1, Kind = ModbusFailureKind.Timeout, RemainingCalls = 1 });

        Start(DevD1 + DevD2,
            "<PointSet id=\"ps1\"><Points><Point id=\"p\" address=\"0\" mode=\"once\" /></Points></PointSet>" + Ps2One,
            globalExtra: "<Reconnect delays=\"1200\" />");

        var enteredAt = WaitForObservation(() => _entered.Count > 0, 5_000);
        Assert.NotNull(enteredAt);
        Assert.Single(Reads(1));

        var frozen = Reads(1).Count;
        var nextReadAt = WaitForObservation(() => Reads(1).Count > frozen, 5_000);
        Assert.NotNull(nextReadAt);   // 退避到期后 once 必须重试（轮询线程不得静默死亡）

        var gap = nextReadAt.Value - enteredAt.Value;
        Assert.True(gap >= TimeSpan.FromMilliseconds(700),
            $"退避窗（1200ms）内该从站不得发请求（实测观测间隔 {gap.TotalMilliseconds:F0}ms）");

        WaitUntil(() => _engine!.GetValueDetail("d1", "p").IsGood, 5_000, "退避到期后 once 必须重试并成功");
        Assert.Equal(2, Reads(1).Count);   // 一次失败 + 一次成功；成功之后不再读
        Assert.Equal((ushort)7, Assert.IsType<ushort>(_engine!.GetValueDetail("d1", "p").Value));
    }

    [Fact]
    public void Once_point_retries_after_a_protocol_exception_without_busy_spin()
    {
        // 协议异常不进两层退避（链路通、设备在回话），但 once 仍必须重试到成功一次；
        // 重试之间按「退避队列最早一档」节流，不是忙等
        Healthy();
        _link.SetReadData(1, DataArea.HoldingRegister, 0, 9);
        _link.FaultRules.Add(new FakeFaultRule
        {
            UnitId = 1,
            Kind = ModbusFailureKind.Protocol,
            ExceptionCode = 0x02,
            RemainingCalls = 2,
        });

        var started = DateTime.UtcNow;
        Start(DevD1 + DevD2,
            "<PointSet id=\"ps1\"><Points><Point id=\"p\" address=\"0\" mode=\"once\" /></Points></PointSet>" + Ps2One,
            globalExtra: "<Reconnect delays=\"300\" />");

        WaitUntil(() => _engine!.GetValueDetail("d1", "p").IsGood, 5_000, "协议异常后 once 必须重试到成功");

        Assert.Empty(_entered.Items);                                  // 协议异常不进队列
        Assert.Equal(3, Reads(1).Count);                               // 两次异常 + 一次成功
        Assert.True(DateTime.UtcNow - started >= TimeSpan.FromMilliseconds(250),
            "两次重试之间必须按最早一档（300ms）节流，不能忙等");
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Once_point_retries_when_backoff_is_disabled()
    {
        // 退避关闭（enabled=false）：once 首读失败仍要重试到成功一次，且不忙等（下限节拍仍生效）
        _link.SetReadData(1, DataArea.HoldingRegister, 0, 3);
        _link.FaultRules.Add(new FakeFaultRule { UnitId = 1, Kind = ModbusFailureKind.Timeout, RemainingCalls = 1 });

        Start(DevD1,
            "<PointSet id=\"ps1\"><Points><Point id=\"p\" address=\"0\" mode=\"once\" /></Points></PointSet>",
            globalExtra: "<Reconnect enabled=\"false\" />");

        WaitUntil(() => _engine!.GetValueDetail("d1", "p").IsGood, 5_000, "退避关闭时 once 仍必须重试");

        Assert.Empty(_entered.Items);
        Assert.Equal(2, Reads(1).Count);
        Assert.Equal((ushort)3, Assert.IsType<ushort>(_engine!.GetValueDetail("d1", "p").Value));
    }

    [Fact]
    public void Auto_point_keeps_polling_and_on_demand_point_never_does()
    {
        // auto/onDemand 的边界：只有 auto 进周期计划
        Healthy();
        Start(DevD1,
            "<PointSet id=\"ps1\"><Points>"
            + "<Point id=\"au\" address=\"0\" intervalMs=\"100\" />"
            + "<Point id=\"od\" address=\"50\" mode=\"onDemand\" />"
            + "</Points></PointSet>");

        Thread.Sleep(400);

        Assert.NotEmpty(Reads(1).Where(r => r.Address == 0));
        Assert.Empty(Reads(1).Where(r => r.Address == 50));
    }

    // ═══════════════ 五、两级退避的自动恢复与生命周期 ═══════════════

    [Fact]
    public void Device_backoff_expiry_resumes_polling_without_any_manual_help()
    {
        // 设备级退避（读超时）到期后必须自己恢复：退避窗内零请求、到期后读写恢复、质量回 Good。
        // 时序判定用「观测到进入退避 → 下一条新请求出现的观测间隔」（findings 记录：本用例曾在满负载下
        // 因「sleep(350) 后断言请求数不变」的固定窗假失败——测试线程被延迟到退避到期之后才断言）。
        Healthy();
        _link.SetReadData(1, DataArea.HoldingRegister, 0, 42);
        _link.FaultRules.Add(new FakeFaultRule { UnitId = 1, Kind = ModbusFailureKind.Timeout, RemainingCalls = 1 });

        Start(DevD1 + DevD2,
            "<PointSet id=\"ps1\"><Points><Point id=\"p\" address=\"0\" intervalMs=\"100\" /></Points></PointSet>" + Ps2One,
            globalExtra: "<Reconnect delays=\"1500\" />");

        var enteredAt = WaitForObservation(
            () => _entered.Items.Any(e => e.Scope == BackoffScope.Device && e.DeviceId == "d1"), 5_000);
        Assert.True(enteredAt != null, "读超时必须进入设备级退避");

        var frozen = Reads(1).Count;
        var resumedAt = WaitForObservation(() => Reads(1).Count > frozen, 5_000);
        Assert.True(resumedAt != null, "退避到期后必须自动恢复采集（轮询线程不得静默死亡）");

        var gap = resumedAt.Value - enteredAt!.Value;
        Assert.True(gap >= TimeSpan.FromMilliseconds(800),
            $"退避窗（1500ms）内该从站一个请求都不发（实测观测间隔 {gap.TotalMilliseconds:F0}ms）");

        WaitUntil(() => _engine!.GetValueDetail("d1", "p").IsGood, 5_000,
            "退避到期后必须自动恢复采集（轮询线程不得静默死亡）");
        Assert.True(Reads(1).Count > frozen, "恢复后请求必须继续增长");
        Assert.Contains(_recovered.Items, r => r.Scope == BackoffScope.Device && r.DeviceId == "d1");
    }

    [Fact]
    public void Link_backoff_expiry_reopens_the_connection_and_both_slaves_resume()
    {
        // IO 失败 → 链路级：退避期间关连接、两台从站一起停；到期后懒重连、两台都自动回来。
        // 故障注入到**两台从站各一次**：退避窗内没有任何「成功通讯」，否则另一台的读成功会按
        // 「成功一次即归零」把链路队列清掉（那是设计语义，不是缺陷）。
        _link.ReopenOnRead = true;
        _link.SetReadData(1, DataArea.HoldingRegister, 0, 1);
        _link.SetReadData(2, DataArea.HoldingRegister, 0, 2);
        _link.FaultRules.Add(new FakeFaultRule { Kind = ModbusFailureKind.LinkDown, RemainingCalls = 2 });

        Start(DevD1 + DevD2,
            "<PointSet id=\"ps1\"><Points><Point id=\"p\" address=\"0\" intervalMs=\"100\" /></Points></PointSet>"
            + "<PointSet id=\"ps2\"><Points><Point id=\"q\" address=\"0\" intervalMs=\"100\" /></Points></PointSet>",
            globalExtra: "<Reconnect delays=\"1500\" />");

        var enteredAt = WaitForObservation(
            () => _entered.Items.Any(e => e.Scope == BackoffScope.Link), 5_000);
        Assert.True(enteredAt != null, "IO 失败必须升级为链路级退避");
        Assert.True(_link.CloseCount >= 1, "链路级退避必须关闭连接");

        var frozen = AllReads.Count;
        var opensBefore = _link.OpenCount;
        var resumedAt = WaitForObservation(() => AllReads.Count > frozen, 5_000);
        Assert.True(resumedAt != null, "链路退避到期后必须恢复请求");

        var gap = resumedAt.Value - enteredAt!.Value;
        Assert.True(gap >= TimeSpan.FromMilliseconds(800),
            $"链路退避窗（1500ms）内整条链路一起等（实测观测间隔 {gap.TotalMilliseconds:F0}ms）");

        WaitUntil(() => _engine!.GetValueDetail("d1", "p").IsGood && _engine.GetValueDetail("d2", "q").IsGood, 5_000,
            "链路退避到期后两台从站都必须自动恢复");
        Assert.True(_link.OpenCount > opensBefore, "恢复必须重新建立连接（懒重连）");
    }

    [Fact]
    public void Failed_windows_in_one_tick_advance_the_backoff_queue_step_by_step()
    {
        // D73 的退避语义（既有口径：**错误聚合单位 = 读取窗口**，ConsecutiveFailures = 窗口点位数）：
        // 同一节拍两个窗口各失败一次 → 两条错误事件 + 退避队列按「失败窗口数」推进两档（Attempt 1 → 2），
        // 既不把同一窗口的失败算两遍（档位不得跳变），也不是整节拍只算一次。
        _link.DefaultReadData = null;   // 两个窗口都失败（LinkDown）
        Start(DevD1,
            "<PointSet id=\"ps1\"><Points>"
            + "<Point id=\"p1\" address=\"0\" intervalMs=\"5000\" />"
            + "<Point id=\"p2\" address=\"100\" intervalMs=\"5000\" />"
            + "</Points></PointSet>",
            globalExtra: "<Scheduler ignoreGap=\"5\" /><Reconnect delays=\"2000,3000\" />");

        WaitUntil(() => _entered.Count >= 2, 5_000, "同节拍的两个失败窗口各推进一档退避");

        var steps = _entered.Items.Take(2).ToArray();
        Assert.Equal(new[] { 1, 2 }, steps.Select(s => s.Attempt).ToArray());
        Assert.Equal(new[] { 2000, 3000 }, steps.Select(s => s.DelayMs).ToArray());   // 档位按失败窗口数逐档推进
        Assert.All(steps, s => Assert.Equal("MODBUS.LINK", s.ReasonCode));
        Assert.All(steps, s => Assert.Equal(BackoffScope.Link, s.Scope));

        WaitUntil(() => _errors.Count >= 2, 5_000, "每个失败窗口各发一条错误事件（不是整节拍一条）");
        Assert.Equal(2, _errors.Count);   // 5s 间隔 → 观察窗内只有一个节拍，事件数与失败窗口数相等
        Assert.Equal(new[] { 0, 100 },
            _errors.Items.Select(e => e.Info.Context.Address ?? -1).OrderBy(a => a).ToArray());
        Assert.All(_errors.Items, e => Assert.Equal(1, e.Info.Context.ConsecutiveFailures));   // 每个窗口 1 个点位

        // 失败窗口的点位全部降质量（退避口径 offlineQuality，默认 offline），没有一个窗口被静默跳过
        Assert.Equal(PointQuality.Offline, _engine!.GetValueDetail("d1", "p1").Quality);
        Assert.Equal(PointQuality.Offline, _engine.GetValueDetail("d1", "p2").Quality);
    }

    [Fact]
    public void Stop_during_a_long_backoff_returns_quickly_and_is_idempotent()
    {
        // 长退避（5s）中 Stop：必须立刻返回（不等退避），之后不再有任何请求；重复 Dispose 不抛
        Healthy();
        _link.FaultRules.Add(new FakeFaultRule { UnitId = 1, Kind = ModbusFailureKind.Timeout });

        var engine = Start(DevD1 + DevD2,
            "<PointSet id=\"ps1\"><Points><Point id=\"p\" address=\"0\" intervalMs=\"100\" /></Points></PointSet>" + Ps2One,
            globalExtra: "<Reconnect delays=\"5000\" />");

        WaitUntil(() => _entered.Count > 0, 5_000, "先进入长退避");

        var started = DateTime.UtcNow;
        engine.Stop();

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(3), "Stop 不得等退避到期");

        var frozen = AllReads.Count;
        Thread.Sleep(400);
        Assert.Equal(frozen, AllReads.Count);   // 线程已退出，不再发请求

        engine.Dispose();                    // 重复 Dispose 幂等
        Assert.Null(Record.Exception(() => engine.Dispose()));
    }

    [Fact]
    public void Dispose_during_heavy_polling_is_clean()
    {
        // 50ms 重负载轮询中 Dispose：立即返回（Join 2000ms 上限内），之后不再有请求
        Healthy();
        var engine = Start(DevD1, "<PointSet id=\"ps1\"><Points><Point id=\"p\" address=\"0\" intervalMs=\"50\" /></Points></PointSet>");

        Thread.Sleep(300);
        var started = DateTime.UtcNow;
        engine.Dispose();
        var elapsed = DateTime.UtcNow - started;

        Assert.True(elapsed < TimeSpan.FromSeconds(3), $"Dispose 必须及时返回（实测 {elapsed.TotalMilliseconds:F0}ms）");

        var frozen = AllReads.Count;
        Thread.Sleep(300);
        Assert.Equal(frozen, AllReads.Count);
    }

    [Fact]
    public void Second_start_does_not_double_the_polling_load()
    {
        // docs/11 §五.2：重复 Start 必须安全——第二次 Start 不得再起一套轮询线程。
        // 口径改为「一个观察窗内的读数上界」：100ms 间隔 700ms 窗内单线程 ≤8 次、双线程才可能翻倍，
        // 上界 12 既容得下满负载（只少不多），又足以在真的起了第二套线程时变红。
        Healthy();
        var engine = Start(DevD1, "<PointSet id=\"ps1\"><Points><Point id=\"p\" address=\"0\" intervalMs=\"100\" /></Points></PointSet>");

        WaitUntil(() => Reads(1).Count >= 5, 5_000, "第一个窗口应有足够读数");

        engine.Start();          // 重复 Start
        _link.ClearCalls();
        Thread.Sleep(700);
        var after = Reads(1).Count;

        Assert.True(after <= 12,
            $"重复 Start 后请求数不得翻倍（100ms 间隔 700ms 窗内实测 {after} 次；单线程 ≈7 次、双线程 ≈14 次）");
    }

    // ═══════════════ 六、并发 ═══════════════

    [Fact]
    public async Task Concurrent_writes_and_polls_never_cross_slaves()
    {
        // 同一条链路上两台从站（unit1 地址 0 / unit2 地址 100）：并发读写期间 unitId 绝不能串台
        _link.SetReadData(1, DataArea.HoldingRegister, 0, 111);
        _link.SetReadData(2, DataArea.HoldingRegister, 100, 222);
        for (var i = 0; i < 50; i++) _link.WriteSingleReplies.Enqueue(ModbusReply.Ok(new ushort[] { 0 }, 0));

        var engine = Start(DevD1 + DevD2,
            "<PointSet id=\"ps1\"><Points><Point id=\"p1\" address=\"0\" intervalMs=\"80\" /></Points></PointSet>"
            + "<PointSet id=\"ps2\"><Points><Point id=\"p2\" address=\"100\" intervalMs=\"80\" access=\"readwrite\" /></Points></PointSet>");

        var writes = Enumerable.Range(0, 12)
            .Select(i => engine.SetValueAsync("d2", "p2", 7 + i))
            .ToArray();
        await Task.WhenAll(writes);

        Thread.Sleep(300);

        // ① 调用记录：地址 0 的读全部发往 unit1、地址 100 的读全部发往 unit2；写全部发往 unit2
        Assert.All(AllReads, call =>
        {
            if (call.Address == 0) Assert.Equal(1, call.UnitId);
            else Assert.Equal(2, call.UnitId);
        });
        Assert.All(_link.Calls.Where(c => c.IsWrite), call =>
        {
            Assert.Equal(2, call.UnitId);
            Assert.Equal(100, call.Address);
        });
        Assert.Equal(12, _link.WriteSingleCallCount);

        // ② 值不串台：d1 读到 unit1 的数据、d2 读到 unit2 的数据（写不改缓存，轮询会覆盖回来）
        Assert.True(_engine!.GetValueDetail("d1", "p1").IsGood);
        Assert.True(_engine.GetValueDetail("d2", "p2").IsGood);
        Assert.Equal((ushort)111, Assert.IsType<ushort>(_engine.GetValueDetail("d1", "p1").Value));
        Assert.Equal((ushort)222, Assert.IsType<ushort>(_engine.GetValueDetail("d2", "p2").Value));

        // ③ 写入值确实按序落到了通道（内容正确）
        Assert.Contains((ushort)7, _link.WriteSingleValues);
        Assert.Contains((ushort)18, _link.WriteSingleValues);
    }

    // ═══════════════ 七、边界 ═══════════════

    [Fact]
    public void Point_at_the_last_address_of_the_area_is_readable()
    {
        // 地址 65535（区容量边界）：一次 1 字请求，值可解
        _link.SetReadData(1, DataArea.HoldingRegister, 65535, 0xBEEF);
        Start(DevD1, "<PointSet id=\"ps1\"><Points><Point id=\"p\" address=\"65535\" /></Points></PointSet>");

        Thread.Sleep(300);

        var read = Assert.Single(Reads(1));
        Assert.Equal(65535, read.Address);
        Assert.Equal(1, read.Count);
        Assert.Equal((ushort)0xBEEF, Assert.IsType<ushort>(_engine!.GetValueDetail("d1", "p").Value));
    }

    [Fact]
    public async Task On_demand_trigger_on_a_disabled_transport_is_a_no_op()
    {
        // Transport@enabled=false：链路上的设备不发任何请求，onDemand 整批触发也必须是「无事发生」，
        // 不能抛内部字典异常（findings D44 的停用口径）
        var engine = Start(DevD1,
            "<PointSet id=\"ps1\"><Points><Point id=\"od\" address=\"0\" mode=\"onDemand\" /></Points></PointSet>",
            transports: "<Transport id=\"tcp1\" host=\"127.0.0.1\" enabled=\"false\" />");

        Thread.Sleep(200);
        Assert.Empty(_link.Calls);

        var updated = await engine.TriggerOnDemandReadAsync("d1");

        Assert.Equal(0, updated);
        Assert.Empty(_link.Calls);
    }

    // ═══════════════ 八、Slices ═══════════════

    [Fact]
    public void Slices_point_stretches_the_group_window_and_neighbours_still_decode()
    {
        // 邻点 9 + 片段点（10 与 20 各取 1 字拼 uint32，占用跨度 [10,21)）+ 邻点 21
        // 三者首尾相接（ignoreGap=0 也连续）→ 一次请求 [9,22) count=13：
        // 片段中间的整段空洞（11..19）一起读回，两侧邻居照常并进同一个窗口
        var data = Enumerable.Range(0, 13).Select(i => (ushort)(100 + i)).ToArray();
        _link.SetReadData(1, DataArea.HoldingRegister, 9, data);

        Start(DevD1,
            "<PointSet id=\"ps1\"><Defaults swap=\"none\" /><Points>"
            + "<Point id=\"n1\" address=\"9\" />"
            + "<Point id=\"s\" address=\"10\" dataType=\"uint32\">"
            + "<Slices><Slice address=\"10\" length=\"1\" /><Slice address=\"20\" length=\"1\" /></Slices></Point>"
            + "<Point id=\"n2\" address=\"21\" />"
            + "</Points></PointSet>",
            defaultInterval: "5000");

        WaitUntil(() => _engine!.GetValueDetail("d1", "s").IsGood, 5_000, "片段点必须解出值");

        var read = Assert.Single(Reads(1));
        Assert.Equal(9, read.Address);
        Assert.Equal(13, read.Count);

        // 索引：n1 = 地址 9 → 100；片段 = 地址 10（101）与地址 20（111）拼 uint32；n2 = 地址 21 → 112
        Assert.Equal((ushort)100, Assert.IsType<ushort>(_engine!.GetValueDetail("d1", "n1").Value));
        Assert.Equal(101u * 65536 + 111u, Assert.IsType<uint>(_engine.GetValueDetail("d1", "s").Value));
        Assert.Equal((ushort)112, Assert.IsType<ushort>(_engine.GetValueDetail("d1", "n2").Value));
    }

    [Fact]
    public void Slices_span_equal_to_the_group_limit_is_accepted_but_one_more_is_rejected()
    {
        // CGV-28 的边界：跨度 == 上限通过；多一个地址就报错
        var accepted = Load(DevD1,
            "<PointSet id=\"ps1\"><Points><Point id=\"s\" address=\"0\" dataType=\"uint32\">"
            + "<Slices><Slice address=\"0\" length=\"1\" /><Slice address=\"9\" length=\"1\" /></Slices></Point>"
            + "</Points></PointSet>",
            globalExtra: "<Scheduler groupLimitRegisters=\"10\" />");
        Assert.True(accepted.IsValidated);

        var ex = Assert.Throws<ConfigValidationException>(() => Load(DevD1,
            "<PointSet id=\"ps1\"><Points><Point id=\"s\" address=\"0\" dataType=\"uint32\">"
            + "<Slices><Slice address=\"0\" length=\"1\" /><Slice address=\"10\" length=\"1\" /></Slices></Point>"
            + "</Points></PointSet>",
            globalExtra: "<Scheduler groupLimitRegisters=\"10\" />"));

        Assert.Contains(ex.Errors, e => e.Contains("片段太分散") && e.Contains("跨度 11"));
    }

    // ═══════════════ 九、计算点 ═══════════════

    [Fact]
    public void Calculated_point_resolves_from_a_polled_dependency_and_from_its_cached_value()
    {
        // 依赖链：raw（轮询点）→ a = raw*2 → b = a+1。
        // 依赖值来自实时缓存，所以「先读到 a 再读 b」必须给出正确值（不依赖求值顺序的缓存模型）。
        _link.SetReadData(1, DataArea.HoldingRegister, 0, 21);
        Start(DevD1,
            "<PointSet id=\"ps1\"><Points><Point id=\"raw\" address=\"0\" intervalMs=\"100\" /></Points>"
            + "<Calculated>"
            + "<Point id=\"a\" dataType=\"uint16\"><Expression>P('raw') * 2</Expression></Point>"
            + "<Point id=\"b\" dataType=\"uint16\"><Expression>P('a') + 1</Expression></Point>"
            + "</Calculated></PointSet>");

        WaitUntil(() => _engine!.GetValueDetail("d1", "raw").IsGood, 5_000, "轮询点先采到值");

        Assert.Equal(42.0, Assert.IsType<double>(_engine!.GetValueDetail("d1", "a").Value));
        Assert.Equal(43.0, Assert.IsType<double>(_engine.GetValueDetail("d1", "b").Value));   // b = 42 + 1
        Assert.Equal("43", _engine.GetValue("d1", "b"));
    }

    [Fact]
    public void Calculated_point_chain_resolves_its_calculated_dependency_recursively()
    {
        // 缺陷 D45 的回归（原 `..._currently_fails` 用例断言的是缺陷现状，已反转）：
        // b 依赖计算点 a，**先读 b** 时框架必须递归求值 a（而不是拿空缓存判坏值）。
        _link.SetReadData(1, DataArea.HoldingRegister, 0, 21);
        Start(DevD1,
            "<PointSet id=\"ps1\"><Points><Point id=\"raw\" address=\"0\" intervalMs=\"100\" /></Points>"
            + "<Calculated>"
            + "<Point id=\"a\" dataType=\"uint16\"><Expression>P('raw') * 2</Expression></Point>"
            + "<Point id=\"b\" dataType=\"uint16\"><Expression>P('a') + 1</Expression></Point>"
            + "</Calculated></PointSet>");

        WaitUntil(() => _engine!.GetValueDetail("d1", "raw").IsGood, 5_000, "轮询点先采到值");

        // 先读依赖方：b = (raw*2)+1 = 43
        var chainFirst = _engine!.GetValueDetail("d1", "b");
        Assert.True(chainFirst.IsGood, "先读 b 时依赖计算点 a 必须被递归求值（D45）");
        Assert.Equal(43.0, Assert.IsType<double>(chainFirst.Value));
        Assert.Equal("43", _engine.GetValue("d1", "b"));

        // 递归求值会把被依赖的计算点写进缓存（EvaluateCalculated 的既有口径）→ 门面随后也能读到它
        Assert.Equal(42.0, Assert.IsType<double>(_engine.GetValueDetail("d1", "a").Value));
        Assert.NotNull(_engine.GetValueAge("d1", "a"));
    }

    [Fact]
    public void Calculated_point_three_level_chain_A_to_B_to_C_resolves()
    {
        // 三层链：c 依赖 b 依赖 a 依赖 raw。只读 c 也必须一次算到底（递归是链式的，不是只解一层）
        _link.SetReadData(1, DataArea.HoldingRegister, 0, 3);
        Start(DevD1,
            "<PointSet id=\"ps1\"><Points><Point id=\"raw\" address=\"0\" intervalMs=\"100\" /></Points>"
            + "<Calculated>"
            + "<Point id=\"a\" dataType=\"uint16\"><Expression>P('raw') + 1</Expression></Point>"
            + "<Point id=\"b\" dataType=\"uint16\"><Expression>P('a') * 10</Expression></Point>"
            + "<Point id=\"c\" dataType=\"uint16\"><Expression>P('b') - 5</Expression></Point>"
            + "</Calculated></PointSet>");

        WaitUntil(() => _engine!.GetValueDetail("d1", "raw").IsGood, 5_000, "轮询点先采到值");

        Assert.Equal(35.0, Assert.IsType<double>(_engine!.GetValueDetail("d1", "c").Value));   // ((3+1)*10)-5
        Assert.Equal(4.0, Assert.IsType<double>(_engine.GetValueDetail("d1", "a").Value));
        Assert.Equal(40.0, Assert.IsType<double>(_engine.GetValueDetail("d1", "b").Value));
    }

    [Fact]
    public void Calculated_point_cycle_at_runtime_does_not_crash_or_hang()
    {
        // 运行期防环兜底（SamplerEngine.MaxEvaluationDepth + 线程内访问集合）：
        // **静态引用**的环（<Expression>/<Script>/<DependsOn> 里的字面 P('id')）已在加载期被 CGV-15 拒绝
        // （findings D46 已关闭）；但脚本可以在运行期**动态拼**引用（下面是 P(device + '/y')，
        // 正则扫不到字面量），这种环只能靠运行期兜底。
        // 要求：不抛、不死循环、不栈溢出，立即返回有界结果。
        Start(DevD1,
            "<PointSet id=\"ps1\"><Points />"
            + "<Calculated>"
            + "<Point id=\"x\"><Script>P(device + '/y') + 1</Script></Point>"
            + "<Point id=\"y\"><Script>P(device + '/x') + 1</Script></Point>"
            + "</Calculated></PointSet>");

        var started = DateTime.UtcNow;
        PointValue? value = null;
        var thrown = Record.Exception(() => value = _engine!.GetValueDetail("d1", "x"));
        var elapsed = DateTime.UtcNow - started;

        Assert.Null(thrown);
        Assert.NotNull(value);
        Assert.True(elapsed < TimeSpan.FromSeconds(3),
            $"求值链上的环必须被兜底截断（实测 {elapsed.TotalMilliseconds:F0}ms）");

        // 反向再读一次也必须同样有界（环的另一端）
        var started2 = DateTime.UtcNow;
        Assert.Null(Record.Exception(() => _engine!.GetValueDetail("d1", "y")));
        Assert.True(DateTime.UtcNow - started2 < TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void Calculated_point_bad_dependency_poisons_the_whole_chain()
    {
        // 坏值传染：raw 采不到（超时）→ a = P('raw')*2 坏 → b = P('a')+1 也坏。
        // 递归求值绝不能把「依赖算出来的坏值」当成好值继续算下去（那是比 Bad 更危险的伪正常数）。
        _link.FaultRules.Add(new FakeFaultRule { UnitId = 1, Kind = ModbusFailureKind.Timeout });

        Start(DevD1 + DevD2,
            "<PointSet id=\"ps1\"><Points><Point id=\"raw\" address=\"0\" intervalMs=\"100\" /></Points>"
            + "<Calculated>"
            + "<Point id=\"a\" dataType=\"uint16\"><Expression>P('raw') * 2</Expression></Point>"
            + "<Point id=\"b\" dataType=\"uint16\"><Expression>P('a') + 1</Expression></Point>"
            + "</Calculated></PointSet>" + Ps2One,
            globalAttrs: " nullText=\"N/A\"",
            globalExtra: "<Reconnect delays=\"200\" />");

        Thread.Sleep(400);

        foreach (var id in new[] { "raw", "a", "b" })
        {
            var value = _engine!.GetValueDetail("d1", id);
            Assert.False(value.IsGood, $"{id} 在依赖坏值时必须是坏值，绝不能输出伪正常数");
            Assert.Equal("N/A", _engine.GetValue("d1", id));
        }

        Assert.Equal("ss.reason.calculate", _engine!.GetValueDetail("d1", "b").Reason);
    }

    [Fact]
    public void Calculated_dependency_seeded_through_the_facade_cache_is_reused_by_the_chain()
    {
        // 「依赖点是门面读过（写进缓存）的计算点」这一路径：先读 a、再读 b 时不该重复求值，
        // 两次读到的 b 必须一致——缓存口径与递归口径不能给出两个答案。
        _link.SetReadData(1, DataArea.HoldingRegister, 0, 21);
        Start(DevD1,
            "<PointSet id=\"ps1\"><Points><Point id=\"raw\" address=\"0\" intervalMs=\"100\" /></Points>"
            + "<Calculated>"
            + "<Point id=\"a\" dataType=\"uint16\"><Expression>P('raw') * 2</Expression></Point>"
            + "<Point id=\"b\" dataType=\"uint16\"><Expression>P('a') + 1</Expression></Point>"
            + "</Calculated></PointSet>");

        WaitUntil(() => _engine!.GetValueDetail("d1", "raw").IsGood, 5_000, "轮询点先采到值");

        Assert.Equal(42.0, Assert.IsType<double>(_engine!.GetValueDetail("d1", "a").Value));   // 门面先读依赖
        Assert.NotNull(_engine.GetValueAge("d1", "a"));
        Assert.Equal(43.0, Assert.IsType<double>(_engine.GetValueDetail("d1", "b").Value));
        Assert.Equal(43.0, Assert.IsType<double>(_engine.GetValueDetail("d1", "b").Value));   // 重复读同值
        Assert.Equal("43", _engine.GetValue("d1", "b"));
    }

    [Fact]
    public void Calculated_point_resolves_a_device_qualified_reference_across_point_sets()
    {
        // 跨点表引用：d2 的计算点读 d1 的点位（deviceId/pointId 限定名）
        _link.SetReadData(1, DataArea.HoldingRegister, 0, 5);
        Start(DevD1 + DevD2,
            "<PointSet id=\"ps1\"><Points><Point id=\"raw\" address=\"0\" intervalMs=\"100\" /></Points></PointSet>"
            + "<PointSet id=\"ps2\"><Calculated>"
            + "<Point id=\"far\" dataType=\"uint16\"><Expression>P('d1/raw') * 10</Expression></Point>"
            + "</Calculated><Points /></PointSet>");

        WaitUntil(() => _engine!.GetValueDetail("d1", "raw").IsGood, 5_000, "被引用的点位先采到值");
        Assert.Equal(50.0, Assert.IsType<double>(_engine!.GetValueDetail("d2", "far").Value));
    }

    [Fact]
    public void Calculated_point_becomes_bad_when_its_dependency_is_bad()
    {
        // 依赖点坏值（从未采到）→ 计算点必须 Bad（绝不当好值输出），门面返回全局 nullText
        _link.FaultRules.Add(new FakeFaultRule { UnitId = 1, Kind = ModbusFailureKind.Timeout });

        Start(DevD1,
            "<PointSet id=\"ps1\"><Points><Point id=\"raw\" address=\"0\" intervalMs=\"100\" /></Points>"
            + "<Calculated><Point id=\"c\" dataType=\"uint16\"><Expression>P('raw') * 2</Expression></Point></Calculated>"
            + "</PointSet>",
            globalAttrs: " nullText=\"N/A\"",
            globalExtra: "<Reconnect delays=\"200\" />");

        Thread.Sleep(400);

        var value = _engine!.GetValueDetail("d1", "c");
        Assert.False(value.IsGood);
        Assert.Null(value.Value);
        Assert.Equal("ss.reason.calculate", value.Reason);
        Assert.Equal("N/A", _engine.GetValue("d1", "c"));
    }

    [Theory]
    [InlineData("1.2.3")]
    [InlineData("5..2")]
    [InlineData(".")]
    [InlineData("1.2 + 3.4.5")]
    public void Malformed_number_literals_evaluate_to_nan_instead_of_throwing(string expression)
    {
        // 表达式求值器「不抛异常、坏表达式判 NaN」的契约：坏字面量绝不能以 FormatException
        // 穿透门面（宿主在界面定时器里调 GetValue 就是一次未处理异常）
        var thrown = Record.Exception(() => ExpressionEvaluator.Evaluate(expression, _ => null, _ => null));

        Assert.Null(thrown);
        Assert.True(double.IsNaN(ExpressionEvaluator.Evaluate(expression, _ => null, _ => null)));
    }

    [Fact]
    public void Calculated_point_with_a_malformed_expression_is_bad_without_throwing()
    {
        // 同一条口径走到门面：坏表达式 → Bad(ss.reason.calculate) + nullText，不抛
        Start(DevD1,
            "<PointSet id=\"ps1\"><Points />"
            + "<Calculated><Point id=\"bad\" dataType=\"uint16\"><Expression>1.2.3</Expression></Point></Calculated>"
            + "</PointSet>");

        var thrown = Record.Exception(() => _engine!.GetValueDetail("d1", "bad"));

        Assert.Null(thrown);
        Assert.Equal(PointQuality.Bad, _engine!.GetValueDetail("d1", "bad").Quality);
        Assert.Equal("ss.reason.calculate", _engine.GetValueDetail("d1", "bad").Reason);
        Assert.Equal("--", _engine.GetValue("d1", "bad"));
    }

    // ═══════════════ 十、门面读值 ═══════════════

    [Fact]
    public void GetValue_returns_the_configured_null_text_for_every_non_good_quality()
    {
        // Global@nullText 生效：未采集 / 通讯失败（退避离线）两种非 Good 都返回配置的文本；Good 返回格式化值
        _link.SetReadData(1, DataArea.HoldingRegister, 0, 5);
        _link.FaultRules.Add(new FakeFaultRule { UnitId = 1, Kind = ModbusFailureKind.Timeout, MinAddress = 20 });

        Start(DevD1,
            "<PointSet id=\"ps1\"><Points>"
            + "<Point id=\"ok\" address=\"0\" intervalMs=\"100\" />"
            + "<Point id=\"never\" address=\"10\" mode=\"onDemand\" />"
            + "<Point id=\"fail\" address=\"20\" intervalMs=\"100\" />"
            + "</Points></PointSet>",
            globalAttrs: " nullText=\"N/A\"",
            globalExtra: "<Reconnect delays=\"200\" />");

        WaitUntil(() => _engine!.GetValueDetail("d1", "ok").IsGood, 5_000, "健康点先变 Good");
        WaitUntil(() => _engine!.GetValueDetail("d1", "fail").Quality != PointQuality.Good, 5_000, "故障点必须置非 Good 质量");

        Assert.Equal("5", _engine!.GetValue("d1", "ok"));                 // Good：正常格式化
        Assert.Equal("N/A", _engine.GetValue("d1", "never"));             // 从未采集
        Assert.Equal("N/A", _engine.GetValue("d1", "fail"));              // 失败/退避期间

        var never = _engine.GetValueDetail("d1", "never");
        Assert.Equal(PointQuality.Bad, never.Quality);
        Assert.Equal("ss.reason.notInCache", never.Reason);
        Assert.Null(_engine.GetValueAge("d1", "never"));
        Assert.True(_engine.GetValueAge("d1", "ok") < TimeSpan.FromSeconds(3));
    }

    // ═══════════════ 辅助 ═══════════════

    /// <summary>n 个严格相邻的线圈点位（地址 0..n-1）。</summary>
    private static string CoilPoints(int count)
    {
        var sb = new StringBuilder("<PointSet id=\"ps1\"><Points>");
        for (var i = 0; i < count; i++)
        {
            sb.Append("<Point id=\"c").Append(i).Append("\" area=\"coil\" address=\"").Append(i)
              .Append("\" intervalMs=\"5000\" />");
        }

        sb.Append("</Points></PointSet>");
        return sb.ToString();
    }
}
