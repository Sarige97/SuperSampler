using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Xml.Linq;
using SuperSampler.Abstractions.Facade;
using SuperSampler.Core.Config;
using SuperSampler.Core.Runtime;
using SuperSampler.Drivers.Modbus;
using SuperSampler.UnitTests.Support;
using Xunit;

namespace SuperSampler.UnitTests.Runtime;

/// <summary>
/// 写管道确定性单测（findings B1）：注入假 IModbusLink，覆盖四态结果与位点读-改-写。
/// 覆盖：范围外 Reject、写成功 Succeeded、超时回读一致转 Succeeded、超时回读不一致 Indeterminate、
/// 链路失败 Failed、位点只翻转目标位。
/// </summary>
public class WritePipelineTests
{
    private static SamplerConfiguration Cfg(string pointXml)
        => SamplerConfigLoader.Load(XDocument.Parse(string.Format(XML, pointXml)), Directory.GetCurrentDirectory());

    private static SamplerEngine Engine(FakeModbusLink link, string pointXml)
        => new(Cfg(pointXml), new Dictionary<string, IModbusLink> { ["tcp1"] = link });

    private static async Task<WriteResult> Write(string pointXml, FakeModbusLink link, object value)
    {
        using var engine = Engine(link, pointXml);
        return await engine.SetValueAsync("d1", "wr", value);
    }

    [Fact]
    public async Task Out_of_high_range_write_is_rejected_without_link_call()
    {
        var link = new FakeModbusLink();
        var result = await Write(WITH_RANGE, link, 50);

        Assert.Equal(WriteOutcome.Rejected, result.Outcome);
        Assert.Empty(link.WriteSingleValues); // 拒绝路径不应发任何通讯
        Assert.Equal(0, link.ReadCalls);
    }

    [Fact]
    public async Task Single_write_success_returns_succeeded()
    {
        var link = new FakeModbusLink();
        link.WriteSingleReplies.Enqueue(ModbusReply.Ok(Array.Empty<ushort>(), 1));
        var result = await Write(PLAIN, link, 5);

        Assert.Equal(WriteOutcome.Succeeded, result.Outcome);
        var single = Assert.Single(link.WriteSingleValues);
        Assert.Equal(5, single);
        Assert.Equal(0, link.ReadCalls); // 无 Verify → 不回读
    }

    [Fact]
    public async Task Timeout_with_matching_readback_is_succeeded()
    {
        var link = new FakeModbusLink();
        link.WriteSingleReplies.Enqueue(ModbusReply.Fail(ModbusFailureKind.Timeout, 0, "timeout", 5));
        link.ReadReplies.Enqueue(ModbusReply.Ok(new ushort[] { 5 }, 3)); // 回读到与写入一致

        var result = await Write(PLAIN, link, 5);

        // 写入超时但已生效：回读一致 → 按成功定论（docs/02 指示）
        Assert.Equal(WriteOutcome.Succeeded, result.Outcome);
        Assert.Equal(1, link.ReadCalls);
        // GATE-1：超时后绝不自动重写——写请求只发一次，改用回读定论
        Assert.Equal(1, link.WriteSingleCallCount);
    }

    /// <summary>
    /// 回读值与写入值不一致 → 确认写入未生效 → Failed（明确失败，可安全重试）。
    /// 不是 Indeterminate：Indeterminate 只留给「回读也失败、状态未知」的情况（docs/02 三态语义）。
    /// </summary>
    [Fact]
    public async Task Timeout_with_mismatched_readback_is_failed()
    {
        var link = new FakeModbusLink();
        link.WriteSingleReplies.Enqueue(ModbusReply.Fail(ModbusFailureKind.Timeout, 0, "timeout", 5));
        link.ReadReplies.Enqueue(ModbusReply.Ok(new ushort[] { 123 }, 3)); // 回读与写入不一致

        var result = await Write(PLAIN, link, 5);

        Assert.Equal(WriteOutcome.Failed, result.Outcome);
        Assert.NotNull(result.Error);
        Assert.Equal(1, link.ReadCalls);
        // GATE-1：回读不一致判 Failed（明确失败，可安全重试的是宿主）——写管道自身也不重写
        Assert.Equal(1, link.WriteSingleCallCount);
    }

    [Fact]
    public async Task Link_failure_returns_failed()
    {
        var link = new FakeModbusLink();
        link.WriteSingleReplies.Enqueue(ModbusReply.Fail(ModbusFailureKind.LinkDown, 0, "link", 1));

        var result = await Write(PLAIN, link, 5);

        Assert.Equal(WriteOutcome.Failed, result.Outcome);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task Bit_point_write_reads_back_and_flips_only_target_bit()
    {
        var link = new FakeModbusLink();
        link.ReadReplies.Enqueue(ModbusReply.Ok(new ushort[] { 0x0003 }, 1)); // 当前位字 0b0011
        link.WriteSingleReplies.Enqueue(ModbusReply.Ok(Array.Empty<ushort>(), 1));
        using var engine = Engine(link, BIT_POINT);

        var result = await engine.SetValueAsync("d1", "wr", true);

        Assert.Equal(WriteOutcome.Succeeded, result.Outcome);
        Assert.Equal(1, link.ReadCalls);           // 读-改-写第一步读到当前
        var written = Assert.Single(link.WriteSingleValues);
        Assert.Equal(0x0007, written);              // 2 号位置 1：0b0011 | 0b0100 = 0b0111
    }

    [Fact]
    public async Task Bit_point_write_zero_clears_only_target_bit()
    {
        var link = new FakeModbusLink();
        link.ReadReplies.Enqueue(ModbusReply.Ok(new ushort[] { 0x00FF }, 1)); // 当前 0xFF
        link.WriteSingleReplies.Enqueue(ModbusReply.Ok(Array.Empty<ushort>(), 1));
        using var engine = Engine(link, BIT_POINT);

        var result = await engine.SetValueAsync("d1", "wr", false);

        Assert.Equal(WriteOutcome.Succeeded, result.Outcome);
        var written = Assert.Single(link.WriteSingleValues);
        Assert.Equal(0x00FB, written);              // 2 号位清 0：0xFF & ~0b0100 = 0xFB
    }

    // ───────────── verify=true 回读校验（D10 决策③） ─────────────

    /// <summary>
    /// verify=true 写成功且回读一致 → Succeeded + VerifyMismatch=false；
    /// Readback 为设备实际值（= 写入值）。
    /// </summary>
    [Fact]
    public async Task Verify_write_with_matching_readback_is_succeeded_without_mismatch()
    {
        var link = new FakeModbusLink();
        link.WriteSingleReplies.Enqueue(ModbusReply.Ok(Array.Empty<ushort>(), 1));
        link.ReadReplies.Enqueue(ModbusReply.Ok(new ushort[] { 5 }, 1)); // 回读一致

        var result = await Write(VERIFY_POINT, link, 5);

        Assert.Equal(WriteOutcome.Succeeded, result.Outcome);
        Assert.False(result.VerifyMismatch);
        Assert.True(result.Readback.HasValue);
        Assert.True(result.Readback!.Value.TryGetValue<ushort>(out var readback));
        Assert.Equal(5, readback);
        Assert.Equal(1, link.ReadCalls);            // verify → 写后必回读
    }

    /// <summary>
    /// verify=true 写成功但回读不一致（设备钳位）→ 仍 Succeeded + VerifyMismatch=true，
    /// Readback 为设备实际值。通讯确实成功，宿主据此提示而不是自动重试。
    /// </summary>
    [Fact]
    public async Task Verify_write_with_mismatched_readback_marks_mismatch_but_succeeds()
    {
        var link = new FakeModbusLink();
        link.WriteSingleReplies.Enqueue(ModbusReply.Ok(Array.Empty<ushort>(), 1));
        link.ReadReplies.Enqueue(ModbusReply.Ok(new ushort[] { 123 }, 1)); // 设备钳位为 123

        var result = await Write(VERIFY_POINT, link, 5);

        Assert.Equal(WriteOutcome.Succeeded, result.Outcome);
        Assert.True(result.VerifyMismatch);
        Assert.True(result.Readback.HasValue);
        Assert.True(result.Readback!.Value.TryGetValue<ushort>(out var readback));
        Assert.Equal(123, readback);                // Readback 是设备实际值而非写入值
        Assert.Equal(1, link.ReadCalls);
    }

    /// <summary>
    /// verify=true 写成功但回读失败（链路已断）→ 仍 Succeeded 且不打 mismatch 标记，
    /// Readback 为 null：通讯成功已确认，只是校验值取不到。
    /// </summary>
    [Fact]
    public async Task Verify_write_with_failed_readback_succeeds_without_mismatch()
    {
        var link = new FakeModbusLink();
        link.WriteSingleReplies.Enqueue(ModbusReply.Ok(Array.Empty<ushort>(), 1));
        // 不排读应答 → 回读 LinkDown 失败

        var result = await Write(VERIFY_POINT, link, 5);

        Assert.Equal(WriteOutcome.Succeeded, result.Outcome);
        Assert.False(result.VerifyMismatch);
        Assert.False(result.Readback.HasValue);
        Assert.Equal(1, link.ReadCalls);
    }

    private const string XML = """
        <SamplerConfig schemaVersion="3.0">
          <Transports><Transport id="tcp1" host="127.0.0.1" /></Transports>
          <Devices><Device id="d1" transport="tcp1" pointSet="ps1" /></Devices>
          <PointSets><PointSet id="ps1"><Points>{0}</Points></PointSet></PointSets>
        </SamplerConfig>
        """;

    private const string POINT = """<Point id="wr" address="0" access="write" />""";

    private const string WITH_RANGE =
        """<Point id="wr" address="0" access="write"><Write min="0" max="10" /></Point>""";

    private const string BIT_POINT =
        """<Point id="wr" address="0" access="write" bit="2" />""";

    private const string VERIFY_POINT =
        """<Point id="wr" address="0" access="write"><Write verify="true" /></Point>""";

    // ───────────── 点动脉冲（findings W22 接线）：写 true 后延时写回 false ─────────────

    /// <summary>
    /// 脉冲点写 true → 先写 1，Sleep(PulseMs) 后自动写回 0（findings W22 接线）。
    /// 断言两次写的值序 [1, 0] 与经过时长 ≥ pulseMs。
    /// </summary>
    [Fact]
    public async Task Pulse_point_write_true_writes_back_zero_after_pulse()
    {
        var link = new FakeModbusLink();
        link.WriteSingleReplies.Enqueue(ModbusReply.Ok(Array.Empty<ushort>(), 1)); // 写 1
        link.WriteSingleReplies.Enqueue(ModbusReply.Ok(Array.Empty<ushort>(), 1)); // 写回 0

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var result = await Write(PULSE_POINT, link, 1);
        stopwatch.Stop();

        Assert.Equal(WriteOutcome.Succeeded, result.Outcome);
        Assert.Equal(new ushort[] { 1, 0 }, link.WriteSingleValues); // 先置 1，脉冲后自动回 0
        Assert.Equal(2, link.WriteSingleCallCount);
        Assert.True(stopwatch.ElapsedMilliseconds >= PULSE_MS,
            $"脉冲应延时至少 {PULSE_MS}ms 再写回，实际 {stopwatch.ElapsedMilliseconds}ms");
    }

    /// <summary>脉冲点写 false → 只写一次 0，不触发脉冲写回（脉冲只在真正时生效）。</summary>
    [Fact]
    public async Task Pulse_point_write_false_is_single_write_without_pulse_back()
    {
        var link = new FakeModbusLink();
        link.WriteSingleReplies.Enqueue(ModbusReply.Ok(Array.Empty<ushort>(), 1));

        var result = await Write(PULSE_POINT, link, 0);

        Assert.Equal(WriteOutcome.Succeeded, result.Outcome);
        Assert.Equal(new ushort[] { 0 }, link.WriteSingleValues);
        Assert.Equal(1, link.WriteSingleCallCount); // 无第二次写回
    }

    private const int PULSE_MS = 10;

    private const string PULSE_POINT =
        """<Point id="wr" address="0" access="write"><Write pulseMs="10" /></Point>""";

    private const string PLAIN = POINT;
}