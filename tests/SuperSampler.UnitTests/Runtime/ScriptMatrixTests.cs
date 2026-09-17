using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Xml.Linq;
using SuperSampler.Abstractions.Errors;
using SuperSampler.Abstractions.Events;
using SuperSampler.Abstractions.Values;
using SuperSampler.Core.Config;
using SuperSampler.Core.Runtime;
using SuperSampler.Drivers.Modbus;
using SuperSampler.Drivers.Modbus.Wire;
using SuperSampler.UnitTests.Support;
using Xunit;

namespace SuperSampler.UnitTests.Runtime;

/// <summary>
/// 点位脚本（ADR D41，Jint ES5.1）质量与错误事件矩阵（模块重测「编解码」覆盖 9 与 10 的脚本部分）：
/// 返回值语义（null/NaN/字符串/布尔/对象）、坏依赖传染（findings D50）、超时与异常的**同错去重**与恢复重臂、
/// 绑定注入（raw/rawValue/P/T/device/point/timestamp）、脚本失败不影响其它点位。
/// 既有覆盖见 <c>ScriptDecodeTests</c>（端到端主路径）与 <c>EvaluatorTests.ScriptEvaluatorTests</c>（执行器语义），本文件只补矩阵缺口。
/// </summary>
public class ScriptMatrixTests
{
    // ═══════════════ A. 执行器层（纯单测） ═══════════════

    private static ScriptEvaluator Evaluator(PointValue? value = null)
        => new(_ => value, key => key == "UNIT" ? "°C" : null);

    private static ScriptResult Run(string script, PointValue? dependency = null, int timeoutMs = 1000)
        => Evaluator(dependency).Execute(script, Array.Empty<object?>(), new ushort[] { 7 }, "d1", "p1", timeoutMs, 64, 7);

    [Fact]
    public void P_of_a_bad_or_missing_dependency_is_undefined_and_records_the_usage()
    {
        // findings D50 的注入侧契约：坏值/缺失 → JS undefined（不是 null！null 在算术里被强制转换）
        var missing = Run("typeof P('nope')");
        Assert.True(missing.Succeeded);
        Assert.Equal("undefined", missing.Value as string);
        Assert.True(missing.UsedBadDependency);

        var bad = Run("typeof P('bad')", PointValue.Bad("ss.reason.shortFrame", DateTimeOffset.UtcNow));
        Assert.Equal("undefined", bad.Value as string);
        Assert.True(bad.UsedBadDependency);
    }

    [Fact]
    public void P_of_a_good_dependency_returns_the_value_and_does_not_mark_the_script()
    {
        var result = Run("P('good') * 2", PointValue.Good(21.0, DateTimeOffset.UtcNow));

        Assert.True(result.Succeeded);
        Assert.Equal(42.0, Assert.IsType<double>(result.Value));
        Assert.False(result.UsedBadDependency);
    }

    [Fact]
    public void Arithmetic_on_a_bad_dependency_yields_NaN_instead_of_a_coerced_number()
    {
        // 修复前：null + 1 === 1（伪正常数）；现在 undefined + 1 === NaN，结果不再「看似合理」
        var result = Run("P('bad') + 1", PointValue.Bad("ss.reason.decode", DateTimeOffset.UtcNow));

        Assert.True(result.Succeeded);
        Assert.True(double.IsNaN(Assert.IsType<double>(result.Value)));
        Assert.True(result.UsedBadDependency);
    }

    [Fact]
    public void Loose_equality_guard_still_detects_a_bad_dependency()
    {
        // 配置里「先判空再决定」的写法必须继续可用：undefined == null 为真
        var result = Run("if (P('bad') == null) { return 'MISSING'; } return 'OK';",
            PointValue.Bad("ss.reason.decode", DateTimeOffset.UtcNow));

        Assert.True(result.Succeeded);
        Assert.Equal("MISSING", result.Value as string);
        Assert.True(result.UsedBadDependency);
    }

    [Fact]
    public void Good_but_non_numeric_dependency_is_passed_through_as_its_value()
    {
        // 口径：框架正常返回的数据不算异常——字符串/布尔点被脚本引用时不毒化（脚本可能就是在拼文本）
        var text = Run("P('txt') + '/tail'", PointValue.Good("AB", DateTimeOffset.UtcNow));
        Assert.Equal("AB/tail", text.Value as string);
        Assert.False(text.UsedBadDependency);

        var flag = Run("P('flag') === true", PointValue.Good(true, DateTimeOffset.UtcNow));
        Assert.Equal(true, flag.Value);
        Assert.False(flag.UsedBadDependency);
    }

    [Fact]
    public void All_documented_bindings_are_injected()
    {
        var result = Run("device + '|' + point + '|' + raw.length + '|' + rawValue + '|' + args.length + '|' + T('UNIT')");

        Assert.True(result.Succeeded);
        Assert.Equal("d1|p1|1|7|0|°C", result.Value as string);
    }

    [Fact]
    public void NaN_result_is_a_successful_script_run_with_a_NaN_value()
    {
        // 质量判定在 ScriptDecoder（NaN → Uncertain(ss.reason.nan)，ADR D32）；执行器只如实交付 NaN
        var result = Run("0/0");

        Assert.True(result.Succeeded);
        Assert.True(double.IsNaN(Assert.IsType<double>(result.Value)));
        Assert.False(result.UsedBadDependency);
    }

    [Fact]
    public void Timeout_failure_reports_timed_out_and_the_kind_is_distinguishable()
    {
        var timeout = Run("while (true) { }", timeoutMs: 50);
        var thrown = Run("nope.method()");

        Assert.True(timeout.TimedOut);
        Assert.False(thrown.TimedOut);
        Assert.NotNull(thrown.Failure);
    }

    // ═══════════════ B. 门面层（真实轮询线程，见文件尾部的 Facade 类） ═══════════════
}

/// <summary>
/// 脚本的点位级行为（真实调度线程）：返回值 → 质量映射、坏依赖传染、错误事件去重与重臂、绑定注入。
/// 加入命名集合 <c>real-polling-threads</c>：本类起真实轮询线程，
/// 与 <c>BackoffTests.B16</c>（断言进程级句柄数）必须串行执行。
/// </summary>
[Collection("real-polling-threads")]
public sealed class ScriptMatrixFacadeTests : IDisposable
{
    private readonly FakeModbusLink _link = new();
    private readonly List<IErrorEvent> _errors = new();
    private SamplerEngine? _engine;

    private const string Template = """
        <SamplerConfig schemaVersion="3.0">
          <Global>
            <Polling defaultIntervalMs="120" requestTimeoutMs="500" />
            <Quality onCommError="bad" onCommErrorValue="null" />
            {0}
          </Global>
          <I18n><Files><File lang="zh_CN" path="script.i18n" /></Files></I18n>
          <Transports><Transport id="tcp1" host="127.0.0.1" /></Transports>
          <Devices><Device id="d1" transport="tcp1" pointSet="ps1" unitId="1" /></Devices>
          <PointSets><PointSet id="ps1">{1}</PointSet></PointSets>
        </SamplerConfig>
        """;

    private static string I18nPath => Path.Combine(Directory.GetCurrentDirectory(), "script.i18n");

    public ScriptMatrixFacadeTests()
    {
        // 脚本里的 T('key') 走配置的 i18n 目录：给测试工作目录放一份资源
        File.WriteAllText(I18nPath, "S_UNIT = \"摄氏度\"\n");

        // FakeModbusLink 的精确读数据是「按请求起始地址」匹配的（SetReadData 的 address 必须等于分组窗口起点），
        // 这里给一个全零兜底，测试只对「从地址 0 起的窗口」用 Feed 精确投喂；其它窗口读到 0（同样是合法数据）。
        _link.DefaultReadData = new ushort[4096];
    }

    public void Dispose()
    {
        _engine?.Dispose();
        try { File.Delete(I18nPath); }
        catch (IOException) { /* 清理失败不影响断言 */ }
    }

    private SamplerEngine Start(string points, string globalExtra = "", FakeModbusLink? link = null)
    {
        var config = SamplerConfigLoader.Load(
            XDocument.Parse(string.Format(Template, globalExtra, points)), Directory.GetCurrentDirectory());

        var target = link ?? _link;
        if (link != null) target.DefaultReadData = new ushort[4096];

        _engine = new SamplerEngine(config, new Dictionary<string, IModbusLink> { ["tcp1"] = target });
        _engine.Bus.Subscribe<IErrorEvent>(e => _errors.Add(e.Body), DeliveryMode.Inline);
        _engine.Start();
        return _engine;
    }

    private PointValue Value(string id) => _engine!.GetValueDetail("d1", id);

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

    /// <summary>脚本类错误事件的快照（列表由轮询线程追加，测试线程取副本后再断言）。</summary>
    private IErrorEvent[] ScriptErrors()
        => _errors.Where(e => e.Info.Code.StartsWith("SS.SCRIPT.", StringComparison.Ordinal)).ToArray();

    [Fact]
    public void Script_NaN_is_degraded_to_uncertain_with_reason_and_value_kept()
    {
        Start("<Points><Point id=\"p\" address=\"0\" dataType=\"float64\"><Script>0/0</Script></Point></Points>");
        Feed(1);

        WaitUntil(() => Value("p").Quality == PointQuality.Uncertain, "NaN 脚本降级");
        Assert.Equal("ss.reason.nan", Value("p").Reason);
        Assert.True(double.IsNaN((double)Value("p").Value!));

        // 非有限浮点不是脚本故障：不刷错误事件（ADR D32）
        Assert.Empty(ScriptErrors());
    }

    [Fact]
    public void Script_object_result_is_normalized_to_text_never_a_script_engine_type()
    {
        Start("<Points><Point id=\"p\" address=\"0\" dataType=\"raw\"><Script>[1,2,3]</Script></Point></Points>");
        Feed(1);

        WaitUntil(() => Value("p").IsGood, "对象脚本出值");
        var text = Assert.IsType<string>(Value("p").Value);
        Assert.DoesNotContain("Jint", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Script_can_compose_text_from_a_point_and_i18n()
    {
        // 偶发加固：数据必须先摆好再 Start。此前先 Start 再 Feed，首个节拍会拿默认全零数据算出一份
        // 「好值」（P('txt') 拿到空串 → "||摄氏度" 也是 Good），等待条件先命中它，断言随即失败。
        Feed(0x4142, 0x0043);   // txt（address 0）= "AB"，p（address 1）的 rawValue = "C"
        Start("<Points><Point id=\"txt\" address=\"0\" dataType=\"string\" length=\"1\" />"
              + "<Point id=\"p\" address=\"1\" dataType=\"string\" length=\"1\">"
              + "<Script>rawValue + '|' + P('txt') + '|' + T('S_UNIT')</Script></Point></Points>");

        // 等待条件与断言对齐：**两个点都落了缓存**再断言（txt 是脚本的依赖）
        WaitUntil(() => Value("txt").IsGood && Value("p").IsGood, "文本拼接脚本出值");
        Assert.Equal("AB", Value("txt").Value);
        Assert.Equal("C|AB|摄氏度", Value("p").Value);
    }

    [Fact]
    public void Script_error_events_are_deduplicated_per_kind_and_rearmed_after_recovery()
    {
        // 同一个脚本按输入换挡：rawValue==1 → 抛异常；==2 → 死循环（超时）；其它 → 正常返回值。
        // 去重口径（ScriptDecoder._lastFailure）：同一点位持续同类失败只发一条；
        // 失败**种类变化**或**中途成功过**都要重新上报。
        Start("<Points><Point id=\"p\" address=\"0\" dataType=\"uint16\">"
              + "<Script timeoutMs=\"50\">"
              + "if (rawValue === 1) { nope.method(); } if (rawValue === 2) { while (true) { } } return rawValue;"
              + "</Script></Point></Points>");

        Feed(1);
        WaitUntil(() => ScriptErrors().Any(e => e.Info.Code == "SS.SCRIPT.FAILED"), "异常失败事件");
        Assert.Equal("ss.reason.scriptError", Value("p").Reason);

        // 同类持续失败：再等几轮不追加事件
        Thread.Sleep(500);
        Assert.Single(ScriptErrors());

        // 恢复一次（清空去重状态）→ 换另一种失败（种类变化）→ 必须重新上报
        Feed(9);
        WaitUntil(() => Value("p").IsGood && Convert.ToInt32(Value("p").Value) == 9, "脚本恢复");
        Feed(2);
        WaitUntil(() => ScriptErrors().Length == 2, "恢复后的新失败事件");
        Assert.Contains(ScriptErrors(), e => e.Info.Code == "SS.SCRIPT.TIMEOUT");

        // 再恢复再失败（同一种）→ 又一次上报（恢复即重臂）
        Feed(5);
        WaitUntil(() => Value("p").IsGood, "第二次恢复");
        Feed(1);
        WaitUntil(() => ScriptErrors().Length == 3, "再次异常失败事件");
        Assert.Equal("SS.SCRIPT.FAILED", ScriptErrors().Last().Info.Code);
    }

    [Fact]
    public void Decode_path_script_with_a_bad_dependency_is_bad_not_a_coerced_number()
    {
        // 解码路径（非计算点）的脚本同样走 P() → 坏依赖必须传染（口径与计算点一致）
        Start("<Points>"
              + "<Point id=\"raw\" address=\"0\" mode=\"onDemand\" />"
              + "<Point id=\"p\" address=\"1\" dataType=\"float64\"><Script>P('raw') + 1</Script></Point>"
              + "</Points>");
        Feed(5, 6, 7, 8, 9);

        // 等「脚本真的评估过」：未轮询前缓存里是 Bad(ss.reason.notInCache)，不能拿它当结论
        WaitUntil(() => Value("p").Reason == "ss.reason.calculate", "解码脚本坏依赖置坏");
        Assert.Equal(PointQuality.Bad, Value("p").Quality);
        Assert.Null(Value("p").Value);
    }

    [Fact]
    public void Script_reading_a_good_sibling_point_yields_the_composed_value()
    {
        // 偶发加固（同 Script_can_compose_text_from_a_point_and_i18n）：数据先摆好再 Start，
        // 等待条件同时要求依赖点与脚本点都出值——否则首个「全零节拍」也算 Good，断言会拿到 0/4 而不是 25。
        Feed(100, 0, 0, 0, 0);
        Start("<Points>"
              + "<Point id=\"a\" address=\"0\" dataType=\"uint16\" intervalMs=\"100\" />"
              + "<Point id=\"b\" address=\"1\" dataType=\"float64\"><Script>P('a') / 4</Script></Point>"
              + "</Points>");

        WaitUntil(() => Value("a").IsGood && Value("b").IsGood, "同点表依赖脚本出值");
        Assert.Equal(100, Convert.ToInt32(Value("a").Value));
        Assert.Equal(25.0, Convert.ToDouble(Value("b").Value));
    }

    [Fact]
    public void Script_reads_a_calculated_dependency_without_any_host_read()
    {
        // D60 的回归（原 `..._is_order_dependent_todays_behaviour_locked` 断言的是缺陷现状，已反转）：
        // 轮询/脚本解码路径与门面路径共用同一份取值口径（CalculatedPointResolver）——
        // 脚本依赖的计算点缓存里没有好值时**递归求值**，不再要求「宿主先读过那个计算点」。
        // 数据先摆好再 Start，保证断言的是真实数据而不是首个全零节拍。
        Feed(21, 0, 0, 0, 0);
        Start("<Points>"
              + "<Point id=\"raw\" address=\"0\" dataType=\"uint16\" intervalMs=\"100\" />"
              + "<Point id=\"b\" address=\"1\" dataType=\"float64\" intervalMs=\"100\"><Script>P('calc') + 1</Script></Point>"
              + "</Points>"
              + "<Calculated><Point id=\"calc\"><Expression>P('raw') * 2</Expression></Point></Calculated>");

        WaitUntil(() => Value("b").IsGood, "轮询路径必须自己把计算点依赖算出来");
        Assert.Equal(43.0, Convert.ToDouble(Value("b").Value));

        // 宿主从未读过 calc：轮询路径的递归求值按既有口径把它写进缓存（求值成功即落缓存 + 记采集年龄）
        Assert.Equal(42.0, Convert.ToDouble(_engine!.GetValueDetail("d1", "calc").Value));
        Assert.NotNull(_engine.GetValueAge("d1", "calc"));
    }

    [Fact]
    public void Script_dependency_chain_of_three_calculated_levels_resolves_in_the_polling_path()
    {
        // A→B→C 三层链：raw（轮询点）→ a = raw+1 → b = a*10 → c = b-5，采集点 s 的脚本读链顶 c。
        // 轮询/脚本解码路径必须**一次算到底**（递归是链式的，不是只解一层），与门面路径同结果。
        Feed(3, 0, 0, 0, 0);
        Start("<Points>"
              + "<Point id=\"raw\" address=\"0\" dataType=\"uint16\" intervalMs=\"100\" />"
              + "<Point id=\"s\" address=\"1\" dataType=\"float64\" intervalMs=\"100\"><Script>P('c') * 2</Script></Point>"
              + "</Points>"
              + "<Calculated>"
              + "<Point id=\"a\"><Expression>P('raw') + 1</Expression></Point>"
              + "<Point id=\"b\"><Expression>P('a') * 10</Expression></Point>"
              + "<Point id=\"c\"><Expression>P('b') - 5</Expression></Point>"
              + "</Calculated>");

        WaitUntil(() => Value("s").IsGood, "脚本读三层计算点链");
        Assert.Equal(70.0, Convert.ToDouble(Value("s").Value));   // c = ((3+1)*10)-5 = 35 → ×2 = 70

        // 同一条链上门面路径给出同一组值（递归求值已把中间层写进缓存）
        Assert.Equal(35.0, Convert.ToDouble(_engine!.GetValueDetail("d1", "c").Value));
        Assert.Equal(40.0, Convert.ToDouble(_engine.GetValueDetail("d1", "b").Value));
        Assert.Equal(4.0, Convert.ToDouble(_engine.GetValueDetail("d1", "a").Value));
    }

    [Fact]
    public void Polling_path_bad_calculated_dependency_poisons_the_script_point()
    {
        // 坏值沿链传染（轮询/脚本路径）：raw 所在窗口采不到 → calc = P('raw')*2 坏 →
        // 脚本点 s 拿到 undefined → Bad(ss.reason.calculate)，绝不给出被强转的伪正常数（null + 1 === 1）。
        // 两个窗口（0 与 100）分属两拍请求：只有地址 0 失败，s 的窗口读成功、脚本真的跑过。
        _link.DefaultReadData = new ushort[256];
        _link.FaultRules.Add(new FakeFaultRule
        {
            Kind = ModbusFailureKind.Timeout,
            Area = DataArea.HoldingRegister,
            MinAddress = 0,
            MaxAddress = 50,
        });

        Start("<Points>"
              + "<Point id=\"raw\" address=\"0\" dataType=\"uint16\" intervalMs=\"100\" />"
              + "<Point id=\"s\" address=\"100\" dataType=\"float64\" intervalMs=\"100\"><Script>P('calc') + 1</Script></Point>"
              + "</Points>"
              + "<Calculated><Point id=\"calc\"><Expression>P('raw') * 2</Expression></Point></Calculated>",
              globalExtra: "<Scheduler ignoreGap=\"5\" />");

        WaitUntil(() => Value("s").Reason == "ss.reason.calculate", "坏依赖必须传染到脚本点");
        Assert.Equal(PointQuality.Bad, Value("s").Quality);
        Assert.Null(Value("s").Value);
        Assert.Equal(PointQuality.Bad, _engine!.GetValueDetail("d1", "calc").Quality);
    }

    [Fact]
    public void Runtime_cycle_through_a_calculated_dependency_does_not_hang_the_polling_path()
    {
        // 运行期防环兜底（线程内访问集合 + 深度上限）在**轮询/脚本解码路径**同样成立：
        // 动态拼引用的环（正则扫不到字面量，加载期 CGV-15 报不了）只能靠运行期兜底。
        // 要求：有界返回坏值，绝不栈溢出/死循环，轮询线程照常发下一拍请求。
        Feed(5, 0, 0, 0, 0);
        Start("<Points>"
              + "<Point id=\"p\" address=\"0\" dataType=\"uint16\" intervalMs=\"100\" />"
              + "<Point id=\"s\" address=\"1\" dataType=\"float64\" intervalMs=\"100\"><Script>P('x') + 1</Script></Point>"
              + "</Points>"
              + "<Calculated>"
              + "<Point id=\"x\"><Script>P(device + '/y') + 1</Script></Point>"
              + "<Point id=\"y\"><Script>P(device + '/x') + 1</Script></Point>"
              + "</Calculated>");

        var started = DateTime.UtcNow;
        WaitUntil(() => Value("s").Reason == "ss.reason.calculate", "环上的依赖必须判坏值");
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(4),
            $"防环兜底必须有界返回（实测 {(DateTime.UtcNow - started).TotalMilliseconds:F0}ms）");
        Assert.Equal(PointQuality.Bad, Value("s").Quality);

        // 轮询线程还活着：之后仍在继续发请求（防环兜底绝不能打死采集线程）
        var reads = _link.Calls.Count(c => c.IsRead);
        WaitUntil(() => _link.Calls.Count(c => c.IsRead) > reads, "轮询线程必须继续跑");
    }

    [Fact]
    public void Polling_path_and_facade_path_agree_no_matter_who_reads_the_calculated_point_first()
    {
        // D60 的本质（最重要的一条）：同一份配置的结果**不得**取决于「宿主有没有先读过那个计算点」。
        // 两个引擎跑同一份配置：A 从不触碰 calc（轮询路径自己算），B 先由宿主读一次 calc（写进缓存）。
        const string points = "<Points>"
            + "<Point id=\"raw\" address=\"0\" dataType=\"uint16\" intervalMs=\"100\" />"
            + "<Point id=\"b\" address=\"1\" dataType=\"float64\" intervalMs=\"100\"><Script>P('calc') + 1</Script></Point>"
            + "</Points>"
            + "<Calculated><Point id=\"calc\"><Expression>P('raw') * 2</Expression></Point></Calculated>";

        // A：宿主从不读 calc
        var linkA = new FakeModbusLink { DefaultReadData = new ushort[4096] };
        linkA.SetReadData(1, DataArea.HoldingRegister, 0, 21, 0, 0, 0);
        var engineA = Start(points, link: linkA);
        WaitUntil(() => engineA.GetValueDetail("d1", "b").IsGood, "A：轮询路径自己算出计算点依赖");
        var byPollingOnly = engineA.GetValueDetail("d1", "b");
        Assert.Equal(43.0, Convert.ToDouble(byPollingOnly.Value));
        engineA.Dispose();

        // B：宿主先读 calc（门面路径递归求值并写缓存），再看轮询路径的结果
        var linkB = new FakeModbusLink { DefaultReadData = new ushort[4096] };
        linkB.SetReadData(1, DataArea.HoldingRegister, 0, 21, 0, 0, 0);
        var engineB = Start(points, link: linkB);
        WaitUntil(() => engineB.GetValueDetail("d1", "raw").IsGood, "B：底点位出值");
        Assert.Equal(42.0, Convert.ToDouble(engineB.GetValueDetail("d1", "calc").Value));   // 宿主先读
        WaitUntil(() => engineB.GetValueDetail("d1", "b").IsGood, "B：轮询路径出值");
        var byFacadeFirst = engineB.GetValueDetail("d1", "b");

        // 两条路径必须同结果、同质量
        Assert.Equal(43.0, Convert.ToDouble(byFacadeFirst.Value));
        Assert.Equal(byPollingOnly.Quality, byFacadeFirst.Quality);
        Assert.Equal(Convert.ToDouble(byPollingOnly.Value), Convert.ToDouble(byFacadeFirst.Value));
    }

    [Fact]
    public void Recursion_depth_limit_yields_a_bad_value_not_a_stack_overflow()
    {
        // 深度上限触发时的质量（运行期兜底防线）：40 层依赖链超过上限 32 →
        // 超深那一层判 Bad(ss.reason.calculate)（不抛、不爆栈），并沿链传染到读它的脚本点。
        var chain = new StringBuilder("<Calculated><Point id=\"a0\"><Expression>P('raw')</Expression></Point>");
        for (var i = 1; i < 40; i++)
        {
            chain.Append("<Point id=\"a").Append(i).Append("\"><Expression>P('a").Append(i - 1)
                 .Append("') + 1</Expression></Point>");
        }

        chain.Append("</Calculated>");

        Feed(1, 0, 0, 0, 0);
        Start("<Points>"
              + "<Point id=\"raw\" address=\"0\" dataType=\"uint16\" intervalMs=\"100\" />"
              + "<Point id=\"s\" address=\"1\" dataType=\"float64\" intervalMs=\"100\"><Script>P('a39') + 1</Script></Point>"
              + "</Points>" + chain.ToString());

        WaitUntil(() => Value("s").Reason == "ss.reason.calculate", "超深依赖链必须判坏值");
        Assert.Equal(PointQuality.Bad, Value("s").Quality);
        Assert.Null(Value("s").Value);

        var deepest = _engine!.GetValueDetail("d1", "a39");
        Assert.Equal(PointQuality.Bad, deepest.Quality);
        Assert.Equal("ss.reason.calculate", deepest.Reason);
    }
}
