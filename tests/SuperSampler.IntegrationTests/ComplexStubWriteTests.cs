using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using SuperSampler.Abstractions.Errors;
using SuperSampler.Abstractions.Events;
using SuperSampler.Abstractions.Facade;
using SuperSampler.Abstractions.Values;
using SuperSampler.Core.Runtime;
using Xunit;

namespace SuperSampler.IntegrationTests;

/// <summary>
/// C2 写链路：范围拒绝（零通讯）、只读从站拒绝（0x02）、verify 回读一致/不一致（设备改写）、
/// 点动脉冲、位点读-改-写、写超时三态（断路器制造，写请求只发一次）、写审计事件。
///
/// 镜像侧语义（complex_points.json / 复杂桩说明.md）：
///  · RO-LOCK（unit 11）写请求回 0x02 非法数据地址；
///  · PROT-O2（unit 12）写多字超过 maxWriteMultipleRegisters=1 回 0x02；
///  · SLOW（unit 14/15）响应延迟 500ms/2000ms，其 120 字块由设备脚本每 100ms 覆写
///    → 天然的「写后被设备改写」点，用来触发 VerifyMismatch。
/// </summary>
[Collection(ComplexStubCollection.Name)]
public sealed class ComplexStubWriteTests
{
    private readonly ComplexStubFixture _stub;

    public ComplexStubWriteTests(ComplexStubFixture stub) => _stub = stub;

    private const string GlobalFast = """
        <Global>
          <Retry count="0" intervalMs="10" />
          <Polling rateMs="200" requestTimeoutMs="3000" />
        </Global>
        <ScanGroups><ScanGroup id="normal" mode="poll" rateMs="200" /><ScanGroup id="od" mode="onDemand" /></ScanGroups>
        """;

    // ─────────────────────── C2-1：range 越界 → Rejected，且零通讯 ───────────────────────

    [SkippableFact]
    public async Task C2_RangeRejection_IsRejectedWithoutAnyCommunication()
    {
        _stub.RequireStub();
        _stub.RequireSimLog();

        // 点放在 onDemand 组：该从站不会有任何轮询请求，镜像日志里的请求只可能来自写
        var point = ComplexStubFixture.Point("env.limit", 2, "int16",
            "area=\"holding\" access=\"readwrite\" swap=\"abcd\" scanGroup=\"od\"",
            "<Write min=\"0\" max=\"500\" verify=\"true\" />");

        var xml = "<SamplerConfig schemaVersion=\"3.0\">" + GlobalFast +
                  "<Transports>" + ComplexStubFixture.Transport("p3", _stub.SimPortP3) + "</Transports>" +
                  "<Devices>" + ComplexStubFixture.Device("env", "p3", 6, "ps") + "</Devices>" +
                  "<PointSets><PointSet id=\"ps\"><Points>" + point + "</Points></PointSet></PointSets>" +
                  "</SamplerConfig>";

        using var engine = new SamplerEngine(_stub.LoadConfig(xml));
        engine.Start();
        try
        {
            var before = _stub.CountSimLogLines(_stub.SimPortP3, 6);

            // 越上限
            var above = await engine.SetValueAsync("env", "env.limit", 5000);
            Assert.Equal(WriteOutcome.Rejected, above.Outcome);
            Assert.NotNull(above.Error);
            Assert.Equal("ss.reason.aboveMax", above.Error!.MessageKey);
            Assert.Equal("SS.WRITE.REJECTED", above.Error.Code);

            // 越下限
            var below = await engine.SetValueAsync("env", "env.limit", -1);
            Assert.Equal(WriteOutcome.Rejected, below.Outcome);
            Assert.Equal("ss.reason.belowMin", below.Error!.MessageKey);

            // 范围拒绝必须发生在下发之前：镜像一条请求都不该收到
            await Task.Delay(300);
            Assert.Equal(before, _stub.CountSimLogLines(_stub.SimPortP3, 6));

            // 对照组：范围内写确实会下发（证明日志口径有效，且 Rejected 的"零通讯"不是伪证）
            var ok = await engine.SetValueAsync("env", "env.limit", 350);
            Assert.Equal(WriteOutcome.Succeeded, ok.Outcome);
            Assert.False(ok.VerifyMismatch);
            Assert.Equal(2, _stub.CountSimLogLines(_stub.SimPortP3, 6) - before); // 写 1 次 + verify 回读 1 次
        }
        finally
        {
            engine.Dispose();
        }
    }

    // ─────────────────────── C2-2：只读从站写 → Failed（0x02），且不重试 ───────────────────────

    [SkippableFact]
    public async Task C2_WriteToReadonlySlave_FailsWithSlaveException()
    {
        _stub.RequireStub();
        _stub.RequireSimLog();

        // RO-LOCK（unit 11）：镜像对任何写请求回 0x02（只读保护）
        var point = ComplexStubFixture.Point("ro.value", 0, "uint16",
            "area=\"holding\" access=\"readwrite\" swap=\"abcd\" scanGroup=\"od\"");
        var xml = "<SamplerConfig schemaVersion=\"3.0\">" + GlobalFast +
                  "<Transports>" + ComplexStubFixture.Transport("p3", _stub.SimPortP3) + "</Transports>" +
                  "<Devices>" + ComplexStubFixture.Device("ro", "p3", 11, "ps") + "</Devices>" +
                  "<PointSets><PointSet id=\"ps\"><Points>" + point + "</Points></PointSet></PointSets>" +
                  "</SamplerConfig>";

        using var engine = new SamplerEngine(_stub.LoadConfig(xml));
        engine.Start();
        try
        {
            var before = _stub.CountSimLogLines(_stub.SimPortP3, 11);
            var result = await engine.SetValueAsync("ro", "ro.value", 1234);

            Assert.Equal(WriteOutcome.Failed, result.Outcome);
            Assert.NotNull(result.Error);
            Assert.Equal("MODBUS.EXCEPTION.02", result.Error!.Code);
            Assert.Equal("ss.error.writeFailed", result.Error.MessageKey);

            // 0x02 是永久码：只发一次请求（绝不自动重写）
            await Task.Delay(200);
            Assert.Equal(1, _stub.CountSimLogLines(_stub.SimPortP3, 11) - before);
        }
        finally
        {
            engine.Dispose();
        }
    }

    // ─────────────────────── C2-3：写保护从站（PROT-O2）多字写 → 0x02 ───────────────────────

    [SkippableFact]
    public async Task C2_WriteMultiToWriteProtectedSlave_RejectedBySlave_AndSingleWordWorks()
    {
        _stub.RequireStub();

        // PROT-O2（unit 12）maxWriteMultipleRegisters=1：多字写回 0x02，单字写正常
        var points = string.Concat(
            ComplexStubFixture.Point("p.u32", 2, "uint32", "area=\"holding\" access=\"readwrite\" swap=\"abcd\""),
            ComplexStubFixture.Point("p.u16", 0, "uint16", "area=\"holding\" access=\"readwrite\" swap=\"abcd\""));

        var xml = "<SamplerConfig schemaVersion=\"3.0\">" + GlobalFast +
                  "<Transports>" + ComplexStubFixture.Transport("p3", _stub.SimPortP3) + "</Transports>" +
                  "<Devices>" + ComplexStubFixture.Device("p2", "p3", 12, "ps") + "</Devices>" +
                  "<PointSets><PointSet id=\"ps\"><Points>" + points + "</Points></PointSet></PointSets>" +
                  "</SamplerConfig>";

        using var engine = new SamplerEngine(_stub.LoadConfig(xml));
        engine.Start();
        try
        {
            await ComplexStubFixture.WaitGoodAsync(engine, "p2", "p.u16");

            var multi = await engine.SetValueAsync("p2", "p.u32", 0x11223344u);
            Assert.Equal(WriteOutcome.Failed, multi.Outcome);
            Assert.Equal("MODBUS.EXCEPTION.02", multi.Error!.Code);

            var single = await engine.SetValueAsync("p2", "p.u16", 0x1234);
            Assert.Equal(WriteOutcome.Succeeded, single.Outcome);

            var readback = await ((IModbusDebugTool)engine).TriggerReadAsync("p2", "p.u16");
            Assert.Equal(0x1234, F.GetAs<ushort>(readback));
        }
        finally
        {
            engine.Dispose();
        }
    }

    // ─────────────────────── C2-4：verify 一致 / 设备改写导致不一致 ───────────────────────

    [SkippableFact]
    public async Task C2_VerifyReadback_MatchesOnStableRegister()
    {
        _stub.RequireStub();

        // ENV-01（unit 6）4x@2 温度报警上限：脚本只读不写 → 写完就是写值，verify 必须一致
        var point = ComplexStubFixture.Point("env.limit", 2, "int16",
            "area=\"holding\" access=\"readwrite\" swap=\"abcd\" scanGroup=\"od\"",
            "<Write verify=\"true\" min=\"0\" max=\"500\" />");
        var xml = "<SamplerConfig schemaVersion=\"3.0\">" + GlobalFast +
                  "<Transports>" + ComplexStubFixture.Transport("p3", _stub.SimPortP3) + "</Transports>" +
                  "<Devices>" + ComplexStubFixture.Device("env", "p3", 6, "ps") + "</Devices>" +
                  "<PointSets><PointSet id=\"ps\"><Points>" + point + "</Points></PointSet></PointSets>" +
                  "</SamplerConfig>";

        using var engine = new SamplerEngine(_stub.LoadConfig(xml));
        engine.Start();
        try
        {
            var result = await engine.SetValueAsync("env", "env.limit", 350);

            Assert.Equal(WriteOutcome.Succeeded, result.Outcome);
            Assert.False(result.VerifyMismatch);
            Assert.True(result.Readback.HasValue);
            Assert.Equal(350, (int)F.GetAs<short>(result.Readback!.Value));
            Assert.Null(result.Error);
        }
        finally
        {
            engine.Dispose();
        }
    }

    [SkippableFact]
    public async Task C2_VerifyReadback_FlagsMismatchWhenDeviceRewritesTheValue()
    {
        _stub.RequireStub();
        _stub.RequireSimLog();

        // SLOW-500（unit 14）4x@25 落在它的 120 字块里：设备脚本每 100ms 覆写一次
        // （(c*3 + i*7 + 123) & 0xFFFF，i=5 → 3c+158 ≡ 2 (mod 3)）。
        // 写入值取 0x4321 = 17185 ≡ 1 (mod 3) → 回读值在数学上不可能与写入值相等，
        // 因此这是「设备改写（钳位/脚本覆写）」导致的确定性 verify 不一致，不依赖时序运气。
        var point = ComplexStubFixture.Point("slow.cell", 25, "uint16",
            "area=\"holding\" access=\"readwrite\" swap=\"abcd\" scanGroup=\"od\"",
            "<Write verify=\"true\" />");
        var xml = "<SamplerConfig schemaVersion=\"3.0\">" +
                  "<Global><Retry count=\"0\" /><Polling rateMs=\"500\" requestTimeoutMs=\"2500\" /></Global>" +
                  "<ScanGroups><ScanGroup id=\"normal\" mode=\"poll\" rateMs=\"500\" /><ScanGroup id=\"od\" mode=\"onDemand\" /></ScanGroups>" +
                  "<Transports>" + ComplexStubFixture.Transport("p3", _stub.SimPortP3, "tcp", 2500) + "</Transports>" +
                  "<Devices>" + ComplexStubFixture.Device("slow", "p3", 14, "ps") + "</Devices>" +
                  "<PointSets><PointSet id=\"ps\"><Points>" + point + "</Points></PointSet></PointSets>" +
                  "</SamplerConfig>";

        using var engine = new SamplerEngine(_stub.LoadConfig(xml));
        engine.Start();
        try
        {
            var result = await engine.SetValueAsync("slow", "slow.cell", 0x4321);

            // 通讯成功（设备确实应答了），但回读到的不是我们写的值 → Succeeded + VerifyMismatch
            Assert.Equal(WriteOutcome.Succeeded, result.Outcome);
            Assert.True(result.VerifyMismatch, "设备覆写后回读值应与写入值不同");
            Assert.True(result.Readback.HasValue);
            Assert.NotEqual(0x4321, F.GetAs<ushort>(result.Readback!.Value));
        }
        finally
        {
            engine.Dispose();
        }
    }

    // ─────────────────────── C2-5：点动脉冲（写 true 后自动写回 false） ───────────────────────

    [SkippableFact]
    public async Task C2_PulseWrite_AutomaticallyWritesBackFalse()
    {
        _stub.RequireStub();
        _stub.RequireSimLog();

        // WO-ABCD（unit 7）线圈 0x@1「测试线圈1」：初值 0，可用脉冲点动
        var point = ComplexStubFixture.Point("wo.pulse", 1, "bool",
            "area=\"coil\" access=\"readwrite\" swap=\"abcd\"",
            "<Write pulseMs=\"400\" />");
        var xml = "<SamplerConfig schemaVersion=\"3.0\">" + GlobalFast +
                  "<Transports>" + ComplexStubFixture.Transport("p3", _stub.SimPortP3) + "</Transports>" +
                  "<Devices>" + ComplexStubFixture.Device("wo", "p3", 7, "ps") + "</Devices>" +
                  "<PointSets><PointSet id=\"ps\"><Points>" + point + "</Points></PointSet></PointSets>" +
                  "</SamplerConfig>";

        using var engine = new SamplerEngine(_stub.LoadConfig(xml));
        engine.Start();
        try
        {
            await ComplexStubFixture.WaitGoodAsync(engine, "wo", "wo.pulse");
            var beforeWrite = _stub.CountSimLogLines(_stub.SimPortP3, 7, "0x05");

            var result = await engine.SetValueAsync("wo", "wo.pulse", true);
            Assert.Equal(WriteOutcome.Succeeded, result.Outcome);

            // 脉冲：一次真写 + 一次自动写回 false
            Assert.Equal(2, _stub.CountSimLogLines(_stub.SimPortP3, 7, "0x05") - beforeWrite);

            // 线圈最终必须是 false（读-改-写路径把 0x0000 写回）
            var final = await ComplexStubFixture.WaitValueAsync(engine, "wo", "wo.pulse", (bool v) => !v, 3000);
            Assert.False(final);
        }
        finally
        {
            engine.Dispose();
        }
    }

    // ─────────────────────── C2-6：位点读-改-写只翻目标位 ───────────────────────

    [SkippableFact]
    public async Task C2_BitWrite_ReadModifyWrite_OnlyTogglesTargetBit()
    {
        _stub.RequireStub();

        // ENV-01（unit 6）4x@30「报警使能位」初值 0x000F（bit0..3 为 1）
        var points = string.Concat(
            ComplexStubFixture.Point("env.bit5", 30, "bool", "area=\"holding\" bit=\"5\" access=\"readwrite\""),
            ComplexStubFixture.Point("env.rawreg", 30, "uint16", "area=\"holding\" swap=\"abcd\" scanGroup=\"od\""));

        var xml = "<SamplerConfig schemaVersion=\"3.0\">" +
                  "<Global><Retry count=\"0\" /><Polling rateMs=\"200\" requestTimeoutMs=\"3000\" /></Global>" +
                  "<ScanGroups><ScanGroup id=\"normal\" mode=\"poll\" rateMs=\"200\" /><ScanGroup id=\"od\" mode=\"onDemand\" /></ScanGroups>" +
                  "<Transports>" + ComplexStubFixture.Transport("p3", _stub.SimPortP3) + "</Transports>" +
                  "<Devices>" + ComplexStubFixture.Device("env", "p3", 6, "ps") + "</Devices>" +
                  "<PointSets><PointSet id=\"ps\"><Defaults swap=\"abcd\" /><Points>" + points + "</Points></PointSet></PointSets>" +
                  "</SamplerConfig>";

        using var engine = new SamplerEngine(_stub.LoadConfig(xml));
        engine.Start();
        try
        {
            var debug = (IModbusDebugTool)engine;
            var initial = F.GetAs<ushort>(await debug.TriggerReadAsync("env", "env.rawreg"));
            Assert.Equal(0x000F, initial);

            // 置位 bit5：读-改-写后应为 0x002F（0x000F | 0x20），而不是 0x0020
            var set = await engine.SetValueAsync("env", "env.bit5", true);
            Assert.Equal(WriteOutcome.Succeeded, set.Outcome);
            var afterSet = F.GetAs<ushort>(await debug.TriggerReadAsync("env", "env.rawreg"));
            Assert.Equal(0x002F, afterSet);

            // 复位 bit5：回到 0x000F，其余位不受影响
            var clear = await engine.SetValueAsync("env", "env.bit5", false);
            Assert.Equal(WriteOutcome.Succeeded, clear.Outcome);
            var afterClear = F.GetAs<ushort>(await debug.TriggerReadAsync("env", "env.rawreg"));
            Assert.Equal(0x000F, afterClear);
        }
        finally
        {
            engine.Dispose();
        }
    }

    // ─────────────────────── C2-7：字符串/多字写回读（含空串） ───────────────────────

    [SkippableFact]
    public async Task C2_StringWriteRoundTrip_IncludingEmptyString()
    {
        _stub.RequireStub();

        // ENV-01（unit 6）4x@8 区域名称（10 字 / 20 字节，UTF-8）
        var point = ComplexStubFixture.Point("env.name", 8, "string",
            "area=\"holding\" length=\"10\" access=\"readwrite\"",
            "<String encoding=\"utf-8\" /><Write verify=\"true\" />");

        var xml = "<SamplerConfig schemaVersion=\"3.0\">" + GlobalFast +
                  "<Transports>" + ComplexStubFixture.Transport("p3", _stub.SimPortP3) + "</Transports>" +
                  "<Devices>" + ComplexStubFixture.Device("env", "p3", 6, "ps") + "</Devices>" +
                  "<PointSets><PointSet id=\"ps\"><Defaults swap=\"abcd\" /><Points>" + point + "</Points></PointSet></PointSets>" +
                  "</SamplerConfig>";

        using var engine = new SamplerEngine(_stub.LoadConfig(xml));
        engine.Start();
        try
        {
            var initial = F.GetAs<string>(await ((IModbusDebugTool)engine).TriggerReadAsync("env", "env.name"));
            Assert.Equal("注塑车间A区", initial);   // 镜像初值（UTF-8 20 字节点）

            var write = await engine.SetValueAsync("env", "env.name", "新厂区-2");
            Assert.Equal(WriteOutcome.Succeeded, write.Outcome);
            Assert.False(write.VerifyMismatch);
            Assert.Equal("新厂区-2",
                F.GetAs<string>(await ((IModbusDebugTool)engine).TriggerReadAsync("env", "env.name")));

            // 空串：编码为全 0x00 + trimNull 解回空串（覆盖 C1 里镜像「空串」点实为字面量 '' 的缺口）
            var empty = await engine.SetValueAsync("env", "env.name", "");
            Assert.Equal(WriteOutcome.Succeeded, empty.Outcome);
            Assert.Equal(string.Empty,
                F.GetAs<string>(await ((IModbusDebugTool)engine).TriggerReadAsync("env", "env.name")));
        }
        finally
        {
            engine.Dispose();
        }
    }

    // ─────────────────────── C2-8：写超时三态（silent → Indeterminate，且写请求只发一次） ───────────────────────

    [SkippableFact]
    public async Task C2_WriteTimeout_IsIndeterminate_AndWriteIsSentExactlyOnce()
    {
        _stub.RequireBreaker();
        _stub.RequireSimLog();

        var publicPort = ComplexStubFixture.PublicBasePort; // 2502 → P1-LINE-A（IM-01 unit 1）
        await _stub.ClearAllAsync();
        try
        {
            // 响应延迟 1200ms > 写超时 800ms：写与回读都超时 → 三态里的 Indeterminate。
            // 请求仍然到达从站 → 镜像日志按功能码可数出「写请求只发了一次」。
            var ack = await _stub.InjectAsync(
                "{\"port\":" + publicPort + ",\"unit\":1,\"action\":\"delay\",\"ms\":1200,\"direction\":\"resp\"}");
            Assert.True(ack.Ok, ack.ToString());

            var point = ComplexStubFixture.Point("im.curve", 41, "uint16",
                "area=\"holding\" access=\"readwrite\" swap=\"abcd\" scanGroup=\"od\"");
            var xml = "<SamplerConfig schemaVersion=\"3.0\">" +
                      "<Global><Retry count=\"0\" /><Polling rateMs=\"300\" requestTimeoutMs=\"800\" /></Global>" +
                      "<ScanGroups><ScanGroup id=\"normal\" mode=\"poll\" rateMs=\"300\" /><ScanGroup id=\"od\" mode=\"onDemand\" /></ScanGroups>" +
                      "<Transports>" + ComplexStubFixture.Transport("pub", publicPort, "tcp", 800) + "</Transports>" +
                      "<Devices>" + ComplexStubFixture.Device("im", "pub", 1, "ps") + "</Devices>" +
                      "<PointSets><PointSet id=\"ps\"><Points>" + point + "</Points></PointSet></PointSets>" +
                      "</SamplerConfig>";

            using var engine = new SamplerEngine(_stub.LoadConfig(xml));
            engine.Start();
            try
            {
                var beforeWrite = _stub.CountSimLogLines(_stub.SimPortP1, 1, "0x06");
                var beforeRead = _stub.CountSimLogLines(_stub.SimPortP1, 1, "0x03");

                var result = await engine.SetValueAsync("im", "im.curve", 0x0BAD);

                Assert.Equal(WriteOutcome.Indeterminate, result.Outcome);
                Assert.NotNull(result.Error);
                Assert.Equal("MODBUS.TIMEOUT", result.Error!.Code);
                Assert.False(result.Readback.HasValue);

                await Task.Delay(500); // 等迟到的应答被丢弃、确认没有第二次写
                Assert.Equal(1, _stub.CountSimLogLines(_stub.SimPortP1, 1, "0x06") - beforeWrite);
                Assert.Equal(1, _stub.CountSimLogLines(_stub.SimPortP1, 1, "0x03") - beforeRead);

                // 断路器日志同样可见被延迟的请求帧（decision=delay）
                Assert.True(_stub.CountBreakerFrames(publicPort, "req", 1) >= 2);
            }
            finally
            {
                engine.Dispose();
            }
        }
        finally
        {
            await _stub.ClearAllAsync();
        }
    }

    // ─────────────────────── C2-9：写审计事件（成败/拒绝 + 操作者身份） ───────────────────────

    [SkippableFact]
    public async Task C2_WriteAudit_EmitsPointWrittenEventsWithOutcomeAndUser()
    {
        _stub.RequireStub();

        var points = string.Concat(
            ComplexStubFixture.Point("env.limit", 2, "int16",
                "area=\"holding\" access=\"readwrite\" swap=\"abcd\" scanGroup=\"od\"",
                "<Write min=\"0\" max=\"500\" verify=\"true\" />"),
            ComplexStubFixture.Point("env.coil", 2, "bool",
                "area=\"coil\" access=\"readwrite\" scanGroup=\"od\""));

        var xml = "<SamplerConfig schemaVersion=\"3.0\">" + GlobalFast +
                  "<Transports>" + ComplexStubFixture.Transport("p3", _stub.SimPortP3) + "</Transports>" +
                  "<Devices>" +
                  ComplexStubFixture.Device("env", "p3", 6, "ps6") +
                  ComplexStubFixture.Device("ro", "p3", 11, "ps11") +
                  "</Devices>" +
                  "<PointSets>" +
                  "<PointSet id=\"ps6\"><Points>" + points + "</Points></PointSet>" +
                  "<PointSet id=\"ps11\"><Defaults swap=\"abcd\" /><Points>" +
                  ComplexStubFixture.Point("ro.v", 0, "uint16", "area=\"holding\" access=\"readwrite\" scanGroup=\"od\"") +
                  "</Points></PointSet>" +
                  "</PointSets></SamplerConfig>";

        using var engine = new SamplerEngine(_stub.LoadConfig(xml));
        engine.Start();
        try
        {
            using var audit = new ComplexStubFixture.EventCollector<PointWrittenEvent>(engine.Bus);

            // ① 成功（带操作者身份）
            var ok = await engine.SetValueAsync("env", "env.limit", 300, new ActingUser("op-a", "operator"));
            Assert.Equal(WriteOutcome.Succeeded, ok.Outcome);

            // ② 范围拒绝
            var rejected = await engine.SetValueAsync("env", "env.limit", 9999, new ActingUser("op-b", "operator"));
            Assert.Equal(WriteOutcome.Rejected, rejected.Outcome);

            // ③ 只读从站 → 设备拒绝
            var failed = await engine.SetValueAsync("ro", "ro.v", 7, new ActingUser("op-c", "operator"));
            Assert.Equal(WriteOutcome.Failed, failed.Outcome);

            // ④ 默认身份（Local）
            var local = await engine.SetValueAsync("env", "env.coil", true);
            Assert.Equal(WriteOutcome.Succeeded, local.Outcome);

            Assert.True(await audit.WaitCountAsync(4, 5000), "写审计事件应发出 4 条（含拒绝），实际 " + audit.Count);
            var events = audit.Items;

            var success = events.Single(e => e.PointId == "env.limit" && e.Outcome == "Succeeded");
            Assert.Equal("op-a", success.User);
            Assert.Equal("env.limit", success.PointId);
            Assert.Equal(300, Convert.ToInt32(success.Value, CultureInfo.InvariantCulture));
            Assert.Null(success.Message);

            var rejectedEvent = events.Single(e => e.Outcome == "Rejected");
            Assert.Equal("op-b", rejectedEvent.User);
            Assert.Equal("ss.reason.aboveMax", rejectedEvent.Message);

            var failedEvent = events.Single(e => e.Outcome == "Failed");
            Assert.Equal("op-c", failedEvent.User);
            Assert.Equal("ss.error.writeFailed", failedEvent.Message);

            var localEvent = events.Single(e => e.PointId == "env.coil");
            Assert.Equal("Local", localEvent.User);   // 无 user 重载 → 框架内置身份
        }
        finally
        {
            engine.Dispose();
        }
    }
}
