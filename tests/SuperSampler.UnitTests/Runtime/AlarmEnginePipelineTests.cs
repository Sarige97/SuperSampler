using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
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
/// 报警与采集链路交界（覆盖方案 §七.4–§七.7）：写回与报警的时序、脚本点坏值挂起、计算点挂报警的现状。
/// 起**真实轮询线程** → 加入命名集合 <c>real-polling-threads</c> 串行执行（与既有 FacadeWriteGapTests 同约定）。
/// </summary>
[Collection("real-polling-threads")]
public sealed class AlarmEnginePipelineTests : IDisposable
{
    private readonly FakeModbusLink _link = new();
    private SamplerEngine? _engine;

    public void Dispose() => _engine?.Dispose();

    private static SamplerConfiguration Cfg(string points, string global = "", string extra = "")
        => SamplerConfigLoader.Load(XDocument.Parse("""
            <SamplerConfig schemaVersion="3.0">
              <Global><Polling defaultIntervalMs="150" requestTimeoutMs="300" />{0}</Global>
              <Transports><Transport id="tcp1" host="127.0.0.1" /></Transports>
              <Devices><Device id="d1" transport="tcp1" pointSet="ps1" unitId="7" /></Devices>
              <PointSets><PointSet id="ps1"><Points>{1}</Points>{2}</PointSet></PointSets>
            </SamplerConfig>
            """.Replace("{0}", global).Replace("{1}", points).Replace("{2}", extra)), Directory.GetCurrentDirectory());

    private SamplerEngine Engine(SamplerConfiguration cfg)
    {
        _engine = new SamplerEngine(cfg, new Dictionary<string, IModbusLink> { ["tcp1"] = _link });
        return _engine;
    }

    private static List<T> Collect<T>(SamplerEngine engine) where T : IEvent
    {
        var list = new List<T>();
        engine.Bus.Subscribe<T>(e => { lock (list) list.Add(e.Body); }, DeliveryMode.Inline);
        return list;
    }

    // ═══════════════ §七.4/§七.5：写回与报警的时序 ═══════════════

    [Fact]
    public async Task Write_does_not_evaluate_alarms_immediately_but_on_the_next_poll_turn()
    {
        _link.DefaultReadData = new ushort[64];
        _link.SetReadData(7, DataArea.HoldingRegister, 0, 0);   // 读回的仍是 0（写入不影响假链路的读数据）
        var engine = Engine(Cfg("""<Point id="wr" address="0" access="write" dataType="uint16" swap="none"><Alarm id="AH" type="high" limit="10" /></Point>"""));
        var raised = Collect<AlarmRaisedEvent>(engine);

        // 未 Start()：写成功但没有任何轮询 → 报警一次都不评估（口径：写不即时触发报警）
        _link.WriteSingleReplies.Enqueue(ModbusReply.Ok(Array.Empty<ushort>(), 1));
        Assert.Equal(WriteOutcome.Succeeded, (await engine.SetValueAsync("d1", "wr", 50)).Outcome);
        Assert.Empty(raised);

        // Start() 后：轮询读到越限值 → 下一轮判定触发
        engine.Start();
        _link.SetReadData(7, DataArea.HoldingRegister, 0, 50);
        Assert.True(SpinWait.SpinUntil(() => { lock (raised) return raised.Count > 0; }, 5000),
            "轮询读到越限值后应在下一轮判定触发");
        Assert.Equal(50.0, Convert.ToDouble(raised[0].Value));
    }

    [Fact]
    public void A_bad_script_result_suspends_the_alarm_instead_of_clearing_it()
    {
        // 脚本点：<Script>return null;</Script> → Bad(ss.reason.scriptNull)（ADR D41 / findings D50）。
        // 报警口径：坏值挂起——已激活的报警不误清除，也不重复发。
        _link.SetReadData(7, DataArea.HoldingRegister, 0, 0, 50);   // 首地址 0 的两个字（脚本点读同一窗口）
        var engine = Engine(Cfg("""
            <Point id="s" address="0" dataType="uint16" swap="none"><Script>return null;</Script><Alarm id="AS" type="high" limit="1" /></Point>
            """));
        var raised = Collect<AlarmRaisedEvent>(engine);
        engine.Start();

        Thread.Sleep(600);
        Assert.Empty(raised);                        // 脚本恒坏值 → 报警一次都不评估
        Assert.Equal(PointQuality.Bad, engine.GetValueDetail("d1", "s").Quality);
    }

    // ═══════════════ 生命周期：Stop/Dispose 期间仍在发布事件 ═══════════════

    [Fact]
    public async Task Events_published_during_stop_are_delivered_and_the_bus_stays_usable_until_dispose()
    {
        // 清理顺序口径（读代码）：SamplerEngine.Dispose() = 先 _scheduler.Dispose()（取消轮询 +
        // Join 各设备线程 + Dispose 主站），再 _bus.Dispose()。因此**停止窗口内**轮询线程/写线程
        // 发出的事件仍然会被投递（Inline 同步）；Stop() 只停调度，总线照旧可用；
        // 引擎 Dispose 之后 Emit 静默（停止语义，W54）。
        _link.DefaultReadData = new ushort[64];
        var engine = Engine(Cfg("""<Point id="p1" address="0" access="write" dataType="uint16" swap="none" />""",
            """<Polling defaultIntervalMs="50" requestTimeoutMs="200" />"""));
        var values = Collect<PointValueChangedEvent>(engine);
        var audits = Collect<PointWrittenEvent>(engine);
        _link.WriteSingleReplies.Enqueue(ModbusReply.Ok(Array.Empty<ushort>(), 1));
        _link.WriteSingleReplies.Enqueue(ModbusReply.Ok(Array.Empty<ushort>(), 1));

        engine.Start();
        Assert.True(SpinWait.SpinUntil(() => { lock (values) return values.Count > 0; }, 5000), "轮询应先产出值事件");

        // Stop 期间并发发布：写审计（同步路径）与轮询事件（采集路径）都不允许把停止流程打崩
        var writer = Task.Run(async () =>
        {
            for (var i = 0; i < 2; i++) await engine.SetValueAsync("d1", "p1", i);
        });

        engine.Stop();
        await writer;

        Assert.Equal(2, audits.Count);                          // 停止窗口内的写审计一条不丢（Inline 同步）
        Assert.True(engine.Bus.Metrics.TotalFaults == 0);
        engine.Bus.Emit(new PointWrittenEvent("d1", "p1", 1, "op", "Succeeded", null));   // Stop 后总线仍可用

        engine.Dispose();
        var before = engine.Bus.Metrics.TotalEmitted;
        engine.Bus.Emit(new PointWrittenEvent("d1", "p1", 1, "op", "Succeeded", null));   // 引擎 Dispose 后静默
        Assert.Equal(before, engine.Bus.Metrics.TotalEmitted);
    }

    [Fact]
    public void Restarting_after_stop_does_not_resume_polling_todays_behaviour_locked()
    {
        // 现状口径：Scheduler.Start 用 Interlocked 幂等门（`_started`），Stop() 之后再次 Start()
        // **不会**重新起轮询线程（宿主需要重启只能重建引擎）。不崩、不抛，也不重复起线程。
        _link.DefaultReadData = new ushort[64];
        var engine = Engine(Cfg("""<Point id="p1" address="0" dataType="uint16" swap="none" />""",
            """<Polling defaultIntervalMs="50" requestTimeoutMs="200" />"""));

        engine.Start();
        Assert.True(SpinWait.SpinUntil(() => _link.Calls.Count(c => c.IsRead) >= 1, 5000));

        engine.Stop();
        Thread.Sleep(150);
        _link.ClearCalls();

        engine.Start();                                          // 再次 Start：应为无事发生
        Thread.Sleep(300);
        Assert.Empty(_link.Calls);                                // 没有恢复轮询（现状）
            }

    // ═══════════════ §七.7：计算点挂报警（findings D72 → 加载期拒绝）═══════════════

    [Fact]
    public void A_threshold_alarm_on_a_calculated_point_is_rejected_at_load()
    {
        // findings D72 / CGV-36：计算点不进轮询、也不走 _alarms.Evaluate → 报警写上去等于没写（静默无效）。
        // 「报警依赖采集轮次读到的值」是框架口径（docs/11 §四），这个组合必须在加载期拒绝。
        var ex = Assert.Throws<ConfigValidationException>(() => Cfg(
            """<Point id="raw" address="0" dataType="uint16" swap="none" />""", "",
            """<Calculated><Point id="c"><Expression>2+2</Expression><Alarm id="AC" type="high" limit="1" /></Point></Calculated>"""));

        Assert.Contains(ex.Errors, e => e.Contains("点位 ps1/c") && e.Contains("计算点不得挂"));
    }

    [Fact]
    public void A_calculated_point_without_alarms_still_evaluates_normally()
    {
        // 边界（不误伤）：不挂报警的计算点照常求值
        _link.DefaultReadData = new ushort[64];
        var engine = Engine(Cfg(
            """<Point id="raw" address="0" dataType="uint16" swap="none" />""", "",
            """<Calculated><Point id="c"><Expression>2+2</Expression></Point></Calculated>"""));

        var value = engine.GetValueDetail("d1", "c");

        Assert.Equal(PointQuality.Good, value.Quality);
        Assert.Equal(4.0, Convert.ToDouble(value.Value));
    }

}
