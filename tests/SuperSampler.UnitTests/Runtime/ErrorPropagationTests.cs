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

/// <summary>错误事件的族归类与字段完整性（纯单测，无端口、无线程）。</summary>
public class ErrorFamilyAndEventFieldTests
{
    /// <summary>没有错误族标记接口的自定义错误事件（口径：按最保守的 Transient 处理）。</summary>
    private sealed class UnmarkedError : IErrorEvent
    {
        public UnmarkedError(ErrorInfo info) => Info = info;

        public ErrorInfo Info { get; }

        public EventCategory Category => Info.Category;

        public EventLevel Level => Info.Level;
    }

    private static ErrorInfo Info(string code = "TEST.ERR") => new(
        code, EventCategory.Device, EventLevel.Error, ErrorSource.Device, "ss.error.comm",
        new ErrorContext(DeviceId: "d1", TransportId: "tcp1", UnitId: 7, PointId: "p", Area: "HoldingRegister",
            Address: 12, FunctionCode: 0x03, Attempt: 2, MaxAttempts: 3, ElapsedMs: 1500, ConsecutiveFailures: 3,
            CorrelationId: 42, TxFrame: new byte[] { 1, 2 }, RxFrame: new byte[] { 3, 4 }));

    public static IEnumerable<object[]> Families()
    {
        yield return new object[] { new TimeoutError(Info("MODBUS.TIMEOUT")), ErrorClass.Transient };
        yield return new object[] { new ProtocolError(Info("MODBUS.CRC")), ErrorClass.Transient };
        yield return new object[] { new LinkError(Info("MODBUS.LINK")), ErrorClass.LinkDown };
        yield return new object[] { new DeviceExceptionError(Info("MODBUS.EXCEPTION.02"), 0x02), ErrorClass.Permanent };
        yield return new object[] { new DecodeError(Info("SS.DECODE")), ErrorClass.Permanent };
        yield return new object[] { new ConfigError(Info("SS.CONFIG"), "points[p].address"), ErrorClass.Permanent };
        yield return new object[] { new WriteIndeterminateError(Info("MODBUS.TIMEOUT")), ErrorClass.Indeterminate };
        yield return new object[] { new PolicyError(Info("SS.BUDGET"), "Budget"), ErrorClass.Policy };
        yield return new object[] { new UnmarkedError(Info("UNMARKED")), ErrorClass.Transient };
    }

    [Theory]
    [MemberData(nameof(Families))]
    public void Every_error_family_maps_to_its_declared_class(IErrorEvent error, ErrorClass expected)
    {
        Assert.Equal(expected, ErrorClasses.Classify(error));
        Assert.Equal(EventCategory.Device, error.Category);       // 类别来自 ErrorInfo，不在事件上重复定义
        Assert.Equal(EventLevel.Error, error.Level);
        Assert.False(string.IsNullOrEmpty(error.Info.Code));
        Assert.False(string.IsNullOrEmpty(error.Info.MessageKey));
    }

    [Fact]
    public void Error_context_keeps_every_structured_field()
    {
        var context = Info().Context;

        Assert.Equal("d1", context.DeviceId);
        Assert.Equal("tcp1", context.TransportId);
        Assert.Equal(7, context.UnitId);
        Assert.Equal("p", context.PointId);
        Assert.Equal("HoldingRegister", context.Area);
        Assert.Equal(12, context.Address);
        Assert.Equal((byte)0x03, context.FunctionCode);
        Assert.Equal(2, context.Attempt);
        Assert.Equal(3, context.MaxAttempts);
        Assert.Equal(1500, context.ElapsedMs);
        Assert.Equal(3, context.ConsecutiveFailures);
        Assert.Equal(42, context.CorrelationId);
        Assert.Equal(new byte[] { 1, 2 }, context.TxFrame!.ToArray());
        Assert.Equal(new byte[] { 3, 4 }, context.RxFrame!.ToArray());

        var summary = context.ToString();
        Assert.Contains("device=d1", summary);
        Assert.Contains("attempt=2/3", summary);
        Assert.Contains("elapsed=1500ms", summary);
        Assert.Contains("corr=42", summary);
    }

    [Fact]
    public void Error_source_enum_distinguishes_the_layers_a_host_must_escalate_to()
    {
        Assert.Equal(new[] { "Transport", "Protocol", "Device", "Codec", "Policy", "Config" },
            Enum.GetNames(typeof(ErrorSource)));
    }
}

/// <summary>非错误类事件的类别/级别与退避事件字段（ADR D40，覆盖方案 §十三）。</summary>
public class EventCategoryAndBackoffFieldTests
{
    [Fact]
    public void Value_alarm_and_write_events_declare_their_category_and_level()
    {
        var value = new PointValueChangedEvent("d1", "p", PointValue.Good(1.0, DateTimeOffset.UtcNow));
        Assert.Equal(EventCategory.Value, value.Category);
        Assert.Equal(EventLevel.Info, value.Level);

        var raised = new AlarmRaisedEvent("a", "d1", "p", "high", 10, 11, "P1", "text");
        Assert.Equal(EventCategory.Alarm, raised.Category);
        Assert.Equal(EventLevel.Warn, raised.Level);

        var cleared = new AlarmClearedEvent("a", "d1", "p", "high");
        Assert.Equal(EventCategory.Alarm, cleared.Category);
        Assert.Equal(EventLevel.Info, cleared.Level);

        var acknowledged = new AlarmAcknowledgedEvent("a", "d1", "p", "op");
        Assert.Equal(EventCategory.Alarm, acknowledged.Category);
        Assert.Equal(EventLevel.Info, acknowledged.Level);

        var written = new PointWrittenEvent("d1", "p", 5, "op", "Succeeded", null);
        Assert.Equal(EventCategory.Write, written.Category);
        Assert.Equal(EventLevel.Info, written.Level);
    }

    [Fact]
    public void Device_scope_backoff_events_carry_the_device_and_a_device_category()
    {
        var next = DateTimeOffset.UtcNow.AddSeconds(5);
        var entered = new BackoffEnteredEvent(BackoffScope.Device, "d1", "tcp1", 7, 2, 5000, next, "MODBUS.TIMEOUT");

        Assert.Equal(BackoffScope.Device, entered.Scope);
        Assert.Equal("d1", entered.DeviceId);
        Assert.Equal("tcp1", entered.TransportId);
        Assert.Equal(7, entered.UnitId);
        Assert.Equal(2, entered.Attempt);
        Assert.Equal(5000, entered.DelayMs);
        Assert.Equal(next, entered.NextRetryAt);
        Assert.Equal("MODBUS.TIMEOUT", entered.ReasonCode);
        Assert.Equal(EventCategory.Device, entered.Category);
        Assert.Equal(EventLevel.Warn, entered.Level);

        var recovered = new BackoffRecoveredEvent(BackoffScope.Device, "d1", "tcp1", 7, 3, 12_000);
        Assert.Equal(EventCategory.Device, recovered.Category);
        Assert.Equal(EventLevel.Info, recovered.Level);
        Assert.Equal(3, recovered.Attempts);
        Assert.Equal(12_000, recovered.DurationMs);
    }

    [Fact]
    public void Link_scope_backoff_events_have_no_device_and_a_connection_category()
    {
        var entered = new BackoffEnteredEvent(BackoffScope.Link, null, "tcp1", null, 1, 500, DateTimeOffset.UtcNow, "MODBUS.LINK");

        Assert.Equal(BackoffScope.Link, entered.Scope);
        Assert.Null(entered.DeviceId);                            // 链路级：设备字段为 null
        Assert.Null(entered.UnitId);
        Assert.Equal(EventCategory.Connection, entered.Category);  // 类别与 Scope 对应
        Assert.Equal(EventLevel.Warn, entered.Level);

        var recovered = new BackoffRecoveredEvent(BackoffScope.Link, null, "tcp1", null, 1, 100);
        Assert.Equal(EventCategory.Connection, recovered.Category);
        Assert.Null(recovered.DeviceId);
    }

    [Fact]
    public void Backoff_events_are_reachable_through_the_shared_marker_interface()
    {
        using var bus = new SuperSampler.Core.Events.InProcessEventBus();
        var seen = new List<IBackoffEvent>();
        bus.Subscribe<IBackoffEvent>(e => seen.Add(e.Body), DeliveryMode.Inline);

        bus.Emit(new BackoffEnteredEvent(BackoffScope.Device, "d1", "tcp1", 7, 1, 100, DateTimeOffset.UtcNow, "MODBUS.TIMEOUT"));
        bus.Emit(new BackoffRecoveredEvent(BackoffScope.Link, null, "tcp1", null, 1, 100));

        Assert.Equal(2, seen.Count);
        Assert.Contains(seen, e => e.Scope == BackoffScope.Device && e.DeviceId == "d1");
        Assert.Contains(seen, e => e.Scope == BackoffScope.Link && e.DeviceId == null);
    }
}

/// <summary>
/// 错误传播三条路径一致性（覆盖方案 §十二）：一次失败在「结果对象 / 质量降级 / 事件」三处的口径，
/// 以及「一个读取窗口 = 一条聚合错误事件」的聚合口径与窗口边界。
/// 走假链路 <see cref="FakeModbusLink"/>，不依赖外部模拟器。
/// </summary>
[Collection("real-polling-threads")]
public sealed class ErrorPropagationPathTests : IDisposable
{
    private readonly FakeModbusLink _link = new();
    private SamplerEngine? _engine;

    public void Dispose() => _engine?.Dispose();

    private static SamplerConfiguration Cfg(string points, string globalExtra = "")
        => SamplerConfigLoader.Load(XDocument.Parse("""
            <SamplerConfig schemaVersion="3.0">
              <Global>
                <Polling defaultIntervalMs="200" requestTimeoutMs="300" />
                <Reconnect enabled="false" />
                <Quality onCommError="bad" onCommErrorValue="null" />
                {0}
              </Global>
              <Transports><Transport id="tcp1" host="127.0.0.1" /></Transports>
              <Devices><Device id="d1" transport="tcp1" pointSet="ps1" unitId="7" /></Devices>
              <PointSets><PointSet id="ps1"><Points>{1}</Points></PointSet></PointSets>
            </SamplerConfig>
            """.Replace("{0}", globalExtra).Replace("{1}", points)), Directory.GetCurrentDirectory());

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

    // ═══════════════ 读失败：三路径一致 ═══════════════

    [Fact]
    public void A_read_timeout_degrades_quality_emits_one_error_event_and_a_quality_change_event()
    {
        _link.FaultRules.Add(new FakeFaultRule { Kind = ModbusFailureKind.Timeout, Area = DataArea.HoldingRegister });
        var engine = Engine(Cfg("""<Point id="p1" address="0" dataType="uint16" swap="none" />"""));
        var errors = Collect<IErrorEvent>(engine);
        var values = Collect<PointValueChangedEvent>(engine);

        engine.Start();

        Assert.True(SpinWait.SpinUntil(() => { lock (errors) return errors.Count > 0; }, 5000), "读超时应发错误事件");
        Assert.True(SpinWait.SpinUntil(() => { lock (values) return values.Count > 0; }, 5000), "质量降级应发值事件");

        // ① 结果对象：质量降级（onCommError=bad + onCommErrorValue=null → Bad）
        Assert.True(SpinWait.SpinUntil(() => engine.GetValueDetail("d1", "p1").Quality == PointQuality.Bad, 5000));

        // ② 事件：TimeoutError + 字段
        IErrorEvent first;
        lock (errors) first = errors[0];
        var timeout = Assert.IsType<TimeoutError>(first);
        Assert.Equal("MODBUS.TIMEOUT", timeout.Info.Code);
        Assert.Equal(EventCategory.Device, timeout.Category);
        Assert.Equal(EventLevel.Error, timeout.Level);
        Assert.Equal(ErrorSource.Device, timeout.Info.Source);
        Assert.Equal("ss.error.comm", timeout.Info.MessageKey);
        Assert.Equal(ErrorClass.Transient, ErrorClasses.Classify(timeout));

        // ③ 值事件：坏值原因可见（宿主据此置灰）
        PointValueChangedEvent change;
        lock (values) change = values[0];
        Assert.Equal(PointQuality.Bad, change.Value.Quality);
        Assert.Equal("ss.reason.comm", change.Value.Reason);
    }

    [Fact]
    public void One_failed_window_emits_exactly_one_error_event_no_matter_how_many_points_it_covers()
    {
        // 三个相邻点位落在同一个读取窗口（同区间隔 + 地址连续）→ 聚合错误事件恰好 1 条，
        // ConsecutiveFailures = 窗口内点位数（受影响面），不是每点一条。
        _link.DefaultReadData = null;                             // 未命中精确规则 → 读失败（LinkDown）
        var engine = Engine(Cfg("""
            <Point id="p1" address="0" dataType="uint16" swap="none" />
            <Point id="p2" address="1" dataType="uint16" swap="none" />
            <Point id="p3" address="2" dataType="uint16" swap="none" />
            """));
        var errors = Collect<IErrorEvent>(engine);

        engine.Start();
        Assert.True(SpinWait.SpinUntil(() => { lock (errors) return errors.Count > 0; }, 5000));

        var calls = _link.Calls.Count(c => c.IsRead);
        Assert.Equal(1, calls);                                   // 本轮只发了一次请求（一个窗口）
        var error = Assert.Single(errors);                        // 一次失败 = 一条事件
        Assert.Equal(3, error.Info.Context.ConsecutiveFailures);
        Assert.Equal("d1", error.Info.Context.DeviceId);
        Assert.Equal("tcp1", error.Info.Context.TransportId);
        Assert.Equal(7, error.Info.Context.UnitId);
        Assert.Equal("HoldingRegister", error.Info.Context.Area);
        Assert.Equal(0, error.Info.Context.Address);
    }

    [Fact]
    public void Windows_in_the_same_tick_are_all_read_when_the_link_is_healthy()
    {
        // 前置事实（自动分组口径）：地址 0 与地址 100 相隔超过 ignoreGap → 两个读取窗口，
        // 同一节拍内背靠背读完（docs/01 §6.2）。这条同时是下一条短路口径的对照。
        _link.DefaultReadData = new ushort[256];
        var engine = Engine(Cfg("""
            <Point id="p1" address="0" dataType="uint16" swap="none" />
            <Point id="p2" address="100" dataType="uint16" swap="none" />
            """, """<Scheduler ignoreGap="5" />"""));

        engine.Start();
        Assert.True(SpinWait.SpinUntil(() => _link.Calls.Count(c => c.IsRead) >= 2, 5000));

        var addresses = _link.Calls.Where(c => c.IsRead).Select(c => c.Address).Distinct().OrderBy(a => a).ToArray();
        Assert.Equal(new[] { 0, 100 }, addresses);
    }

    [Fact]
    public async Task A_failing_window_does_not_skip_the_other_windows_of_the_same_tick()
    {
        // D73 的回归（原 `A_first_window_failure_short_circuits_..._todays_behaviour_locked`
        // 断言的是缺陷现状，已反转）：同一节拍内每个读取窗口**独立发请求、独立失败**。
        // 缺陷现状：`firstFailure ??= ReadWindow(...)` 让首个失败窗口短路同节拍其余窗口——
        // 被跳过窗口的点位既不降质量也不发错误事件，宿主看到「好值的陈旧数据」（只能用 GetValueAge 察觉），
        // 且与 `Scheduler` 自身注释、docs/01 §6.1「一组数据必须来自同一时刻」冲突。
        _link.DefaultReadData = new ushort[256];
        var engine = Engine(Cfg("""
            <Point id="p1" address="0" dataType="uint16" swap="none" intervalMs="5000" />
            <Point id="p2" address="100" dataType="uint16" swap="none" intervalMs="5000" />
            """, """<Scheduler ignoreGap="5" />"""));
        var errors = Collect<IErrorEvent>(engine);

        // 先各成功采一次（不启动轮询）：两个点都有好值与采集年龄
        Assert.Equal(PointQuality.Good, (await engine.TriggerReadAsync("d1", "p1")).Quality);
        Assert.Equal(PointQuality.Good, (await engine.TriggerReadAsync("d1", "p2")).Quality);

        // 只让第 1 个窗口失败（地址 0），第 2 个窗口（地址 100）保持可读
        _link.FaultRules.Add(new FakeFaultRule { Kind = ModbusFailureKind.Timeout, Area = DataArea.HoldingRegister, MinAddress = 0, MaxAddress = 50 });
        _link.ClearCalls();

        engine.Start();
        Assert.True(SpinWait.SpinUntil(() => { lock (errors) return errors.Count > 0; }, 5000), "失败窗口必须发错误事件");

        // ① 同节拍两个窗口都发了请求（5s 间隔 → 观察窗内只有一个节拍）
        Assert.True(SpinWait.SpinUntil(
            () => _link.Calls.Count(c => c.IsRead && c.Address == 100) > 0, 5000),
            "首个窗口失败不得短路同节拍的其它窗口");

        var reads = _link.Calls.Where(c => c.IsRead).Select(c => c.Address).Distinct().OrderBy(a => a).ToArray();
        Assert.Equal(new[] { 0, 100 }, reads);

        // ② 错误事件按「读取窗口」聚合：只有失败的窗口 0 发事件，恰好一条、ConsecutiveFailures = 该窗口点位数
        IErrorEvent[] snapshot;
        lock (errors) snapshot = errors.ToArray();
        var error = Assert.Single(snapshot);
        Assert.IsType<TimeoutError>(error);
        Assert.Equal(0, error.Info.Context.Address);
        Assert.Equal(1, error.Info.Context.ConsecutiveFailures);

        // ③ 质量：失败窗口降级；同节拍的其它窗口照常刷新（不是「好值的陈旧数据」）
        Assert.Equal(PointQuality.Bad, engine.GetValueDetail("d1", "p1").Quality);
        Assert.Equal(PointQuality.Good, engine.GetValueDetail("d1", "p2").Quality);
        var age = engine.GetValueAge("d1", "p2");
        Assert.NotNull(age);
        Assert.True(age!.Value < TimeSpan.FromMilliseconds(1000),
            $"同节拍的窗口必须刷新采集年龄（实测 {age.Value.TotalMilliseconds:F0}ms）");
    }

    [Fact]
    public void Every_failed_window_in_a_tick_reports_its_own_error_and_quality()
    {
        // D73 的聚合口径：错误聚合单位 = **读取窗口**（findings 既有口径，不另造一套）。
        // 同节拍两个窗口都失败 → 两条错误事件（各带自己的 Address 与 ConsecutiveFailures），
        // 两个窗口的点位都降质量——既不是「整节拍只发一条事件」，也不是「同一窗口被算两遍」。
        _link.DefaultReadData = null;   // 两个窗口都失败（LinkDown）
        var engine = Engine(Cfg("""
            <Point id="p1" address="0" dataType="uint16" swap="none" intervalMs="5000" />
            <Point id="p2" address="100" dataType="uint16" swap="none" intervalMs="5000" />
            """, """<Scheduler ignoreGap="5" />"""));
        var errors = Collect<IErrorEvent>(engine);

        engine.Start();
        Assert.True(SpinWait.SpinUntil(() => { lock (errors) return errors.Count >= 2; }, 5000),
            "每个失败窗口各发一条错误事件");

        IErrorEvent[] snapshot;
        lock (errors) snapshot = errors.ToArray();
        Assert.Equal(2, snapshot.Length);
        Assert.Equal(new[] { 0, 100 }, snapshot.Select(e => e.Info.Context.Address ?? -1).OrderBy(a => a).ToArray());
        Assert.All(snapshot, e => Assert.Equal(1, e.Info.Context.ConsecutiveFailures));   // 每个窗口 1 个点位
        Assert.All(snapshot, e => Assert.IsType<LinkError>(e));

        var reads = _link.Calls.Where(c => c.IsRead).Select(c => c.Address).Distinct().OrderBy(a => a).ToArray();
        Assert.Equal(new[] { 0, 100 }, reads);   // 一发一败：请求数 = 窗口数（无短路、无重复）
        Assert.Equal(2, _link.Calls.Count(c => c.IsRead));

        Assert.Equal(PointQuality.Bad, engine.GetValueDetail("d1", "p1").Quality);
        Assert.Equal(PointQuality.Bad, engine.GetValueDetail("d1", "p2").Quality);
    }

    [Fact]
    public void Consecutive_failed_turns_emit_one_error_event_per_turn_todays_behaviour_locked()
    {
        // 跨轮次口径（现状锁定）：退避关闭时，每轮失败各发一条（不是「只在首次失败发一条」）。
        // 频次由退避策略兜住（见 BackoffTests）；宿主若想降噪，按 (deviceId, code) 自行聚合。
        _link.DefaultReadData = null;
        var engine = Engine(Cfg("""<Point id="p1" address="0" dataType="uint16" swap="none" />"""));
        var errors = Collect<IErrorEvent>(engine);

        engine.Start();
        Assert.True(SpinWait.SpinUntil(() => { lock (errors) return errors.Count >= 3; }, 5000));
        Thread.Sleep(100);

        var reads = _link.Calls.Count(c => c.IsRead);
        Assert.Equal(reads, errors.Count);                        // 一发一败：请求数 = 事件数（无静默、无重复聚合）
    }

    [Fact]
    public void A_protocol_exception_and_a_link_failure_map_to_different_error_families_and_codes()
    {
        _link.FaultRules.Add(new FakeFaultRule { Kind = ModbusFailureKind.Protocol, ExceptionCode = 0x02, Area = DataArea.HoldingRegister });
        var engine = Engine(Cfg("""<Point id="p1" address="0" dataType="uint16" swap="none" />"""));
        var errors = Collect<IErrorEvent>(engine);
        engine.Start();

        Assert.True(SpinWait.SpinUntil(() => { lock (errors) return errors.Count > 0; }, 5000));
        var permanent = Assert.IsType<DeviceExceptionError>(errors[0]);
        Assert.Equal("MODBUS.EXCEPTION.02", permanent.Info.Code);
        Assert.Equal(0x02, permanent.ExceptionCode);
        Assert.Equal("ss.error.modbusException", permanent.Info.MessageKey);
        Assert.Equal(ErrorClass.Permanent, ErrorClasses.Classify(permanent));
        engine.Dispose();

        // 链路失败（IO）：另一条链路 → LinkError
        _link.FaultRules.Clear();
        _link.FaultRules.Add(new FakeFaultRule { Kind = ModbusFailureKind.LinkDown, Area = DataArea.HoldingRegister });
        var second = new FakeModbusLink();
        second.FaultRules.Add(new FakeFaultRule { Kind = ModbusFailureKind.LinkDown, Area = DataArea.HoldingRegister });
        var engine2 = new SamplerEngine(Cfg("""<Point id="p1" address="0" dataType="uint16" swap="none" />"""),
            new Dictionary<string, IModbusLink> { ["tcp1"] = second });
        _engine = engine2;
        var errors2 = Collect<IErrorEvent>(engine2);
        engine2.Start();

        Assert.True(SpinWait.SpinUntil(() => { lock (errors2) return errors2.Count > 0; }, 5000));
        var link = Assert.IsType<LinkError>(errors2[0]);
        Assert.Equal("MODBUS.LINK", link.Info.Code);
        Assert.Equal(ErrorClass.LinkDown, ErrorClasses.Classify(link));
    }

    // ═══════════════ 写失败：三路径（同步结果对象 / 质量 / 事件）═══════════════

    [Fact]
    public async Task A_write_timeout_reports_indeterminate_keeps_quality_and_audits_the_write_event()
    {
        var cfg = Cfg("""<Point id="wr" address="0" access="write" dataType="uint16" swap="none" />""");
        _link.SetReadData(7, DataArea.HoldingRegister, 0, 1234);
        var engine = Engine(cfg);

        // 先成功采一次（TriggerReadAsync 走同一条读管道，不启动轮询线程）
        var seeded = await engine.TriggerReadAsync("d1", "wr");
        Assert.Equal(PointQuality.Good, seeded.Quality);

        var written = Collect<PointWrittenEvent>(engine);
        var errors = Collect<IErrorEvent>(engine);

        _link.WriteSingleReplies.Enqueue(ModbusReply.Fail(ModbusFailureKind.Timeout, 0, "写超时", 250));
        _link.ReadReplies.Enqueue(ModbusReply.Fail(ModbusFailureKind.Timeout, 0, "回读超时", 250));   // 回读也失败

        var result = await engine.SetValueAsync("d1", "wr", 99);

        // ① 结果对象：不确定 + 错误码
        Assert.Equal(WriteOutcome.Indeterminate, result.Outcome);
        Assert.Equal("MODBUS.TIMEOUT", result.Error!.Code);
        Assert.Null(result.Readback);

        // ② 质量路径：写失败**不降级**采集质量（写不是采集；缓存保持上一次好值）
        var after = engine.GetValueDetail("d1", "wr");
        Assert.Equal(PointQuality.Good, after.Quality);
        Assert.Equal((ushort)1234, after.Value);

        // ③ 事件路径：写审计四态 + 不额外发错误族事件（口径记录：写错误走同步结果对象 + 审计事件）
        var audit = Assert.Single(written);
        Assert.Equal("Indeterminate", audit.Outcome);
        Assert.Equal("ss.error.writeFailed", audit.Message);   // i18n key（不是错误码：码在同步结果的 Error.Code 上）
        Assert.Equal("MODBUS.TIMEOUT", result.Error!.Code);
        Assert.True(audit.ElapsedMs >= 0);
        Assert.True(audit.DidReadback);                          // 写超时必回读定论
        Assert.Null(audit.Readback);
        Assert.False(audit.VerifyMismatch);
        Assert.Empty(errors);
    }

    [Fact]
    public async Task A_rejected_write_never_touches_the_wire_and_keeps_quality_unchanged()
    {
        var cfg = Cfg("""<Point id="wr" address="0" dataType="uint16" swap="none" />""");   // access 默认只读
        _link.SetReadData(7, DataArea.HoldingRegister, 0, 7);
        var engine = Engine(cfg);
        var written = Collect<PointWrittenEvent>(engine);

        _ = await engine.TriggerReadAsync("d1", "wr");
        var before = engine.GetValueDetail("d1", "wr");

        var result = await engine.SetValueAsync("d1", "wr", 99);

        Assert.Equal(WriteOutcome.Rejected, result.Outcome);
        Assert.Empty(_link.Calls.Where(c => c.IsWrite));          // 未发出任何通讯
        Assert.Equal(before, engine.GetValueDetail("d1", "wr"));  // 质量与值都不动
        var audit = Assert.Single(written);
        Assert.Equal("ss.reason.notWritable", audit.Message);
        Assert.False(audit.DidReadback);
    }
}
