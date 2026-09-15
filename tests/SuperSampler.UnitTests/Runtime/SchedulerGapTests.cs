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
/// Scheduler 补测（docs/07 测试计划 §3.2 G-S 族）：轮询节奏、onDemand/once 组、块读与散点合并、
/// ≤125 分批、窗口聚合错误、keepLast/Offline 质量策略、值/质量变化事件、故障下线程存活、报警喂入。
/// 全部经 SamplerEngine 注入 FakeModbusLink 驱动真实调度线程（findings B1 的接缝）。
/// </summary>
public class SchedulerGapTests : IDisposable
{
    private readonly FakeModbusLink _link = new();
    private readonly List<PointValueChangedEvent> _valueEvents = new();
    private readonly List<IErrorEvent> _errorEvents = new();
    private readonly List<AlarmRaisedEvent> _alarms = new();
    private SamplerEngine? _engine;

    public void Dispose() => _engine?.Dispose();

    private SamplerEngine Start(string devices, string pointSets, string quality = "keepLast", string scanGroups = DEFAULT_GROUPS, string onCommError = "bad", string globalExtra = "", string globalAttrs = "")
    {
        var xml = string.Format(TEMPLATE, quality, scanGroups, devices, pointSets, onCommError, globalExtra, globalAttrs);
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

    // ─────────────── G-S-1：扫描组按 RateMs 节奏轮询 ───────────────

    [Fact]
    public void Gs1_scan_group_polls_at_configured_rate()
    {
        Start(DEV_D1_FAST, PS_ONE_POINT_FAST, scanGroups: DEFAULT_GROUPS + FAST_GROUP);

        Thread.Sleep(700); // rate=150ms → 期望 ~5 个窗口

        var reads = Reads(1);
        // 宽松区间防抖动：至少 2 个窗口，且不失控（≤ 8）
        Assert.True(reads.Count >= 2, $"700ms 内只读了 {reads.Count} 次（rate=150ms）");
        Assert.True(reads.Count <= 8, $"700ms 内读了 {reads.Count} 次，疑似节奏失控");
    }

    // ─────────────── D12（已修复）：onDemand 组不参与轮询 ───────────────
    // 修复：模式比较改为 OrdinalIgnoreCase（D12）。本用例断言修复后的正确行为。

    [Fact]
    public void Gs2a_on_demand_group_is_not_polled()
    {
        Start(DEV_D1, PS_ONE_POINT_ONDEMAND);

        Thread.Sleep(400);

        Assert.Empty(Reads(1)); // onDemand 只能手动触发
    }

    // ─────────────── D11（已修复）：once 组只读一次 ───────────────
    // 修复：Scheduler 识别 once 模式，读完一个窗口后不再排程（D11）。断言修复后的正确行为。

    [Fact]
    public void Gs2b_once_group_reads_exactly_once()
    {
        Start(DEV_D1_ONCE, PS_ONE_POINT, scanGroups: DEFAULT_GROUPS + ONCE_GROUP);

        Thread.Sleep(400); // once 组 rate=50ms → 若被当作 poll 会读 ~8 次

        Assert.Single(Reads(1));
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

    // ─────────────── G-S-4：散点合并——严格相邻才合并 ───────────────

    [Fact]
    public void Gs4_adjacent_standalone_points_merge_gap_does_not()
    {
        // a@0、b@1 相邻（合并 count=2），c@20 有空洞（单独读）
        Start(DEV_D1, PS_SCATTER);

        Thread.Sleep(300);

        var reads = Reads(1);
        Assert.Single(reads.Where(r => r.Address == 0 && r.Count == 2));
        Assert.Single(reads.Where(r => r.Address == 20 && r.Count == 1));
        Assert.Equal(2, reads.Count); // 两个批次
        Assert.Contains(reads, r => r.Address == 0 && r.Count == 2);
        Assert.Contains(reads, r => r.Address == 20 && r.Count == 1);
    }

    // ─────────────── G-S-5：相邻散点超 125 自动分批 ───────────────

    [Fact]
    public void Gs5_merged_batch_capped_at_protocol_limit_125()
    {
        Start(DEV_D1, BuildScatterPoints(126)); // 地址 0..125 严格相邻

        Thread.Sleep(300);

        var reads = Reads(1);
        Assert.True(reads.Count == 2, $"期望 125+1 两批，实际 {reads.Count} 批"); // 125 + 1
        Assert.Contains(reads, r => r.Address == 0 && r.Count == 125);
        Assert.Contains(reads, r => r.Address == 125 && r.Count == 1);
    }

    // ─────────────── G-S-5b：单次读上限来自 Global/Scheduler@maxRegistersPerRead ───────────────

    [Fact]
    public void Gs5b_merge_limit_follows_configured_max_registers_per_read()
    {
        // 配置上限 2（默认 125）：0..4 五个严格相邻点应切成 3 批（2+2+1）
        Start(DEV_D1, BuildScatterPoints(5), globalExtra: "<Scheduler maxRegistersPerRead=\"2\" />");

        Thread.Sleep(300);

        var reads = Reads(1);
        Assert.Equal(3, reads.Count);
        Assert.Equal(2, reads.Count(r => r.Count == 2));
        Assert.Equal(1, reads.Count(r => r.Count == 1));
    }

    // ─────────────── G-S-5c：位区按 Global/Scheduler@maxBitsPerRead 单独限流 ───────────────

    [Fact]
    public void Gs5c_bit_area_uses_max_bits_per_read()
    {
        // 线圈区 5 个相邻位：maxBitsPerRead=2 → 3 批（位区不吃 maxRegistersPerRead）
        Start(DEV_D1, BuildCoilPoints(5), globalExtra: "<Scheduler maxBitsPerRead=\"2\" maxRegistersPerRead=\"125\" />");

        Thread.Sleep(300);

        var reads = Reads(1);
        Assert.Equal(3, reads.Count);
    }

    // ─────────────── G-S-4b/4c：mergeGap 容差生效（0 = 严格相邻） ───────────────

    [Fact]
    public void Gs4b_merge_gap_zero_keeps_hole_split()
    {
        // a@0、b@10：间隔 9。默认 mergeGap=0 → 空洞不合并，2 批
        Start(DEV_D1, BuildGapPoints());

        Thread.Sleep(300);

        Assert.Equal(2, Reads(1).Count);
    }

    [Fact]
    public void Gs4c_merge_gap_tolerance_merges_holes_within_gap()
    {
        // mergeGap=9 → a@0 与 b@10 容差内合并为一批 count=11
        Start(DEV_D1, BuildGapPoints(), globalExtra: "<Scheduler mergeGap=\"9\" />");

        Thread.Sleep(300);

        var reads = Reads(1);
        Assert.Single(reads);
        Assert.Equal(0, reads[0].Address);
        Assert.Equal(11, reads[0].Count);
    }

    private static string BuildGapPoints()
        => "<PointSet id=\"ps1\"><Points>"
           + "<Point id=\"a\" address=\"0\" scanGroup=\"slow\" />"
           + "<Point id=\"b\" address=\"10\" scanGroup=\"slow\" />"
           + "</Points></PointSet>";

    private static string BuildCoilPoints(int count)
    {
        var sb = new StringBuilder("<PointSet id=\"ps1\"><Points>");
        for (var i = 0; i < count; i++)
        {
            sb.Append("<Point id=\"c").Append(i).Append("\" area=\"coil\" address=\"").Append(i).Append("\" scanGroup=\"slow\" />");
        }
        sb.Append("</Points></PointSet>");
        return sb.ToString();
    }

    private static string BuildScatterPoints(int count)
    {        var sb = new StringBuilder("<PointSet id=\"ps1\"><Points>");
        for (var i = 0; i < count; i++)
        {
            sb.Append("<Point id=\"p").Append(i).Append("\" address=\"").Append(i).Append("\" scanGroup=\"slow\" />");
        }
        sb.Append("</Points></PointSet>");
        return sb.ToString();
    }

    // ─────────────── G-S-6：单窗口失败只发一条聚合错误事件 ───────────────

    [Fact]
    public void Gs6_single_window_failure_emits_one_aggregated_error()
    {
        // 设备 scanGroup=slow（rate=1000ms）：观察窗内只有一个窗口，全部读失败（无数据注册 → LinkDown）
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
        Start(DEV_D1_FAST, PS_ONE_POINT_FAST, scanGroups: DEFAULT_GROUPS + FAST_GROUP); // 默认 keepLast

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
        Start(DEV_D1_FAST, PS_ONE_POINT_FAST, quality: "bad", onCommError: "offline",
              scanGroups: DEFAULT_GROUPS + FAST_GROUP);

        SpinWait.SpinUntil(() => _errorEvents.Count > 0, 5_000);

        var detail = _engine!.GetValueDetail("d1", "p");
        Assert.False(detail.IsGood); // 通讯失败 → 离线坏值
        Assert.Equal(PointQuality.Offline, detail.Quality);
    }

    [Fact]
    public void Gs8b_default_on_comm_error_marks_bad_after_failure()
    {
        _link.ReadReplies.Enqueue(ModbusReply.Ok(new ushort[] { 5 }, 1));
        Start(DEV_D1_FAST, PS_ONE_POINT_FAST, quality: "bad", scanGroups: DEFAULT_GROUPS + FAST_GROUP);

        SpinWait.SpinUntil(() => _errorEvents.Count > 0, 5_000);

        var detail = _engine!.GetValueDetail("d1", "p");
        Assert.False(detail.IsGood);
        Assert.Equal(PointQuality.Bad, detail.Quality); // onCommError 缺省 = bad
    }

    [Fact]
    public void Gs8c_uncertain_policy_marks_uncertain_after_failure()
    {
        _link.ReadReplies.Enqueue(ModbusReply.Ok(new ushort[] { 5 }, 1));
        Start(DEV_D1_FAST, PS_ONE_POINT_FAST, quality: "bad", onCommError: "uncertain",
              scanGroups: DEFAULT_GROUPS + FAST_GROUP);

        SpinWait.SpinUntil(() => _errorEvents.Count > 0, 5_000);

        var detail = _engine!.GetValueDetail("d1", "p");
        Assert.False(detail.IsGood);
        Assert.Equal(PointQuality.Uncertain, detail.Quality);
    }

    // ─────────────── G-S-9：值不变不发事件；质量变化发事件（D13 修复后） ───────────────

    [Fact]
    public void Gs9_value_event_only_on_change_quality_change_emits()
    {
        _link.ReadReplies.Enqueue(ModbusReply.Ok(new ushort[] { 5 }, 1)); // 窗口 1：空→Good(5)
        // 窗口 2：Good→Bad（质量变化）；窗口 3+：持续 Bad（无变化 → 不再发）
        Start(DEV_D1_FAST, PS_ONE_POINT_FAST, quality: "bad", scanGroups: DEFAULT_GROUPS + FAST_GROUP);

        Thread.Sleep(900); // rate=150ms → ≥4 个窗口
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
        Start(DEV_D1_FAST, PS_ONE_POINT_FAST, quality: "bad", onCommError: "offline",
              scanGroups: DEFAULT_GROUPS + FAST_GROUP);

        Thread.Sleep(900);

        Assert.Equal(2, _valueEvents.Count); // 空→Good(5)、Good→Offline 各一条
        Assert.Equal(PointQuality.Offline, _engine!.GetValueDetail("d1", "p").Quality);
    }

    // ─────────────── G-S-10：故障设备的异常不终止调度（线程存活） ───────────────

    [Fact]
    public void Gs10_failing_device_does_not_kill_scheduling_of_healthy_device()
    {
        // d1(unit 1) 全失败；d2(unit 2) 正常。同链路串行，d1 失败是瞬时 LinkDown 不阻塞。
        _link.SetReadData(2, DataArea.HoldingRegister, 0, 7);
        Start(DEV_D1 + DEV_D2, PS_ONE_POINT + PS_ONE_POINT_D2, scanGroups: DEFAULT_GROUPS + FAST_GROUP);

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
        // d1=BADCD 原始字按 BADC 排布，d2 的原始字按 CDAB 排布，两者都必须解出 3.14
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

        Start(DEV_D1_FAST, PS_ONE_POINT_FAST, quality: "bad", scanGroups: DEFAULT_GROUPS + FAST_GROUP);

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

    private const string TEMPLATE = """
        <SamplerConfig schemaVersion="3.0">
          <Global{6}>
            <Polling rateMs="500" requestTimeoutMs="500" />
            <Quality onCommErrorValue="{0}" onCommError="{4}" />
            {5}
          </Global>
          <ScanGroups>{1}</ScanGroups>
          <Transports><Transport id="tcp1" host="127.0.0.1" /></Transports>
          <Devices>{2}</Devices>
          <PointSets>{3}</PointSets>
        </SamplerConfig>
        """;

    private const string DEFAULT_GROUPS = """
        <ScanGroup id="normal" rateMs="500" />
        <ScanGroup id="slow" rateMs="100000" />
        <ScanGroup id="onDemand" mode="onDemand" />
        """;

    private const string FAST_GROUP = """
        <ScanGroup id="fast" rateMs="150" />
        """;

    private const string ONCE_GROUP = """
        <ScanGroup id="once" mode="once" rateMs="50" />
        """;

    private const string DEV_D1 = """
        <Device id="d1" transport="tcp1" pointSet="ps1" unitId="1" scanGroup="slow" />
        """;

    private const string DEV_D1_ONDEMAND = """
        <Device id="d1" transport="tcp1" pointSet="ps1" unitId="1" scanGroup="onDemand" />
        """;

    private const string DEV_D1_ONCE = """
        <Device id="d1" transport="tcp1" pointSet="ps1" unitId="1" scanGroup="once" />
        """;

    private const string DEV_D1_FAST = """
        <Device id="d1" transport="tcp1" pointSet="ps1" unitId="1" scanGroup="fast" />
        """;

    private const string DEV_D2 = """
        <Device id="d2" transport="tcp1" pointSet="ps2" unitId="2" scanGroup="fast" />
        """;

    private const string PS_ONE_POINT_FAST = """
        <PointSet id="ps1"><Points><Point id="p" address="0" scanGroup="fast" /></Points></PointSet>
        """;

    private const string PS_ONE_POINT_ONDEMAND = """
        <PointSet id="ps1"><Points><Point id="p" address="0" scanGroup="onDemand" /></Points></PointSet>
        """;

    private const string PS_ONE_POINT = """
        <PointSet id="ps1"><Points><Point id="p" address="0" scanGroup="slow" /></Points></PointSet>
        """;

    private const string PS_ONE_POINT_D2 = """
        <PointSet id="ps2"><Points><Point id="p" address="0" scanGroup="fast" /></Points></PointSet>
        """;

    private const string PS_BLOCK_3 = """
        <PointSet id="ps1">
          <Blocks><Block id="b1" start="0" count="3" scanGroup="slow">
            <Point id="p0" address="0" /><Point id="p1" address="1" /><Point id="p2" address="2" />
          </Block></Blocks>
          <Points />
        </PointSet>
        """;

    private const string PS_SCATTER = """
        <PointSet id="ps1"><Points>
          <Point id="a" address="0" scanGroup="slow" />
          <Point id="b" address="1" scanGroup="slow" />
          <Point id="c" address="20" scanGroup="slow" />
        </Points></PointSet>
        """;

    private const string PS_ONE_POINT_DISABLED = """
        <PointSet id="ps1"><Points><Point id="p" address="0" scanGroup="slow" enabled="false" /></Points></PointSet>
        """;

    private const string PS_DISABLED_BLOCK = """
        <PointSet id="ps1">
          <Blocks><Block id="b1" start="0" count="3" scanGroup="slow" enabled="false">
            <Point id="p0" address="0" /><Point id="p1" address="1" />
          </Block></Blocks>
          <Points />
        </PointSet>
        """;

    private const string PS_WITH_ALARM = """
        <PointSet id="ps1"><Points>
          <Point id="p" address="0" scanGroup="slow">
            <Alarm type="high" limit="10" />
          </Point>
        </Points></PointSet>
        """;

    // ─────────────── D19/D38 用例的模板 ───────────────

    /// <summary>同一设备下两个相邻点，p2 覆盖 unitId=2（网关场景）。</summary>
    private const string PS_TWO_UNITS = """
        <PointSet id="ps1"><Points>
          <Point id="p1" address="0" scanGroup="slow" />
          <Point id="p2" address="1" scanGroup="slow" unitId="2" />
        </Points></PointSet>
        """;

    /// <summary>点位级 unitId 的缺省形态：PointSet/Defaults@unitId。</summary>
    private const string PS_DEFAULTS_UNIT2 = """
        <PointSet id="ps1"><Defaults unitId="2" /><Points>
          <Point id="p1" address="0" scanGroup="slow" />
        </Points></PointSet>
        """;

    private const string DEV_D1_BADC_SWAP = """
        <Device id="d1" transport="tcp1" pointSet="ps1" unitId="1" scanGroup="slow" swap="badc" />
        """;

    private const string DEV_D2_CDAB_SWAP = """
        <Device id="d2" transport="tcp1" pointSet="ps1" unitId="2" scanGroup="slow" swap="cdab" />
        """;

    /// <summary>点位不写 swap（待设备/全局兜底）。</summary>
    private const string PS_ONE_FLOAT = """
        <PointSet id="ps1"><Points><Point id="p" address="0" dataType="float32" scanGroup="slow" /></Points></PointSet>
        """;

    /// <summary>点表缺省给了 swap=abcd：显式声明，优先于设备级。</summary>
    private const string PS_DEFAULTS_ABCD_SWAP = """
        <PointSet id="ps1"><Defaults swap="abcd" /><Points><Point id="p" address="0" dataType="float32" scanGroup="slow" /></Points></PointSet>
        """;
}
