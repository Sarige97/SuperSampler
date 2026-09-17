using System;
using System.Collections.Generic;
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
/// 写管道矩阵（本轮重测：四态判定 / verify 回读 / 写编码 / 范围与类型容量 / 点动脉冲 / 审计 / 并发与生命周期）。
/// 全部走 <see cref="FakeModbusLink"/>（确定性，无端口、无线程），断言**实际发出的寄存器值**与调用次数，
/// 覆盖条目对应 <c>_testplan/模块重测-写管道门面-方案.md</c> §一–§七、§九。
/// </summary>
/// <remarks>
/// 与既有 <c>WritePipelineTests</c>（四态主分支 7 例）和 <c>FacadeWriteGapTests</c>（G-W/G-F 族）**不重复**：
/// 本类补的是逐态触发条件的穷举、编码线上值、类型容量与区可写性、脉冲与 verify 组合、
/// 退避与生命周期口径、以及本轮缺陷 D61–D66 的回归证据。
/// </remarks>
public class WritePipelineMatrixTests
{
    // ═══════════════ 装配 ═══════════════

    private static SamplerConfiguration Cfg(string pointsXml, string globalExtra = "", string devicesXml = "",
        string? transportsXml = null)
        => SamplerConfigLoader.Load(
            XDocument.Parse(string.Format(XML, globalExtra,
                transportsXml ?? """<Transports><Transport id="tcp1" host="127.0.0.1" /></Transports>""",
                devicesXml == string.Empty
                    ? """<Devices><Device id="d1" transport="tcp1" pointSet="ps1" unitId="7" /></Devices>"""
                    : devicesXml,
                pointsXml)),
            Directory.GetCurrentDirectory());

    private static SamplerEngine Engine(FakeModbusLink link, string pointsXml, string globalExtra = "")
        => new(Cfg(pointsXml, globalExtra), new Dictionary<string, IModbusLink> { ["tcp1"] = link });

    /// <summary>排一次成功写应答。</summary>
    private static void QueueWrite(FakeModbusLink link, bool multi = false, int times = 1)
    {
        for (var i = 0; i < times; i++)
        {
            if (multi) link.WriteMultiReplies.Enqueue(ModbusReply.Ok(Array.Empty<ushort>(), 1));
            else link.WriteSingleReplies.Enqueue(ModbusReply.Ok(Array.Empty<ushort>(), 1));
        }
    }

    private static IReadOnlyList<FakeLinkCall> Writes(FakeModbusLink link) => link.Calls.Where(c => c.IsWrite).ToList();

    private static IReadOnlyList<FakeLinkCall> Reads(FakeModbusLink link) => link.Calls.Where(c => c.IsRead).ToList();

    private static ushort[] Words(PointValue value) => Assert.IsType<ushort[]>(Assert.IsType<PointValue>(value).Value);

    private const string XML = """
        <SamplerConfig schemaVersion="3.0">
          <Global nullText="--">{0}</Global>
          {1}
          {2}
          <PointSets><PointSet id="ps1"><Points>{3}</Points></PointSet></PointSets>
        </SamplerConfig>
        """;

    // 点位定义（同一份 XML 里放多个点位，按 id 取用）
    private const string PLAIN = """<Point id="wr" address="0" access="write" />""";
    private const string RANGE = """<Point id="wr" address="0" access="write"><Write min="0" max="10" /></Point>""";
    private const string READONLY = """<Point id="wr" address="0" />""";
    private const string BIT2 = """<Point id="wr" address="0" access="write" bit="2" />""";
    private const string BITRANGE = """<Point id="wr" address="0" access="write" bitRange="2-4" />""";
    private const string BIT_VERIFY = """<Point id="wr" address="0" access="write" bit="2"><Write verify="true" /></Point>""";
    private const string VERIFY = """<Point id="wr" address="0" access="write"><Write verify="true" /></Point>""";
    private const string PULSE = """<Point id="wr" address="0" access="write"><Write pulseMs="120" /></Point>""";
    private const string PULSE_VERIFY =
        """<Point id="wr" address="0" access="write"><Write pulseMs="120" verify="true" /></Point>""";

    // ═══════════════ 一、四态判定 ═══════════════

    /// <summary>
    /// Succeeded：一次单字写、无回读、无错误；调用明细带齐地址/从站/超时/重试预算（findings D61：写不得带重试预算）。
    /// </summary>
    [Fact]
    public async Task Succeeded_write_sends_one_single_write_and_touches_nothing_else()
    {
        var link = new FakeModbusLink();
        QueueWrite(link);
        using var engine = Engine(link, PLAIN, """<Polling requestTimeoutMs="1500" />""");

        var result = await engine.SetValueAsync("d1", "wr", 5);

        Assert.Equal(WriteOutcome.Succeeded, result.Outcome);
        Assert.Null(result.Error);
        Assert.False(result.Readback.HasValue);       // 无 verify → 不回读
        Assert.Equal(1, link.WriteSingleCallCount);
        Assert.Equal(0, link.ReadCalls);

        var call = Assert.Single(Writes(link));
        Assert.True(call.IsWrite);
        Assert.False(call.IsMulti);                   // 单字走 06
        Assert.Equal(DataArea.HoldingRegister, call.Area);
        Assert.Equal(0, call.Address);
        Assert.Equal(1, call.Count);
        Assert.Equal(7, call.UnitId);
        Assert.Equal(1500, call.TimeoutMs);
        Assert.Equal(0, call.Retries);                // findings D61
        Assert.Equal(new ushort[] { 5 }, link.WriteSingleValues);
    }

    /// <summary>
    /// Rejected 的三类触发条件（范围 / 可写性 / 编码）都必须在**下发之前**返回：链路一次调用都不能有
    /// （连读-改-写的读也不许有）。docs/04 §3「Rejected = 未发出任何通讯」。
    /// </summary>
    [Fact]
    public async Task Rejected_covers_range_access_and_encode_without_any_link_call()
    {
        var cases = new (string Name, string PointXml, object? Value, string ReasonKey)[]
        {
            ("范围超上限", RANGE, 11, "ss.reason.aboveMax"),
            ("范围超下限", RANGE, -1, "ss.reason.belowMin"),
            ("只读点（access=read）", READONLY, 1, "ss.reason.notWritable"),
            ("位置区点（bit 但 access=read）", """<Point id="wr" address="0" bit="2" />""", true, "ss.reason.notWritable"),
            // 只读区（input/discrete）+ access=write 的组合已由 CGV-35 在加载期拒绝（findings D66），
            // 经加载器构造不出来 → 引擎侧的区守卫（D65）成为纯纵深防御，不再有可达的用例入口。
            ("文本给数值点", PLAIN, "abc", "ss.reason.encode"),
            ("null 值", PLAIN, null, "ss.reason.encode"),
            ("任意对象", PLAIN, new object(), "ss.reason.encode"),
            ("位点非法值", BIT2, "abc", "ss.reason.encode"),
            ("位域点非法值", BITRANGE, "abc", "ss.reason.encode"),
            ("整型溢出", PLAIN, 70000, "ss.reason.encode"),
        };

        foreach (var item in cases)
        {
            var link = new FakeModbusLink();
            using var engine = Engine(link, item.PointXml);

            var result = await engine.SetValueAsync("d1", "wr", item.Value);

            Assert.Equal(WriteOutcome.Rejected, result.Outcome);
            Assert.NotNull(result.Error);
            Assert.Equal("SS.WRITE.REJECTED", result.Error!.Code);
            Assert.Equal(item.ReasonKey, result.Error.MessageKey);
            Assert.False(result.Readback.HasValue);
            Assert.Empty(link.Calls);     // 关键断言：零通讯（含读）
        }
    }

    /// <summary>范围校验含边界（只有严格越界才拒）。</summary>
    [Fact]
    public async Task Range_boundaries_are_inclusive()
    {
        var link = new FakeModbusLink();
        QueueWrite(link, times: 2);
        using var engine = Engine(link, RANGE);

        var atMin = await engine.SetValueAsync("d1", "wr", 0);
        var atMax = await engine.SetValueAsync("d1", "wr", 10);

        Assert.Equal(WriteOutcome.Succeeded, atMin.Outcome);
        Assert.Equal(WriteOutcome.Succeeded, atMax.Outcome);
        Assert.Equal(new ushort[] { 0, 10 }, link.WriteSingleValues);
    }

    /// <summary>链路失败 = 明确失败（可安全重试），且不做回读（回读只属于超时/verify 路径）。</summary>
    [Fact]
    public async Task Link_failure_is_failed_without_readback()
    {
        var link = new FakeModbusLink();
        link.WriteSingleReplies.Enqueue(ModbusReply.Fail(ModbusFailureKind.LinkDown, 0, "link", 1));
        using var engine = Engine(link, PLAIN);

        var result = await engine.SetValueAsync("d1", "wr", 5);

        Assert.Equal(WriteOutcome.Failed, result.Outcome);
        Assert.Equal("MODBUS.LINK", result.Error!.Code);
        Assert.Equal("ss.error.writeFailed", result.Error.MessageKey);
        Assert.Equal(1, link.WriteSingleCallCount);
        Assert.Equal(0, link.ReadCalls);
        Assert.False(result.Readback.HasValue);
    }

    /// <summary>
    /// 设备异常码（02 非法地址 / 03 非法数据值 / 04 从站故障）→ 都是 Failed（明确失败，可安全重试），
    /// 一次请求发完即回（永久码绝不重试），错误码原样带出。
    /// </summary>
    [Theory]
    [InlineData(0x02)]
    [InlineData(0x03)]
    [InlineData(0x04)]
    public async Task Protocol_exception_codes_classify_as_failed_without_retry(byte code)
    {
        var link = new FakeModbusLink();
        link.FaultRules.Add(new FakeFaultRule { ForWrite = true, Kind = ModbusFailureKind.Protocol, ExceptionCode = code });
        using var engine = Engine(link, PLAIN);

        var result = await engine.SetValueAsync("d1", "wr", 5);

        Assert.Equal(WriteOutcome.Failed, result.Outcome);
        Assert.Equal("MODBUS.EXCEPTION." + code.ToString("X2"), result.Error!.Code);
        Assert.Equal(1, link.WriteCallCount);      // 0x02/0x03 是永久码：绝不重试
        Assert.Equal(0, link.ReadCalls);           // 非超时路径不做回读
    }

    /// <summary>
    /// 写超时 = Indeterminate（写可能已生效）：绝不自动重写（写请求恰好 1 次），
    /// 只有一次回读尝试；回读也失败时不给回读值（不确定）。
    /// </summary>
    [Fact]
    public async Task Write_timeout_without_readback_is_indeterminate_and_never_rewrites()
    {
        var link = new FakeModbusLink();
        link.FaultRules.Add(new FakeFaultRule { ForWrite = true, Kind = ModbusFailureKind.Timeout });
        link.DefaultReadData = null;                 // 回读也失败（无读应答 → LinkDown）
        using var engine = Engine(link, PLAIN);

        var result = await engine.SetValueAsync("d1", "wr", 5);

        Assert.Equal(WriteOutcome.Indeterminate, result.Outcome);
        Assert.Equal("MODBUS.TIMEOUT", result.Error!.Code);
        Assert.False(result.Readback.HasValue);
        Assert.Equal(1, link.WriteSingleCallCount);  // GATE-1：绝不自动重写
        Assert.Equal(1, link.ReadCalls);             // 恰好一次回读尝试
    }

    /// <summary>写超时 + 回读一致 → 按成功定论（回读把「不确定」变成「已生效」）。</summary>
    [Fact]
    public async Task Write_timeout_with_matching_readback_concludes_succeeded()
    {
        var link = new FakeModbusLink();
        link.FaultRules.Add(new FakeFaultRule { ForWrite = true, Kind = ModbusFailureKind.Timeout, RemainingCalls = 1 });
        link.ReadReplies.Enqueue(ModbusReply.Ok(new ushort[] { 5 }, 3));
        using var engine = Engine(link, PLAIN);

        var result = await engine.SetValueAsync("d1", "wr", 5);

        Assert.Equal(WriteOutcome.Succeeded, result.Outcome);
        Assert.Equal(1, link.WriteSingleCallCount);
        Assert.Equal(1, link.ReadCalls);
        Assert.Equal((ushort)5, Assert.IsType<ushort>(Assert.IsType<PointValue>(result.Readback).Value));
    }

    /// <summary>写超时 + 回读不一致 → 确认未生效 → Failed（可安全重试），Readback 是设备实际值。</summary>
    [Fact]
    public async Task Write_timeout_with_different_readback_concludes_failed()
    {
        var link = new FakeModbusLink();
        link.FaultRules.Add(new FakeFaultRule { ForWrite = true, Kind = ModbusFailureKind.Timeout, RemainingCalls = 1 });
        link.ReadReplies.Enqueue(ModbusReply.Ok(new ushort[] { 9 }, 3));
        using var engine = Engine(link, PLAIN);

        var result = await engine.SetValueAsync("d1", "wr", 5);

        Assert.Equal(WriteOutcome.Failed, result.Outcome);
        Assert.Equal(1, link.WriteSingleCallCount);
        Assert.Equal((ushort)9, Assert.IsType<ushort>(Assert.IsType<PointValue>(result.Readback).Value));
    }

    /// <summary>
    /// findings D61：写请求的重试预算恒为 0（设备配置的 <c>Retry@count</c> 只作用于**读**）。
    /// 否则驱动会把超时的写重发，「启动」这类命令会被执行两次（docs/02 §6）。
    /// </summary>
    [Fact]
    public async Task Write_requests_carry_zero_retry_budget_while_reads_keep_theirs()
    {
        var link = new FakeModbusLink();
        link.ReadReplies.Enqueue(ModbusReply.Ok(new ushort[] { 0x000F }, 1));  // 位改写第一步的读
        link.ReadReplies.Enqueue(ModbusReply.Ok(new ushort[] { 0x0007 }, 1));  // verify 回读
        QueueWrite(link);
        using var engine = Engine(link, BIT_VERIFY,
            """<Retry count="5" intervalMs="42" />""");

        var result = await engine.SetValueAsync("d1", "wr", true);

        Assert.Equal(WriteOutcome.Succeeded, result.Outcome);
        var write = Assert.Single(Writes(link));
        Assert.Equal(0, write.Retries);                       // 写：一次即回
        Assert.Equal(2, Reads(link).Count);
        Assert.All(Reads(link), read => Assert.Equal(5, read.Retries));      // 读：吃配置预算
        Assert.All(Reads(link), read => Assert.Equal(42, read.RetryIntervalMs));
    }

    /// <summary>链路停用（findings D44）：该链路上的设备没有主站 → Rejected(noChannel)，零通讯。</summary>
    [Fact]
    public async Task Write_on_a_disabled_transport_is_rejected_without_any_call()
    {
        var link = new FakeModbusLink();
        using var engine = new SamplerEngine(
            Cfg(PLAIN, transportsXml:
                """<Transports><Transport id="tcp1" host="127.0.0.1" enabled="false" /></Transports>"""),
            new Dictionary<string, IModbusLink> { ["tcp1"] = link });

        var result = await engine.SetValueAsync("d1", "wr", 5);

        Assert.Equal(WriteOutcome.Rejected, result.Outcome);
        Assert.Equal("ss.reason.noChannel", result.Error!.MessageKey);
        Assert.Empty(link.Calls);
    }

    // ═══════════════ 二、verify=true 回读 ═══════════════

    /// <summary>verify 一致 → Succeeded + 无 mismatch；恰好回读一次。</summary>
    [Fact]
    public async Task Verify_match_reports_no_mismatch_and_returns_the_decoded_readback()
    {
        var link = new FakeModbusLink();
        QueueWrite(link);
        link.ReadReplies.Enqueue(ModbusReply.Ok(new ushort[] { 5 }, 1));
        using var engine = Engine(link, VERIFY);

        var result = await engine.SetValueAsync("d1", "wr", 5);

        Assert.Equal(WriteOutcome.Succeeded, result.Outcome);
        Assert.False(result.VerifyMismatch);
        Assert.Equal((ushort)5, Assert.IsType<ushort>(Assert.IsType<PointValue>(result.Readback).Value));
        Assert.Equal(1, link.ReadCalls);
        Assert.Equal(1, link.WriteCallCount);
    }

    /// <summary>设备钳位（回读 ≠ 写入）→ 通讯确实成功：仍 Succeeded + VerifyMismatch=true，不自动重试。</summary>
    [Fact]
    public async Task Verify_clamp_marks_mismatch_but_stays_succeeded()
    {
        var link = new FakeModbusLink();
        QueueWrite(link);
        link.ReadReplies.Enqueue(ModbusReply.Ok(new ushort[] { 123 }, 1));
        using var engine = Engine(link, VERIFY);

        var result = await engine.SetValueAsync("d1", "wr", 5);

        Assert.Equal(WriteOutcome.Succeeded, result.Outcome);
        Assert.True(result.VerifyMismatch);
        Assert.Equal((ushort)123, Assert.IsType<ushort>(Assert.IsType<PointValue>(result.Readback).Value));
        Assert.Equal(1, link.WriteCallCount);   // 不自动重写
        Assert.Equal(1, link.ReadCalls);
    }

    /// <summary>
    /// findings D7 口径：回读比对用**原始寄存器**，不是工程值。
    /// 方向一：原始字相同但工程值不同（Scale factor=0.3：写 1.0 → 线上 3 → 解码 0.9）→ 不算不一致。
    /// 若退化成浮点比较，本用例必红。
    /// </summary>
    [Fact]
    public async Task Verify_compares_raw_registers_so_equal_words_are_not_a_mismatch()
    {
        var link = new FakeModbusLink();
        QueueWrite(link);
        link.ReadReplies.Enqueue(ModbusReply.Ok(new ushort[] { 3 }, 1));   // 设备原样保持写入的原始 3
        using var engine = Engine(link, """<Point id="wr" address="0" access="write"><Scale factor="0.3" /><Write verify="true" /></Point>""");

        var result = await engine.SetValueAsync("d1", "wr", 1.0);

        Assert.Equal(WriteOutcome.Succeeded, result.Outcome);
        Assert.Equal(new ushort[] { 3 }, link.WriteSingleValues);          // 线上原始值 3（1.0/0.3 取整）
        Assert.False(result.VerifyMismatch);                               // 原始字相同 → 一致
        Assert.Equal(0.9, Assert.IsType<double>(Assert.IsType<PointValue>(result.Readback).Value), 6);
    }

    /// <summary>
    /// findings D7 口径方向二：工程值相同但原始字不同（Scale factor=10 + Clamp high=10：
    /// 写 10 → 线上 1，设备回 3，两者解码都被钳到 10）→ **必须**判不一致。
    /// 浮点比较会漏报这一路。
    /// </summary>
    [Fact]
    public async Task Verify_compares_raw_registers_so_different_words_are_a_mismatch()
    {
        var link = new FakeModbusLink();
        QueueWrite(link);
        link.ReadReplies.Enqueue(ModbusReply.Ok(new ushort[] { 3 }, 1));
        using var engine = Engine(link,
            """<Point id="wr" address="0" access="write"><Scale factor="10"><Clamp mode="both" low="0" high="10" /></Scale><Write verify="true" /></Point>""");

        var result = await engine.SetValueAsync("d1", "wr", 10.0);

        Assert.Equal(WriteOutcome.Succeeded, result.Outcome);
        Assert.Equal(new ushort[] { 1 }, link.WriteSingleValues);
        Assert.Equal(10.0, Assert.IsType<double>(Assert.IsType<PointValue>(result.Readback).Value), 6);
        Assert.True(result.VerifyMismatch);                                // 原始字不同 → 不一致（浮点比较会漏）
    }

    /// <summary>多字点位的回读比对覆盖整串寄存器：末字不同即判不一致。</summary>
    [Fact]
    public async Task Verify_multi_register_readback_compares_the_whole_word_sequence()
    {
        var point = """<Point id="wr" address="0" access="write" dataType="uint32" length="2" swap="abcd"><Write verify="true" /></Point>""";

        var same = new FakeModbusLink();
        QueueWrite(same, multi: true);
        same.ReadReplies.Enqueue(ModbusReply.Ok(new ushort[] { 0x1122, 0x3344 }, 1));
        using (var engine = Engine(same, point))
        {
            var result = await engine.SetValueAsync("d1", "wr", 0x11223344u);
            Assert.Equal(WriteOutcome.Succeeded, result.Outcome);
            Assert.False(result.VerifyMismatch);
            Assert.Equal(new ushort[] { 0x1122, 0x3344 }, same.WriteMultiValues.Single());
        }

        var lastWordOff = new FakeModbusLink();
        QueueWrite(lastWordOff, multi: true);
        lastWordOff.ReadReplies.Enqueue(ModbusReply.Ok(new ushort[] { 0x1122, 0x3345 }, 1));
        using (var engine = Engine(lastWordOff, point))
        {
            var result = await engine.SetValueAsync("d1", "wr", 0x11223344u);
            Assert.Equal(WriteOutcome.Succeeded, result.Outcome);
            Assert.True(result.VerifyMismatch);      // 只有末字不同也必须抓到
        }
    }

    /// <summary>位点 + verify：比对的是读-改-写合并后的整字。</summary>
    [Fact]
    public async Task Verify_on_a_bit_point_compares_the_merged_word()
    {
        var held = new FakeModbusLink();
        held.ReadReplies.Enqueue(ModbusReply.Ok(new ushort[] { 0x0003 }, 1));   // 读-改-写第一步
        held.ReadReplies.Enqueue(ModbusReply.Ok(new ushort[] { 0x0007 }, 1));   // 回读：设备保持了置位
        QueueWrite(held);
        using (var engine = Engine(held, BIT_VERIFY))
        {
            var result = await engine.SetValueAsync("d1", "wr", true);
            Assert.Equal(WriteOutcome.Succeeded, result.Outcome);
            Assert.False(result.VerifyMismatch);
            Assert.Equal(new ushort[] { 0x0007 }, held.WriteSingleValues);
        }

        var clamped = new FakeModbusLink();
        clamped.ReadReplies.Enqueue(ModbusReply.Ok(new ushort[] { 0x0003 }, 1));
        clamped.ReadReplies.Enqueue(ModbusReply.Ok(new ushort[] { 0x0003 }, 1));  // 设备没接受置位
        QueueWrite(clamped);
        using (var engine = Engine(clamped, BIT_VERIFY))
        {
            var result = await engine.SetValueAsync("d1", "wr", true);
            Assert.Equal(WriteOutcome.Succeeded, result.Outcome);
            Assert.True(result.VerifyMismatch);
        }
    }

    /// <summary>verify 的回读失败（链路断）不改判定：Succeeded + 无 mismatch + 无回读值。</summary>
    [Fact]
    public async Task Verify_readback_failure_keeps_succeeded_without_readback()
    {
        var link = new FakeModbusLink();
        QueueWrite(link);
        link.FaultRules.Add(new FakeFaultRule { Kind = ModbusFailureKind.LinkDown });   // 只作用于读
        using var engine = Engine(link, VERIFY);

        var result = await engine.SetValueAsync("d1", "wr", 5);

        Assert.Equal(WriteOutcome.Succeeded, result.Outcome);
        Assert.False(result.VerifyMismatch);
        Assert.False(result.Readback.HasValue);
        Assert.Equal(1, link.ReadCalls);
    }

    /// <summary>verify=false（默认）绝不回读。</summary>
    [Fact]
    public async Task Without_verify_no_readback_request_is_ever_sent()
    {
        var link = new FakeModbusLink();
        QueueWrite(link);
        using var engine = Engine(link, PLAIN);

        var result = await engine.SetValueAsync("d1", "wr", 5);

        Assert.Equal(WriteOutcome.Succeeded, result.Outcome);
        Assert.Equal(0, link.ReadCalls);
        Assert.False(result.Readback.HasValue);
    }

    // ═══════════════ 三、写编码（断言线上实际值） ═══════════════

    /// <summary>整型/浮点的线上字（含字序）：uint16/int16/uint32/float32/float64。</summary>
    [Fact]
    public async Task Writing_encodes_integrals_and_floats_to_the_wire_words()
    {
        var link = new FakeModbusLink();
        QueueWrite(link, times: 2);
        QueueWrite(link, multi: true, times: 5);
        using var engine = Engine(link, string.Concat(
            """<Point id="u16" address="0" access="write" />""",
            """<Point id="i16" address="1" access="write" dataType="int16" />""",
            """<Point id="u32" address="2" access="write" dataType="uint32" length="2" swap="abcd" />""",
            """<Point id="u32w" address="4" access="write" dataType="uint32" length="2" swap="cdab" />""",
            """<Point id="f32" address="6" access="write" dataType="float32" length="2" swap="abcd" />""",
            """<Point id="f32w" address="8" access="write" dataType="float32" length="2" swap="cdab" />""",
            """<Point id="f64" address="10" access="write" dataType="float64" length="4" swap="abcd" />"""));

        Assert.Equal(WriteOutcome.Succeeded, (await engine.SetValueAsync("d1", "u16", 5)).Outcome);
        Assert.Equal(WriteOutcome.Succeeded, (await engine.SetValueAsync("d1", "i16", -1)).Outcome);
        Assert.Equal(WriteOutcome.Succeeded, (await engine.SetValueAsync("d1", "u32", 0x11223344u)).Outcome);
        Assert.Equal(WriteOutcome.Succeeded, (await engine.SetValueAsync("d1", "u32w", 0x11223344u)).Outcome);
        Assert.Equal(WriteOutcome.Succeeded, (await engine.SetValueAsync("d1", "f32", 1.5f)).Outcome);
        Assert.Equal(WriteOutcome.Succeeded, (await engine.SetValueAsync("d1", "f32w", 1.5f)).Outcome);
        Assert.Equal(WriteOutcome.Succeeded, (await engine.SetValueAsync("d1", "f64", Math.PI)).Outcome);

        Assert.Equal((ushort)5, link.WriteSingleValues[0]);
        Assert.Equal((ushort)0xFFFF, link.WriteSingleValues[1]);

        var multi = link.WriteMultiValues;
        Assert.Equal(5, multi.Count);
        Assert.Equal(new ushort[] { 0x1122, 0x3344 }, multi[0]);                       // uint32 大端
        Assert.Equal(new ushort[] { 0x3344, 0x1122 }, multi[1]);                       // swap=word 成对交换
        Assert.Equal(new ushort[] { 0x3FC0, 0x0000 }, multi[2]);                       // 1.5f
        Assert.Equal(new ushort[] { 0x0000, 0x3FC0 }, multi[3]);                       // 1.5f + swap=word
        Assert.Equal(new ushort[] { 0x4009, 0x21FB, 0x5444, 0x2D18 }, multi[4]);       // π（float64 大端）
    }

    /// <summary>float64 的 4 字编码（单独一条，避免和上面的多字队列混在一起）。</summary>
    [Fact]
    public async Task Writing_a_float64_point_sends_four_big_endian_words()
    {
        var link = new FakeModbusLink();
        QueueWrite(link, multi: true);
        using var engine = Engine(link, """<Point id="wr" address="0" access="write" dataType="float64" length="4" swap="abcd" />""");

        var result = await engine.SetValueAsync("d1", "wr", Math.PI);

        Assert.Equal(WriteOutcome.Succeeded, result.Outcome);
        Assert.Equal(new ushort[] { 0x4009, 0x21FB, 0x5444, 0x2D18 }, link.WriteMultiValues.Single());
        Assert.Equal(1, link.WriteMultiCallCount);
        Assert.Equal(0, link.WriteSingleCallCount);
    }

    /// <summary>字符串 / BCD（D42/D59 口径）/ datetime（D55）/ raw（D56）的线上字。</summary>
    [Fact]
    public async Task Writing_encodes_string_bcd_datetime_and_raw_to_the_wire_words()
    {
        var link = new FakeModbusLink();
        QueueWrite(link, times: 1);
        QueueWrite(link, multi: true, times: 6);
        using var engine = Engine(link, string.Concat(
            """<Point id="s" address="0" access="write" dataType="string" length="1"><String encoding="ascii" /></Point>""",
            """<Point id="s2" address="2" access="write" dataType="string" length="2"><String encoding="ascii" /></Point>""",
            """<Point id="bcd" address="4" access="write" dataType="bcd" length="2" swap="abcd"><Bcd digits="8" /></Point>""",
            """<Point id="bcdw" address="6" access="write" dataType="bcd" length="2" swap="cdab"><Bcd digits="8" /></Point>""",
            """<Point id="dt6" address="8" access="write" dataType="datetime" length="6" swap="abcd"><DateTime format="plc6" /></Point>""",
            """<Point id="dts" address="14" access="write" dataType="datetime" length="2" swap="abcd"><DateTime format="unixsec" /></Point>""",
            """<Point id="raw" address="16" access="write" dataType="raw" length="2" />"""));

        Assert.Equal(WriteOutcome.Succeeded, (await engine.SetValueAsync("d1", "s", "ABCD")).Outcome);   // 超长 → 截断
        Assert.Equal(WriteOutcome.Succeeded, (await engine.SetValueAsync("d1", "s2", "AB")).Outcome);
        Assert.Equal(WriteOutcome.Succeeded, (await engine.SetValueAsync("d1", "bcd", 12345678)).Outcome);
        Assert.Equal(WriteOutcome.Succeeded, (await engine.SetValueAsync("d1", "bcdw", 12345678)).Outcome);
        Assert.Equal(WriteOutcome.Succeeded, (await engine.SetValueAsync("d1", "dt6", new DateTime(2026, 9, 16, 10, 30, 15))).Outcome);
        Assert.Equal(WriteOutcome.Succeeded, (await engine.SetValueAsync("d1", "dts", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero))).Outcome);
        Assert.Equal(WriteOutcome.Succeeded, (await engine.SetValueAsync("d1", "raw", new ushort[] { 0x1111, 0x2222 })).Outcome);

        Assert.Equal(new ushort[] { 0x4142 }, link.WriteSingleValues);                                   // "ABCD" → 1 字，截断
        var multi = link.WriteMultiValues;
        Assert.Equal(6, multi.Count);
        Assert.Equal(new ushort[] { 0x4142, 0x0000 }, multi[0]);        // "AB" + 0x00 补齐
        Assert.Equal(new ushort[] { 0x1234, 0x5678 }, multi[1]);        // BCD 十进制字位
        Assert.Equal(new ushort[] { 0x1234, 0x5678 }, multi[2]);        // findings D59：BCD 不吃字序
        Assert.Equal(new ushort[] { 2026, 9, 16, 10, 30, 15 }, multi[3]);
        Assert.Equal(UnixSecWords(1767225600, swapWord: false), multi[4]);
        Assert.Equal(new ushort[] { 0x1111, 0x2222 }, multi[5]);        // raw 原样写回
    }

    private static ushort[] UnixSecWords(long seconds, bool swapWord)
    {
        var hi = (ushort)((seconds >> 16) & 0xFFFF);
        var lo = (ushort)(seconds & 0xFFFF);
        return swapWord ? new[] { lo, hi } : new[] { hi, lo };
    }

    /// <summary>位点读-改-写：只翻目标位，其它位原样保留（先读后写，共两次调用）。</summary>
    [Fact]
    public async Task Bit_write_is_read_modify_write_and_preserves_every_other_bit()
    {
        var link = new FakeModbusLink();
        link.ReadReplies.Enqueue(ModbusReply.Ok(new ushort[] { 0x00A5 }, 1));
        link.ReadReplies.Enqueue(ModbusReply.Ok(new ushort[] { 0x00A5 }, 1));
        QueueWrite(link, times: 2);
        using var engine = Engine(link, BIT2);

        var set = await engine.SetValueAsync("d1", "wr", true);
        var clear = await engine.SetValueAsync("d1", "wr", false);

        Assert.Equal(WriteOutcome.Succeeded, set.Outcome);
        Assert.Equal(WriteOutcome.Succeeded, clear.Outcome);
        Assert.Equal(new ushort[] { 0x00A5 | 0x04, (ushort)(0x00A5 & ~0x04) }, link.WriteSingleValues);
        Assert.Equal(2, link.ReadCalls);                 // 每次写都先读
        Assert.Equal(new[] { "R", "W", "R", "W" }, link.Calls.Select(c => c.IsWrite ? "W" : "R"));   // 先读后写
        Assert.Equal(2, Writes(link).Count);
    }

    /// <summary>位域读-改-写：只有 [from,to] 段被替换（含端点），其它位不变。</summary>
    [Fact]
    public async Task Bit_range_write_replaces_only_the_declared_field()
    {
        var link = new FakeModbusLink();
        link.ReadReplies.Enqueue(ModbusReply.Ok(new ushort[] { 0x00F3 }, 1));
        QueueWrite(link);
        using var engine = Engine(link, BITRANGE);

        var result = await engine.SetValueAsync("d1", "wr", 3);

        Assert.Equal(WriteOutcome.Succeeded, result.Outcome);
        // [2,4] 掩码 0x1C：0xF3 & ~0x1C = 0xE3 → | (3<<2 = 0x0C) = 0xEF
        Assert.Equal(new ushort[] { 0x00EF }, link.WriteSingleValues);
        Assert.Equal(1, link.ReadCalls);
    }

    /// <summary>1 个寄存器走单写（06），≥2 个寄存器走多写（10）——且不会退化成 N 次单写。</summary>
    [Fact]
    public async Task Single_word_points_use_write_single_and_multi_word_points_use_write_multi()
    {
        var link = new FakeModbusLink();
        QueueWrite(link);
        QueueWrite(link, multi: true, times: 2);
        using var engine = Engine(link, string.Concat(
            """<Point id="one" address="0" access="write" />""",
            """<Point id="two" address="1" access="write" dataType="uint32" length="2" swap="abcd" />""",
            """<Point id="six" address="3" access="write" dataType="string" length="3"><String encoding="ascii" /></Point>"""));

        await engine.SetValueAsync("d1", "one", 1);
        await engine.SetValueAsync("d1", "two", 2u);
        await engine.SetValueAsync("d1", "six", "ABC");

        Assert.Equal(1, link.WriteSingleCallCount);
        Assert.Equal(2, link.WriteMultiCallCount);
        Assert.Equal(1, Writes(link).Count(c => !c.IsMulti));
        Assert.Equal(new[] { 0, 1, 3 }, Writes(link).Select(c => c.Address));      // 各点各自的起始地址
        Assert.Equal(new[] { 1, 2, 3 }, Writes(link).Select(c => c.Count));          // 1 字 / 2 字 / 3 字
    }

    // ═══════════════ 四、范围与值口径 ═══════════════

    /// <summary>
    /// findings D63：超出目标整型可表示范围的值在下发前被拒（原本会静默取模回绕并报 Succeeded）。
    /// </summary>
    [Fact]
    public async Task Out_of_type_range_integral_values_are_rejected_before_encoding()
    {
        var cases = new (string Name, string PointXml, object? Value)[]
        {
            ("uint16 ← 70000", PLAIN, 70000),
            ("uint16 ← -1", PLAIN, -1),
            ("int16 ← -40000", """<Point id="wr" address="0" access="write" dataType="int16" />""", -40000),
            ("int16 ← 40000", """<Point id="wr" address="0" access="write" dataType="int16" />""", 40000),
            ("uint32 ← -1", """<Point id="wr" address="0" access="write" dataType="uint32" length="2" swap="abcd" />""", -1),
            ("uint16 ← NaN", PLAIN, double.NaN),
            ("uint16 ← +Inf", PLAIN, double.PositiveInfinity),
            ("uint16 ← -Inf", PLAIN, double.NegativeInfinity),
            ("uint16 ← 数字字符串 70000", PLAIN, "70000"),
        };

        foreach (var item in cases)
        {
            var link = new FakeModbusLink();
            using var engine = Engine(link, item.PointXml);

            var result = await engine.SetValueAsync("d1", "wr", item.Value);

            Assert.Equal(WriteOutcome.Rejected, result.Outcome);
            Assert.Equal("ss.reason.encode", result.Error!.MessageKey);
            Assert.Empty(link.Calls);
        }
    }

    /// <summary>类型容量边界是闭区间：uint16 写 65535 通过、写 65536 被拒。</summary>
    [Fact]
    public async Task Type_capacity_boundaries_are_inclusive()
    {
        var link = new FakeModbusLink();
        QueueWrite(link);
        using var engine = Engine(link, PLAIN);

        var ok = await engine.SetValueAsync("d1", "wr", 65535);
        var over = await engine.SetValueAsync("d1", "wr", 65536);

        Assert.Equal(WriteOutcome.Succeeded, ok.Outcome);
        Assert.Equal(new ushort[] { 65535 }, link.WriteSingleValues);
        Assert.Equal(WriteOutcome.Rejected, over.Outcome);
    }

    /// <summary>
    /// 带缩放的整型点按**原始值**判容量：factor=0.1 时写 1000（原始 10000）通过、写 10000（原始 100000）被拒。
    /// </summary>
    [Fact]
    public async Task Scaled_integral_points_check_the_reverse_scaled_raw_value()
    {
        var link = new FakeModbusLink();
        QueueWrite(link);
        using var engine = Engine(link, """<Point id="wr" address="0" access="write"><Scale factor="0.1" /></Point>""");

        var ok = await engine.SetValueAsync("d1", "wr", 1000.0);
        var over = await engine.SetValueAsync("d1", "wr", 10000.0);

        Assert.Equal(WriteOutcome.Succeeded, ok.Outcome);
        Assert.Equal(new ushort[] { 10000 }, link.WriteSingleValues);
        Assert.Equal(WriteOutcome.Rejected, over.Outcome);
    }

    /// <summary>小数写整型点：落位取整（AwayFromZero），不是截断。</summary>
    [Fact]
    public async Task Fractional_values_on_integral_points_round_away_from_zero()
    {
        var link = new FakeModbusLink();
        QueueWrite(link, times: 3);
        QueueWrite(link, times: 1);
        using var engine = Engine(link, string.Concat(PLAIN,
            """<Point id="i16" address="1" access="write" dataType="int16" />"""));

        await engine.SetValueAsync("d1", "wr", 1.5);
        await engine.SetValueAsync("d1", "wr", 1.4);
        await engine.SetValueAsync("d1", "wr", 0.5);
        await engine.SetValueAsync("d1", "i16", -1.5);

        Assert.Equal(new ushort[] { 2, 1, 1, 0xFFFE }, link.WriteSingleValues);
    }

    /// <summary>数字字符串与布尔都能写进数值点（TryNumber / Convert.ToDouble 口径）。</summary>
    [Fact]
    public async Task Numeric_strings_and_booleans_are_accepted_for_numeric_points()
    {
        var link = new FakeModbusLink();
        QueueWrite(link, times: 3);
        using var engine = Engine(link, PLAIN);

        var text = await engine.SetValueAsync("d1", "wr", "123");
        var on = await engine.SetValueAsync("d1", "wr", true);
        var off = await engine.SetValueAsync("d1", "wr", 0);

        Assert.Equal(WriteOutcome.Succeeded, text.Outcome);
        Assert.Equal(WriteOutcome.Succeeded, on.Outcome);
        Assert.Equal(new ushort[] { 123, 1, 0 }, link.WriteSingleValues);
        Assert.Equal(WriteOutcome.Succeeded, off.Outcome);
    }

    /// <summary>空字符串不是「null 值」：按空文本编码（全补齐字节）。</summary>
    [Fact]
    public async Task Empty_string_encodes_to_padding_bytes()
    {
        var link = new FakeModbusLink();
        QueueWrite(link, multi: true);
        using var engine = Engine(link, """<Point id="wr" address="0" access="write" dataType="string" length="2"><String encoding="ascii" /></Point>""");

        var result = await engine.SetValueAsync("d1", "wr", string.Empty);

        Assert.Equal(WriteOutcome.Succeeded, result.Outcome);
        Assert.Equal(new ushort[] { 0x0000, 0x0000 }, link.WriteMultiValues.Single());
    }

    /// <summary>
    /// raw 点的写入值必须是 ushort[]（线上寄存器序列，findings D56）：数字/文本/对象一律 Rejected + 零通讯
    /// （此前数字会被当整型拆字写出——设备上落下 [0,123]，与 raw 的读写语义完全对不上）。
    /// </summary>
    [Fact]
    public async Task Raw_point_write_rejects_a_non_register_sequence()
    {
        var link = new FakeModbusLink();
        using var engine = Engine(link, """<Point id="wr" address="0" access="write" dataType="raw" length="2" />""");

        foreach (var value in new object?[] { 123, "123", new object(), new List<ushort> { 1, 2 } })
        {
            var result = await engine.SetValueAsync("d1", "wr", value);

            Assert.Equal(WriteOutcome.Rejected, result.Outcome);
            Assert.Equal("ss.reason.encode", result.Error!.MessageKey);
        }

        Assert.Empty(link.Calls);
    }

    // ═══════════════ 五、点动脉冲 ═══════════════

    /// <summary>写 true → 先写 1，等 pulseMs，再写回 0（两次同地址/同从站）；等待时长 ≥ pulseMs。</summary>
    [Fact]
    public async Task Pulse_writes_true_then_auto_resets_after_the_configured_delay()
    {
        var link = new FakeModbusLink();
        QueueWrite(link, times: 2);
        using var engine = Engine(link, PULSE);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = await engine.SetValueAsync("d1", "wr", true);
        watch.Stop();

        Assert.Equal(WriteOutcome.Succeeded, result.Outcome);
        Assert.Equal(new ushort[] { 1, 0 }, link.WriteSingleValues);
        Assert.True(watch.ElapsedMilliseconds >= 120, "点动间隔必须真的等 ≥ pulseMs，实际 " + watch.ElapsedMilliseconds + "ms");
        var writes = Writes(link);
        Assert.Equal(2, writes.Count);
        Assert.All(writes, c => Assert.Equal(0, c.Address));
        Assert.All(writes, c => Assert.Equal(7, c.UnitId));
        Assert.All(writes, c => Assert.Equal(DataArea.HoldingRegister, c.Area));
    }

    /// <summary>写 false 不触发脉冲（点动只在真值上生效）。</summary>
    [Fact]
    public async Task Pulse_false_is_a_single_write_without_the_reset()
    {
        var link = new FakeModbusLink();
        QueueWrite(link);
        using var engine = Engine(link, PULSE);

        var result = await engine.SetValueAsync("d1", "wr", false);

        Assert.Equal(WriteOutcome.Succeeded, result.Outcome);
        Assert.Equal(new ushort[] { 0 }, link.WriteSingleValues);
        Assert.Equal(1, link.WriteCallCount);
    }

    /// <summary>
    /// findings D64：脉冲点 + verify 的回读必须发生在**自动归零之前**——
    /// 否则框架自己的归零会让正常设备被判 VerifyMismatch=true。
    /// 调用序断言 R/W 序列为 W,R,W。
    /// </summary>
    [Fact]
    public async Task Pulse_with_verify_reads_back_before_the_automatic_reset()
    {
        var link = new FakeModbusLink();
        QueueWrite(link, times: 2);
        link.ReadReplies.Enqueue(ModbusReply.Ok(new ushort[] { 1 }, 1));   // 设备保持了点动值
        using var engine = Engine(link, PULSE_VERIFY);

        var result = await engine.SetValueAsync("d1", "wr", true);

        Assert.Equal(WriteOutcome.Succeeded, result.Outcome);
        Assert.Equal(new[] { "W", "R", "W" }, link.Calls.Select(c => c.IsWrite ? "W" : "R"));
        Assert.False(result.VerifyMismatch);                                // 归零不是「设备没接受」
        Assert.Equal(new ushort[] { 1, 0 }, link.WriteSingleValues);
    }

    /// <summary>脉冲 + verify：设备确实没接受点动值 → 仍然如实报不一致。</summary>
    [Fact]
    public async Task Pulse_with_verify_still_reports_a_real_mismatch()
    {
        var link = new FakeModbusLink();
        QueueWrite(link, times: 2);
        link.ReadReplies.Enqueue(ModbusReply.Ok(new ushort[] { 0 }, 1));   // 设备没置位
        using var engine = Engine(link, PULSE_VERIFY);

        var result = await engine.SetValueAsync("d1", "wr", true);

        Assert.Equal(WriteOutcome.Succeeded, result.Outcome);
        Assert.True(result.VerifyMismatch);
    }

    /// <summary>机械手取件语义：写 1 → 机械手自保持 → 框架归零；归零写回失败也不改变「点动信号已发出」的结论。</summary>
    [Fact]
    public async Task Pulse_reset_failure_does_not_turn_the_pulse_into_a_failure()
    {
        var link = new FakeModbusLink();
        link.WriteSingleReplies.Enqueue(ModbusReply.Ok(Array.Empty<ushort>(), 1));      // 点动写成功
        link.WriteSingleReplies.Enqueue(ModbusReply.Fail(ModbusFailureKind.LinkDown, 0, "reset failed", 1));
        using var engine = Engine(link, PULSE);

        var result = await engine.SetValueAsync("d1", "wr", true);

        Assert.Equal(WriteOutcome.Succeeded, result.Outcome);
        Assert.Equal(new ushort[] { 1, 0 }, link.WriteSingleValues);   // 归零照发
        Assert.Equal(2, link.WriteCallCount);
    }

    /// <summary>取消令牌在写开始前已取消 → 任务取消，一次通讯都不发（不留半途状态）。</summary>
    [Fact]
    public async Task An_already_cancelled_token_prevents_every_communication()
    {
        var link = new FakeModbusLink();
        QueueWrite(link, times: 2);
        using var engine = Engine(link, PULSE);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => engine.SetValueAsync("d1", "wr", true, new ActingUser("op", "operator"), cts.Token));

        Assert.Empty(link.Calls);
    }

    /// <summary>
    /// 点动脉冲等待期间取消：**绝不把线圈留在 ON**——归零写回照发（安全优先），
    /// 结果仍按点动本身是否成功给（口径锁定：取消只在「开始前」短路）。
    /// </summary>
    [Fact]
    public async Task Cancelling_during_the_pulse_still_leaves_the_coil_off()
    {
        var link = new FakeModbusLink();
        QueueWrite(link, times: 2);
        using var engine = Engine(link, PULSE);
        using var cts = new CancellationTokenSource();

        var task = engine.SetValueAsync("d1", "wr", true, new ActingUser("op", "operator"), cts.Token);
        Assert.True(SpinWait.SpinUntil(() => link.WriteCallCount >= 1, 2000), "点动写必须先发出");
        cts.Cancel();                                   // 脉冲等待期间取消

        var result = await task;

        Assert.Equal(WriteOutcome.Succeeded, result.Outcome);
        Assert.Equal(new ushort[] { 1, 0 }, link.WriteSingleValues);   // 归零没有被取消打断
    }

    // ═══════════════ 六、审计事件 ═══════════════

    /// <summary>写审计：用户 / 点位 / 值 / 结果 / 拒绝原因齐全；无 user 重载以 Local 记账。</summary>
    [Fact]
    public async Task Write_audit_events_carry_user_point_value_outcome_and_reason()
    {
        var link = new FakeModbusLink();
        QueueWrite(link, times: 1);
        link.WriteSingleReplies.Enqueue(ModbusReply.Fail(ModbusFailureKind.Protocol, 0x02, "ro", 1));
        QueueWrite(link, times: 1);
        using var engine = Engine(link, string.Concat(RANGE, """<Point id="ro" address="1" access="write" />"""));

        var events = new List<PointWrittenEvent>();
        engine.Bus.Subscribe<PointWrittenEvent>(e => { lock (events) events.Add(e.Body); }, DeliveryMode.Inline);

        var ok = await engine.SetValueAsync("d1", "wr", 5, new ActingUser("op-a", "operator"));
        var rejected = await engine.SetValueAsync("d1", "wr", 99, new ActingUser("op-b", "operator"));
        var failed = await engine.SetValueAsync("d1", "ro", 1, new ActingUser("op-c", "maintenance"));
        var local = await engine.SetValueAsync("d1", "wr", 6);

        Assert.Equal(WriteOutcome.Succeeded, ok.Outcome);
        Assert.Equal(WriteOutcome.Rejected, rejected.Outcome);
        Assert.Equal(WriteOutcome.Failed, failed.Outcome);
        Assert.Equal(WriteOutcome.Succeeded, local.Outcome);

        List<PointWrittenEvent> snapshot;
        lock (events) snapshot = events.ToList();
        Assert.Equal(4, snapshot.Count);                       // 成败都发
        Assert.Equal("op-a", snapshot[0].User);
        Assert.Equal("d1", snapshot[0].DeviceId);
        Assert.Equal("wr", snapshot[0].PointId);
        Assert.Equal("Succeeded", snapshot[0].Outcome);
        Assert.Equal(5, Convert.ToInt32(snapshot[0].Value));
        Assert.Null(snapshot[0].Message);

        Assert.Equal("op-b", snapshot[1].User);
        Assert.Equal("Rejected", snapshot[1].Outcome);
        Assert.Equal("ss.reason.aboveMax", snapshot[1].Message);   // 拒绝原因进审计

        Assert.Equal("op-c", snapshot[2].User);
        Assert.Equal("Failed", snapshot[2].Outcome);
        Assert.Equal("ss.error.writeFailed", snapshot[2].Message);

        Assert.Equal("Local", snapshot[3].User);                   // 框架内置身份
        Assert.Equal(EventCategory.Write, snapshot[3].Category);
    }

    /// <summary>null user（编程错误）抛 ArgumentNullException，不下发。</summary>
    [Fact]
    public async Task A_null_acting_user_is_a_programming_error()
    {
        var link = new FakeModbusLink();
        using var engine = Engine(link, PLAIN);

        await Assert.ThrowsAsync<ArgumentNullException>(() => engine.SetValueAsync("d1", "wr", 1, (ActingUser)null!));
        Assert.Empty(link.Calls);
    }

    // ═══════════════ 七、并发与生命周期 ═══════════════

    /// <summary>
    /// 同一点位并发写：全部有结论、不抛、不串值（链路收到 N 次写）；写**不改写缓存**
    /// （写入值只对设备生效，门面读到的是采集侧的值——宿主需回读判断）。
    /// </summary>
    [Fact]
    public async Task Concurrent_writes_to_one_point_all_conclude_and_never_touch_the_cache()
    {
        var link = new FakeModbusLink();
        QueueWrite(link, times: 40);
        using var engine = Engine(link, PLAIN);

        var results = await Task.WhenAll(Enumerable.Range(0, 40)
            .Select(i => engine.SetValueAsync("d1", "wr", (i % 60000) + 1)));

        Assert.All(results, r => Assert.Equal(WriteOutcome.Succeeded, r.Outcome));
        Assert.Equal(40, link.WriteSingleCallCount);
        Assert.Equal(40, link.WriteSingleValues.Distinct().Count());           // 40 个不同的值，一个都没串
        Assert.Equal(40, link.WriteSingleValues.Count);

        var cached = engine.GetValueDetail("d1", "wr");
        Assert.False(cached.IsGood);                                          // 写不改缓存
        Assert.Equal("ss.reason.notInCache", cached.Reason);
        Assert.Null(engine.GetValueAge("d1", "wr"));                           // 写不算「成功采集」
        Assert.Equal("--", engine.GetValue("d1", "wr"));
    }

    /// <summary>
    /// 写失败进入退避后：**写不进退避闸门**（照发，不静默丢弃宿主的操作）；写成功一次即把退避清零。
    /// 口径锁定 + 事件证据（观察项 W53：文档只说「退避期间不发任何请求」，未区分轮询与宿主写入）。
    /// </summary>
    [Fact]
    public async Task Writes_are_not_gated_by_backoff_and_a_successful_write_clears_it()
    {
        var link = new FakeModbusLink();
        link.FaultRules.Add(new FakeFaultRule { ForWrite = true, Kind = ModbusFailureKind.Timeout, RemainingCalls = 1 });
        QueueWrite(link);
        using var engine = Engine(link, PLAIN, """<Reconnect delays="60000" />""");

        var entered = new List<BackoffEnteredEvent>();
        var recovered = new List<BackoffRecoveredEvent>();
        engine.Bus.Subscribe<BackoffEnteredEvent>(e => { lock (entered) entered.Add(e.Body); }, DeliveryMode.Inline);
        engine.Bus.Subscribe<BackoffRecoveredEvent>(e => { lock (recovered) recovered.Add(e.Body); }, DeliveryMode.Inline);

        var first = await engine.SetValueAsync("d1", "wr", 1);
        Assert.Equal(WriteOutcome.Indeterminate, first.Outcome);      // 写超时 + 回读失败
        lock (entered)
        {
            // 单设备链路退避：先设备级，再按「同链路全部从站都退避」升级链路级（ADR D40 双保险）
            Assert.Contains(entered, e => e.Scope == BackoffScope.Device && e.ReasonCode == "MODBUS.TIMEOUT");
            Assert.Contains(entered, e => e.Scope == BackoffScope.Link);
        }

        var second = await engine.SetValueAsync("d1", "wr", 2);       // 退避中：仍然下发

        Assert.Equal(WriteOutcome.Succeeded, second.Outcome);
        Assert.Equal(2, link.WriteCallCount);
        lock (recovered) Assert.NotEmpty(recovered);                  // 成功一次即归零（设备级 + 链路级都收到恢复）
    }

    /// <summary>
    /// 生命周期口径（docs/07 PROD-2 授权按实现口径固化）：写路径不做 Start/Stop/Dispose 门禁——
    /// 未 Start 的引擎照发（主站在构造期建立），Stop/Dispose 后仍会尝试下发。
    /// 真实链路下成败由链路状态决定（观察项 W54）。
    /// </summary>
    [Fact]
    public async Task Writes_before_start_and_after_stop_are_still_dispatched()
    {
        var link = new FakeModbusLink();
        QueueWrite(link, times: 3);
        var engine = Engine(link, PLAIN);
        try
        {
            var beforeStart = await engine.SetValueAsync("d1", "wr", 1);
            engine.Start();
            engine.Stop();
            var afterStop = await engine.SetValueAsync("d1", "wr", 2);
            engine.Dispose();
            var afterDispose = await engine.SetValueAsync("d1", "wr", 3);

            Assert.Equal(WriteOutcome.Succeeded, beforeStart.Outcome);
            Assert.Equal(WriteOutcome.Succeeded, afterStop.Outcome);
            Assert.Equal(WriteOutcome.Succeeded, afterDispose.Outcome);
            Assert.Equal(new ushort[] { 1, 2, 3 }, link.WriteSingleValues);
        }
        finally
        {
            engine.Dispose();
        }
    }

    /// <summary>在途写与 Dispose 竞态（docs/07 PROD-4）：不挂死、不抛 NRE（ObjectDisposedException 可接受）。</summary>
    [Fact]
    public async Task Dispose_racing_an_in_flight_write_neither_hangs_nor_throws_nre()
    {
        var link = new FakeModbusLink();
        link.FaultRules.Add(new FakeFaultRule { ForWrite = true, Kind = ModbusFailureKind.Timeout, DelayMs = 300, RemainingCalls = 1 });
        var engine = Engine(link, PLAIN);
        try
        {
            var write = engine.SetValueAsync("d1", "wr", 1);
            engine.Dispose();                                   // 与在途写并发：总线/主站/调度一起拆

            var completed = await Task.WhenAny(write, Task.Delay(5000));
            Assert.True(ReferenceEquals(write, completed), "写任务不能在 Dispose 竞态中挂死");

            var thrown = await Record.ExceptionAsync(() => write);
            Assert.True(thrown is null || thrown is ObjectDisposedException,
                "只允许业务结果或 ObjectDisposedException，实际 " + thrown?.GetType().Name);
        }
        finally
        {
            engine.Dispose();
        }
    }

    /// <summary>同一 pointId 在两条设备上不串台：各写各的从站号与地址。</summary>
    [Fact]
    public async Task The_same_point_id_on_two_devices_writes_to_its_own_slave()
    {
        var link = new FakeModbusLink();
        QueueWrite(link, times: 2);
        using var engine = new SamplerEngine(
            Cfg(PLAIN,
                devicesXml: """<Devices><Device id="d1" transport="tcp1" pointSet="ps1" unitId="7" /><Device id="d2" transport="tcp1" pointSet="ps1" unitId="8" /></Devices>"""),
            new Dictionary<string, IModbusLink> { ["tcp1"] = link });

        await engine.SetValueAsync("d1", "wr", 11);
        await engine.SetValueAsync("d2", "wr", 22);

        Assert.Equal(new byte[] { 7, 8 }, Writes(link).Select(c => c.UnitId));
        Assert.Equal(new ushort[] { 11, 22 }, link.WriteSingleValues);
    }

    // ═══════════════ 八、异常与移交证据 ═══════════════

    /// <summary>id 是 null / 空串 / 超长 → KeyNotFoundException（配置错误尽早暴露，不是 NRE）。</summary>
    [Fact]
    public async Task Unknown_and_malformed_ids_throw_key_not_found()
    {
        var link = new FakeModbusLink();
        using var engine = Engine(link, PLAIN);

        await Assert.ThrowsAsync<KeyNotFoundException>(() => engine.SetValueAsync("d1", "nope", 1));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => engine.SetValueAsync("nope", "wr", 1));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => engine.SetValueAsync("d1", null!, 1));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => engine.SetValueAsync(null!, "wr", 1));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => engine.SetValueAsync("d1", string.Empty, 1));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => engine.SetValueAsync("d1", new string('x', 4096), 1));
        Assert.Empty(link.Calls);
    }

    /// <summary>回读应答延迟（设备慢）：有超时上界、不挂死（假链路按 DelayMs 模拟）。</summary>
    [Fact]
    public async Task A_slow_readback_does_not_hang_the_pipeline()
    {
        var link = new FakeModbusLink();
        QueueWrite(link);
        link.FaultRules.Add(new FakeFaultRule { Kind = ModbusFailureKind.Timeout, DelayMs = 200 });
        using var engine = Engine(link, VERIFY);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = await engine.SetValueAsync("d1", "wr", 5);
        watch.Stop();

        Assert.Equal(WriteOutcome.Succeeded, result.Outcome);
        Assert.False(result.Readback.HasValue);
        Assert.True(watch.ElapsedMilliseconds < 5000, "回读失败也必须在上界内返回，实际 " + watch.ElapsedMilliseconds + "ms");
    }

    /// <summary>
    /// findings D66 / CGV-35：`access="write"` 落在**只读区**（input/discrete）**加载期必须报错**——
    /// Modbus 的 0x03/0x04 区没有写功能码，写请求在真驱动上必然失败（见本类末尾的驱动侧证据），
    /// 「配了个永远写不进去的点位」是纯配置矛盾，不能留到运行期由引擎每次拒绝。
    /// </summary>
    [Theory]
    [InlineData("input")]
    [InlineData("discrete")]
    public void Writable_points_on_read_only_areas_are_rejected_at_load(string area)
    {
        var ex = Assert.Throws<ConfigValidationException>(
            () => Cfg($"""<Point id="wr" address="0" area="{area}" access="write" />"""));

        Assert.Contains(ex.Errors, e => e.Contains("点位 ps1/wr") && e.Contains("只读区"));
    }

    /// <summary>
    /// findings D66 / CGV-35：位区（coil/discrete）声明**多字**点位加载期必须报错。
    /// 此前零错误放行，而引擎按「区=coil」走单写 → 除首字外全部静默丢弃（读取侧同样解不出正确值）。
    /// </summary>
    [Fact]
    public void Multi_word_points_in_a_bit_area_are_rejected_at_load()
    {
        // 显式多字：位区一个地址只有 1 位，写只发首字、读也拼不出值
        var byLength = Assert.Throws<ConfigValidationException>(() => Cfg(
            """<Point id="wr" address="0" area="coil" access="write" dataType="string" length="2"><String encoding="ascii" /></Point>"""));
        Assert.Contains(byLength.Errors, e => e.Contains("点位 ps1/wr") && e.Contains("位区") && e.Contains("多字"));

        // 多字类型（不写 length，按 dataType 推导出 4 字的 float64 同样拒绝）
        var byType = Assert.Throws<ConfigValidationException>(() => Cfg(
            """<Point id="wr2" address="0" area="discrete" dataType="float64" />"""));
        Assert.Contains(byType.Errors, e => e.Contains("点位 ps1/wr2") && e.Contains("位区"));

        // 边界：位区的单字布尔点位照常加载（读点位不受影响）
        var ok = Cfg("""<Point id="ok" address="0" area="coil" access="write" dataType="bool" />""");
        Assert.Single(ok.PointSets[0].Points);
    }

    /// <summary>
    /// 驱动侧证据（无端口、无 IO）：`ModbusMaster` 对只读区的写请求在发帧前就判协议失败，
    /// 所以「只读区的可写点位」在真链路上必然失败——加载期必须拦（D66），引擎侧已拦（D65）。
    /// </summary>
    [Fact]
    public void The_driver_refuses_writes_to_read_only_areas_before_touching_the_wire()
    {
        using var master = new ModbusMaster(ChannelVariant.Tcp, new TransportLike { Host = "127.0.0.1", Port = 1 });

        var input = master.WriteSingle(DataArea.InputRegister, 0, 1, 1, 100, 0, 0);
        var discrete = master.WriteMulti(DataArea.DiscreteInput, 0, new ushort[] { 1, 2 }, 1, 100, 0, 0);

        Assert.False(input.Success);
        Assert.Equal(ModbusFailureKind.Protocol, input.Kind);
        Assert.False(discrete.Success);
        Assert.Equal(ModbusFailureKind.Protocol, discrete.Kind);
    }
}
