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
/// 两层退避的真链路演练（PROD-17..20，docs/01 §6.3）：复杂桩 + 可编程断路器。
/// <list type="bullet">
/// <item>设备级：只对 unit1 注入 <c>silent</c>（读超时）→ 只排 unit1；退避期间**不再发请求**（断路器帧计数不增）；
/// 同链路 unit2 照常采集；退避到期自动恢复 + 进入/恢复事件各一条；</item>
/// <item>链路级：整口 <c>refuse</c>（连接被拒/RST）→ 判链路级、关闭连接、等待后自动重连；</item>
/// <item>手动重试：<c>RetryDeviceAsync</c> 立刻打断退避、立即重试成功（不等满当前档位）；</item>
/// <item>质量口径：退避期间按 <c>Reconnect@offlineQuality</c>（offline/bad）置位。</item>
/// </list>
/// 与 C4 组（ComplexStubBreakerTests）的分工：C4 用 <c>Reconnect enabled="false"</c> 测质量策略与断路器形态，
/// 本组开退避，专门测退避的判定、节流与恢复。
/// </summary>
[Collection(ComplexStubCollection.Name)]
public sealed class ComplexStubBackoffTests
{
    private readonly ComplexStubFixture _stub;

    public ComplexStubBackoffTests(ComplexStubFixture stub) => _stub = stub;

    private const int PortP1 = ComplexStubFixture.PublicBasePort;   // 2502 → IM/MTC/DRY/ROB unit 1-4

    private static string Point(string id, int address, string attrs = "")
        => ComplexStubFixture.Point(id, address, "uint16", "area=\"holding\" swap=\"abcd\" " + attrs);

    /// <summary>造引擎：reconnect 为 <c>&lt;Reconnect&gt;</c> 的属性串；两/多台从站在同一条链路上。</summary>
    private SamplerEngine BuildEngine(string devicesXml, string pointsXml, string reconnect,
        int intervalMs = 200, int requestTimeoutMs = 600)
    {
        var xml = "<SamplerConfig schemaVersion=\"3.0\">" +
                  "<Global><Retry count=\"0\" /><Polling defaultIntervalMs=\"" + intervalMs +
                  "\" requestTimeoutMs=\"" + requestTimeoutMs + "\" />" +
                  "<Quality onCommError=\"bad\" onCommErrorValue=\"null\" />" +
                  "<Reconnect " + reconnect + " /></Global>" +
                  "<Transports>" + ComplexStubFixture.Transport("pub", PortP1, "tcp", requestTimeoutMs) + "</Transports>" +
                  "<Devices>" + devicesXml + "</Devices>" +
                  "<PointSets><PointSet id=\"ps\"><Defaults swap=\"abcd\" /><Points>" + pointsXml + "</Points></PointSet></PointSets>" +
                  "</SamplerConfig>";
        var engine = new SamplerEngine(_stub.LoadConfig(xml));
        engine.Start();
        return engine;
    }

    /// <summary>IM-01(unit1) 与 MTC-01(unit2) 同链路：一个坏点各一台，用于「设备级只影响自己」。</summary>
    private const string TwoSlaves =
        "<Device id=\"im\" transport=\"pub\" unitId=\"1\" pointSet=\"ps\" />" +
        "<Device id=\"mtc\" transport=\"pub\" unitId=\"2\" pointSet=\"ps\" />";

    private const string TwoPoints = "<Point id=\"p.im\" address=\"0\" /><Point id=\"p.mtc\" address=\"0\" />";

    // ─────────────────────── PROD-17：silent → 设备级 ───────────────────────

    [SkippableFact]
    public async Task Backoff_SilentOnOneSlave_IsDeviceScope_AndStopsRequestsUntilRecovery()
    {
        _stub.RequireBreaker();
        await _stub.ClearAllAsync();
        try
        {
            // 第一档 4000ms：远大于本用例的观察窗，退避期间「不发请求」才可观测
            using var engine = BuildEngine(TwoSlaves, TwoPoints, "delays=\"4000,8000\"");
            using var entered = new ComplexStubFixture.EventCollector<BackoffEnteredEvent>(engine.Bus);
            using var recovered = new ComplexStubFixture.EventCollector<BackoffRecoveredEvent>(engine.Bus);
            using var errors = new ComplexStubFixture.EventCollector<IErrorEvent>(engine.Bus);

            await ComplexStubFixture.WaitGoodAsync(engine, "im", "p.im");
            await ComplexStubFixture.WaitGoodAsync(engine, "mtc", "p.mtc");

            var ack = await _stub.InjectAsync("{\"port\":" + PortP1 + ",\"unit\":1,\"action\":\"silent\"}");
            Assert.True(ack.Ok, ack.ToString());

            // ① 判为设备级（链路级另行断言），且入口事件带档位与下次重试时刻
            Assert.True(await entered.WaitAsync(e => e.Scope == BackoffScope.Device && e.DeviceId == "im", 8000),
                "silent（读超时）必须判为设备级退避");
            var entry = entered.Items.First(e => e.Scope == BackoffScope.Device && e.DeviceId == "im");
            Assert.Equal(1, entry.Attempt);
            Assert.Equal(4000, entry.DelayMs);
            Assert.Equal("MODBUS.TIMEOUT", entry.ReasonCode);
            Assert.Equal("pub", entry.TransportId);
            Assert.Equal(1, entry.UnitId);
            Assert.Contains(errors.Items, e => e.Info.Code == "MODBUS.TIMEOUT"); // 失败本身照旧上报
            Assert.DoesNotContain(entered.Items, e => e.Scope == BackoffScope.Link);

            // ② 质量按 offlineQuality（默认 offline）置位
            var during = await ComplexStubFixture.WaitQualityAsync(engine, "im", "p.im", PointQuality.Offline, 6000);
            Assert.Null(during.Value);

            // ③ 退避期间**不再发任何请求**（断路器口径：unit1 的请求帧计数冻结）
            var framesWhenEntered = _stub.CountBreakerFrames(PortP1, "req", 1);
            Assert.True(framesWhenEntered >= 1, "进入退避前必须至少发过一次请求");
            await Task.Delay(1500);
            Assert.Equal(framesWhenEntered, _stub.CountBreakerFrames(PortP1, "req", 1));

            // ④ 其它从站不受影响（设备级不是链路级）
            await ComplexStubFixture.WaitQualityAsync(engine, "mtc", "p.mtc", PointQuality.Good, 3000);

            // ⑤ 恢复在线 → 自动回来 + 恢复事件一条
            await _stub.OnlineAsync(PortP1, 1);
            await ComplexStubFixture.WaitQualityAsync(engine, "im", "p.im", PointQuality.Good, 15000);
            Assert.True(await recovered.WaitAsync(e => e.Scope == BackoffScope.Device && e.DeviceId == "im", 6000),
                "恢复必须发一条 BackoffRecoveredEvent");
            Assert.Equal(1, recovered.Items.Count(e => e.Scope == BackoffScope.Device && e.DeviceId == "im"));
        }
        finally
        {
            await _stub.ClearAllAsync();
        }
    }

    // ─────────────────────── PROD-18：refuse → 链路级 ───────────────────────

    [SkippableFact]
    public async Task Backoff_RefuseOnPort_IsLinkScope_AndAutoRecovers()
    {
        _stub.RequireBreaker();
        await _stub.ClearAllAsync();
        try
        {
            using var engine = BuildEngine(TwoSlaves, TwoPoints, "delays=\"1000,2000\"", intervalMs: 300);
            using var entered = new ComplexStubFixture.EventCollector<BackoffEnteredEvent>(engine.Bus);
            using var recovered = new ComplexStubFixture.EventCollector<BackoffRecoveredEvent>(engine.Bus);
            using var errors = new ComplexStubFixture.EventCollector<IErrorEvent>(engine.Bus);

            await ComplexStubFixture.WaitGoodAsync(engine, "im", "p.im");
            await ComplexStubFixture.WaitGoodAsync(engine, "mtc", "p.mtc");

            // 整口 refuse：停监听 + RST 现有连接（不自动恢复 → 末尾用 online 显式恢复，
            // 这样「退避期间」的观察窗由我们掌控，不与自动恢复赛跑）
            var ack = await _stub.InjectAsync("{\"port\":" + PortP1 + ",\"action\":\"offline\",\"mode\":\"refuse\"}");
            Assert.True(ack.Ok, ack.ToString());

            // ① 质率先行：链路级退避期间两台从站都按 offlineQuality（默认 offline）置位
            await ComplexStubFixture.WaitQualityAsync(engine, "im", "p.im", PointQuality.Offline, 15000);
            await ComplexStubFixture.WaitQualityAsync(engine, "mtc", "p.mtc", PointQuality.Offline, 15000);

            // ② IO 层失败 → 链路级（设备级那条也可能先出现：谁先读到失败谁先记，这里只锁链路级）
            Assert.True(await entered.WaitAsync(e => e.Scope == BackoffScope.Link, 8000),
                "refuse（连接被拒/RST）必须判为链路级退避");
            var linkEntry = entered.Items.First(e => e.Scope == BackoffScope.Link);
            Assert.Equal("MODBUS.LINK", linkEntry.ReasonCode);
            Assert.Null(linkEntry.DeviceId);
            Assert.True(await errors.WaitAsync(e => e is LinkError, 5000), "链路失败必须上报 LinkError");

            // ③ 显式恢复在线：链路级退避到期后懒重连 → 两台从站都自动回来
            await _stub.OnlineAsync(PortP1);
            await ComplexStubFixture.WaitQualityAsync(engine, "im", "p.im", PointQuality.Good, 20000);
            await ComplexStubFixture.WaitQualityAsync(engine, "mtc", "p.mtc", PointQuality.Good, 20000);
            Assert.True(await recovered.WaitAsync(e => e.Scope == BackoffScope.Link, 8000),
                "链路恢复必须发一条链路级 BackoffRecoveredEvent");
        }
        finally
        {
            await _stub.ClearAllAsync();
        }
    }

    // ─────────────────────── PROD-19：手动重试立即生效 ───────────────────────

    [SkippableFact]
    public async Task Backoff_ManualRetry_InterruptsTheWaitImmediately()
    {
        _stub.RequireBreaker();
        await _stub.ClearAllAsync();
        try
        {
            // 第一档 30s：没有手动重试就得干等半分钟，因此「打断」是可观测的事实
            using var engine = BuildEngine(TwoSlaves, TwoPoints, "delays=\"30000\"");
            using var entered = new ComplexStubFixture.EventCollector<BackoffEnteredEvent>(engine.Bus);

            await ComplexStubFixture.WaitGoodAsync(engine, "im", "p.im");
            var ack = await _stub.InjectAsync("{\"port\":" + PortP1 + ",\"unit\":1,\"action\":\"silent\"}");
            Assert.True(ack.Ok, ack.ToString());

            Assert.True(await entered.WaitAsync(e => e.Scope == BackoffScope.Device && e.DeviceId == "im", 8000),
                "先进入设备级退避（30s）");
            await ComplexStubFixture.WaitQualityAsync(engine, "im", "p.im", PointQuality.Offline, 6000);

            // 现场修好了：在线恢复，但退避还要等 30s → 手动重试必须立刻打断
            await _stub.OnlineAsync(PortP1, 1);

            var started = DateTime.UtcNow;
            var result = await engine.RetryDeviceAsync("im");

            Assert.Equal(ManualRetryOutcome.Succeeded, result.Outcome);
            Assert.True(result.InterruptedBackoff, "RetryDeviceAsync 必须打断正在进行的退避等待");
            Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(3), "必须立即重试，而不是等满 30s");
            Assert.True(engine.GetValueDetail("im", "p.im").IsGood, "手动重试成功后质量回 Good");
            Assert.True(engine.GetValueAge("im", "p.im") < TimeSpan.FromSeconds(2), "手动重试也是一次成功采集");
        }
        finally
        {
            await _stub.ClearAllAsync();
        }
    }

    // ─────────────────────── PROD-20：质量口径 offlineQuality ───────────────────────

    [SkippableFact]
    public async Task Backoff_OfflineQualityBad_DrivesPointQualityDuringBackoff()
    {
        _stub.RequireBreaker();
        await _stub.ClearAllAsync();
        try
        {
            using var engine = BuildEngine(TwoSlaves, TwoPoints,
                "delays=\"4000\" offlineQuality=\"bad\"", intervalMs: 300);
            using var entered = new ComplexStubFixture.EventCollector<BackoffEnteredEvent>(engine.Bus);

            await ComplexStubFixture.WaitGoodAsync(engine, "mtc", "p.mtc");

            var ack = await _stub.InjectAsync("{\"port\":" + PortP1 + ",\"unit\":2,\"action\":\"silent\"}");
            Assert.True(ack.Ok, ack.ToString());

            Assert.True(await entered.WaitAsync(e => e.Scope == BackoffScope.Device && e.DeviceId == "mtc", 8000),
                "先进入退避");
            var during = await ComplexStubFixture.WaitQualityAsync(engine, "mtc", "p.mtc", PointQuality.Bad, 6000);

            Assert.Equal(PointQuality.Bad, during.Quality);   // 按 offlineQuality=bad，而不是 onCommError 的 Offline 分支
            Assert.Null(during.Value);                        // 值策略仍由 onCommErrorValue=null 决定
        }
        finally
        {
            await _stub.ClearAllAsync();
        }
    }
}
