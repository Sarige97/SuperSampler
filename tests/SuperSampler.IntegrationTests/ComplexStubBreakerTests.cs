using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SuperSampler.Abstractions.Errors;
using SuperSampler.Abstractions.Events;
using SuperSampler.Abstractions.Facade;
using SuperSampler.Abstractions.Values;
using SuperSampler.Core.Runtime;
using Xunit;

namespace SuperSampler.IntegrationTests;

/// <summary>
/// C4 断线演练（可编程断路器，公共口 2502-2505 → 内部口 16002-16005）：
/// refuse / silent / offline(Ns) / drop% / delay / halfopen / badcrc(仅 RTU) 逐个注入，断言
/// 质量策略映射、一窗一条聚合错误事件、恢复后质量回 Good、调度线程不死、事件总线丢弃计数可见。
///
/// 每条用例自成一体：开头 ClearAll 保证无残留故障，finally 再清一次；故障只作用于本条用例的
/// (端口[, 从站]) 作用域。
/// </summary>
[Collection(ComplexStubCollection.Name)]
public sealed class ComplexStubBreakerTests
{
    private readonly ComplexStubFixture _stub;

    public ComplexStubBreakerTests(ComplexStubFixture stub) => _stub = stub;

    private const int PortP1 = ComplexStubFixture.PublicBasePort;       // 2502 → IM/MTC/DRY/ROB unit 1-4
    private const int PortP3 = ComplexStubFixture.PublicBasePort + 2;   // 2504 → VERIFY unit 5-15
    private const int PortP4 = ComplexStubFixture.PublicBasePort + 3;   // 2505 → RTU 线 unit 1-4/20/21

    private static string PointPolled(string id, int address, string dataType, string area = "holding")
        => ComplexStubFixture.Point(id, address, dataType,
            "area=\"" + area + "\" swap=\"abcd\"");

    /// <summary>造一台只读一个点的设备（点随配置走），默认 polling 200ms / 超时 800ms。</summary>
    private SamplerEngine BuildEngine(int publicPort, int unitId, string pointsXml,
        string quality = "<Quality onCommError=\"bad\" onCommErrorValue=\"null\" />",
        int intervalMs = 200, int requestTimeoutMs = 800)
    {
        // 关掉两层退避：本组用例测的是 Quality@onCommError 策略、断路器各种故障形态与
        // 一窗一条聚合错误；退避期间不发请求会改变这些计数的口径（退避自身见 ComplexStubBackoffTests）
        var xml = "<SamplerConfig schemaVersion=\"3.0\">" +
                  "<Global><Retry count=\"0\" intervalMs=\"10\" /><Polling defaultIntervalMs=\"" + intervalMs +
                  "\" requestTimeoutMs=\"" + requestTimeoutMs + "\" />" +
                  "<Reconnect enabled=\"false\" />" + quality + "</Global>" +
                  "<Transports>" + ComplexStubFixture.Transport("pub", publicPort, "tcp", requestTimeoutMs) + "</Transports>" +
                  "<Devices>" + ComplexStubFixture.Device("d", "pub", unitId, "ps") + "</Devices>" +
                  "<PointSets><PointSet id=\"ps\"><Points>" + pointsXml + "</Points></PointSet></PointSets>" +
                  "</SamplerConfig>";
        var engine = new SamplerEngine(_stub.LoadConfig(xml));
        engine.Start();
        return engine;
    }

    // ─────────────────────── C4-1：质量策略映射 bad/offline/uncertain/keepLast ───────────────────────

    [SkippableTheory]
    [InlineData("bad", "null", "Bad")]
    [InlineData("offline", "null", "Offline")]
    [InlineData("uncertain", "null", "Uncertain")]
    [InlineData("bad", "keepLast", "KeepLast")]
    public async Task C4_QualityPolicy_OnCommErrorMapsToConfiguredQuality(string onCommError, string onCommErrorValue,
        string expected)
    {
        _stub.RequireBreaker();
        await _stub.ClearAllAsync();
        try
        {
            var quality = "<Quality onCommError=\"" + onCommError + "\" onCommErrorValue=\"" + onCommErrorValue + "\" />";
            using var engine = BuildEngine(PortP1, 1, PointPolled("p.temp", 0, "uint16", "input"), quality);
            using var errors = new ComplexStubFixture.EventCollector<IErrorEvent>(engine.Bus);

            await ComplexStubFixture.WaitGoodAsync(engine, "d", "p.temp");

            var beforeFault = engine.GetValueDetail("d", "p.temp");
            var ack = await _stub.InjectAsync("{\"port\":" + PortP1 + ",\"unit\":1,\"action\":\"silent\"}");
            Assert.True(ack.Ok, ack.ToString());

            if (expected == "KeepLast")
            {
                // keepLast：缓存完全不动（值 + 时间戳都不刷新），但故障必须已经上报（否则就是静默失真）
                Assert.True(await errors.WaitCountAsync(1, 6000), "通讯故障必须上报错误事件");
                await Task.Delay(800);
                var during = engine.GetValueDetail("d", "p.temp");
                Assert.Equal(PointQuality.Good, during.Quality);
                Assert.Equal(beforeFault.Timestamp, during.Timestamp);
                Assert.Equal(beforeFault.Value, during.Value);
            }
            else
            {
                var expectedQuality = (PointQuality)Enum.Parse(typeof(PointQuality), expected);
                var during = await ComplexStubFixture.WaitQualityAsync(engine, "d", "p.temp", expectedQuality, 6000);
                Assert.Null(during.Value);   // onCommErrorValue=null：坏值不留旧值
            }

            // 恢复：质量必须回到 Good，且时间戳推进（调度线程还活着、继续采集）
            await _stub.OnlineAsync(PortP1, 1);
            await ComplexStubFixture.WaitQualityAsync(engine, "d", "p.temp", PointQuality.Good, 8000);
            Assert.True(await ComplexStubFixture.WaitUntilAsync(
                    () => engine.GetValueDetail("d", "p.temp").Timestamp > beforeFault.Timestamp, 5000),
                "恢复后时间戳应推进（是否仍在持续采集）");
        }
        finally
        {
            await _stub.ClearAllAsync();
        }
    }

    // ─────────────────────── C4-2：refuse（连接被拒/重置）+ 自动恢复 ───────────────────────

    [SkippableFact]
    public async Task C4_Refuse_ResetsConnection_ThenAutoRecovers()
    {
        _stub.RequireBreaker();
        await _stub.ClearAllAsync();
        try
        {
            using var engine = BuildEngine(PortP1, 1, PointPolled("p.temp", 0, "uint16", "input"));
            using var errors = new ComplexStubFixture.EventCollector<IErrorEvent>(engine.Bus);

            await ComplexStubFixture.WaitGoodAsync(engine, "d", "p.temp");

            // 整口 refuse：停监听 + RST 现有连接；1.5s 后自动恢复在线
            var ack = await _stub.InjectAsync(
                "{\"port\":" + PortP1 + ",\"action\":\"offline\",\"mode\":\"refuse\",\"seconds\":1.5}");
            Assert.True(ack.Ok, ack.ToString());
            Assert.Equal("refuse", ack.Results.Single().Str("mode"));
            Assert.Equal("offline", ack.Results.Single().Str("state"));

            await ComplexStubFixture.WaitQualityAsync(engine, "d", "p.temp", PointQuality.Bad, 6000);
            // 分类（ADR D37，findings D23）：refuse 把已有连接 RST 掉之后，对端已关闭/复位 →
            // 必须归类为**链路错误**（MODBUS.LINK），而不是超时。宿主据此区分「设备断电/被 RST」
            // 与「设备慢」——两者的退避与告警策略不同。
            Assert.True(await errors.WaitAsync(e => e is LinkError, 5000),
                "RST/连接被拒必须上报链路错误事件");
            Assert.Contains(errors.Items, e => e.Info.Code == "MODBUS.LINK");
            Assert.InRange(_stub.CountBreakerFrames(PortP1, "req", 1), 1, int.MaxValue);

            // 到点自动恢复：质量回 Good、时间戳推进（轮询线程没被异常打死）
            var beforeRecover = engine.GetValueDetail("d", "p.temp");
            var recovered = await ComplexStubFixture.WaitQualityAsync(engine, "d", "p.temp", PointQuality.Good, 10000);
            Assert.True(recovered.Timestamp > beforeRecover.Timestamp, "恢复后应继续采集（时间戳推进）");
        }
        finally
        {
            await _stub.ClearAllAsync();
        }
    }

    // ─────────────────────── C4-3：silent（黑洞）→ 超时 + 一窗一条聚合错误 + 恢复 ───────────────────────

    [SkippableFact]
    public async Task C4_Silent_BlackholeAggregatesOneErrorPerWindow_AndRecovers()
    {
        _stub.RequireBreaker();
        await _stub.ClearAllAsync();
        try
        {
            // 两个相邻点（3x@1、3x@2）会被合并成同一个读窗口 → 窗口内两个点共 void 一条聚合错误事件
            var points = string.Concat(
                PointPolled("p.a", 1, "uint16", "input"),
                PointPolled("p.b", 2, "uint16", "input"));
            using var engine = BuildEngine(PortP3, 7, points, intervalMs: 300, requestTimeoutMs: 600);
            using var timeouts = new ComplexStubFixture.EventCollector<TimeoutError>(engine.Bus);

            await ComplexStubFixture.WaitGoodAsync(engine, "d", "p.a");
            await ComplexStubFixture.WaitGoodAsync(engine, "d", "p.b");

            var ack = await _stub.InjectAsync("{\"port\":" + PortP3 + ",\"unit\":7,\"action\":\"silent\"}");
            Assert.True(ack.Ok, ack.ToString());

            var silentUntil = DateTime.UtcNow.AddMilliseconds(2500);
            var badA = await ComplexStubFixture.WaitQualityAsync(engine, "d", "p.a", PointQuality.Bad, 6000);
            var badB = await ComplexStubFixture.WaitQualityAsync(engine, "d", "p.b", PointQuality.Bad, 6000);
            Assert.Equal(PointQuality.Bad, badA.Quality);
            Assert.Equal(PointQuality.Bad, badB.Quality);

            var remainder = silentUntil - DateTime.UtcNow;
            if (remainder > TimeSpan.Zero) await Task.Delay(remainder);

            // 单窗口超时 600ms → 2.5s 内实际只有 3~5 个窗口；若按点位发事件会 ≥ 8 条
            var count = timeouts.Count;
            Assert.InRange(count, 1, 6);
            Assert.True(count < 8, "一窗一条：窗口内 2 个点共 1 条聚合错误，实测 " + count + " 条");
            Assert.All(timeouts.Items, e => Assert.Equal("MODBUS.TIMEOUT", e.Info.Code));

            // 断言被吞掉的请求帧（断路器口径）
            Assert.Contains(_stub.ReadBreakerLog(), r =>
                r.Str("event") == "frame" && r.Int("port") == PortP3 && r.Int("unit") == 7 && r.Str("decision") == "swallow");

            await _stub.OnlineAsync(PortP3, 7);
            var recovered = await ComplexStubFixture.WaitQualityAsync(engine, "d", "p.a", PointQuality.Good, 8000);
            Assert.True(recovered.Timestamp > badA.Timestamp, "恢复后应继续采集（时间戳推进）");
        }
        finally
        {
            await _stub.ClearAllAsync();
        }
    }

    // ─────────────────────── C4-4：drop 30% ───────────────────────

    [SkippableFact]
    public async Task C4_Drop30Percent_CausesErrorsButKeepsPolling()
    {
        _stub.RequireBreaker();
        await _stub.ClearAllAsync();
        try
        {
            using var engine = BuildEngine(PortP1, 2, PointPolled("p.temp", 0, "uint16", "input"),
                intervalMs: 150, requestTimeoutMs: 300);
            using var errors = new ComplexStubFixture.EventCollector<IErrorEvent>(engine.Bus);

            await ComplexStubFixture.WaitGoodAsync(engine, "d", "p.temp");

            var ack = await _stub.InjectAsync(
                "{\"port\":" + PortP1 + ",\"unit\":2,\"action\":\"drop\",\"percent\":30,\"direction\":\"both\"}");
            Assert.True(ack.Ok, ack.ToString());

            // 双向 30% 丢帧：单窗口干干净净的概率只有 0.49 → 6 秒（约 20 个窗口）内必出错误事件
            Assert.True(await errors.WaitCountAsync(1, 6000), "30% 丢帧下必须出现错误事件");
            Assert.Contains(_stub.ReadBreakerLog(), r =>
                r.Str("event") == "frame" && r.Int("port") == PortP1 && r.Int("unit") == 2 && r.Str("decision") == "drop");

            // 调度线程不死：清掉故障规则（drop 是规则型故障，online 清不掉）后继续采集
            await _stub.ClearAsync(PortP1, 2);
            await ComplexStubFixture.WaitQualityAsync(engine, "d", "p.temp", PointQuality.Good, 8000);
        }
        finally
        {
            await _stub.ClearAllAsync();
        }
    }

    // ─────────────────────── C4-5：delay 300ms（在超时预算内 → 不误报） ───────────────────────

    [SkippableFact]
    public async Task C4_Delay300ms_StaysGoodWithinTimeoutBudget()
    {
        _stub.RequireBreaker();
        await _stub.ClearAllAsync();
        try
        {
            // 超时预算 1200ms > 延迟 300ms → 不应产生任何超时错误
            using var engine = BuildEngine(PortP1, 3, PointPolled("p.temp", 0, "uint16", "input"),
                intervalMs: 300, requestTimeoutMs: 1200);
            using var errors = new ComplexStubFixture.EventCollector<IErrorEvent>(engine.Bus);

            await ComplexStubFixture.WaitGoodAsync(engine, "d", "p.temp");
            var ack = await _stub.InjectAsync(
                "{\"port\":" + PortP1 + ",\"unit\":3,\"action\":\"delay\",\"ms\":300,\"direction\":\"resp\"}");
            Assert.True(ack.Ok, ack.ToString());

            await Task.Delay(2000);
            Assert.Equal(0, errors.Count);
            Assert.Equal(PointQuality.Good, engine.GetValueDetail("d", "p.temp").Quality);
            Assert.Contains(_stub.ReadBreakerLog(), r =>
                r.Str("event") == "frame" && r.Int("port") == PortP1 && r.Int("unit") == 3
                && r.Str("note") != null && r.Str("note")!.IndexOf("delay", StringComparison.Ordinal) >= 0);
        }
        finally
        {
            await _stub.ClearAllAsync();
        }
    }

    // ─────────────────────── C4-6：halfopen（接受连接但永不转发，到点恢复） ───────────────────────

    [SkippableFact]
    public async Task C4_HalfOpen_BlackholesThenRecovers()
    {
        _stub.RequireBreaker();
        await _stub.ClearAllAsync();
        try
        {
            using var engine = BuildEngine(PortP1, 4, PointPolled("p.temp", 0, "uint16", "input"),
                intervalMs: 300, requestTimeoutMs: 600);
            using var errors = new ComplexStubFixture.EventCollector<IErrorEvent>(engine.Bus);

            await ComplexStubFixture.WaitGoodAsync(engine, "d", "p.temp");

            var ack = await _stub.InjectAsync(
                "{\"port\":" + PortP1 + ",\"action\":\"offline\",\"mode\":\"halfopen\",\"seconds\":2}");
            Assert.True(ack.Ok, ack.ToString());
            Assert.Equal("halfopen", ack.Results.Single().Str("mode"));

            await ComplexStubFixture.WaitQualityAsync(engine, "d", "p.temp", PointQuality.Bad, 6000);
            var bad = engine.GetValueDetail("d", "p.temp");
            Assert.True(await errors.WaitCountAsync(1, 4000));
            Assert.Contains(_stub.ReadBreakerLog(), r =>
                r.Str("event") == "frame" && r.Int("port") == PortP1 && r.Str("decision") == "swallow");

            var recovered = await ComplexStubFixture.WaitQualityAsync(engine, "d", "p.temp", PointQuality.Good, 10000);
            Assert.True(recovered.Timestamp > bad.Timestamp, "恢复后应继续采集（时间戳推进）");
        }
        finally
        {
            await _stub.ClearAllAsync();
        }
    }

    // ─────────────────────── C4-7：badcrc（仅 RTU 口） ───────────────────────

    [SkippableFact]
    public async Task C4_BadCrc_OnRtuPort_IsDetectedAsLinkError()
    {
        _stub.RequireBreaker();
        _stub.RequireRtu();
        await _stub.ClearAllAsync();
        try
        {
            // P4 为 rtuOverTcp：篡改应答 CRC 后，主站必须校验出来（MODBUS.LINK，而不是超时）
            var points = PointPolled("p.temp", 0, "uint16", "input");
            var xml = "<SamplerConfig schemaVersion=\"3.0\">" +
                      "<Global><Retry count=\"0\" /><Polling defaultIntervalMs=\"300\" requestTimeoutMs=\"800\" />" +
                      "<Reconnect enabled=\"false\" />" +
                      "<Quality onCommError=\"bad\" onCommErrorValue=\"null\" /></Global>" +
                      "<Transports>" + ComplexStubFixture.Transport("pub", PortP4, "rtuovertcp", 800) + "</Transports>" +
                      "<Devices>" + ComplexStubFixture.Device("d", "pub", 1, "ps") + "</Devices>" +
                      "<PointSets><PointSet id=\"ps\"><Points>" + points + "</Points></PointSet></PointSets>" +
                      "</SamplerConfig>";

            using var engine = new SamplerEngine(_stub.LoadConfig(xml));
            engine.Start();
            using var errors = new ComplexStubFixture.EventCollector<IErrorEvent>(engine.Bus);
            try
            {
                await ComplexStubFixture.WaitGoodAsync(engine, "d", "p.temp");

                var ack = await _stub.InjectAsync(
                    "{\"port\":" + PortP4 + ",\"unit\":1,\"action\":\"badcrc\",\"percent\":100}");
                Assert.True(ack.Ok, ack.ToString());

                await ComplexStubFixture.WaitQualityAsync(engine, "d", "p.temp", PointQuality.Bad, 6000);
                Assert.True(await errors.WaitAsync(e => e is LinkError, 5000), "CRC 失败应分类为链路错误");
                Assert.All(errors.Items.OfType<LinkError>(), e => Assert.Equal("MODBUS.LINK", e.Info.Code));
                Assert.Contains(_stub.ReadBreakerLog(), r =>
                    r.Str("event") == "frame" && r.Int("port") == PortP4
                    && r.Str("note") != null && r.Str("note")!.IndexOf("badcrc", StringComparison.Ordinal) >= 0);

                // badcrc 同样是规则型故障：必须用 clear（online 不会清规则）
                await _stub.ClearAsync(PortP4, 1);
                await ComplexStubFixture.WaitQualityAsync(engine, "d", "p.temp", PointQuality.Good, 10000);
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

    // ─────────────────────── C4-8：事件总线丢弃计数可见（IEventBusMetrics） ───────────────────────

    [SkippableFact]
    public async Task C4_EventBusMetrics_ExposeDroppedEventsUnderSlowSubscriber()
    {
        _stub.RequireStub();

        // 直连镜像（不走断路器）：WO-ABCD 三个每拍都变的点位，20ms 快轮询 → 高频值事件
        var points = string.Concat(
            PointPolled("p.heartbeat", 220, "uint16"),
            PointPolled("p.hb2", 3, "uint16", "input"),
            PointPolled("p.sin", 221, "float32"));

        using var engine = BuildEngineForDirectStub(points);
        // 慢订阅者 + 容量 1 队列 + DropNewest：必然溢出，丢弃必须可见（绝不允许静默丢事件）
        using var slow = engine.Bus.Subscribe<PointValueChangedEvent>(
            _ => Thread.Sleep(150), DeliveryMode.Queued, OverflowPolicy.DropNewest, queueCapacity: 1);

        await Task.Delay(2500);

        Assert.True(slow.Dropped > 0, "慢订阅者必然丢事件：Dropped 应 > 0");
        var metrics = engine.Bus.Metrics;
        Assert.True(metrics.TotalEmitted > 0);
        Assert.True(metrics.TotalDropped > 0, "IEventBusMetrics.TotalDropped 必须可见");
        Assert.True(metrics.TotalDropped < metrics.TotalEmitted, "丢弃数不应超过发出数");
        Assert.True(metrics.ActiveSubscriptions >= 1);
        Assert.Equal(0, metrics.TotalFaults);
    }

    private SamplerEngine BuildEngineForDirectStub(string pointsXml)
    {
        var xml = "<SamplerConfig schemaVersion=\"3.0\">" +
                  "<Global><Retry count=\"0\" /><Polling defaultIntervalMs=\"60\" requestTimeoutMs=\"1000\" />" +
                  "<Reconnect enabled=\"false\" />" +
                  "<Quality onCommError=\"bad\" onCommErrorValue=\"null\" /></Global>" +
                  "<Transports>" + ComplexStubFixture.Transport("sim", _stub.SimPortP3, "tcp", 1000) + "</Transports>" +
                  "<Devices>" + ComplexStubFixture.Device("wo", "sim", 7, "ps") + "</Devices>" +
                  "<PointSets><PointSet id=\"ps\"><Defaults swap=\"abcd\" /><Points>" + pointsXml + "</Points></PointSet></PointSets>" +
                  "</SamplerConfig>";
        var engine = new SamplerEngine(_stub.LoadConfig(xml));
        engine.Start();
        return engine;
    }
}
