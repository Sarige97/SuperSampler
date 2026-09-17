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
/// 点位脚本解码的端到端（假链路）测试（ADR D41）：脚本真的在解码路径上生效——原始寄存器/原始数值进、
/// 工程值出；返回 null / 抛异常 / 超时按 onError 置 Bad 并记错误事件；脚本出问题绝不影响采集线程；
/// 点位未写 timeoutMs/onError 时继承 <c>Global/Script</c>；ES5.1 语法限制按 onError 处理不崩。
/// 全链路走 <see cref="SamplerEngine"/> + <see cref="FakeModbusLink"/>（真实调度线程）。
/// </summary>
/// <remarks>
/// 加入命名集合 <c>real-polling-threads</c>：本类会起**真实轮询线程**，
/// 与 <c>BackoffTests.B16</c>（断言进程级句柄数）必须串行执行，否则并行开的线程会被误判成泄漏。
/// </remarks>
[Collection("real-polling-threads")]
public class ScriptDecodeTests : IDisposable
{
    private readonly FakeModbusLink _link = new();
    private readonly List<IErrorEvent> _errors = new();
    private readonly List<PointValueChangedEvent> _valueEvents = new();
    private SamplerEngine? _engine;

    public void Dispose() => _engine?.Dispose();

    // ─────────────── 夹具 ───────────────

    private const string Template = """
        <SamplerConfig schemaVersion="3.0">
          <Global>
            <Polling defaultIntervalMs="120" requestTimeoutMs="500" />
            <Quality onCommError="bad" onCommErrorValue="null" />
            {0}
          </Global>
          <Transports><Transport id="tcp1" host="127.0.0.1" /></Transports>
          <Devices><Device id="d1" transport="tcp1" pointSet="ps1" unitId="1" /></Devices>
          <PointSets><PointSet id="ps1">{1}</PointSet></PointSets>
        </SamplerConfig>
        """;

    private SamplerEngine Start(string points, string globalExtra = "", bool start = true)
    {
        var config = SamplerConfigLoader.Load(
            XDocument.Parse(string.Format(Template, globalExtra, points)), Directory.GetCurrentDirectory());

        _engine = new SamplerEngine(config, new Dictionary<string, IModbusLink> { ["tcp1"] = _link });
        _engine.Bus.Subscribe<IErrorEvent>(e => _errors.Add(e.Body), DeliveryMode.Inline);
        _engine.Bus.Subscribe<PointValueChangedEvent>(e => _valueEvents.Add(e.Body), DeliveryMode.Inline);
        if (start) _engine.Start();
        return _engine;
    }

    /// <summary>给从站 1 的准备寄存器区放进一组读回数据。</summary>
    private void Feed(params ushort[] registers)
        => _link.SetReadData(1, DataArea.HoldingRegister, 0, registers);

    private static void WaitUntil(Func<bool> condition, string what, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            Thread.Sleep(20);
        }

        Assert.Fail("等待超时：" + what);
    }

    private PointValue Value(string pointId) => _engine!.GetValueDetail("d1", pointId);

    private static IEnumerable<IErrorEvent> ScriptErrors(IEnumerable<IErrorEvent> events)
        => events.Where(e => e.Info.Code.StartsWith("SS.SCRIPT.", StringComparison.Ordinal));

    // ─────────────── ① 成功转换：脚本返回值就是工程值 ───────────────

    [Fact]
    public void Script_converts_raw_registers_to_engineering_value()
    {
        Start("<Points><Point id=\"p\" address=\"0\" dataType=\"uint16\"><Script>rawValue * 0.1</Script></Point></Points>");
        Feed(250);

        WaitUntil(() => Value("p").IsGood, "脚本点位出值");

        Assert.Equal(25.0, Convert.ToDouble(Value("p").Value));
        Assert.Empty(ScriptErrors(_errors));
    }

    [Fact]
    public void Script_can_branch_look_up_and_return_string()
    {
        // 一个寄存器里塞了两种语义（低位计数 + 高位状态）——正是「畸形打包只能靠脚本解」的场景
        Start("<Points><Point id=\"p\" address=\"0\" dataType=\"uint16\">"
              + "<Script>var state = (rawValue &gt;&gt; 12) &amp; 0xF; state === 3 ? 'ALARM' : (rawValue &amp; 0xFFF)</Script>"
              + "</Point></Points>");

        Feed(0x3ABC);
        WaitUntil(() => Value("p").IsGood, "分支脚本出值");

        Assert.Equal("ALARM", Value("p").Value);
    }

    [Fact]
    public void Script_receives_all_raw_registers_of_a_multi_word_point()
    {
        // raw 是「该点位声明字长」的原始寄存器数组，脚本可自行拼装（例如非常规字序的 32 位量）
        Start("<Points><Point id=\"p\" address=\"0\" dataType=\"uint32\" length=\"2\" swap=\"none\">"
              + "<Script>raw[0] + raw[1]</Script></Point></Points>");

        Feed(7, 5);
        WaitUntil(() => Value("p").IsGood, "多字脚本出值");

        Assert.Equal(12.0, Convert.ToDouble(Value("p").Value));
    }

    [Fact]
    public void Script_output_skips_scale_and_yields_engineering_value()
    {
        // 脚本产出即工程值：Scale 只在无脚本点位上生效（否则会二次换算，把脚本结果又缩一遍）
        Start("<Points><Point id=\"p\" address=\"0\" dataType=\"uint16\"><Scale factor=\"10\" />"
              + "<Script>rawValue + 1</Script></Point></Points>");

        Feed(4);
        WaitUntil(() => Value("p").IsGood, "脚本点位出值");

        Assert.Equal(5.0, Convert.ToDouble(Value("p").Value));   // 不是 (4+1)*10
    }

    // ─────────────── ② 返回 null → 按 onError 置 Bad ───────────────

    [Fact]
    public void Script_returning_null_marks_bad_with_reason()
    {
        Start("<Points><Point id=\"p\" address=\"0\" dataType=\"uint16\">"
              + "<Script>var x = null; x</Script></Point></Points>");
        Feed(1);

        WaitUntil(() => Value("p").Reason == "ss.reason.scriptNull", "null 脚本置坏");

        Assert.Equal(PointQuality.Bad, Value("p").Quality);
        Assert.Equal("ss.reason.scriptNull", Value("p").Reason);
        Assert.Empty(ScriptErrors(_errors));   // 主动返回 null 不是故障，不刷错误事件
    }

    // ─────────────── ③ 抛异常 → Bad + 错误事件 ───────────────

    [Fact]
    public void Script_exception_marks_bad_and_emits_error_event()
    {
        Start("<Points><Point id=\"p\" address=\"0\" dataType=\"uint16\">"
              + "<Script>undefinedVar.notAMethod()</Script></Point></Points>");
        Feed(1);

        WaitUntil(() => ScriptErrors(_errors).Any(), "脚本异常错误事件");

        Assert.Equal(PointQuality.Bad, Value("p").Quality);
        Assert.Equal("ss.reason.scriptError", Value("p").Reason);

        var error = Assert.Single(ScriptErrors(_errors));
        Assert.Equal("SS.SCRIPT.FAILED", error.Info.Code);
        Assert.Equal(ErrorSource.Codec, error.Info.Source);
        Assert.Equal("d1", error.Info.Context.DeviceId);
        Assert.Equal("p", error.Info.Context.PointId);
        Assert.IsType<DecodeError>(error);   // 解码层永久错误（重试无意义）
    }

    // ─────────────── ④ 超时 → Bad + 错误事件（且只记一次） ───────────────

    [Fact]
    public void Script_timeout_marks_bad_and_emits_one_error_event()
    {
        Start("<Points><Point id=\"p\" address=\"0\" dataType=\"uint16\">"
              + "<Script timeoutMs=\"50\">while (true) { }</Script></Point></Points>");
        Feed(1);

        WaitUntil(() => Value("p").Quality == PointQuality.Bad, "超时脚本置坏");
        // 再等几轮（120ms 周期），确认持续超时不会每轮刷一条事件
        Thread.Sleep(600);

        Assert.Equal("ss.reason.scriptTimeout", Value("p").Reason);

        var error = Assert.Single(ScriptErrors(_errors));
        Assert.Equal("SS.SCRIPT.TIMEOUT", error.Info.Code);
        Assert.IsType<DecodeError>(error);
    }

    // ─────────────── ⑤ 脚本失败不影响采集线程：后续点位照常 ───────────────

    [Fact]
    public void Script_failure_does_not_stop_the_polling_thread()
    {
        // 同设备两个点位：坏脚本 + 正常点。坏脚本既超时又抛异常，正常点必须继续刷新。
        Start("""
            <Points>
              <Point id="bad" address="0" dataType="uint16"><Script timeoutMs="50">while (true) { }</Script></Point>
              <Point id="throwing" address="1" dataType="uint16"><Script>nope.x()</Script></Point>
              <Point id="good" address="2" dataType="uint16" />
            </Points>
            """);
        Feed(11, 22, 33);

        WaitUntil(() => Value("good").IsGood && Convert.ToInt32(Value("good").Value) == 33, "正常点出值");

        var readsBefore = _link.ReadCalls;
        WaitUntil(() => _link.ReadCalls > readsBefore + 1, "轮询线程仍在发请求");

        Assert.Equal(PointQuality.Bad, Value("bad").Quality);
        Assert.Equal(PointQuality.Bad, Value("throwing").Quality);
        Assert.True(Value("good").IsGood);   // 线程活着，同窗口的其它点位照常入库
    }

    // ─────────────── ⑥ Global/Script 缺省继承 ───────────────

    [Fact]
    public void Point_script_inherits_global_timeout_and_on_error()
    {
        var engine = Start(
            "<Points><Point id=\"p\" address=\"0\" dataType=\"uint16\">"
            + "<Script>while (true) { }</Script></Point></Points>",
            "<Script timeoutMs=\"80\" onError=\"markBad\" />");
        Feed(1);

        // 解析层：点位未写覆盖值 → 全局默认生效
        var point = engine.GetValueDetail("d1", "p");   // 触发一次读取，确保脚本路径跑过
        _ = point;
        WaitUntil(() => Value("p").Reason == "ss.reason.scriptTimeout", "继承全局超时后置坏");
        Assert.Equal("ss.reason.scriptTimeout", Value("p").Reason);
        Assert.Single(ScriptErrors(_errors));
    }

    [Fact]
    public void Global_script_defaults_are_parsed_and_point_override_wins()
    {
        var config = SamplerConfigLoader.Load(XDocument.Parse("""
            <SamplerConfig schemaVersion="3.0">
              <Global>
                <Script timeoutMs="120" onError="markBad" />
              </Global>
              <Transports><Transport id="tcp1" host="127.0.0.1" /></Transports>
              <Devices><Device id="d1" transport="tcp1" pointSet="ps1" unitId="1" /></Devices>
              <PointSets><PointSet id="ps1"><Points>
                <Point id="inherits" address="0" dataType="uint16"><Script>rawValue</Script></Point>
                <Point id="override" address="1" dataType="uint16"><Script timeoutMs="7" onError="markBad">rawValue</Script></Point>
              </Points></PointSet></PointSets>
            </SamplerConfig>
            """), Directory.GetCurrentDirectory());

        Assert.Equal(120, config.Global.ScriptTimeoutMs);
        Assert.Equal("markBad", config.Global.ScriptOnError);

        var points = config.PointSets[0].Points;
        Assert.Null(points[0].ScriptTimeoutMs);          // 未写 → 运行期取全局
        Assert.Null(points[0].ScriptOnError);
        Assert.Equal(7, points[1].ScriptTimeoutMs);      // 点位覆盖优先
        Assert.Equal("markBad", points[1].ScriptOnError);
    }

    // ─────────────── ⑦ ES5.1 限制：箭头函数 → 按 onError 处理，不崩 ───────────────

    [Fact]
    public void Es6_arrow_function_is_rejected_via_on_error_not_a_crash()
    {
        Start("<Points><Point id=\"p\" address=\"0\" dataType=\"uint16\">"
              + "<Script>var f = (x) =&gt; x * 2; f(rawValue)</Script></Point></Points>");
        Feed(3);

        WaitUntil(() => Value("p").Reason == "ss.reason.scriptError", "ES6 语法置坏");

        Assert.Equal("ss.reason.scriptError", Value("p").Reason);
        Assert.Equal("SS.SCRIPT.FAILED", Assert.Single(ScriptErrors(_errors)).Info.Code);

        // 线程照常：读计数继续增长
        var readsBefore = _link.ReadCalls;
        WaitUntil(() => _link.ReadCalls > readsBefore + 1, "语法错误后轮询线程仍活着");
    }

    // ─────────────── ⑧ 计算点脚本型（FR-7.2） ───────────────

    [Fact]
    public void Calculated_point_script_is_evaluated_on_read()
    {
        var engine = Start("""
            <Points><Point id="p" address="0" dataType="uint16" /></Points>
            <Calculated><Point id="c"><Script>P('p') * 2</Script></Point></Calculated>
            """);
        Feed(21);

        WaitUntil(() => Value("p").IsGood, "底点位出值");
        WaitUntil(() => Value("c").IsGood, "脚本型计算点出值");

        Assert.Equal(42.0, Convert.ToDouble(Value("c").Value));
        Assert.Equal("42", engine.GetValue("d1", "c"));   // 门面（格式化）走同一条路：21 × 2
    }

    [Fact]
    public void Calculated_point_script_timeout_marks_bad_and_records_error()
    {
        Start("""
            <Points><Point id="p" address="0" dataType="uint16" /></Points>
            <Calculated><Point id="c"><Script timeoutMs="50">while (true) { }</Script></Point></Calculated>
            """);
        Feed(1);

        WaitUntil(() => Value("c").Reason == "ss.reason.scriptTimeout", "计算点脚本超时置坏");
        Assert.Equal("ss.reason.scriptTimeout", Value("c").Reason);
        Assert.Equal("SS.SCRIPT.TIMEOUT", Assert.Single(ScriptErrors(_errors)).Info.Code);
    }

    // ─────────────── ⑨ 按需读 / 块读路径与轮询同口径 ───────────────

    [Fact]
    public async Task Trigger_read_applies_the_same_script_semantics()
    {
        var engine = Start("<Points><Point id=\"p\" address=\"0\" dataType=\"uint16\">"
                           + "<Script>rawValue / 4</Script></Point></Points>");
        Feed(100);

        var value = await engine.TriggerReadAsync("d1", "p");

        Assert.True(value.IsGood);
        Assert.Equal(25.0, Convert.ToDouble(value.Value));
    }

    [Fact]
    public async Task Block_read_applies_the_same_script_semantics()
    {
        var engine = Start("""
            <Blocks><Block id="b1" start="0" count="4"><Point id="p" address="0" dataType="uint16">
              <Script>rawValue + 1000</Script></Point></Block></Blocks>
            """);
        Feed(23, 0, 0, 0);

        await engine.TriggerBlockReadAsync("d1/b1");

        var value = engine.GetValueDetail("d1", "p");
        Assert.True(value.IsGood);
        Assert.Equal(1023.0, Convert.ToDouble(value.Value));
    }

    // ─────────────── ⑩ 两条取值路径共用同一个 ScriptDecoder（findings W72） ───────────────

    [Fact]
    public async Task Same_script_failure_emits_one_error_across_facade_and_scheduler_paths()
    {
        // 同一点位有两条取值路径：门面按需读（TriggerReadAsync → 引擎解码器）与
        // 调度路径（RetryDeviceAsync → Scheduler.ReadWindow → 调度器解码器）。
        // 此前两条路径各 new 一个 ScriptDecoder，「同点位同类失败只发一条」的去重状态分裂成两份
        // → 同一次脚本失败会发两条 SS.SCRIPT.FAILED（findings W72）。合并实例后必须「同错一条」。
        // 本用例**不启动轮询线程**（start: false）：两条路径都由测试显式驱动，计数才可判定。
        _link.DefaultReadData = new ushort[8];   // 够点位字长 → 脚本真的会跑（短帧会跳过脚本）
        var engine = Start("<Points><Point id=\"p\" address=\"0\" dataType=\"uint16\">"
                           + "<Script>nope.x()</Script></Point></Points>", start: false);

        // ① 门面路径 → 第一条（也是唯一一条）失败事件
        var facade = await engine.TriggerReadAsync("d1", "p");
        Assert.Equal("ss.reason.scriptError", facade.Reason);
        Assert.Single(ScriptErrors(_errors));

        // ② 调度路径：同一失败种类不得再发一条
        var retry = await engine.RetryDeviceAsync("d1");
        Assert.Equal(ManualRetryOutcome.Succeeded, retry.Outcome);   // 通讯本身成功（脚本失败不是通讯失败）
        Assert.Equal(PointQuality.Bad, Value("p").Quality);
        Assert.Single(ScriptErrors(_errors));

        // ③ 再跑一轮两条路径：仍然是同一条（去重状态在实例级，不随路径/轮次重置）
        await engine.TriggerReadAsync("d1", "p");
        await engine.RetryDeviceAsync("d1");
        var only = Assert.Single(ScriptErrors(_errors));
        Assert.Equal("SS.SCRIPT.FAILED", only.Info.Code);
        Assert.Equal("d1", only.Info.Context.DeviceId);
        Assert.Equal("p", only.Info.Context.PointId);
    }
}
