using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using System.Threading;
using System.Xml.Linq;
using SuperSampler.Abstractions.Errors;
using SuperSampler.Abstractions.Events;
using SuperSampler.Abstractions.Values;
using SuperSampler.Core.Config;
using SuperSampler.Core.Runtime;
using SuperSampler.Drivers.Modbus.Wire;
using SuperSampler.Drivers.Modbus;
using SuperSampler.UnitTests.Support;
using Xunit;

namespace SuperSampler.UnitTests.Runtime;

/// <summary>
/// Scheduler 补测（docs/07 测试计划 §3.2 G-S 族）：按 intervalMs 分桶的轮询节奏、onDemand / once 模式、
/// 自动分组（连续地址合并、ignoreGap 容差、超地址组上限切分）、窗口聚合错误、keepLast/Offline 质量策略、
/// 值/质量变化事件、故障下线程存活、报警喂入。
/// 全部经 SamplerEngine 注入 FakeModbusLink 驱动真实调度线程（findings B1 的接缝）。
/// </summary>
/// <remarks>
/// 加入命名集合 <c>real-polling-threads</c>：本类会起**真实轮询线程**，
/// 与 <c>BackoffTests.B16</c>（断言进程级句柄数）必须串行执行，否则并行开的线程会被误判成泄漏。
/// </remarks>
[Collection("real-polling-threads")]
public class SchedulerGapTests : IDisposable
{
    private readonly FakeModbusLink _link = new();
    private readonly List<PointValueChangedEvent> _valueEvents = new();
    private readonly List<IErrorEvent> _errorEvents = new();
    private readonly List<AlarmRaisedEvent> _alarms = new();
    private SamplerEngine? _engine;

    public void Dispose() => _engine?.Dispose();

    private SamplerEngine Start(string devices, string pointSets, string quality = "keepLast", string onCommError = "bad",
        string globalExtra = "", string globalAttrs = "", string defaultInterval = "2000")
    {
        var xml = string.Format(TEMPLATE, quality, devices, pointSets, onCommError, globalExtra, globalAttrs, defaultInterval);
        var config = SamplerConfigLoader.Load(XDocument.Parse(xml), Directory.GetCurrentDirectory());
        _engine = new SamplerEngine(config, new Dictionary<string, IModbusLink> { ["tcp1"] = _link });
        _engine.Bus.Subscribe<PointValueChangedEvent>(e => _valueEvents.Add(e.Body), DeliveryMode.Inline);
        _engine.Bus.Subscribe<IErrorEvent>(e => _errorEvents.Add(e.Body), DeliveryMode.Inline);
        _engine.Bus.Subscribe<AlarmRaisedEvent>(e => _alarms.Add(e.Body), DeliveryMode.Inline);
        _engine.Start();
        return _engine;
    }

    private List<FakeLinkCall> Reads(byte unitId)
        => _link.Calls.Where(c => c.IsRead && c.UnitId == unitId).ToList();

    /// <summary>
    /// 链路正常（读得到数据）：分组/节奏类用例只关心「怎么读」，不该被两层退避的节流影响。
    /// 第四步起「读失败」会进退避（退避期间不发请求），失败的注入一律走 <see cref="FakeFaultRule"/>。
    /// </summary>
    private void HealthyLink() => _link.DefaultReadData = new ushort[512];

    /// <summary>关掉两层退避：只测 <c>Quality@onCommError*</c> 质量策略时用（退避口径会盖住它）。</summary>
    private const string NoBackoff = "<Reconnect enabled=\"false\" />";

    // ─────────────── G-S-1：按 intervalMs 的节拍轮询 ───────────────

    [Fact]
    public void Gs1_point_polls_at_its_configured_interval()
    {
        Start(DEV_D1, PS_ONE_POINT_FAST); // intervalMs=150

        Thread.Sleep(700); // 期望 ~4 个窗口

        var reads = Reads(1);
        // 宽松区间防抖动：至少 2 个窗口，且不失控（≤ 8）
        Assert.True(reads.Count >= 2, $"700ms 内只读了 {reads.Count} 次（intervalMs=150）");
        Assert.True(reads.Count <= 8, $"700ms 内读了 {reads.Count} 次，疑似节奏失控");
    }

    // ─────────────── 分桶：不同 intervalMs 的点各走各的节拍 ───────────────

    [Fact]
    public void Gs1b_intervals_are_bucketed_each_bucket_keeps_its_own_cadence()
    {
        // 同设备两点：a@0 每 100ms、b@20 每 400ms（地址不连续 → 两个组，各在自己的桶里）
        HealthyLink();
        Start(DEV_D1, """
            <PointSet id="ps1"><Points>
              <Point id="a" address="0" intervalMs="100" />
              <Point id="b" address="20" intervalMs="400" />
            </Points></PointSet>
            """);

        Thread.Sleep(700);

        var fast = Reads(1).Count(r => r.Address == 0);
        var slow = Reads(1).Count(r => r.Address == 20);

        Assert.True(fast >= 4, $"100ms 间隔的点 700ms 内应读 ≥4 次，实际 {fast} 次");
        Assert.True(slow is >= 1 and <= 3, $"400ms 间隔的点 700ms 内应读 1~2 次（误差 ≤ 一个节拍），实际 {slow} 次");
        Assert.True(fast > slow, "不同间隔必须分桶：快桶的请求数应明显多于慢桶");
    }

    // ─────────────── D12：onDemand 不参与轮询 ───────────────

    [Fact]
    public void Gs2a_on_demand_point_is_not_polled()
    {
        Start(DEV_D1, PS_ONE_POINT_ONDEMAND);

        Thread.Sleep(400);

        Assert.Empty(Reads(1)); // onDemand 只能手动触发
    }

    // ─────────────── 门面方法：onDemand 整批触发 ───────────────

    [Fact]
    public async System.Threading.Tasks.Task Gs2a2_on_demand_points_are_read_in_one_batch_by_facade()
    {
        // od1@0、od2@1 连续（一组，一次请求）；od3@10 单独一组；auto 点不在此方法范围内
        _link.SetReadData(1, DataArea.HoldingRegister, 0, 11, 22);
        _link.SetReadData(1, DataArea.HoldingRegister, 10, 33);
        _link.SetReadData(1, DataArea.HoldingRegister, 50, 44);

        Start(DEV_D1, """
            <PointSet id="ps1"><Points>
              <Point id="od1" address="0" mode="onDemand" />
              <Point id="od2" address="1" mode="onDemand" />
              <Point id="od3" address="10" mode="onDemand" />
              <Point id="au" address="50" intervalMs="100" />
            </Points></PointSet>
            """);

        Thread.Sleep(300);
        Assert.NotEmpty(Reads(1).Where(r => r.Address == 50));                 // auto 点在轮询
        Assert.Empty(Reads(1).Where(r => r.Address == 0 || r.Address == 10));  // onDemand 不被轮询

        var updated = await _engine!.TriggerOnDemandReadAsync("d1");

        Assert.Equal(3, updated);                      // 3 个 onDemand 点全部刷新
        Assert.Equal((ushort)11, Assert.IsType<ushort>(_engine.GetValueDetail("d1", "od1").Value));
        Assert.Equal((ushort)22, Assert.IsType<ushort>(_engine.GetValueDetail("d1", "od2").Value));
        Assert.Equal((ushort)33, Assert.IsType<ushort>(_engine.GetValueDetail("d1", "od3").Value));

        // 连续的两点合并成一次请求（地址 0、长度 2），od3 单独一次
        var batch = _link.Calls.Where(c => c.IsRead && c.Address == 0).ToList();
        Assert.Single(batch);
        Assert.Equal(2, batch[0].Count);
    }

    [Fact]
    public async System.Threading.Tasks.Task Gs2a3_on_demand_trigger_on_unknown_device_throws()
    {
        Start(DEV_D1, PS_ONE_POINT_ONDEMAND);

        await Assert.ThrowsAsync<KeyNotFoundException>(() => _engine!.TriggerOnDemandReadAsync("nope"));
    }

    // ─────────────── D11：once 只读一次 ───────────────

    [Fact]
    public void Gs2b_once_point_reads_exactly_once()
    {
        HealthyLink(); // 读得到 → 一次即成功（失败重试的节奏由退避闸门管，见 Gs2c）
        Start(DEV_D1, PS_ONE_POINT_ONCE);

        Thread.Sleep(400); // 若被当作周期点会读很多次

        Assert.Single(Reads(1));
    }

    [Fact]
    public void Gs2c_once_point_retries_until_first_success_then_stops()
    {
        // 前两次读失败（超时），第三次成功 → 必须重试到成功一次，之后不再读
        _link.SetReadData(1, DataArea.HoldingRegister, 0, 7);
        _link.FaultRules.Add(new FakeFaultRule { RemainingCalls = 2, Kind = ModbusFailureKind.Timeout });

        var started = DateTime.UtcNow;
        Start(DEV_D1, PS_ONE_POINT_ONCE, defaultInterval: "60");

        Assert.True(SpinWait.SpinUntil(
                () => _engine!.GetValueDetail("d1", "p").IsGood, 5_000),
            "once 点第一次没读到必须重试到成功一次");

        var afterSuccess = Reads(1).Count;
        Assert.Equal(3, afterSuccess);   // 两次失败 + 一次成功

        // 重试节奏来自两层退避（第四步），不再是全局默认间隔：
        // 默认队列第一档 300ms → 两次失败之间不可能按 defaultInterval=60ms 猛试
        var elapsed = DateTime.UtcNow - started;
        Assert.True(elapsed >= TimeSpan.FromMilliseconds(280),
            $"once 的失败重试必须按退避队列节流（实测 {elapsed.TotalMilliseconds:F0}ms，全局默认间隔仅 60ms）");

        Thread.Sleep(400);
        Assert.Equal(afterSuccess, Reads(1).Count);   // 成功一次之后不再读
    }

    [Fact]
    public void Gs2d_once_block_reads_once()
    {
        _link.SetReadData(1, DataArea.HoldingRegister, 0, 5, 6);
        Start(DEV_D1, """
            <PointSet id="ps1">
              <Blocks><Block id="b1" start="0" count="2" mode="once">
                <Point id="p0" address="0" /><Point id="p1" address="1" />
              </Block></Blocks>
              <Points />
            </PointSet>
            """);

        Thread.Sleep(400);

        Assert.Single(Reads(1));       // once 块只读一次
        Assert.Equal((ushort)5, Assert.IsType<ushort>(_engine!.GetValueDetail("d1", "p0").Value));
    }

    // ─────────────── G-S-12/13：Point@enabled / Block@enabled 生效（findings W16/W17） ───────────────

    [Fact]
    public void Gs12_disabled_point_is_not_polled()
    {
        Start(DEV_D1, PS_ONE_POINT_DISABLED);

        Thread.Sleep(400);

        Assert.Empty(Reads(1));
    }

    [Fact]
    public void Gs13_disabled_block_is_not_polled()
    {
        Start(DEV_D1, PS_DISABLED_BLOCK);

        Thread.Sleep(400);

        Assert.Empty(Reads(1));
    }

    // ─────────────── G-S-3：块读合并——块内 3 点一次读覆盖 ───────────────

    [Fact]
    public void Gs3_block_points_merged_into_single_read()
    {
        Start(DEV_D1, PS_BLOCK_3);

        Thread.Sleep(300);

        var reads = Reads(1);
        Assert.True(reads.Count >= 1);
        var first = reads[0];
        Assert.Equal(0, first.Address);
        Assert.Equal(3, first.Count); // 一次块读覆盖 3 点
    }

    // ─────────────── G-S-4：自动分组——严格相邻才合并 ───────────────

    [Fact]
    public void Gs4_adjacent_standalone_points_merge_gap_does_not()
    {
        // a@0、b@1 相邻（合并 count=2），c@20 有空洞（单独读）
        HealthyLink();
        Start(DEV_D1, PS_SCATTER);

        Thread.Sleep(300);

        var reads = Reads(1);
        Assert.Single(reads.Where(r => r.Address == 0 && r.Count == 2));
        Assert.Single(reads.Where(r => r.Address == 20 && r.Count == 1));
        Assert.Equal(2, reads.Count); // 两个组
        Assert.Contains(reads, r => r.Address == 0 && r.Count == 2);
        Assert.Contains(reads, r => r.Address == 20 && r.Count == 1);
    }

    // ─────────────── G-S-5：相邻散点超地址组上限自动切分 ───────────────

    [Fact]
    public void Gs5_merged_group_capped_at_protocol_limit_125()
    {
        HealthyLink();
        Start(DEV_D1, BuildScatterPoints(126)); // 地址 0..125 严格相邻

        Thread.Sleep(300);

        var reads = Reads(1);
        Assert.True(reads.Count == 2, $"期望 125+1 两次请求，实际 {reads.Count} 次"); // 125 + 1
        Assert.Contains(reads, r => r.Address == 0 && r.Count == 125);
        Assert.Contains(reads, r => r.Address == 125 && r.Count == 1);
    }

    // ─────────────── 超地址组上限：切成多次请求，但同一节拍内读完并拼回一组 ───────────────

    [Fact]
    public void Gs5d_group_over_limit_splits_into_multiple_requests_in_one_tick()
    {
        // 300 个严格相邻点、上限 125 → 125 + 125 + 50 三次请求，
        // 同一节拍内背靠背完成（intervalMs=5000：600ms 内只应出现这三次）
        // 三次切分请求各自的地址窗口都要有数据（假链路按 (从站,区,地址) 精确命中）
        _link.SetReadData(1, DataArea.HoldingRegister, 0, Enumerable.Range(100, 125).Select(i => (ushort)i).ToArray());
        _link.SetReadData(1, DataArea.HoldingRegister, 125, Enumerable.Range(225, 125).Select(i => (ushort)i).ToArray());
        _link.SetReadData(1, DataArea.HoldingRegister, 250, Enumerable.Range(350, 50).Select(i => (ushort)i).ToArray());

        Start(DEV_D1, BuildScatterPoints(300, intervalMs: 5000), globalExtra: "<Scheduler groupLimitRegisters=\"125\" />");

        Thread.Sleep(600);

        var reads = Reads(1);
        Assert.Equal(3, reads.Count);
        Assert.Equal(new[] { 125, 125, 50 }, reads.Select(r => r.Count).ToArray());

        // 三次请求的结果都落在缓存里（整组在同一次触发里刷新完毕）
        Assert.Equal((ushort)100, Assert.IsType<ushort>(_engine!.GetValueDetail("d1", "p0").Value));
        Assert.Equal((ushort)224, Assert.IsType<ushort>(_engine.GetValueDetail("d1", "p124").Value));
        Assert.Equal((ushort)225, Assert.IsType<ushort>(_engine.GetValueDetail("d1", "p125").Value));
        Assert.Equal((ushort)399, Assert.IsType<ushort>(_engine.GetValueDetail("d1", "p299").Value));
    }

    // ─────────────── G-S-5b：地址组上限来自 Global/Scheduler@groupLimitRegisters ───────────────

    [Fact]
    public void Gs5b_merge_limit_follows_configured_group_limit_registers()
    {
        // 配置上限 2（默认 125）：0..4 五个严格相邻点应切成 3 次请求（2+2+1）
        HealthyLink();
        Start(DEV_D1, BuildScatterPoints(5), globalExtra: "<Scheduler groupLimitRegisters=\"2\" />");

        Thread.Sleep(300);

        var reads = Reads(1);
        Assert.Equal(3, reads.Count);
        Assert.Equal(2, reads.Count(r => r.Count == 2));
        Assert.Equal(1, reads.Count(r => r.Count == 1));
    }

    // ─────────────── G-S-5c：位区按 Global/Scheduler@groupLimitBits 单独限流 ───────────────

    [Fact]
    public void Gs5c_bit_area_uses_group_limit_bits()
    {
        // 线圈区 5 个相邻位：groupLimitBits=2 → 3 次请求（位区不吃 groupLimitRegisters）
        HealthyLink();
        Start(DEV_D1, BuildCoilPoints(5), globalExtra: "<Scheduler groupLimitBits=\"2\" groupLimitRegisters=\"125\" />");

        Thread.Sleep(300);

        Assert.Equal(3, Reads(1).Count);
    }

    // ─────────────── G-S-4b/4c：ignoreGap 容差生效（0 = 严格相邻） ───────────────

    [Fact]
    public void Gs4b_ignore_gap_zero_keeps_hole_split()
    {
        // a@0、b@10：间隔 9。默认 ignoreGap=0 → 空洞不合并，2 个组
        HealthyLink();
        Start(DEV_D1, BuildGapPoints());

        Thread.Sleep(300);

        Assert.Equal(2, Reads(1).Count);
    }

    [Fact]
    public void Gs4c_ignore_gap_tolerance_merges_holes_within_gap()
    {
        // ignoreGap=9 → a@0 与 b@10 容差内合并为一组 count=11
        Start(DEV_D1, BuildGapPoints(), globalExtra: "<Scheduler ignoreGap=\"9\" />");

        Thread.Sleep(300);

        var reads = Reads(1);
        Assert.Single(reads);
        Assert.Equal(0, reads[0].Address);
        Assert.Equal(11, reads[0].Count);
    }

    [Fact]
    public void Gs4d_ignore_gap_one_merges_single_address_hole()
    {
        // ignoreGap=1：中间空一个地址也算连续（0 与 2 → 一次读 count=3）
        Start(DEV_D1, """
            <PointSet id="ps1"><Points>
              <Point id="a" address="0" />
              <Point id="b" address="2" />
            </Points></PointSet>
            """, globalExtra: "<Scheduler ignoreGap=\"1\" />");

        Thread.Sleep(300);

        var reads = Reads(1);
        Assert.Single(reads);
        Assert.Equal(0, reads[0].Address);
        Assert.Equal(3, reads[0].Count);
    }

    private static string BuildGapPoints()
        => "<PointSet id=\"ps1\"><Points>"
           + "<Point id=\"a\" address=\"0\" />"
           + "<Point id=\"b\" address=\"10\" />"
           + "</Points></PointSet>";

    private static string BuildCoilPoints(int count)
    {
        var sb = new StringBuilder("<PointSet id=\"ps1\"><Points>");
        for (var i = 0; i < count; i++)
        {
            sb.Append("<Point id=\"c").Append(i).Append("\" area=\"coil\" address=\"").Append(i).Append("\" />");
        }
        sb.Append("</Points></PointSet>");
        return sb.ToString();
    }

    private static string BuildScatterPoints(int count, int intervalMs = 2000)
    {
        var sb = new StringBuilder("<PointSet id=\"ps1\"><Points>");
        for (var i = 0; i < count; i++)
        {
            sb.Append("<Point id=\"p").Append(i).Append("\" address=\"").Append(i)
              .Append("\" intervalMs=\"").Append(intervalMs).Append("\" />");
        }
        sb.Append("</Points></PointSet>");
        return sb.ToString();
    }

    // ─────────────── G-S-6：单窗口失败只发一条聚合错误事件 ───────────────

    [Fact]
    public void Gs6_single_window_failure_emits_one_aggregated_error()
    {
        // 默认间隔 2000ms：观察窗内只有一个窗口，全部读失败（无数据注册 → LinkDown）
        Start(DEV_D1, PS_ONE_POINT);

        Thread.Sleep(500);

        var error = Assert.Single(_errorEvents); // 一窗一条，不按点数翻倍
        Assert.IsAssignableFrom<ILinkError>(error); // 无应答类失败归类链路错误
    }

    // ─────────────── G-S-7：onCommErrorValue=keepLast（默认）失败窗口保留旧值 ───────────────

    [Fact]
    public void Gs7_keep_last_preserves_good_value_after_failure()
    {
        _link.ReadReplies.Enqueue(ModbusReply.Ok(new ushort[] { 5 }, 1)); // 第一窗成功
        // 之后队列空 → LinkDown
        // 关退避：本用例测的是 Quality@onCommErrorValue=keepLast 的置值策略（退避会按 offlineQuality 置位）
        Start(DEV_D1, PS_ONE_POINT_FAST, globalExtra: NoBackoff); // 默认 keepLast
        SpinWait.SpinUntil(() => _errorEvents.Count > 0, 5_000); // 等到失败窗口发生
        Assert.True(_errorEvents.Count > 0, "应已发生失败窗口");

        var detail = _engine!.GetValueDetail("d1", "p");
        Assert.True(detail.IsGood); // 旧值保留，不置坏
        Assert.Equal((ushort)5, Assert.IsType<ushort>(detail.Value));
    }

    // ─────────────── G-S-8：失败窗口质量等级由 Global/Quality@onCommError 决定 ───────────────

    [Fact]
    public void Gs8_offline_policy_marks_offline_after_failure()
    {
        _link.ReadReplies.Enqueue(ModbusReply.Ok(new ushort[] { 5 }, 1));
        // onCommErrorValue 非 keepLast → 走失败置值路径；质量等级取 onCommError="offline"
        Start(DEV_D1, PS_ONE_POINT_FAST, quality: "null", onCommError: "offline", globalExtra: NoBackoff);

        // 置坏发生在聚合错误事件之后，故等质量落定而不是等事件（避免与调度线程竞态）
        Assert.True(SpinWait.SpinUntil(
                () => _engine!.GetValueDetail("d1", "p").Quality == PointQuality.Offline, 5_000),
            "通讯失败窗口必须按 onCommError=offline 置离线质量");
    }

    [Fact]
    public void Gs8b_default_on_comm_error_marks_bad_after_failure()
    {
        _link.ReadReplies.Enqueue(ModbusReply.Ok(new ushort[] { 5 }, 1));
        Start(DEV_D1, PS_ONE_POINT_FAST, quality: "null", globalExtra: NoBackoff);

        Assert.True(SpinWait.SpinUntil(
                () => _engine!.GetValueDetail("d1", "p").Quality == PointQuality.Bad, 5_000),
            "onCommError 缺省 bad：失败窗口必须置 Bad");
    }

    [Fact]
    public void Gs8c_uncertain_policy_marks_uncertain_after_failure()
    {
        _link.ReadReplies.Enqueue(ModbusReply.Ok(new ushort[] { 5 }, 1));
        Start(DEV_D1, PS_ONE_POINT_FAST, quality: "null", onCommError: "uncertain", globalExtra: NoBackoff);

        Assert.True(SpinWait.SpinUntil(
                () => _engine!.GetValueDetail("d1", "p").Quality == PointQuality.Uncertain, 5_000),
            "onCommError=uncertain：失败窗口必须置 Uncertain");
    }

    // ─────────────── G-S-9：值不变不发事件；质量变化发事件（D13 修复后） ───────────────

    [Fact]
    public void Gs9_value_event_only_on_change_quality_change_emits()
    {
        _link.ReadReplies.Enqueue(ModbusReply.Ok(new ushort[] { 5 }, 1)); // 窗口 1：空→Good(5)
        // 窗口 2：Good→Bad（质量变化）；窗口 3+：持续 Bad（无变化 → 不再发）
        // 关退避：退避会把质量改判 offlineQuality，事件数不再是「空→Good→Bad」这两条
        Start(DEV_D1, PS_ONE_POINT_FAST, quality: "null", globalExtra: NoBackoff);

        Thread.Sleep(900); // intervalMs=150 → ≥4 个窗口
        var afterTransition = _valueEvents.Count;

        Thread.Sleep(400); // 再跑 ≥2 个窗口，状态不变

        // D13 修复后：MarkWindowFailed 走 PublishIfChanged——「空→Good(5)」与「Good→Bad」各一条，
        // 后续窗口质量不变不再重复发（值/质量变化才发）。
        Assert.Equal(2, afterTransition);
        Assert.Equal(2, _valueEvents.Count);
    }

    // ─────────────── G-S-9b（原 D13 缺陷复现，已修复）：失败窗口置坏必发值事件 ───────────────

    [Fact]
    public void Gs9b_offline_transition_emits_value_event()
    {
        _link.ReadReplies.Enqueue(ModbusReply.Ok(new ushort[] { 5 }, 1));
        Start(DEV_D1, PS_ONE_POINT_FAST, quality: "null", onCommError: "offline", globalExtra: NoBackoff);

        Thread.Sleep(900);

        Assert.Equal(2, _valueEvents.Count); // 空→Good(5)、Good→Offline 各一条
        Assert.Equal(PointQuality.Offline, _engine!.GetValueDetail("d1", "p").Quality);
    }

    // ─────────────── G-S-10：故障设备的异常不终止调度（线程存活） ───────────────

    [Fact]
    public void Gs10_failing_device_does_not_kill_scheduling_of_healthy_device()
    {
        // d1(unit 1) 持续**读超时**；d2(unit 2) 正常。
        // 第四步起失败分层（docs/01 §6.3）：读超时 = **设备级**退避 → 只排 d1，d2 照常轮询；
        // （IO 层失败则是链路级，整条链路一起等——那是「断线」而不是「单个从站不回」，
        //  对应的用例见 BackoffTests.B4。）
        _link.SetReadData(2, DataArea.HoldingRegister, 0, 7);
        _link.FaultRules.Add(new FakeFaultRule { UnitId = 1, Kind = ModbusFailureKind.Timeout });
        Start(DEV_D1 + DEV_D2, PS_ONE_POINT + PS_ONE_POINT_D2, defaultInterval: "150");

        Thread.Sleep(600);
        var reads1 = Reads(2).Count;
        Assert.True(reads1 > 0, "健康设备应已在采集");

        Thread.Sleep(600);
        var reads2 = Reads(2).Count;

        Assert.True(reads2 > reads1, "d1 持续报错期间 d2 必须继续轮询（调度线程存活）");
        Assert.True(_errorEvents.Count > 0); // d1 的失败已被聚合上报
    }

    // ─────────────── G-S-11：调度解码后喂报警引擎 ───────────────

    [Fact]
    public void Gs11_scheduler_feeds_alarm_engine()
    {
        _link.SetReadData(1, DataArea.HoldingRegister, 0, 11); // 越过 high 限值 10
        Start(DEV_D1, PS_WITH_ALARM);

        Assert.True(SpinWait.SpinUntil(() => _alarms.Count > 0, 5_000), "越限值应触发报警事件");

        var alarm = _alarms[0];
        Assert.Equal("high", alarm.AlarmType);
        Assert.Equal(11.0, Convert.ToDouble(alarm.Value));
    }

    // ─────────────── D19（ADR D33）：点位级 unitId 参与轮询分组 ───────────────

    [Fact]
    public void Gs14_points_with_different_unit_ids_go_to_separate_requests_on_their_own_slave()
    {
        // unit 1 与 unit 2 各有一个点，地址相邻（0 / 1）：合并前是一个请求，修复后必须拆成两个
        _link.SetReadData(1, DataArea.HoldingRegister, 0, 11);
        _link.SetReadData(2, DataArea.HoldingRegister, 1, 22);

        Start(DEV_D1, PS_TWO_UNITS);

        Thread.Sleep(400);

        var unit1 = Reads(1);
        var unit2 = Reads(2);
        Assert.Single(unit1);                       // 两个点不合并到同一请求
        Assert.Equal(0, unit1[0].Address);
        Assert.Equal(1, unit1[0].Count);
        Assert.Single(unit2);                       // 覆盖点发往自己的从站
        Assert.Equal(1, unit2[0].Address);
        Assert.Equal(1, unit2[0].Count);

        Assert.Equal((ushort)11, Assert.IsType<ushort>(_engine!.GetValueDetail("d1", "p1").Value));
        Assert.Equal((ushort)22, Assert.IsType<ushort>(_engine!.GetValueDetail("d1", "p2").Value));
    }

    [Fact]
    public void Gs14b_pointset_defaults_unit_id_also_selects_the_slave()
    {
        // PointSet/Defaults@unitId 是点位级覆盖的缺省形态，轮询同样必须按它下发
        _link.SetReadData(2, DataArea.HoldingRegister, 0, 33);

        Start(DEV_D1, PS_DEFAULTS_UNIT2);

        Thread.Sleep(400);

        Assert.Empty(Reads(1));
        var unit2 = Reads(2);
        Assert.Single(unit2);
        Assert.Equal(0, unit2[0].Address);
        Assert.Equal((ushort)33, Assert.IsType<ushort>(_engine!.GetValueDetail("d1", "p1").Value));
    }

    // ─────────────── D18（ADR D38）：设备级 swap 生效 + 点表共用不串味 ───────────────

    [Fact]
    public void Gs15_device_level_swap_applies_to_point_without_explicit_swap()
    {
        // 点位没写 swap → 取设备级。原始字 0x4840,0xC3F5 是 3.14f 的 BADC 排布
        _link.SetReadData(1, DataArea.HoldingRegister, 0, 0x4840, 0xC3F5);

        Start(DEV_D1_BADC_SWAP, PS_ONE_FLOAT);

        Thread.Sleep(400);

        var value = _engine!.GetValueDetail("d1", "p");
        Assert.Equal(3.14f, Assert.IsType<float>(value.Value), 3);
    }

    [Fact]
    public void Gs15b_shared_pointset_resolves_swap_per_device()
    {
        // 同一个 PointSet 被两个设备共用、各自 swap 不同（ADR D38 的核心约束）：
        // d1 的原始字按 BADC 排布，d2 的原始字按 CDAB 排布，两者都必须解出 3.14
        _link.SetReadData(1, DataArea.HoldingRegister, 0, 0x4840, 0xC3F5); // BADC
        _link.SetReadData(2, DataArea.HoldingRegister, 0, 0xF5C3, 0x4048); // CDAB

        Start(DEV_D1_BADC_SWAP + DEV_D2_CDAB_SWAP, PS_ONE_FLOAT);

        Thread.Sleep(500);

        Assert.Equal(3.14f, Assert.IsType<float>(_engine!.GetValueDetail("d1", "p").Value), 3);
        Assert.Equal(3.14f, Assert.IsType<float>(_engine.GetValueDetail("d2", "p").Value), 3);
    }

    [Fact]
    public void Gs15c_point_level_and_defaults_swap_win_over_device()
    {
        // 显式声明（Point@swap / Defaults@swap）优先于设备级：设备给 word，点表缺省给 none
        _link.SetReadData(1, DataArea.HoldingRegister, 0, 0x4048, 0xF5C3); // 3.14f 的 ABCD 排布

        Start(DEV_D1_BADC_SWAP, PS_DEFAULTS_ABCD_SWAP);

        Thread.Sleep(400);

        Assert.Equal(3.14f, Assert.IsType<float>(_engine!.GetValueDetail("d1", "p").Value), 3);
    }

    [Fact]
    public void Gs15d_global_swap_is_the_last_resort()
    {
        // 点位与设备都没声明 → 取 Global@swap（none）：ABCD 原始字直接解出 3.14
        _link.SetReadData(1, DataArea.HoldingRegister, 0, 0x4048, 0xF5C3);

        Start(DEV_D1, PS_ONE_FLOAT, globalAttrs: " swap=\"none\"");

        Thread.Sleep(400);

        Assert.Equal(3.14f, Assert.IsType<float>(_engine!.GetValueDetail("d1", "p").Value), 3);
    }

    // ─────────────── D24（ADR D37）：兜底 catch 不得静默 ───────────────

    [Fact]
    public void Gs16_unexpected_exception_emits_error_marks_quality_and_keeps_polling()
    {
        // 模拟驱动层未包装的裸 IOException（D24 的场景）：旧实现被 catch {} 静默吞掉，
        // 既无事件也不更新质量。修复后：发事件 + 按策略置坏 + 线程存活继续采集。
        _link.SetReadData(1, DataArea.HoldingRegister, 0, 5);
        _link.FaultRules.Add(new FakeFaultRule { ThrowIo = true, RemainingCalls = 1 });

        Start(DEV_D1, PS_ONE_POINT_FAST, quality: "null", globalExtra: NoBackoff);

        Assert.True(SpinWait.SpinUntil(() => _errorEvents.Count > 0, 5_000),
            "兜底 catch 必须发出错误事件，绝不能静默");
        Assert.Contains(_errorEvents, e => e.Info.Code == "MODBUS.LINK");
        Assert.Contains(_errorEvents, e => e is LinkError);

        // 该窗口质量必须可见地变坏（onCommError=bad）
        Assert.True(SpinWait.SpinUntil(
                () => _engine!.GetValueDetail("d1", "p").Quality == PointQuality.Bad, 2_000),
            "未归类异常的窗口必须按 onCommError 策略置坏");

        // 线程不死：故障只注入一次，后续窗口继续采集
        Assert.True(SpinWait.SpinUntil(
                () => _engine!.GetValueDetail("d1", "p").IsGood, 5_000),
            "兜底 catch 之后轮询线程必须继续采集（GATE-5）");
        Assert.Equal((ushort)5, Assert.IsType<ushort>(_engine!.GetValueDetail("d1", "p").Value));
    }

    // ─────────────── 配置模板 ───────────────
    //
    // 0=onCommErrorValue 1=Devices 2=PointSets 3=onCommError 4=Global 附加段 5=Global 属性 6=默认间隔
    // 节奏一律由点位/块自带（intervalMs/mode）：默认间隔取大值（2000ms），
    // 需要快节奏的用例显式写 intervalMs 或调小 defaultInterval。

    private const string TEMPLATE = """
        <SamplerConfig schemaVersion="3.0">
          <Global{5}>
            <Polling defaultIntervalMs="{6}" requestTimeoutMs="500" />
            <Quality onCommErrorValue="{0}" onCommError="{3}" />
            {4}
          </Global>
          <Transports><Transport id="tcp1" host="127.0.0.1" /></Transports>
          <Devices>{1}</Devices>
          <PointSets>{2}</PointSets>
        </SamplerConfig>
        """;

    private const string DEV_D1 = """
        <Device id="d1" transport="tcp1" pointSet="ps1" unitId="1" />
        """;

    private const string DEV_D2 = """
        <Device id="d2" transport="tcp1" pointSet="ps2" unitId="2" />
        """;

    /// <summary>默认间隔（2000ms）的点：一个 400ms 的观察窗内只会读一次。</summary>
    private const string PS_ONE_POINT = """
        <PointSet id="ps1"><Points><Point id="p" address="0" /></Points></PointSet>
        """;

    private const string PS_ONE_POINT_FAST = """
        <PointSet id="ps1"><Points><Point id="p" address="0" intervalMs="150" /></Points></PointSet>
        """;

    private const string PS_ONE_POINT_ONDEMAND = """
        <PointSet id="ps1"><Points><Point id="p" address="0" mode="onDemand" /></Points></PointSet>
        """;

    private const string PS_ONE_POINT_ONCE = """
        <PointSet id="ps1"><Points><Point id="p" address="0" mode="once" /></Points></PointSet>
        """;

    private const string PS_ONE_POINT_D2 = """
        <PointSet id="ps2"><Points><Point id="p" address="0" intervalMs="150" /></Points></PointSet>
        """;

    private const string PS_BLOCK_3 = """
        <PointSet id="ps1">
          <Blocks><Block id="b1" start="0" count="3">
            <Point id="p0" address="0" /><Point id="p1" address="1" /><Point id="p2" address="2" />
          </Block></Blocks>
          <Points />
        </PointSet>
        """;

    private const string PS_SCATTER = """
        <PointSet id="ps1"><Points>
          <Point id="a" address="0" />
          <Point id="b" address="1" />
          <Point id="c" address="20" />
        </Points></PointSet>
        """;

    private const string PS_ONE_POINT_DISABLED = """
        <PointSet id="ps1"><Points><Point id="p" address="0" enabled="false" /></Points></PointSet>
        """;

    private const string PS_DISABLED_BLOCK = """
        <PointSet id="ps1">
          <Blocks><Block id="b1" start="0" count="3" enabled="false">
            <Point id="p0" address="0" /><Point id="p1" address="1" />
          </Block></Blocks>
          <Points />
        </PointSet>
        """;

    private const string PS_WITH_ALARM = """
        <PointSet id="ps1"><Points>
          <Point id="p" address="0">
            <Alarm type="high" limit="10" />
          </Point>
        </Points></PointSet>
        """;

    // ─────────────── D19/D38 用例的模板 ───────────────

    /// <summary>同一设备下两个相邻点，p2 覆盖 unitId=2（网关场景）。</summary>
    private const string PS_TWO_UNITS = """
        <PointSet id="ps1"><Points>
          <Point id="p1" address="0" />
          <Point id="p2" address="1" unitId="2" />
        </Points></PointSet>
        """;

    /// <summary>点位级 unitId 的缺省形态：PointSet/Defaults@unitId。</summary>
    private const string PS_DEFAULTS_UNIT2 = """
        <PointSet id="ps1"><Defaults unitId="2" /><Points>
          <Point id="p1" address="0" />
        </Points></PointSet>
        """;

    private const string DEV_D1_BADC_SWAP = """
        <Device id="d1" transport="tcp1" pointSet="ps1" unitId="1" swap="badc" />
        """;

    private const string DEV_D2_CDAB_SWAP = """
        <Device id="d2" transport="tcp1" pointSet="ps1" unitId="2" swap="cdab" />
        """;

    /// <summary>点位不写 swap（待设备/全局兜底）。</summary>
    private const string PS_ONE_FLOAT = """
        <PointSet id="ps1"><Points><Point id="p" address="0" dataType="float32" /></Points></PointSet>
        """;

    /// <summary>点表缺省给了 swap=abcd：显式声明，优先于设备级。</summary>
    private const string PS_DEFAULTS_ABCD_SWAP = """
        <PointSet id="ps1"><Defaults swap="abcd" /><Points><Point id="p" address="0" dataType="float32" /></Points></PointSet>
        """;
}
