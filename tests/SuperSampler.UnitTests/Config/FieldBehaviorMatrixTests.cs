using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Xml.Linq;
using SuperSampler.Abstractions.Facade;
using SuperSampler.Abstractions.Values;
using SuperSampler.Core.Config;
using SuperSampler.Core.Runtime;
using SuperSampler.Drivers.Modbus;
using SuperSampler.UnitTests.Support;
using Xunit;

namespace SuperSampler.UnitTests.Config;

/// <summary>
/// 字段「解析生效 + 行为生效」的补充证据（矩阵见 <c>_testplan/field-behavior-matrix.md</c>）。
///
/// 为什么有这个文件：<see cref="FieldCoverageTests"/> 证的是「XML → 模型」，
/// 本文件证的是「XML → 模型 → **真的影响运行**」：行为类用例经真实加载器 + 真实调度线程
/// （<see cref="SamplerEngine"/> 注入 <see cref="FakeModbusLink"/>）断言请求次数/地址/窗口，
/// 编解码类用例经真实加载器 + <see cref="PointRegistry"/> 断言解码值与显示文本。
///
/// 覆盖的字段：<c>Global/Polling@defaultIntervalMs</c>、<c>Point@intervalMs</c>、<c>Block@intervalMs</c>、
/// <c>Device@enabled</c>、<c>Pause@maintenance</c>、<c>Point/Slices</c>、<c>Point/Bits</c>、
/// <c>Write@min/@max</c>、<c>Scale</c>（双点映射 / Clamp）、<c>Format@decimals/@thousands/@pattern</c>、
/// <c>String@trimNull</c>、以及 ADR D30 的「仅解析字段写了即告警」。
/// </summary>
/// <remarks>
/// 与 <c>BackoffTests</c> 同属一个 xUnit 集合：两者都会起**真实轮询线程**，
/// 而 B16 断言的是**进程级**句柄数（HandlesBefore/After），必须与这类用例串行采样，
/// 否则并行执行时别的用例开的线程会把它误判成泄漏（实测偶发 878 → 942）。
/// </remarks>
[Collection("real-polling-threads")]
public class FieldBehaviorMatrixTests : IDisposable
{
    private readonly FakeModbusLink _link = new();
    private SamplerEngine? _engine;

    public void Dispose() => _engine?.Dispose();

    // ─────────────── 装载器（行为类：真引擎 + 假链路）───────────────

    private const string Transports = "<Transports><Transport id=\"tcp1\" host=\"127.0.0.1\" /></Transports>";

    /// <summary>起引擎：默认间隔 2000ms（观察窗内只该读一次），链路正常可读。</summary>
    private SamplerEngine Start(
        string pointSets,
        string? devices = null,
        string defaultInterval = "2000",
        string? transports = null,
        string globalExtra = "")
    {
        var xml =
            "<SamplerConfig schemaVersion=\"3.0\">"
            + "<Global><Polling defaultIntervalMs=\"" + defaultInterval + "\" requestTimeoutMs=\"500\" />"
            + globalExtra + "</Global>"
            + (transports ?? Transports)
            + "<Devices>" + (devices ?? "<Device id=\"d1\" transport=\"tcp1\" pointSet=\"ps1\" unitId=\"1\" />") + "</Devices>"
            + "<PointSets>" + pointSets + "</PointSets>"
            + "</SamplerConfig>";

        var config = SamplerConfigLoader.Load(XDocument.Parse(xml), Directory.GetCurrentDirectory());
        _link.DefaultReadData = new ushort[512];
        _engine = new SamplerEngine(config, new Dictionary<string, IModbusLink> { ["tcp1"] = _link });
        _engine.Start();
        return _engine;
    }

    private List<FakeLinkCall> Reads() => _link.Calls.Where(c => c.IsRead).ToList();

    // ─────────────── 装载器（解析类：真加载器 + 注册表）───────────────

    private static RuntimePoint LoadPoint(string attributes, string children = "")
    {
        var point = children.Length == 0
            ? "<Point id=\"p\" address=\"0\" " + attributes + " />"
            : "<Point id=\"p\" address=\"0\" " + attributes + ">" + children + "</Point>";

        var xml =
            "<SamplerConfig schemaVersion=\"3.0\">" + Transports
            + "<Devices><Device id=\"d1\" transport=\"tcp1\" pointSet=\"ps1\" /></Devices>"
            + "<PointSets><PointSet id=\"ps1\"><Points>" + point + "</Points></PointSet></PointSets>"
            + "</SamplerConfig>";

        var config = SamplerConfigLoader.Load(XDocument.Parse(xml), Directory.GetCurrentDirectory());
        return new PointRegistry(config).GetPoint("d1", "p");
    }

    private static SamplerConfiguration Load(string top)
        => SamplerConfigLoader.Load(XDocument.Parse(
            "<SamplerConfig schemaVersion=\"3.0\">" + top + "</SamplerConfig>"), Directory.GetCurrentDirectory());

    private static SamplerConfiguration LoadWithPoint(string point)
        => Load(Transports
            + "<Devices><Device id=\"d1\" transport=\"tcp1\" pointSet=\"ps1\" /></Devices>"
            + "<PointSets><PointSet id=\"ps1\"><Points>" + point + "</Points></PointSet></PointSets>");

    // ═══════════════ 1. 节奏：intervalMs 真的决定请求次数 ═══════════════

    [Fact]
    public void Default_interval_applies_to_points_without_an_interval()
    {
        // 点位没有 intervalMs → 必须取 Global/Polling@defaultIntervalMs（不是别的常量）
        Start("<PointSet id=\"ps1\"><Points><Point id=\"p\" address=\"0\" /></Points></PointSet>",
            defaultInterval: "100");

        Thread.Sleep(700);

        var reads = Reads().Count(r => r.Address == 0);
        Assert.True(reads >= 3, $"defaultIntervalMs=100 时 700ms 内应读到 ≥3 次，实际 {reads} 次");
    }

    [Fact]
    public void Point_interval_overrides_the_global_default()
    {
        // 全局默认 2000ms（观察窗内最多 1 次），点自己写 100ms → 必须按点的来
        Start("<PointSet id=\"ps1\"><Points><Point id=\"p\" address=\"0\" intervalMs=\"100\" /></Points></PointSet>");

        Thread.Sleep(700);

        var reads = Reads().Count(r => r.Address == 0);
        Assert.True(reads >= 3, $"Point@intervalMs=100 应压过全局默认 2000，实际 {reads} 次");
    }

    [Fact]
    public void Block_interval_drives_block_requests()
    {
        Start("<PointSet id=\"ps1\">"
            + "<Blocks><Block id=\"b1\" start=\"0\" count=\"2\" intervalMs=\"100\">"
            + "<Point id=\"bp\" address=\"0\" /></Block></Blocks><Points /></PointSet>");

        Thread.Sleep(700);

        var reads = Reads().Count(r => r.Address == 0 && r.Count == 2);
        Assert.True(reads >= 3, $"Block@intervalMs=100 应压过全局默认 2000，实际 {reads} 次");
    }

    [Fact]
    public void Disabled_device_never_sends_a_request()
    {
        Start("<PointSet id=\"ps1\"><Points><Point id=\"p\" address=\"0\" intervalMs=\"100\" /></Points></PointSet>",
            devices: "<Device id=\"d1\" transport=\"tcp1\" pointSet=\"ps1\" unitId=\"1\" enabled=\"false\" />");

        Thread.Sleep(400);

        Assert.Empty(Reads());
    }

    [Fact]
    public void Paused_device_never_sends_a_request()
    {
        // Device/Pause@maintenance=true：检修暂停（保留配置，不轮询）
        Start("<PointSet id=\"ps1\"><Points><Point id=\"p\" address=\"0\" intervalMs=\"100\" /></Points></PointSet>",
            devices: "<Device id=\"d1\" transport=\"tcp1\" pointSet=\"ps1\" unitId=\"1\"><Pause maintenance=\"true\" /></Device>");

        Thread.Sleep(400);

        Assert.Empty(Reads());
    }

    // ═══════════════ 2. 取数窗口：Slices / Bits ═══════════════

    [Fact]
    public void Slices_point_is_fetched_by_one_request_covering_the_whole_span()
    {
        // 两段：@10 长 2、@20 长 1 → 一次请求覆盖 [10,21)（中间空洞也读回来，不单独发请求）
        Start("<PointSet id=\"ps1\"><Points><Point id=\"p\" address=\"10\" dataType=\"uint16\" intervalMs=\"100\">"
            + "<Slices><Slice address=\"10\" length=\"2\" /><Slice address=\"20\" length=\"1\" /></Slices>"
            + "</Point></Points></PointSet>");

        Thread.Sleep(500);

        var reads = Reads();
        Assert.NotEmpty(reads);
        // 每次请求都是「片段跨度」[10,21)：中间空洞一起读回来，绝不为了一个点拆成多次请求
        Assert.All(reads, r => Assert.Equal(10, r.Address));
        Assert.All(reads, r => Assert.Equal(11, r.Count));
    }

    [Fact]
    public void Bits_expansion_creates_child_points_with_their_text()
    {
        var point = LoadPoint("intervalMs=\"100\"",
            "<Bits><Bit index=\"0\" name=\"running\" text=\"运行\" /><Field from=\"4\" to=\"7\" name=\"code\" /></Bits>");

        Assert.Equal(2, point.Source.Bits!.Count);

        // 子点位进注册表（可读/可订阅），Text 进 Name、未写 text 时用 name
        var registry = new PointRegistry(LoadWithPoint(
            "<Point id=\"p\" address=\"0\"><Bits><Bit index=\"0\" name=\"running\" text=\"运行\" />"
            + "<Field from=\"4\" to=\"7\" name=\"code\" /></Bits></Point>"));
        var child = registry.GetPoint("d1", "p.running");
        Assert.Equal("运行", child.Name);
        Assert.Equal(0, child.Bit);
        Assert.Equal("p", child.ParentPointId);

        var field = registry.GetPoint("d1", "p.code");
        Assert.Equal("code", field.Name);
        Assert.Equal("4-7", field.Source.BitRange);
    }

    // ═══════════════ 3. 写约束：min/max 拒绝且不发通讯 ═══════════════

    [Fact]
    public async System.Threading.Tasks.Task Write_min_and_max_reject_without_any_communication()
    {
        Start("<PointSet id=\"ps1\"><Points>"
            + "<Point id=\"p\" address=\"0\" access=\"write\"><Write min=\"10\" max=\"20\" /></Point>"
            + "</Points></PointSet>");

        var below = await _engine!.SetValueAsync("d1", "p", 5);
        var above = await _engine.SetValueAsync("d1", "p", 25);

        Assert.Equal(WriteOutcome.Rejected, below.Outcome);
        Assert.Equal(WriteOutcome.Rejected, above.Outcome);
        Assert.Equal("ss.reason.belowMin", below.Error!.MessageKey);
        Assert.Equal("ss.reason.aboveMax", above.Error!.MessageKey);
        Assert.Equal(0, _link.WriteCallCount);   // 拒绝就是「未发出任何通讯」
    }

    // ═══════════════ 4. 值处理管道：Scale / Format / String ═══════════════

    [Fact]
    public void Scale_two_point_mapping_decodes_into_the_engineering_range()
    {
        var point = LoadPoint("dataType=\"uint16\" swap=\"none\"",
            "<Scale rawLow=\"4\" rawHigh=\"20\" scaledLow=\"0\" scaledHigh=\"100\" />");

        var value = PointCodec.Decode(point, new ushort[] { 12 }, DateTimeOffset.UtcNow);

        Assert.True(value.IsGood);
        Assert.Equal(50.0, Assert.IsType<double>(value.Value), 6);
    }

    [Theory]
    [InlineData(50, 10.0)]     // 超上限 → 钳到 high
    [InlineData(-5, 0.0)]      // 低于下限 → 钳到 low
    [InlineData(7, 7.0)]       // 区间内原样
    public void Scale_clamp_both_limits_the_engineering_value(short raw, double expected)
    {
        var point = LoadPoint("dataType=\"int16\" swap=\"none\"",
            "<Scale factor=\"1\"><Clamp mode=\"both\" low=\"0\" high=\"10\" /></Scale>");

        var value = PointCodec.Decode(point, new ushort[] { unchecked((ushort)raw) }, DateTimeOffset.UtcNow);

        Assert.Equal(expected, Assert.IsType<double>(value.Value), 6);
    }

    [Fact]
    public void Bcd_scale_applies_on_decode_and_round_trips_through_encode()
    {
        // findings D43 回归：<Scale> 此前在 bcd 点上解码被静默忽略（Encode 却做逆缩放，读写不对称）
        var point = LoadPoint("dataType=\"bcd\" length=\"2\" swap=\"none\"",
            "<Bcd digits=\"8\" /><Scale factor=\"0.01\" /><Format decimals=\"2\" />");

        var value = PointCodec.Decode(point, new ushort[] { 0x1234, 0x5678 }, DateTimeOffset.UtcNow);

        Assert.Equal(123456.78, Assert.IsType<double>(value.Value), 6);
        Assert.Equal("123456.78", PointCodec.Format(point, value, "--"));   // D44：整型/BCD 也要认 decimals

        // 逆换算（写路径）与原值一致
        var words = PointCodec.Encode(point, 123456.78);
        Assert.Equal(new ushort[] { 0x1234, 0x5678 }, words);
    }

    [Fact]
    public void Format_decimals_applies_to_integral_engineering_values()
    {
        // findings D44 回归：整型工程值此前只认 thousands，decimals 被静默忽略
        var point = LoadPoint("dataType=\"uint16\" swap=\"none\"", "<Format decimals=\"2\" />");
        var value = PointCodec.Decode(point, new ushort[] { 1234 }, DateTimeOffset.UtcNow);

        Assert.Equal("1234.00", PointCodec.Format(point, value, "--"));
    }

    [Fact]
    public void Format_thousands_groups_digits()
    {
        var point = LoadPoint("dataType=\"uint16\" swap=\"none\"", "<Format thousands=\"true\" />");
        var value = PointCodec.Decode(point, new ushort[] { 1234 }, DateTimeOffset.UtcNow);

        Assert.Equal("1,234", PointCodec.Format(point, value, "--"));
    }

    [Fact]
    public void Format_pattern_formats_datetime_points()
    {
        var point = LoadPoint("dataType=\"datetime\" length=\"6\" swap=\"none\"",
            "<DateTime format=\"plc6\" /><Format pattern=\"yyyy-MM-dd HH:mm\" />");

        var value = PointCodec.Decode(point, new ushort[] { 2026, 9, 16, 10, 30, 0 }, DateTimeOffset.UtcNow);

        Assert.Equal("2026-09-16 10:30", PointCodec.Format(point, value, "--"));
    }

    [Fact]
    public void String_trim_null_false_keeps_the_padding_bytes()
    {
        var point = LoadPoint("dataType=\"string\" length=\"2\"",
            "<String padding=\"0x20\" trimNull=\"false\" />");

        var value = PointCodec.Decode(point, new ushort[] { 0x4142, 0x2020 }, DateTimeOffset.UtcNow);

        Assert.Equal("AB  ", Assert.IsType<string>(value.Value));
    }

    // ═══════════════ 5. ADR D30：仅解析字段写了必须显式告警 ═══════════════

    [Fact]
    public void Unconsumed_retry_strategy_attributes_are_reported_as_warnings()
    {
        var config = Load("<Global><Retry backoff=\"linear\" escalateAfter=\"5\" budgetMs=\"800\" /></Global>"
            + "<PointSets><PointSet id=\"ps1\"><Points><Point id=\"p\" address=\"0\" /></Points></PointSet></PointSets>");

        var warning = Assert.Single(config.Warnings);
        Assert.Contains("尚未实现", warning);
        Assert.Contains("backoff", warning);
        Assert.Contains("escalateAfter", warning);
        Assert.Contains("budgetMs", warning);
    }

    [Fact]
    public void Unconsumed_write_constraints_and_range_are_reported_as_warnings()
    {
        var config = Load(Transports
            + "<Devices><Device id=\"d1\" transport=\"tcp1\" pointSet=\"ps1\" /></Devices>"
            + "<PointSets><PointSet id=\"ps1\"><Points>"
            + "<Point id=\"p\" address=\"0\" range=\"0..300\" readonlyBy=\"op\" desc=\"进水压力\" access=\"write\">"
            + "<Write confirm=\"true\" step=\"0.5\" permission=\"op\" /></Point>"
            + "</Points></PointSet></PointSets>");

        var warning = Assert.Single(config.Warnings);
        Assert.Contains("range", warning);
        Assert.Contains("confirm", warning);
        Assert.Contains("step", warning);
        Assert.Contains("permission", warning);

        // desc/readonlyBy 是宿主元数据（已解析、可达），不产生告警
        Assert.DoesNotContain("desc=", warning);
    }

    [Fact]
    public void Unconsumed_global_and_transport_leftovers_are_reported_as_warnings()
    {
        var config = Load("<Global timeZone=\"China Standard Time\" />"
            + "<Transports><Transport id=\"tcp1\" driver=\"modbus\" maxConcurrent=\"4\">"
            + "<Reconnect enabled=\"false\" /></Transport></Transports>"
            + "<Devices><Device id=\"d1\" transport=\"tcp1\" pointSet=\"ps1\" /></Devices>"
            + "<PointSets><PointSet id=\"ps1\"><Points><Point id=\"p\" address=\"0\" /></Points></PointSet></PointSets>");

        var warning = Assert.Single(config.Warnings);
        Assert.Contains("timeZone", warning);
        Assert.Contains("driver/maxConcurrent", warning);
        Assert.Contains("Reconnect 段", warning);
    }

    [Fact]
    public void No_warning_when_the_unconsumed_attributes_are_not_written()
    {
        var config = Load("<Global><Retry count=\"3\" intervalMs=\"50\" /></Global>"
            + Transports
            + "<Devices><Device id=\"d1\" transport=\"tcp1\" pointSet=\"ps1\" desc=\"注射机\" /></Devices>"
            + "<PointSets><PointSet id=\"ps1\"><Points>"
            + "<Point id=\"p\" address=\"0\" desc=\"压力\" readonlyBy=\"op\" access=\"write\">"
            + "<Write min=\"0\" max=\"10\" /></Point>"
            + "</Points></PointSet></PointSets>");

        Assert.Empty(config.Warnings);
        Assert.Equal("注射机", config.Devices[0].Desc);   // Device@desc 已解析（本轮接线）
    }

    [Fact]
    public void Unconsumed_attributes_follow_unsupported_policy_error()
    {
        // 显式告警同样受 unsupportedPolicy 控制：error 时升级为「拒绝加载」
        var ex = Assert.Throws<ConfigValidationException>(() => SamplerConfigLoader.Load(
            XDocument.Parse("<SamplerConfig schemaVersion=\"3.0\" unsupportedPolicy=\"error\">"
                + "<Global><Retry backoff=\"linear\" /></Global>"
                + "<PointSets><PointSet id=\"ps1\"><Points><Point id=\"p\" address=\"0\" /></Points></PointSet></PointSets>"
                + "</SamplerConfig>"),
            Directory.GetCurrentDirectory()));

        Assert.Contains(ex.Errors, e => e.Contains("backoff") && e.Contains("unsupportedPolicy=error"));
    }

    // ═══════════════ 6. Device@desc：本轮接线（此前完全没解析）═══════════════

    [Fact]
    public void Device_desc_is_parsed_into_the_model()
    {
        var config = Load(Transports
            + "<Devices><Device id=\"d1\" transport=\"tcp1\" pointSet=\"ps1\" desc=\"3 号注塑机\" /></Devices>"
            + "<PointSets><PointSet id=\"ps1\"><Points><Point id=\"p\" address=\"0\" /></Points></PointSet></PointSets>");

        Assert.Equal("3 号注塑机", config.Devices[0].Desc);
    }

    // ═══════════════ 7. 链路/设备的超时与重试真的传到驱动 ═══════════════

    [Fact]
    public void Transport_disabled_stops_the_whole_link()
    {
        // findings D45 回归：Transport@enabled=false 此前只进模型，链路上的设备照常轮询
        Start("<PointSet id=\"ps1\"><Points><Point id=\"p\" address=\"0\" intervalMs=\"100\" /></Points></PointSet>",
            devices: "<Device id=\"d1\" transport=\"tcp0\" pointSet=\"ps1\" unitId=\"1\" />",
            transports: "<Transports><Transport id=\"tcp0\" host=\"127.0.0.1\" enabled=\"false\" /></Transports>");

        Thread.Sleep(400);

        Assert.Empty(Reads());
    }

    [Fact]
    public void Request_timeout_and_retry_settings_reach_the_driver()
    {
        // 设备级覆盖：requestTimeoutMs / <Retry count intervalMs> 必须原样交给驱动（不是硬编码常量）
        Start("<PointSet id=\"ps1\"><Points><Point id=\"p\" address=\"0\" intervalMs=\"100\" /></Points></PointSet>",
            devices: "<Device id=\"d1\" transport=\"tcp1\" pointSet=\"ps1\" unitId=\"1\" requestTimeoutMs=\"777\">"
                     + "<Retry count=\"5\" intervalMs=\"42\" /></Device>");

        Thread.Sleep(300);

        var read = Reads().First();
        Assert.Equal(777, read.TimeoutMs);
        Assert.Equal(5, read.Retries);
        Assert.Equal(42, read.RetryIntervalMs);
    }

    [Fact]
    public void Global_request_timeout_and_retry_are_the_fallback_when_nothing_narrower_declares_them()
    {
        // 链路与设备都不写 → 取 Global/Polling@requestTimeoutMs 与 Global/Retry
        Start("<PointSet id=\"ps1\"><Points><Point id=\"p\" address=\"0\" intervalMs=\"100\" /></Points></PointSet>",
            defaultInterval: "100",
            globalExtra: "<Retry count=\"4\" intervalMs=\"33\" />");

        // 上面的 Start 用固定 requestTimeoutMs=500；这里单独用一份 321ms 的配置再验一次超时
        _engine!.Dispose();
        _engine = null;

        var xml =
            "<SamplerConfig schemaVersion=\"3.0\">"
            + "<Global><Polling defaultIntervalMs=\"100\" requestTimeoutMs=\"321\" />"
            + "<Retry count=\"4\" intervalMs=\"33\" /></Global>"
            + Transports
            + "<Devices><Device id=\"d1\" transport=\"tcp1\" pointSet=\"ps1\" unitId=\"1\" /></Devices>"
            + "<PointSets><PointSet id=\"ps1\"><Points><Point id=\"p\" address=\"0\" intervalMs=\"100\" /></Points></PointSet></PointSets>"
            + "</SamplerConfig>";

        var config = SamplerConfigLoader.Load(XDocument.Parse(xml), Directory.GetCurrentDirectory());
        _link.ClearCalls();
        _engine = new SamplerEngine(config, new Dictionary<string, IModbusLink> { ["tcp1"] = _link });
        _engine.Start();
        Thread.Sleep(300);

        var read = Reads().First();
        Assert.Equal(321, read.TimeoutMs);
        Assert.Equal(4, read.Retries);
        Assert.Equal(33, read.RetryIntervalMs);
    }

    [Fact]
    public void Transport_retry_is_the_middle_priority_layer_at_runtime()
    {
        // 链路级 <Retry> 覆盖全局；设备未写 → 用链路值
        Start("<PointSet id=\"ps1\"><Points><Point id=\"p\" address=\"0\" intervalMs=\"100\" /></Points></PointSet>",
            transports: "<Transports><Transport id=\"tcp1\" host=\"127.0.0.1\">"
                        + "<Retry count=\"3\" intervalMs=\"21\" /></Transport></Transports>",
            globalExtra: "<Retry count=\"9\" intervalMs=\"99\" />");

        Thread.Sleep(300);

        var read = Reads().First();
        Assert.Equal(3, read.Retries);
        Assert.Equal(21, read.RetryIntervalMs);
    }
}
