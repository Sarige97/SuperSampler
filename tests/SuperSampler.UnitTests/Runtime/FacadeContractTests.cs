using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
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
/// 设备管理门面契约（本轮重测）：<c>GetValue</c> / <c>GetValueDetail</c> / <c>GetValueAge</c> 与寻址边界。
/// 对应 <c>_testplan/模块重测-写管道门面-方案.md</c> §八、§九。
/// </summary>
/// <remarks>
/// 不重复已有的：<c>FacadeWriteGapTests</c> G-F 族（格式化 / nullText / 未知 id / 零通讯 / RawRead 开关）、
/// <c>FieldBehaviorMatrixTests</c>（缩放 / 枚举 / 前后缀 / i18n 的显示串）、
/// <c>BackoffTests.B13–B15</c>（年龄的 null / 增长 / 未知 id）。
/// </remarks>
public class FacadeContractTests
{
    private static SamplerConfiguration Cfg(string pointsXml, string globalExtra = "", string calculatedXml = "")
        => SamplerConfigLoader.Load(
            XDocument.Parse(string.Format(XML, globalExtra, pointsXml, calculatedXml)),
            Directory.GetCurrentDirectory());

    private static SamplerEngine Engine(FakeModbusLink link, string pointsXml, string globalExtra = "",
        string calculatedXml = "")
        => new(Cfg(pointsXml, globalExtra, calculatedXml), new Dictionary<string, IModbusLink> { ["tcp1"] = link });

    private const string XML = """
        <SamplerConfig schemaVersion="3.0">
          <Global nullText="--">{0}</Global>
          <Transports><Transport id="tcp1" host="127.0.0.1" /></Transports>
          <Devices><Device id="d1" transport="tcp1" pointSet="ps1" unitId="7" /></Devices>
          <PointSets><PointSet id="ps1">
            <Points>{1}</Points>
            <Calculated>{2}</Calculated>
          </PointSet></PointSets>
        </SamplerConfig>
        """;

    private const string U16 = """<Point id="p" address="10" mode="onDemand" />""";
    private const string NAN_F32 = """<Point id="nan" address="12" dataType="float32" length="2" swap="abcd" mode="onDemand" />""";
    private const string CALC_BAD_DEP = """<Point id="c" dataType="uint16"><Expression>P('raw') * 2</Expression></Point>""";

    // ═══════════════ GetValue / GetValueDetail ═══════════════

    /// <summary>
    /// 质量非 Good 一律给全局 nullText：未采集（notInCache）、解码降级（NaN → Uncertain）、
    /// 计算点坏值（Bad）三态都验；Good 时走 Format 管道。
    /// （离线/通讯失败态由 SchedulerFunctionTests.GetValue_returns_the_configured_null_text_for_every_non_good_quality 覆盖。）
    /// </summary>
    [Fact]
    public async Task GetValue_returns_the_global_null_text_for_every_non_good_quality()
    {
        var link = new FakeModbusLink();
        using var engine = Engine(link, string.Concat(U16, NAN_F32), calculatedXml: CALC_BAD_DEP);

        // ① 从未采集 → Bad(notInCache)
        Assert.Equal("--", engine.GetValue("d1", "p"));
        Assert.Equal("ss.reason.notInCache", engine.GetValueDetail("d1", "p").Reason);

        // ② 解码降级：NaN → Uncertain（值保留但不许当好值显示）
        link.SetReadData(7, DataArea.HoldingRegister, 12, 0x7FC0, 0x0000);   // float32 NaN
        var nan = await ((IModbusDebugTool)engine).TriggerReadAsync("d1", "nan");
        Assert.Equal(PointQuality.Uncertain, nan.Quality);
        Assert.Equal("ss.reason.nan", nan.Reason);
        Assert.Equal("--", engine.GetValue("d1", "nan"));

        // ③ 计算点依赖坏值 → Bad(ss.reason.calculate)
        var bad = engine.GetValueDetail("d1", "c");
        Assert.Equal(PointQuality.Bad, bad.Quality);
        Assert.Equal("ss.reason.calculate", bad.Reason);
        Assert.Equal("--", engine.GetValue("d1", "c"));

        // ④ Good → 走 Format（无 <Format> 时 Invariant 直出）
        link.SetReadData(7, DataArea.HoldingRegister, 10, 42);
        var good = await ((IModbusDebugTool)engine).TriggerReadAsync("d1", "p");
        Assert.True(good.IsGood);
        Assert.Equal("42", engine.GetValue("d1", "p"));
    }

    /// <summary>GetValueDetail 三元组：值 / 质量 / 时间戳都在，且时间戳落在采集窗口内。</summary>
    [Fact]
    public async Task GetValueDetail_carries_value_quality_timestamp_and_reason()
    {
        var link = new FakeModbusLink();
        link.SetReadData(7, DataArea.HoldingRegister, 10, 42);
        using var engine = Engine(link, U16);

        var before = DateTimeOffset.UtcNow;
        await ((IModbusDebugTool)engine).TriggerReadAsync("d1", "p");
        var after = DateTimeOffset.UtcNow;

        var detail = engine.GetValueDetail("d1", "p");

        Assert.Equal((ushort)42, Assert.IsType<ushort>(detail.Value));
        Assert.Equal(PointQuality.Good, detail.Quality);
        Assert.True(detail.Reason is null or "", "Good 值不该带原因，实际 " + detail.Reason);
        Assert.InRange(detail.Timestamp, before.AddSeconds(-1), after.AddSeconds(1));
    }

    /// <summary>GetValueDetail 读缓存不发通讯（含计算点实时求值）；高频调用安全。</summary>
    [Fact]
    public void GetValueDetail_never_communicates_and_tolerates_high_frequency_reads()
    {
        var link = new FakeModbusLink();
        using var engine = Engine(link, string.Concat(U16, NAN_F32), calculatedXml: CALC_BAD_DEP);

        for (var i = 0; i < 10_000; i++)
        {
            _ = engine.GetValueDetail("d1", "p");
            _ = engine.GetValueDetail("d1", "c");   // 计算点：实时求值，同样不该发通讯
            _ = engine.GetValue("d1", "p");
        }

        Assert.Empty(link.Calls);
    }

    /// <summary>未知 / 非法 id 一律 KeyNotFoundException（不是 NRE、不是静默返回空）。</summary>
    [Fact]
    public void Unknown_and_malformed_ids_throw_key_not_found_on_every_facade_method()
    {
        var link = new FakeModbusLink();
        using var engine = Engine(link, U16);

        Assert.Throws<KeyNotFoundException>(() => engine.GetValue("d1", "nope"));
        Assert.Throws<KeyNotFoundException>(() => engine.GetValue("nope", "p"));
        Assert.Throws<KeyNotFoundException>(() => engine.GetValueDetail("d1", "nope"));
        Assert.Throws<KeyNotFoundException>(() => engine.GetValueDetail(null!, "p"));
        Assert.Throws<KeyNotFoundException>(() => engine.GetValueDetail("d1", null!));
        Assert.Throws<KeyNotFoundException>(() => engine.GetValueAge("d1", "nope"));

        // 寻址主键是 Ordinal：大小写不折叠、空格不 trim（配置里的 id 写成什么就必须写成什么）
        Assert.Throws<KeyNotFoundException>(() => engine.GetValue("D1", "p"));
        Assert.Throws<KeyNotFoundException>(() => engine.GetValue("d1 ", "p"));
        Assert.Throws<KeyNotFoundException>(() => engine.GetValue("d1", " p"));
        Assert.Throws<KeyNotFoundException>(() => engine.GetValue("d1", "P"));
        Assert.Empty(link.Calls);
    }

    /// <summary>同一 pointId 存在于两台设备时不串台：缓存与解码各归各的从站。</summary>
    [Fact]
    public async Task The_same_point_id_on_two_devices_reads_its_own_slave()
    {
        var link = new FakeModbusLink();
        link.SetReadData(7, DataArea.HoldingRegister, 10, 111);
        link.SetReadData(8, DataArea.HoldingRegister, 10, 222);
        var two = SamplerConfigLoader.Load(XDocument.Parse("""
            <SamplerConfig schemaVersion="3.0">
              <Global nullText="--" />
              <Transports><Transport id="tcp1" host="127.0.0.1" /></Transports>
              <Devices>
                <Device id="d1" transport="tcp1" pointSet="ps1" unitId="7" />
                <Device id="d2" transport="tcp1" pointSet="ps1" unitId="8" />
              </Devices>
              <PointSets><PointSet id="ps1"><Points>
                <Point id="p" address="10" mode="onDemand" />
              </Points></PointSet></PointSets>
            </SamplerConfig>
            """), Directory.GetCurrentDirectory());
        using var engine = new SamplerEngine(two, new Dictionary<string, IModbusLink> { ["tcp1"] = link });

        await ((IModbusDebugTool)engine).TriggerReadAsync("d1", "p");
        await ((IModbusDebugTool)engine).TriggerReadAsync("d2", "p");

        Assert.Equal((ushort)111, Assert.IsType<ushort>(engine.GetValueDetail("d1", "p").Value));
        Assert.Equal((ushort)222, Assert.IsType<ushort>(engine.GetValueDetail("d2", "p").Value));
        Assert.Equal("111", engine.GetValue("d1", "p"));
        Assert.Equal("222", engine.GetValue("d2", "p"));
        Assert.Equal(new byte[] { 7, 8 }, link.Calls.Where(c => c.IsRead).Select(c => c.UnitId));
    }

    /// <summary>计算点不可写：SetValueAsync 在门面层就被拒绝，且不发任何通讯。</summary>
    [Fact]
    public async Task Calculated_points_are_not_writable()
    {
        var link = new FakeModbusLink();
        using var engine = Engine(link, string.Empty, calculatedXml: CALC_BAD_DEP);

        var result = await engine.SetValueAsync("d1", "c", 5);

        Assert.Equal(WriteOutcome.Rejected, result.Outcome);
        Assert.Equal("ss.reason.notWritable", result.Error!.MessageKey);
        Assert.Empty(link.Calls);
    }

    // ═══════════════ GetValueAge（ADR D40 / docs/11 §一.6） ═══════════════

    /// <summary>
    /// 年龄数据源：从未成功采集 → null；手动触发读 / 触发块读（都是「成功采集」）之后有值；
    /// **写不算采集**（写过的点年龄仍是 null）。
    /// </summary>
    [Fact]
    public async Task GetValueAge_is_null_until_a_successful_acquisition_and_writes_do_not_count()
    {
        var link = new FakeModbusLink();
        link.SetReadData(7, DataArea.HoldingRegister, 10, 1);
        link.SetReadData(7, DataArea.HoldingRegister, 20, 2);
        link.WriteSingleReplies.Enqueue(ModbusReply.Ok(Array.Empty<ushort>(), 1));
        using var engine = Engine(link, string.Concat(
            U16,
            """<Point id="w" address="30" access="write" mode="onDemand" />""",
            """<Point id="b" address="40" mode="onDemand" />"""));

        Assert.Null(engine.GetValueAge("d1", "p"));            // 从未采集
        Assert.Null(engine.GetValueAge("d1", "b"));
        Assert.Null(engine.GetValueAge("d1", "w"));

        var trigger = await engine.SetValueAsync("d1", "w", 5);   // 写：不算采集
        Assert.Equal(WriteOutcome.Succeeded, trigger.Outcome);
        Assert.Null(engine.GetValueAge("d1", "w"));

        await ((IModbusDebugTool)engine).TriggerReadAsync("d1", "p");
        var age = engine.GetValueAge("d1", "p");
        Assert.NotNull(age);
        Assert.InRange(age!.Value, TimeSpan.Zero, TimeSpan.FromSeconds(5));

        await engine.SetValueAsync("d1", "w", 5);              // 再写一次仍然不算
        Assert.Null(engine.GetValueAge("d1", "w"));
    }

    /// <summary>
    /// 解码降级算「通讯成功」：NaN → Uncertain、脚本/未知类型 → Bad，仍然刷新年龄；
    /// 而**通讯失败置坏不算**（这里是「读请求本身失败」的路径：TriggerRead 直接抛，缓存与年龄都不动）。
    /// 轮询路径的「失败不推进」由 <c>BackoffTests.B14</c> 覆盖。
    /// </summary>
    [Fact]
    public async Task GetValueAge_advances_for_degraded_decodes_but_never_for_a_failed_request()
    {
        var link = new FakeModbusLink();
        link.DefaultReadData = new ushort[] { 0x7FC0, 0x0000 };   // float32 NaN → Uncertain
        using var engine = Engine(link, NAN_F32);

        await ((IModbusDebugTool)engine).TriggerReadAsync("d1", "nan");
        Assert.Equal(PointQuality.Uncertain, engine.GetValueDetail("d1", "nan").Quality);
        Assert.NotNull(engine.GetValueAge("d1", "nan"));

        var before = engine.GetValueAge("d1", "nan")!.Value;
        Thread.Sleep(30);

        // 读请求失败：TriggerReadAsync 抛，缓存与年龄都不该被推进
        link.DefaultReadData = null;
        link.FaultRules.Add(new FakeFaultRule { Kind = ModbusFailureKind.Timeout });
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ((IModbusDebugTool)engine).TriggerReadAsync("d1", "nan"));

        Assert.Equal(PointQuality.Uncertain, engine.GetValueDetail("d1", "nan").Quality);   // 失败不改缓存
        Assert.True(engine.GetValueAge("d1", "nan")!.Value > before, "失败不该刷新「上次成功采集」");
    }

    /// <summary>计算点按「上次求值成功」计：依赖坏值 → Bad → 年龄仍是 null；依赖变好 → 年龄出现。</summary>
    [Fact]
    public async Task GetValueAge_for_calculated_points_counts_only_successful_evaluations()
    {
        var link = new FakeModbusLink();
        link.SetReadData(7, DataArea.HoldingRegister, 10, 21);
        using var engine = Engine(link,
            """<Point id="raw" address="10" mode="onDemand" />""",
            calculatedXml: CALC_BAD_DEP);

        Assert.Null(engine.GetValueAge("d1", "c"));               // 依赖从未采到 → 未求值成功
        Assert.Equal(PointQuality.Bad, engine.GetValueDetail("d1", "c").Quality);
        Assert.Null(engine.GetValueAge("d1", "c"));               // 求值失败不推进

        await ((IModbusDebugTool)engine).TriggerReadAsync("d1", "raw");
        Assert.Equal(42.0, Assert.IsType<double>(engine.GetValueDetail("d1", "c").Value));
        Assert.NotNull(engine.GetValueAge("d1", "c"));            // 求值成功 → 有年龄

        // 年龄随时间增长（框架不做陈旧判定：只给时长，不给 bool/阈值）
        var first = engine.GetValueAge("d1", "c")!.Value;
        Thread.Sleep(40);
        Assert.True(engine.GetValueAge("d1", "c")!.Value > first);
    }

    /// <summary>返回值类型就是 <c>TimeSpan?</c>（没有 bool/阈值版本）——签名契约编译期即锁死。</summary>
    [Fact]
    public void GetValueAge_returns_a_nullable_time_span_without_any_staleness_verdict()
    {
        var link = new FakeModbusLink();
        using var engine = Engine(link, U16);

        TimeSpan? age = engine.GetValueAge("d1", "p");
        Assert.Null(age);
    }
}

/// <summary>
/// 门面 + 真实轮询线程（NaN 解码降级的采集、永久失败窗口的年龄）：
/// 与 <c>BackoffTests.B16</c>（进程级句柄数断言）串行采样，故加入同一集合。
/// </summary>
[Collection("real-polling-threads")]
public class FacadePollingAgeTests
{
    private static SamplerEngine Start(FakeModbusLink link, string pointsXml, string globalExtra = "")
    {
        var engine = new SamplerEngine(
            SamplerConfigLoader.Load(XDocument.Parse($"""
                <SamplerConfig schemaVersion="3.0">
                  <Global nullText="--"><Polling defaultIntervalMs="100" requestTimeoutMs="500" />{globalExtra}</Global>
                  <Transports><Transport id="tcp1" host="127.0.0.1" /></Transports>
                  <Devices><Device id="d1" transport="tcp1" pointSet="ps1" unitId="7" /></Devices>
                  <PointSets><PointSet id="ps1"><Points>{pointsXml}</Points></PointSet></PointSets>
                </SamplerConfig>
                """), Directory.GetCurrentDirectory()),
            new Dictionary<string, IModbusLink> { ["tcp1"] = link });
        engine.Start();
        return engine;
    }

    /// <summary>
    /// 轮询路径的「解码降级也算成功采集」：NaN（Uncertain）点年龄必须有值；
    /// 而**一直读不到**的点（窗口请求永远失败）从未成功采集 → 年龄为 null，质量非 Good。
    /// </summary>
    [Fact]
    public void Polling_age_distinguishes_degraded_decodes_from_failed_windows()
    {
        var link = new FakeModbusLink();
        link.SetReadData(7, DataArea.HoldingRegister, 10, 0x7FC0, 0x0000);            // NaN
        link.FaultRules.Add(new FakeFaultRule { Kind = ModbusFailureKind.Timeout, MinAddress = 20 });
        using var engine = Start(link,
            """<Point id="nan" address="10" dataType="float32" length="2" swap="abcd" intervalMs="100" />"""
            + """<Point id="dead" address="20" intervalMs="100" />""",
            globalExtra: """<Reconnect enabled="false" />""");   // 关退避：失败窗口每拍都会再试，不受退避冻结干扰

        Assert.True(SpinWait.SpinUntil(() => engine.GetValueAge("d1", "nan") != null, 5000),
            "解码降级（Uncertain）也必须算一次成功采集");

        Thread.Sleep(200);

        Assert.Equal(PointQuality.Uncertain, engine.GetValueDetail("d1", "nan").Quality);
        Assert.NotNull(engine.GetValueAge("d1", "nan"));
        Assert.NotEqual(PointQuality.Good, engine.GetValueDetail("d1", "dead").Quality);
        Assert.Null(engine.GetValueAge("d1", "dead"));      // 窗口一直失败 → 从未成功采集

        // 全局 nullText 对「从未采到」与「解码降级」都给 --（非 Good 一律不给旧值）
        Assert.Equal("--", engine.GetValue("d1", "nan"));
        Assert.Equal("--", engine.GetValue("d1", "dead"));
    }

    /// <summary>
    /// 写与轮询并发（真实轮询线程）：读只出现在轮询窗口、写只出现在写地址，点值不串、不抛、写全有结论。
    /// </summary>
    [Fact]
    public async Task Concurrent_writes_and_polling_never_cross_answers()
    {
        var link = new FakeModbusLink();
        link.SetReadData(7, DataArea.HoldingRegister, 10, 5);      // 轮询点：恒为 5
        for (var i = 0; i < 20; i++) link.WriteSingleReplies.Enqueue(ModbusReply.Ok(Array.Empty<ushort>(), 1));
        using var engine = Start(link,
            """<Point id="p" address="10" intervalMs="100" />"""
            + """<Point id="w" address="20" access="write" mode="onDemand" />""");
        try
        {
            await Task.Delay(250);                                  // 先让轮询跑起来

            var results = await Task.WhenAll(Enumerable.Range(0, 20)
                .Select(i => engine.SetValueAsync("d1", "w", i + 1)));

            Assert.All(results, r => Assert.Equal(WriteOutcome.Succeeded, r.Outcome));
            Assert.All(link.Calls.Where(c => c.IsWrite), c => Assert.Equal(20, c.Address));
            Assert.All(link.Calls.Where(c => c.IsRead), c => Assert.Equal(10, c.Address));
            Assert.Equal(20, link.WriteCallCount);

            Thread.Sleep(200);
            var polled = engine.GetValueDetail("d1", "p");
            Assert.True(polled.IsGood, "轮询点在写入风暴后仍必须是好值，实际 " + polled);
            Assert.Equal((ushort)5, Assert.IsType<ushort>(polled.Value));
        }
        finally
        {
            engine.Dispose();
        }
    }
}
