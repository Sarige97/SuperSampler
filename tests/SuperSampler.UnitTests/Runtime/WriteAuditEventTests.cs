using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
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
/// 写审计事件字段（findings D67，本轮修复）：四态下 `ElapsedMs` / `DidReadback` / `Readback` / `VerifyMismatch`
/// 必须与同步结果对象一致，宿主据此做写追溯（不再需要回到同步返回值）。
/// 纯单测（假链路、不启动轮询），覆盖方案 §十四。
/// </summary>
public class WriteAuditEventTests
{
    private static SamplerConfiguration Cfg(string points, string globalExtra = "")
        => SamplerConfigLoader.Load(XDocument.Parse("""
            <SamplerConfig schemaVersion="3.0">
              <Global><Polling defaultIntervalMs="2000" requestTimeoutMs="300" />{0}</Global>
              <Transports><Transport id="tcp1" host="127.0.0.1" /></Transports>
              <Devices><Device id="d1" transport="tcp1" pointSet="ps1" unitId="7" /></Devices>
              <PointSets><PointSet id="ps1"><Points>{1}</Points></PointSet></PointSets>
            </SamplerConfig>
            """.Replace("{0}", globalExtra).Replace("{1}", points)), Directory.GetCurrentDirectory());

    private static SamplerEngine Engine(FakeModbusLink link, string points, string globalExtra = "")
        => new(Cfg(points, globalExtra), new Dictionary<string, IModbusLink> { ["tcp1"] = link });

    private async Task<(WriteResult Result, PointWrittenEvent Event)> WriteAsync(string points, FakeModbusLink link,
        object? value, Action<FakeModbusLink>? arrange = null)
    {
        using var engine = Engine(link, points);
        var written = new List<PointWrittenEvent>();
        engine.Bus.Subscribe<PointWrittenEvent>(e => written.Add(e.Body), DeliveryMode.Inline);

        arrange?.Invoke(link);
        var result = await engine.SetValueAsync("d1", "wr", value);

        Assert.Single(written);                        // 写审计：无论成败恰发一条
        return (result, written[0]);
    }

    // ═══════════════ 成功（无 verify）═══════════════

    [Fact]
    public async Task A_plain_successful_write_reports_no_readback_and_a_non_negative_elapsed()
    {
        var link = new FakeModbusLink();
        var (result, audit) = await WriteAsync("""<Point id="wr" address="0" access="write" />""", link, 7,
            l => l.WriteSingleReplies.Enqueue(ModbusReply.Ok(Array.Empty<ushort>(), 1)));

        Assert.Equal(WriteOutcome.Succeeded, result.Outcome);
        Assert.Equal("Succeeded", audit.Outcome);
        Assert.Equal("d1", audit.DeviceId);
        Assert.Equal("wr", audit.PointId);
        Assert.Equal(7, audit.Value);
        Assert.Equal("Local", audit.User);             // 无 user 重载用内置身份
        Assert.Null(audit.Message);
        Assert.True(audit.ElapsedMs >= 0);
        Assert.False(audit.DidReadback);
        Assert.Null(audit.Readback);
        Assert.False(audit.VerifyMismatch);
    }

    [Fact]
    public async Task The_elapsed_field_really_measures_the_pipeline_duration()
    {
        var link = new FakeModbusLink();
        var (_, audit) = await WriteAsync("""<Point id="wr" address="0" access="write" />""", link, 7,
            l => l.WriteSingleReplies.Enqueue(ModbusReply.Ok(Array.Empty<ushort>(), 1)));

        var slow = new FakeModbusLink();
        slow.FaultRules.Add(new FakeFaultRule { ForWrite = true, Kind = ModbusFailureKind.Protocol, ExceptionCode = 0x02, DelayMs = 200 });
        var (_, slowAudit) = await WriteAsync("""<Point id="wr" address="0" access="write" />""", slow, 7);

        // 慢写（注入 200ms 延迟）必须体现在耗时上：证明 ElapsedMs 不是恒 0 的占位
        Assert.True(slowAudit.ElapsedMs >= 150, "慢写耗时未体现（" + slowAudit.ElapsedMs + "ms）");
        Assert.True(audit.ElapsedMs <= slowAudit.ElapsedMs);
    }

    // ═══════════════ 成功 + verify ═══════════════

    [Fact]
    public async Task A_verified_write_reports_the_readback_value_and_no_mismatch()
    {
        var link = new FakeModbusLink();
        var (result, audit) = await WriteAsync("""<Point id="wr" address="0" access="write"><Write verify="true" /></Point>""",
            link, 7, l =>
            {
                l.WriteSingleReplies.Enqueue(ModbusReply.Ok(Array.Empty<ushort>(), 1));
                l.ReadReplies.Enqueue(ModbusReply.Ok(new ushort[] { 7 }, 1));   // 回读一致
            });

        Assert.Equal(WriteOutcome.Succeeded, result.Outcome);
        Assert.True(audit.DidReadback);
        Assert.NotNull(audit.Readback);
        Assert.Equal((ushort)7, audit.Readback!.Value.Value);
        Assert.Equal(PointQuality.Good, audit.Readback.Value.Quality);
        Assert.False(audit.VerifyMismatch);
    }

    [Fact]
    public async Task A_device_that_clamps_the_value_is_visible_as_a_verify_mismatch_with_the_actual_readback()
    {
        // D10 决策③：通讯成功但值不符 → 仍 Succeeded + VerifyMismatch=true，且审计里能看到设备实际值
        var link = new FakeModbusLink();
        var (result, audit) = await WriteAsync("""<Point id="wr" address="0" access="write"><Write verify="true" /></Point>""",
            link, 7, l =>
            {
                l.WriteSingleReplies.Enqueue(ModbusReply.Ok(Array.Empty<ushort>(), 1));
                l.ReadReplies.Enqueue(ModbusReply.Ok(new ushort[] { 3 }, 1));   // 设备自己钳到 3
            });

        Assert.Equal(WriteOutcome.Succeeded, result.Outcome);
        Assert.True(result.VerifyMismatch);
        Assert.True(audit.DidReadback);
        Assert.True(audit.VerifyMismatch);
        Assert.Equal((ushort)3, audit.Readback!.Value.Value);
    }

    // ═══════════════ 失败 / 不确定 / 拒绝 ═══════════════

    [Fact]
    public async Task A_permanent_device_exception_reports_failed_without_claiming_a_readback()
    {
        var link = new FakeModbusLink();
        link.FaultRules.Add(new FakeFaultRule { ForWrite = true, Kind = ModbusFailureKind.Protocol, ExceptionCode = 0x02 });
        var (result, audit) = await WriteAsync("""<Point id="wr" address="0" access="write" />""", link, 7);

        Assert.Equal(WriteOutcome.Failed, result.Outcome);
        Assert.Equal("Failed", audit.Outcome);
        Assert.Equal("ss.error.writeFailed", audit.Message);
        Assert.False(audit.DidReadback);               // 非超时失败不进入回读定论路径
        Assert.Null(audit.Readback);
        Assert.False(audit.VerifyMismatch);
        Assert.True(audit.ElapsedMs >= 0);
    }

    [Fact]
    public async Task A_write_timeout_with_a_failed_readback_reports_indeterminate_and_that_a_readback_was_attempted()
    {
        var link = new FakeModbusLink();
        var (result, audit) = await WriteAsync("""<Point id="wr" address="0" access="write" />""", link, 7, l =>
        {
            l.WriteSingleReplies.Enqueue(ModbusReply.Fail(ModbusFailureKind.Timeout, 0, "写超时", 300));
            l.ReadReplies.Enqueue(ModbusReply.Fail(ModbusFailureKind.Timeout, 0, "回读也超时", 300));
        });

        Assert.Equal(WriteOutcome.Indeterminate, result.Outcome);
        Assert.Equal("Indeterminate", audit.Outcome);
        Assert.True(audit.DidReadback);                // 发起过回读（拿不到值也算）
        Assert.Null(audit.Readback);
        Assert.False(audit.VerifyMismatch);
        Assert.Equal(1, link.WriteCallCount);          // 写请求只发一次（绝不自动重写）
        Assert.Equal(1, link.Calls.Count(c => c.IsRead));
    }

    [Fact]
    public async Task A_write_timeout_resolved_by_the_readback_reports_succeeded_with_the_readback_value()
    {
        var link = new FakeModbusLink();
        var (result, audit) = await WriteAsync("""<Point id="wr" address="0" access="write" />""", link, 7, l =>
        {
            l.WriteSingleReplies.Enqueue(ModbusReply.Fail(ModbusFailureKind.Timeout, 0, "写超时", 300));
            l.ReadReplies.Enqueue(ModbusReply.Ok(new ushort[] { 7 }, 1));   // 回读证明已生效
        });

        Assert.Equal(WriteOutcome.Succeeded, result.Outcome);
        Assert.True(audit.DidReadback);
        Assert.Equal((ushort)7, audit.Readback!.Value.Value);
        Assert.False(audit.VerifyMismatch);
    }

    [Fact]
    public async Task A_write_timeout_whose_readback_shows_another_value_reports_failed_todays_semantics()
    {
        var link = new FakeModbusLink();
        var (result, audit) = await WriteAsync("""<Point id="wr" address="0" access="write" />""", link, 7, l =>
        {
            l.WriteSingleReplies.Enqueue(ModbusReply.Fail(ModbusFailureKind.Timeout, 0, "写超时", 300));
            l.ReadReplies.Enqueue(ModbusReply.Ok(new ushort[] { 3 }, 1));   // 设备上是别的值 → 未生效
        });

        Assert.Equal(WriteOutcome.Failed, result.Outcome);
        Assert.Equal("Failed", audit.Outcome);
        Assert.True(audit.DidReadback);
        Assert.Equal((ushort)3, audit.Readback!.Value.Value);   // 审计里能看到设备实际值
    }

    [Fact]
    public async Task A_rejected_write_never_touches_the_wire_and_reports_no_readback()
    {
        var link = new FakeModbusLink();
        var (result, audit) = await WriteAsync("""<Point id="wr" address="0" access="write"><Write min="0" max="10" /></Point>""", link, 999);

        Assert.Equal(WriteOutcome.Rejected, result.Outcome);
        Assert.Equal("Rejected", audit.Outcome);
        Assert.Equal("ss.reason.aboveMax", audit.Message);   // 999 > max=10
        Assert.Empty(link.Calls);                      // 未发出任何通讯
        Assert.False(audit.DidReadback);
        Assert.Null(audit.Readback);
        Assert.False(audit.VerifyMismatch);
        Assert.True(audit.ElapsedMs >= 0);
    }

    [Fact]
    public async Task A_verified_point_whose_readback_request_fails_is_reported_as_attempted_but_unavailable()
    {
        // 口径（本轮补的 DidReadback）：verify=true 的点位即使回读请求本身失败（拿不到值），
        // 审计也必须能区分「没做回读」与「做了但没拿到值」——后者 DidReadback=true 且 Readback=null。
        var link = new FakeModbusLink();
        var (result, audit) = await WriteAsync("""<Point id="wr" address="0" access="write"><Write verify="true" /></Point>""",
            link, 7, l =>
            {
                l.WriteSingleReplies.Enqueue(ModbusReply.Ok(Array.Empty<ushort>(), 1));
                l.ReadReplies.Enqueue(ModbusReply.Fail(ModbusFailureKind.LinkDown, 0, "回读链路断", 10));
            });

        Assert.Equal(WriteOutcome.Succeeded, result.Outcome);
        Assert.False(result.VerifyMismatch);
        Assert.True(audit.DidReadback);
        Assert.Null(audit.Readback);
        Assert.False(audit.VerifyMismatch);
    }

    [Fact]
    public void The_legacy_six_field_constructor_still_compiles_and_defaults_the_new_fields()
    {
        // 兼容面：只加不改——老调用方（6 参数）照旧可用，新字段取默认值
        var legacy = new PointWrittenEvent("d1", "p", 1, "op", "Succeeded", null);

        Assert.Equal("d1", legacy.DeviceId);
        Assert.Equal("p", legacy.PointId);
        Assert.Equal(1, legacy.Value);
        Assert.Equal("op", legacy.User);
        Assert.Equal("Succeeded", legacy.Outcome);
        Assert.Null(legacy.Message);
        Assert.Equal(0, legacy.ElapsedMs);
        Assert.False(legacy.DidReadback);
        Assert.Null(legacy.Readback);
        Assert.False(legacy.VerifyMismatch);
        Assert.Equal(EventCategory.Write, legacy.Category);
    }
}
