using System;
using System.Collections.Generic;
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
/// 驱动层混沌健壮性（真链路 + 可编程断路器 2502-2505 → 复杂桩 16002-16005）。
/// 与既有 C4（形态逐个注入）/ Backoff（设备级/链路级）的分工：本类补差集——
/// 按 unit 拦截的**故障影响范围**、两级退避的**升级**（同链路全从站退避 → 链路级）、
/// 故障叠加、手动 RetryLink、错误一窗一条的**聚合计数**、故障期间线程不静默死亡、混合拓扑隔离。
/// </summary>
[Collection(ComplexStubCollection.Name)]
public sealed class ComplexStubChaosTests
{
    private readonly ComplexStubFixture _stub;

    public ComplexStubChaosTests(ComplexStubFixture stub) => _stub = stub;

    private const int PortP1 = ComplexStubFixture.PublicBasePort;       // 2502 → IM/MTC/DRY/ROB unit 1-4
    private const int PortP2 = ComplexStubFixture.PublicBasePort + 1;   // 2503 → 产线 B unit 1-4
    private const int PortP3 = ComplexStubFixture.PublicBasePort + 2;   // 2504 → VERIFY unit 5-15
    private const int PortP4 = ComplexStubFixture.PublicBasePort + 3;   // 2505 → RTU 线 unit 1-4/20/21

    private static string Polled(string id, int address, string dataType = "uint16", string area = "holding")
        => ComplexStubFixture.Point(id, address, dataType, "area=\"" + area + "\"");

    private static string PointSet(string id, string points)
        => "<PointSet id=\"" + id + "\"><Defaults swap=\"abcd\" /><Points>" + points + "</Points></PointSet>";

    private static string Device(string id, string transport, int unitId, string pointSet)
        => ComplexStubFixture.Device(id, transport, unitId, pointSet);

    /// <summary>造引擎：多链路 + 多设备；默认开两层退避，onCommError=bad/null。</summary>
    private SamplerEngine BuildEngine(string transportsXml, string devicesXml, string pointSetsXml,
        string reconnect = "delays=\"2500,6000\"", int intervalMs = 250, int requestTimeoutMs = 600)
    {
        var xml = "<SamplerConfig schemaVersion=\"3.0\">" +
                  "<Global><Retry count=\"0\" intervalMs=\"10\" />" +
                  "<Polling defaultIntervalMs=\"" + intervalMs + "\" requestTimeoutMs=\"" + requestTimeoutMs + "\" />" +
                  "<Quality onCommError=\"bad\" onCommErrorValue=\"null\" />" +
                  "<Reconnect " + reconnect + " /></Global>" +
                  "<Transports>" + transportsXml + "</Transports>" +
                  "<Devices>" + devicesXml + "</Devices>" +
                  "<PointSets>" + pointSetsXml + "</PointSets>" +
                  "</SamplerConfig>";
        var engine = new SamplerEngine(_stub.LoadConfig(xml));
        engine.Start();
        return engine;
    }

    /// <summary>四个从站各一个点（各自点表），用于「按 unit 拦截不连累同链路其它从站」。</summary>
    private static (string devices, string pointSets) FourSlavesOnOneLink(string transport)
    {
        var devices = string.Empty;
        var pointSets = string.Empty;
        for (var unit = 1; unit <= 4; unit++)
        {
            devices += Device("d" + unit, transport, unit, "ps" + unit);
            pointSets += PointSet("ps" + unit, Polled("p", 0));
        }

        return (devices, pointSets);
    }

    // ───────────── H2：按 unit 拦截（一个从站坏不连累同链路其它从站，用户重点） ─────────────

    [SkippableFact]
    public async Task Chaos_UnitScopedFault_OneBadSlaveDoesNotDisturbItsSiblings()
    {
        _stub.RequireBreaker();
        await _stub.ClearAllAsync();
        try
        {
            var (devices, pointSets) = FourSlavesOnOneLink("pub");
            using var engine = BuildEngine(ComplexStubFixture.Transport("pub", PortP1), devices, pointSets);
            using var entered = new ComplexStubFixture.EventCollector<BackoffEnteredEvent>(engine.Bus);
            using var errors = new ComplexStubFixture.EventCollector<IErrorEvent>(engine.Bus);

            for (var unit = 1; unit <= 4; unit++)
            {
                await ComplexStubFixture.WaitGoodAsync(engine, "d" + unit, "p");
            }

            var before = Enumerable.Range(1, 4).ToDictionary(u => u, u => engine.GetValueDetail("d" + u, "p").Timestamp);
            var framesBefore = Enumerable.Range(1, 4).ToDictionary(u => u, u => _stub.CountBreakerFrames(PortP1, "req", u));

            // 只挂 unit2：silent（读超时 → 设备级）
            var ack = await _stub.InjectAsync("{\"port\":" + PortP1 + ",\"unit\":2,\"action\":\"silent\"}");
            Assert.True(ack.Ok, ack.ToString());

            // ① 故障从站：设备级退避 + 质量按 offlineQuality
            Assert.True(await entered.WaitAsync(e => e.Scope == BackoffScope.Device && e.DeviceId == "d2", 8000),
                "unit2 的读超时必须判为设备级退避");
            Assert.DoesNotContain(entered.Items, e => e.Scope == BackoffScope.Link);
            await ComplexStubFixture.WaitQualityAsync(engine, "d2", "p", PointQuality.Offline, 6000);
            Assert.Contains(errors.Items, e => e.Info.Code == "MODBUS.TIMEOUT");

            // ② 三个兄弟从站：质量保持 Good、时间戳推进（调度线程照常出请求）
            await Task.Delay(1500);
            for (var unit = 1; unit <= 4; unit++)
            {
                if (unit == 2) continue;
                var value = engine.GetValueDetail("d" + unit, "p");
                Assert.Equal(PointQuality.Good, value.Quality);
                Assert.True(value.Timestamp > before[unit], "unit" + unit + " 的采集被 unit2 的故障拖住了");
                Assert.True(_stub.CountBreakerFrames(PortP1, "req", unit) > framesBefore[unit],
                    "unit" + unit + " 的请求帧数在故障期间必须继续增长（线程没被拖死）");
            }

            // ③ 清除后 unit2 立刻恢复
            await _stub.ClearAsync(PortP1, 2);
            var recovered = await ComplexStubFixture.WaitQualityAsync(engine, "d2", "p", PointQuality.Good, 12000);
            Assert.True(recovered.Timestamp > before[2]);
        }
        finally
        {
            await _stub.ClearAllAsync();
        }
    }

    // ───────────── H3：两级退避升级（同链路全部从站退避 → 链路级） ─────────────

    [SkippableFact]
    public async Task Chaos_AllSlavesOnALinkBackingOff_EscalatesToLinkScope()
    {
        _stub.RequireBreaker();
        await _stub.ClearAllAsync();
        try
        {
            var (devices, pointSets) = FourSlavesOnOneLink("pub");
            using var engine = BuildEngine(ComplexStubFixture.Transport("pub", PortP1), devices, pointSets);
            using var entered = new ComplexStubFixture.EventCollector<BackoffEnteredEvent>(engine.Bus);

            for (var unit = 1; unit <= 4; unit++)
            {
                await ComplexStubFixture.WaitGoodAsync(engine, "d" + unit, "p");
            }

            // 整口 silent：四个从站的读全部超时 → 各自进入设备级 → 全部退避 → 升级链路级（防半开第二道防线）
            var ack = await _stub.InjectAsync("{\"port\":" + PortP1 + ",\"action\":\"silent\"}");
            Assert.True(ack.Ok, ack.ToString());

            Assert.True(await entered.WaitAsync(e => e.Scope == BackoffScope.Link, 20000),
                "同链路全部从站都进入设备级退避后必须升级为链路级退避（SS.BACKOFF.ALL_DEVICES）");
            var linkEntry = entered.Items.First(e => e.Scope == BackoffScope.Link);
            Assert.Equal("SS.BACKOFF.ALL_DEVICES", linkEntry.ReasonCode);
            Assert.Equal("pub", linkEntry.TransportId);
            Assert.Null(linkEntry.DeviceId);

            // 恢复在线 → 两台以上从站自动回到 Good（链路级退避到期后懒重连）
            await _stub.OnlineAsync(PortP1);
            for (var unit = 1; unit <= 4; unit++)
            {
                await ComplexStubFixture.WaitQualityAsync(engine, "d" + unit, "p", PointQuality.Good, 25000);
            }
        }
        finally
        {
            await _stub.ClearAllAsync();
        }
    }

    // ───────────── H3b：升级的对照——只一部分从站退避时**不**升级链路级 ─────────────

    [SkippableFact]
    public async Task Chaos_PartialSlavesBackingOff_DoNotEscalateToLinkScope()
    {
        _stub.RequireBreaker();
        await _stub.ClearAllAsync();
        try
        {
            var (devices, pointSets) = FourSlavesOnOneLink("pub");
            using var engine = BuildEngine(ComplexStubFixture.Transport("pub", PortP1), devices, pointSets);
            using var entered = new ComplexStubFixture.EventCollector<BackoffEnteredEvent>(engine.Bus);

            for (var unit = 1; unit <= 4; unit++)
            {
                await ComplexStubFixture.WaitGoodAsync(engine, "d" + unit, "p");
            }

            // 只挂 1/2/3 三台（留下 d4 正常）：三台各自进入设备级，但**不满足「全部从站退避」**
            var ack = await _stub.InjectAsync(TimeSpan.FromSeconds(8),
                "{\"port\":" + PortP1 + ",\"unit\":1,\"action\":\"silent\"}",
                "{\"port\":" + PortP1 + ",\"unit\":2,\"action\":\"silent\"}",
                "{\"port\":" + PortP1 + ",\"unit\":3,\"action\":\"silent\"}");
            Assert.True(ack.Ok, ack.ToString());

            for (var unit = 1; unit <= 3; unit++)
            {
                var unitId = unit;
                Assert.True(await entered.WaitAsync(
                        e => e.Scope == BackoffScope.Device && e.DeviceId == "d" + unitId, 10000),
                    "d" + unit + " 必须进入设备级退避");
            }

            var before = engine.GetValueDetail("d4", "p").Timestamp;
            await Task.Delay(3000);

            // 对照组：没有「全部从站退避」就不该出现 SS.BACKOFF.ALL_DEVICES 的链路级升级
            Assert.DoesNotContain(entered.Items,
                e => e.Scope == BackoffScope.Link && e.ReasonCode == "SS.BACKOFF.ALL_DEVICES");
            Assert.Equal(PointQuality.Good, engine.GetValueDetail("d4", "p").Quality);
            Assert.True(engine.GetValueDetail("d4", "p").Timestamp > before,
                "未受影响的从站必须照常采集（升级条件不能被部分退避误触发）");

            await _stub.ClearAllAsync();
            for (var unit = 1; unit <= 3; unit++)
            {
                await ComplexStubFixture.WaitQualityAsync(engine, "d" + unit, "p", PointQuality.Good, 15000);
            }
        }
        finally
        {
            await _stub.ClearAllAsync();
        }
    }

    // ───────────── H4：故障叠加（丢包 + 延迟抖动） ─────────────

    [SkippableFact]
    public async Task Chaos_DropPlusDelay_SuperimposedFaultsStillRecover()
    {
        _stub.RequireBreaker();
        await _stub.ClearAllAsync();
        try
        {
            using var engine = BuildEngine(ComplexStubFixture.Transport("pub", PortP1),
                Device("d", "pub", 1, "ps"), PointSet("ps", Polled("p", 0)),
                intervalMs: 300, requestTimeoutMs: 400);
            using var errors = new ComplexStubFixture.EventCollector<IErrorEvent>(engine.Bus);

            await ComplexStubFixture.WaitGoodAsync(engine, "d", "p");

            // 叠加：双向丢 25% + 应答延迟 250±250ms（超时预算 400ms → 一部分应答必然迟到）
            var ack = await _stub.InjectAsync(TimeSpan.FromSeconds(8),
                "{\"port\":" + PortP1 + ",\"unit\":1,\"action\":\"drop\",\"percent\":25,\"direction\":\"both\"}",
                "{\"port\":" + PortP1 + ",\"unit\":1,\"action\":\"delay\",\"ms\":250,\"jitter\":250,\"direction\":\"resp\"}");
            Assert.True(ack.Ok, ack.ToString());

            var framesBefore = _stub.CountBreakerFrames(PortP1, "req", 1);
            Assert.True(await errors.WaitCountAsync(1, 8000), "叠加故障下必须上报错误");
            await Task.Delay(1500);

            // 线程不静默死亡：叠加故障期间仍在出请求（帧数增长），错误不过量（一窗一条）
            Assert.True(_stub.CountBreakerFrames(PortP1, "req", 1) > framesBefore, "故障期间必须继续发请求");
            Assert.True(errors.Count <= 60, "错误事件数不应刷屏，实测 " + errors.Count);
            Assert.Contains(errors.Items, e => e.Info.Code == "MODBUS.TIMEOUT");

            // 清除两条规则 → 必须恢复 Good（没有永久卡死）
            await _stub.ClearAsync(PortP1, 1);
            await ComplexStubFixture.WaitQualityAsync(engine, "d", "p", PointQuality.Good, 15000);
        }
        finally
        {
            await _stub.ClearAllAsync();
        }
    }

    // ───────────── H5：手动 RetryLink 立即打断退避（不等满档位） ─────────────

    [SkippableFact]
    public async Task Chaos_ManualRetryLink_InterruptsBackoffImmediately()
    {
        _stub.RequireBreaker();
        await _stub.ClearAllAsync();
        try
        {
            var (devices, pointSets) = FourSlavesOnOneLink("pub");
            // 第一档 30s：没有手动重试就得干等半分钟
            using var engine = BuildEngine(ComplexStubFixture.Transport("pub", PortP1), devices, pointSets,
                reconnect: "delays=\"30000\"");
            using var entered = new ComplexStubFixture.EventCollector<BackoffEnteredEvent>(engine.Bus);

            await ComplexStubFixture.WaitGoodAsync(engine, "d1", "p");
            await ComplexStubFixture.WaitGoodAsync(engine, "d2", "p");

            var ack = await _stub.InjectAsync("{\"port\":" + PortP1 + ",\"action\":\"silent\"}");
            Assert.True(ack.Ok, ack.ToString());
            Assert.True(await entered.WaitAsync(e => e.Scope is BackoffScope.Device or BackoffScope.Link, 15000),
                "先进入退避（设备级或链路级）");

            // 现场修好：在线恢复，但退避还要等 30s → 手动 RetryLink 必须立刻打断
            await _stub.OnlineAsync(PortP1);

            var started = DateTime.UtcNow;
            var result = await ((IModbusDebugTool)engine).RetryLinkAsync("pub");
            var elapsed = DateTime.UtcNow - started;

            Assert.Equal(ManualRetryOutcome.Succeeded, result.Outcome);
            Assert.True(result.InterruptedBackoff, "RetryLinkAsync 必须打断正在进行的退避等待");
            Assert.True(elapsed < TimeSpan.FromSeconds(5), "必须立即重试，而不是等满 30s（实测 " + elapsed.TotalSeconds + "s）");
            await ComplexStubFixture.WaitQualityAsync(engine, "d1", "p", PointQuality.Good, 8000);
            await ComplexStubFixture.WaitQualityAsync(engine, "d2", "p", PointQuality.Good, 8000);
        }
        finally
        {
            await _stub.ClearAllAsync();
        }
    }

    // ───────────── H6：一窗一条聚合（不是每点一条）+ 丢弃计数可见 ─────────────

    [SkippableFact]
    public async Task Chaos_SilentOnOneWindow_AggregatesOneErrorPerWindow()
    {
        _stub.RequireBreaker();
        await _stub.ClearAllAsync();
        try
        {
            // 四个相邻点（holding 0..3）会被合并成同一个读窗口：窗口内 4 点共 void 一条聚合错误
            var points = string.Concat(Polled("p0", 0), Polled("p1", 1), Polled("p2", 2), Polled("p3", 3));
            using var engine = BuildEngine(ComplexStubFixture.Transport("pub", PortP3),
                Device("d", "pub", 7, "ps"), PointSet("ps", points),
                intervalMs: 300, requestTimeoutMs: 600);
            using var errors = new ComplexStubFixture.EventCollector<IErrorEvent>(engine.Bus);

            for (var i = 0; i < 4; i++)
            {
                await ComplexStubFixture.WaitGoodAsync(engine, "d", "p" + i);
            }

            var ack = await _stub.InjectAsync("{\"port\":" + PortP3 + ",\"unit\":7,\"action\":\"silent\"}");
            Assert.True(ack.Ok, ack.ToString());

            var until = DateTime.UtcNow.AddMilliseconds(2500);
            // 退避开启时失败设备的质量按 offlineQuality 置位（Offline），首拍可能先经 onCommError=bad
            await ComplexStubFixture.WaitUntilAsync(() => engine.GetValueDetail("d", "p0").Quality != PointQuality.Good, 6000);
            var remainder = until - DateTime.UtcNow;
            if (remainder > TimeSpan.Zero) await Task.Delay(remainder);

            // 600ms 超时 + 300ms 节拍 ≈ 2.5s 内 3~4 个窗口；按点发事件会是 ≥12 条
            Assert.InRange(errors.Count, 1, 6);
            Assert.All(errors.Items, e => Assert.Equal("MODBUS.TIMEOUT", e.Info.Code));
            Assert.All(errors.Items, e => Assert.Equal(4, e.Info.Context.ConsecutiveFailures)); // 聚合计数 = 窗口点位数

            // 丢弃必须可见（docs/03 §4.4）：指标可读且自洽
            var metrics = engine.Bus.Metrics;
            Assert.True(metrics.TotalEmitted >= errors.Count);
            Assert.True(metrics.TotalDropped <= metrics.TotalEmitted);
            Assert.Equal(0, metrics.TotalFaults);

            await _stub.OnlineAsync(PortP3, 7);
            await ComplexStubFixture.WaitQualityAsync(engine, "d", "p0", PointQuality.Good, 12000);
        }
        finally
        {
            await _stub.ClearAllAsync();
        }
    }

    // ───────────── H1/H7：形态逐个跑一段——线程不静默死亡 + 恢复时序 ─────────────

    /// <summary>形态表：注入命令、故障时长、是否需要 clear（规则型故障 online 清不掉）。</summary>
    public static IEnumerable<object[]> FaultForms()
    {
        yield return new object[] { "silent", "{\"port\":{P},\"unit\":1,\"action\":\"silent\"}", 1500, true, true };
        yield return new object[] { "refuse", "{\"port\":{P},\"action\":\"offline\",\"mode\":\"refuse\",\"seconds\":1.5}", 1800, false, false };
        yield return new object[] { "halfopen", "{\"port\":{P},\"action\":\"offline\",\"mode\":\"halfopen\",\"seconds\":2}", 2000, false, false };
        yield return new object[] { "drop50", "{\"port\":{P},\"unit\":1,\"action\":\"drop\",\"percent\":50,\"direction\":\"both\"}", 1500, true, true };
        yield return new object[] { "delay700", "{\"port\":{P},\"unit\":1,\"action\":\"delay\",\"ms\":700,\"direction\":\"resp\"}", 1500, true, true };
    }

    /// <summary>
    /// 5 种断路器形态逐个跑一段（另外 2 种：按 unit 拦截见 H2、RTU 坏 CRC 见既有 C4-7）：
    /// 每种都断言「故障期质量降级 + 有聚合错误 + 线程仍在出请求（能计帧的形态）+ 解除后恢复 Good」。
    /// </summary>
    [SkippableTheory]
    [MemberData(nameof(FaultForms))]
    public async Task Chaos_EachFaultForm_KeepsThePollingThreadAliveAndRecovers(
        string name, string commandTemplate, int faultMs, bool ruleBased, bool framesVisible)
    {
        _stub.RequireBreaker();
        await _stub.ClearAllAsync();
        try
        {
            using var engine = BuildEngine(ComplexStubFixture.Transport("pub", PortP1),
                Device("d", "pub", 1, "ps"), PointSet("ps", Polled("p", 0)),
                intervalMs: 250, requestTimeoutMs: 400);
            using var errors = new ComplexStubFixture.EventCollector<IErrorEvent>(engine.Bus);
            using var entered = new ComplexStubFixture.EventCollector<BackoffEnteredEvent>(engine.Bus);

            await ComplexStubFixture.WaitGoodAsync(engine, "d", "p");
            var before = engine.GetValueDetail("d", "p");
            var framesBefore = _stub.CountBreakerFrames(PortP1, "req", 1);

            var ack = await _stub.InjectAsync(commandTemplate.Replace("{P}", PortP1.ToString()));
            Assert.True(ack.Ok, name + " 注入失败：" + ack);

            // ① 质量必须降级（绝不静默失真）
            await ComplexStubFixture.WaitUntilAsync(
                () => engine.GetValueDetail("d", "p").Quality != PointQuality.Good, 8000);
            Assert.NotEqual(PointQuality.Good, engine.GetValueDetail("d", "p").Quality);

            // ② 必须有一窗一条的聚合错误
            Assert.True(await errors.WaitCountAsync(1, 6000), name + " 必须上报错误事件");

            // ③ 线程不静默死亡：能计帧的形态在故障期间必须继续出请求
            if (framesVisible)
            {
                Assert.True(_stub.CountBreakerFrames(PortP1, "req", 1) > framesBefore,
                    name + " 故障期间必须继续发请求（线程死了就再也不会增长）");
            }
            else
            {
                // refuse / halfopen：连接阶段就失败（请求帧到不了从站）→ 用「退避状态被记录」证明
                // 失败被分类、状态机仍在推进、线程没有静默死掉；故障期间不刷屏（退避抑制重复请求）
                Assert.True(await entered.WaitAsync(e => e.Scope is BackoffScope.Device or BackoffScope.Link, 8000),
                    name + " 必须记录退避状态事件（线程没静默死）");
            }

            await Task.Delay(Math.Max(0, faultMs - 800));

            // ④ 解除后恢复：时间戳推进 + 质量回 Good
            if (ruleBased)
            {
                await _stub.ClearAsync(PortP1, 1);
            }
            else
            {
                await _stub.OnlineAsync(PortP1);
            }

            var recovered = await ComplexStubFixture.WaitQualityAsync(engine, "d", "p", PointQuality.Good, 15000);
            Assert.True(recovered.Timestamp > before.Timestamp, name + " 恢复后必须继续采集（时间戳推进）");
        }
        finally
        {
            await _stub.ClearAllAsync();
        }
    }

    // ───────────── H9：混合拓扑（TCP + RTU-over-TCP + 多从站）互不连累 ─────────────

    [SkippableFact]
    public async Task Chaos_MixedTopology_TcpAndRtuLinksDoNotDragEachOther()
    {
        _stub.RequireBreaker();
        _stub.RequireRtu();
        await _stub.ClearAllAsync();
        try
        {
            // P1(TCP) unit1 + P2(TCP) unit1 + P4(RTU) unit1 三台从站同时采集
            using var engine = BuildEngine(
                ComplexStubFixture.Transport("t1", PortP1) + ComplexStubFixture.Transport("t2", PortP2) +
                ComplexStubFixture.Transport("rtu", PortP4, "rtuovertcp"),
                Device("a", "t1", 1, "ps1") + Device("b", "t2", 1, "ps2") + Device("c", "rtu", 1, "ps3"),
                PointSet("ps1", Polled("p", 0)) + PointSet("ps2", Polled("p", 0)) + PointSet("ps3", Polled("p", 0)),
                intervalMs: 300, requestTimeoutMs: 800);

            var a = await ComplexStubFixture.WaitGoodAsync(engine, "a", "p");
            var b = await ComplexStubFixture.WaitGoodAsync(engine, "b", "p");
            var c = await ComplexStubFixture.WaitGoodAsync(engine, "c", "p");
            Assert.True(a.Value != null && b.Value != null && c.Value != null);

            var framesA = _stub.CountBreakerFrames(PortP1, "req", 1);
            var framesB = _stub.CountBreakerFrames(PortP2, "req", 1);
            var framesC = _stub.CountBreakerFrames(PortP4, "req", 1);

            // 只挂 RTU 链路的 unit1：TCP 两条链路必须照常采集
            var ack = await _stub.InjectAsync("{\"port\":" + PortP4 + ",\"unit\":1,\"action\":\"silent\"}");
            Assert.True(ack.Ok, ack.ToString());

            await ComplexStubFixture.WaitUntilAsync(() => engine.GetValueDetail("c", "p").Quality != PointQuality.Good, 8000);
            await Task.Delay(1200);

            Assert.Equal(PointQuality.Good, engine.GetValueDetail("a", "p").Quality);
            Assert.Equal(PointQuality.Good, engine.GetValueDetail("b", "p").Quality);
            Assert.True(engine.GetValueDetail("a", "p").Timestamp > a.Timestamp);
            Assert.True(engine.GetValueDetail("b", "p").Timestamp > b.Timestamp);
            Assert.True(_stub.CountBreakerFrames(PortP1, "req", 1) > framesA);
            Assert.True(_stub.CountBreakerFrames(PortP2, "req", 1) > framesB);
            Assert.True(_stub.CountBreakerFrames(PortP4, "req", 1) > framesC);

            await _stub.ClearAsync(PortP4, 1);
            await ComplexStubFixture.WaitQualityAsync(engine, "c", "p", PointQuality.Good, 15000);
        }
        finally
        {
            await _stub.ClearAllAsync();
        }
    }
}
