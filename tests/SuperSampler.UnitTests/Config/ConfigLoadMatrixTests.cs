using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using SuperSampler.Core.Config;
using SuperSampler.Core.Runtime;
using SuperSampler.Drivers.Modbus;
using Xunit;

namespace SuperSampler.UnitTests.Config;

/// <summary>
/// 配置加载矩阵（模块重测「配置加载」覆盖 11–16）：两阶段加载与「一次报全」、CGV 规则正反例抽查、
/// <c>DetectUnconsumed</c>（14 个未消费属性 + 三种 policy + 模板段）、i18n 选文件（不重复加载）、
/// 配置模型完备性（Bits/Slices/DependsOn/模板/引用定位）与畸形 XML（空文件/非 XML/未知元素/深嵌套）。
/// 既有覆盖见 <c>LoadTimeValidationTests</c>（CGV-8~33）、<c>FieldCoverageGapsTests</c>（未实现段与策略）、
/// <c>FieldBehaviorMatrixTests</c>（未消费属性告警）、<c>I18nLanguageSelectionTests</c>（选文件），本文件只补矩阵缺口。
/// </summary>
public class ConfigLoadMatrixTests
{
    // ─────────────── 通用夹具 ───────────────

    /// <summary>最小可加载骨架：Global（可加属性/子元素/Polling）、可插的顶层段、一条 tcp 链路与一台设备、一个点表。</summary>
    private static string Shell(string points, string globalAttrs = "", string globalInner = "", string topLevel = "",
        string polling = "<Polling defaultIntervalMs=\"1000\" requestTimeoutMs=\"500\" />")
        => "<SamplerConfig schemaVersion=\"3.0\">"
           + "<Global" + globalAttrs + ">"
           + polling
           + globalInner + "</Global>"
           + topLevel
           + "<Transports><Transport id=\"tcp1\" host=\"127.0.0.1\" /></Transports>"
           + "<Devices><Device id=\"d1\" transport=\"tcp1\" pointSet=\"ps1\" unitId=\"1\" /></Devices>"
           + "<PointSets><PointSet id=\"ps1\">" + points + "</PointSet></PointSets>"
           + "</SamplerConfig>";

    private static SamplerConfiguration Load(string xml, string? baseDir = null)
        => SamplerConfigLoader.Load(XDocument.Parse(xml), baseDir ?? Directory.GetCurrentDirectory());

    private static ConfigValidationException LoadError(string xml)
        => Assert.Throws<ConfigValidationException>(() => Load(xml));

    private static void AssertReports(ConfigValidationException ex, params string[] fragments)
        => Assert.All(fragments, fragment => Assert.Contains(ex.Errors, e => e.IndexOf(fragment, StringComparison.Ordinal) >= 0));

    // ═══════════════ 11. 两阶段加载与全量校验 ═══════════════

    [Fact]
    public void One_load_reports_every_contradiction_at_once_with_locations()
    {
        // 一份配置里塞 9 类互不相关的矛盾：阶段一必须一次报全（不是遇到第一个就停），每条带定位。
        var xml = Shell(
            "<Points>"
            + "<Point id=\"enum\" address=\"0\" dataType=\"bogus\" />"                       // CGV-22 非法枚举
            + "<Point id=\"len\" address=\"1\" dataType=\"float32\" length=\"3\" />"          // CGV-8 位宽不符
            + "<Point id=\"cap\" address=\"65535\" dataType=\"uint32\" />"                    // CGV-18 越出区容量
            + "<Point id=\"dup\" address=\"4\" /><Point id=\"dup\" address=\"5\" />"          // CGV-5 重复 id
            + "<Point id=\"alarm\" address=\"6\">"
            + "<Alarm type=\"high\" limit=\"90\" /><Alarm type=\"highHigh\" limit=\"80\" /></Point>"   // CGV-14 限值倒挂
            + "<Point id=\"wide\" address=\"7\" dataType=\"string\" length=\"200\" />"        // CGV-28 单点宽超上限
            + "</Points>"
            + "<Blocks><Block id=\"b\" start=\"300\" count=\"4\">"
            + "<Point id=\"bp1\" address=\"300\" /><Point id=\"bp2\" address=\"302\" /></Block></Blocks>"   // CGV-16 块内空洞
            + "<Calculated>"
            + "<Point id=\"cx\"><Expression>P('cy') + 1</Expression></Point>"
            + "<Point id=\"cy\"><Expression>P('cx') + 1</Expression></Point>"                 // CGV-15 成环
            + "</Calculated>");

        var ex = LoadError(xml);

        AssertReports(ex,
            "dataType 非法值", "bogus",
            "length=3", "位宽",
            "地址+长度越出",
            "重复",
            "报警限值大小关系不成立",
            "地址组上限",
            "空洞",
            "循环依赖");
        Assert.True(ex.Errors.Count >= 8, "一次加载必须报出全部矛盾，实际只报出 " + ex.Errors.Count + " 条");
    }

    [Fact]
    public void One_load_also_reports_scheduling_and_reference_contradictions_together()
    {
        var xml = "<SamplerConfig schemaVersion=\"3.0\">"
            + "<Global><Polling defaultIntervalMs=\"1000\" requestTimeoutMs=\"0\" />"
            + "<Scheduler groupLimitRegisters=\"0\" /></Global>"
            + "<Transports><Transport id=\"tcp1\" host=\"127.0.0.1\" /></Transports>"
            + "<Devices><Device id=\"d1\" transport=\"missing\" pointSet=\"nope\" unitId=\"1\" /></Devices>"
            + "<PointSets><PointSet id=\"ps1\"><Points>"
            + "<Point id=\"p\" address=\"0\" intervalMs=\"10\" />"
            + "</Points></PointSet></PointSets>"
            + "</SamplerConfig>";

        AssertReports(LoadError(xml),
            "requestTimeoutMs", "groupLimitRegisters", "不存在的链路 missing", "不存在的点表 nope", "intervalMs=10");
    }

    [Fact]
    public void Unvalidated_config_cannot_be_used_by_the_runtime()
    {
        // 阶段二闸门：Loader 从不交付半成品；手工构造的配置（IsValidated 默认 false）必须被运行时拒绝
        var manual = new SamplerConfiguration();

        var registry = Assert.Throws<InvalidOperationException>(() => new PointRegistry(manual));
        Assert.Contains("未通过加载期全量校验", registry.Message);

        var engine = Assert.Throws<InvalidOperationException>(
            () => new SamplerEngine(manual, new Dictionary<string, IModbusLink>()));
        Assert.Contains("未通过加载期全量校验", engine.Message);
    }

    [Fact]
    public void A_failed_load_does_not_poison_a_later_valid_load()
    {
        LoadError(Shell("<Points><Point id=\"p\" address=\"0\" dataType=\"bogus\" /></Points>"));

        var good = Load(Shell("<Points><Point id=\"p\" address=\"0\" /></Points>"));

        Assert.True(good.IsValidated);
        Assert.Empty(good.Warnings);
        Assert.Equal("p", good.PointSets[0].Points[0].Id);
    }

    // ═══════════════ 12. CGV 规则正反例抽查（只补缺口） ═══════════════

    [Fact]
    public void Cgv6_block_window_must_contain_each_point_and_the_boundary_is_exact()
    {
        // 窗口是半开区间 [start, start+count)：点位末端正好落在上界也属越界
        var ex = LoadError(Shell("<Blocks><Block id=\"b\" start=\"0\" count=\"4\">"
            + "<Point id=\"p\" address=\"9\" /></Block></Blocks><Points />"));
        AssertReports(ex, "窗口 [0,4)", "不含点位 p", "[9,10)");

        // 边界：末尾寄存器刚好落在窗口内 → 通过
        var atEdge = Load(Shell("<Blocks><Block id=\"b\" start=\"0\" count=\"4\">"
            + "<Point id=\"p\" address=\"3\" /></Block></Blocks><Points />"));
        Assert.True(atEdge.IsValidated);
        Assert.Equal(4, atEdge.PointSets[0].Blocks[0].Count);
        Assert.Equal(3, atEdge.PointSets[0].Blocks[0].Points.Single().Address);
    }

    [Fact]
    public void Cgv14_equal_limits_are_rejected_because_the_relation_must_be_strict()
    {
        // low < high < highHigh 都是严格小于：相等同样矛盾（永远判不出合理报警）
        AssertReports(LoadError(Shell("<Points><Point id=\"p\" address=\"0\">"
                + "<Alarm type=\"low\" limit=\"50\" /><Alarm type=\"high\" limit=\"50\" /></Point></Points>")),
            "报警限值大小关系不成立", "low=50", "high=50");

        Assert.Contains(LoadError(Shell("<Points><Point id=\"p\" address=\"0\">"
                + "<Alarm type=\"high\" limit=\"90\" /><Alarm type=\"highHigh\" limit=\"90\" /></Point></Points>")).Errors,
            e => e.Contains("报警限值大小关系不成立"));
    }

    [Fact]
    public void Cgv19_request_timeout_boundary_one_is_accepted_zero_is_rejected()
    {
        // D47 的边界面：1ms 是允许的最小值，0/负数报错（超时来源三处：Global/Transport/Device）
        var minimal = Load(Shell("<Points><Point id=\"p\" address=\"0\" /></Points>",
            polling: "<Polling defaultIntervalMs=\"1000\" requestTimeoutMs=\"1\" />"));
        Assert.Equal(1, minimal.Global.RequestTimeoutMs);

        var ex = LoadError(Shell("<Points />",
            polling: "<Polling defaultIntervalMs=\"1000\" requestTimeoutMs=\"-5\" />"));
        AssertReports(ex, "requestTimeoutMs=-5", "≥ 1");
    }

    [Fact]
    public void Cgv22_all_illegal_enumerations_are_reported_in_one_pass()
    {
        var ex = LoadError("<SamplerConfig schemaVersion=\"3.0\">"
            + "<Transports><Transport id=\"tcp1\" variant=\"bogus\" parity=\"weird\" stopBits=\"many\" /></Transports>"
            + "<Devices><Device id=\"d1\" transport=\"tcp1\" pointSet=\"ps1\" /></Devices>"
            + "<PointSets><PointSet id=\"ps1\"><Points>"
            + "<Point id=\"p\" address=\"0\" area=\"nowhere\" dataType=\"bogus\" swap=\"flip\" access=\"admin\" mode=\"sometimes\" />"
            + "</Points></PointSet></PointSets>"
            + "</SamplerConfig>");

        AssertReports(ex, "variant 非法值", "parity 非法值", "stopBits 非法值",
            "area 非法值", "dataType 非法值", "swap 非法值", "access 非法值", "mode 非法值");
    }

    [Fact]
    public void Cgv25_removed_and_renamed_syntax_is_reported_together()
    {
        var ex = LoadError("<SamplerConfig schemaVersion=\"3.0\">"
            + "<Global><Polling defaultIntervalMs=\"1000\" rateMs=\"500\" />"
            + "<Scheduler mergeGap=\"2\" maxRegistersPerRead=\"100\" maxBitsPerRead=\"1000\" /></Global>"
            + "<ScanGroups><ScanGroup id=\"fast\" /></ScanGroups>"
            + "<Transports><Transport id=\"tcp1\" host=\"127.0.0.1\" /></Transports>"
            + "<Devices><Device id=\"d1\" transport=\"tcp1\" pointSet=\"ps1\" /></Devices>"
            + "<PointSets><PointSet id=\"ps1\"><Defaults intervalMs=\"200\" mode=\"auto\" /><Points>"
            + "<Point id=\"p\" address=\"0\" scanGroup=\"fast\" />"
            + "</Points></PointSet></PointSets>"
            + "</SamplerConfig>");

        AssertReports(ex, "rateMs", "mergeGap", "maxRegistersPerRead", "maxBitsPerRead",
            "ScanGroups 段已删除", "scanGroup=\"fast\"", "Defaults", "intervalMs", "mode");
    }

    // ═══════════════ 13. DetectUnconsumed（14 个属性 + policy + 模板） ═══════════════

    public static IEnumerable<object[]> UnconsumedCases()
    {
        // globalAttrs, globalInner, topLevel, points, 期望告警关键词
        yield return new object[] { " timeZone=\"Asia/Shanghai\"", "", "", "<Points />", "Global@timeZone" };
        yield return new object[] { "", "<Retry backoff=\"linear\" />", "", "<Points />", "Global/Retry@backoff" };
        yield return new object[] { "", "<Retry escalateAfter=\"3\" />", "", "<Points />", "Global/Retry@escalateAfter" };
        yield return new object[] { "", "<Retry budgetMs=\"5000\" />", "", "<Points />", "Global/Retry@budgetMs" };
        yield return new object[] { "", "<Retry maxBackoffMs=\"1000\" />", "", "<Points />", "Global/Retry@maxBackoffMs" };
        yield return new object[] { "", "<Retry onLinkError=\"retry\" />", "", "<Points />", "Global/Retry@onLinkError" };
        yield return new object[] { "", "<Retry onException=\"retry\" />", "", "<Points />", "Global/Retry@onException" };
        yield return new object[] { "", "", "", "<Points><Point id=\"p\" address=\"0\" range=\"0..300\" /></Points>", "Point@range" };
        yield return new object[] { "", "", "", Write("confirm=\"true\""), "Write@confirm" };
        yield return new object[] { "", "", "", Write("step=\"10\""), "Write@step" };
        yield return new object[] { "", "", "", Write("permission=\"op\""), "Write@permission" };
        yield return new object[] { "", "", Transport("driver=\"modbus\""), "<Points />", "driver/maxConcurrent" };
        yield return new object[] { "", "", Transport("maxConcurrent=\"4\""), "<Points />", "driver/maxConcurrent" };
        yield return new object[] { "", "", Transport("", "<Reconnect enabled=\"true\" />"), "<Points />", "Reconnect 段" };
    }

    private static string Write(string attribute)
        => "<Points><Point id=\"w\" address=\"0\" access=\"readwrite\"><Write " + attribute + " /></Point></Points>";

    private static string Transport(string attribute, string inner = "")
        => "<Transports><Transport id=\"t2\" host=\"127.0.0.1\" " + attribute + ">" + inner + "</Transport></Transports>";

    [Theory]
    [MemberData(nameof(UnconsumedCases))]
    public void Every_unconsumed_attribute_is_reported_when_written(
        string globalAttrs, string globalInner, string topLevel, string points, string expectedKeyword)
    {
        var config = Load(Shell(points, globalAttrs, globalInner, topLevel));

        Assert.True(config.IsValidated);
        Assert.Contains(config.Warnings, w => w.IndexOf(expectedKeyword, StringComparison.Ordinal) >= 0);
    }

    [Fact]
    public void Clean_config_reports_no_warnings()
    {
        var config = Load(Shell("<Points><Point id=\"p\" address=\"0\" intervalMs=\"200\" /></Points>"));

        Assert.Empty(config.Warnings);
    }

    [Fact]
    public void Unconsumed_attributes_follow_all_three_unsupported_policies()
    {
        const string fragment = "<Points><Point id=\"p\" address=\"0\" range=\"0..100\" /></Points>";

        // warn（默认）：加载成功 + 进 Warnings
        var warn = Load(Shell(fragment));
        Assert.True(warn.IsValidated);
        Assert.Contains(warn.Warnings, w => w.IndexOf("Point@range", StringComparison.Ordinal) >= 0);

        // error：拒绝加载
        AssertReports(LoadError("<SamplerConfig schemaVersion=\"3.0\" unsupportedPolicy=\"error\">"
            + "<Transports><Transport id=\"tcp1\" host=\"127.0.0.1\" /></Transports>"
            + "<Devices><Device id=\"d1\" transport=\"tcp1\" pointSet=\"ps1\" /></Devices>"
            + "<PointSets><PointSet id=\"ps1\">" + fragment + "</PointSet></PointSets></SamplerConfig>"),
            "unsupportedPolicy=error");

        // ignore：静默加载
        var ignore = Load("<SamplerConfig schemaVersion=\"3.0\" unsupportedPolicy=\"ignore\">"
            + "<Transports><Transport id=\"tcp1\" host=\"127.0.0.1\" /></Transports>"
            + "<Devices><Device id=\"d1\" transport=\"tcp1\" pointSet=\"ps1\" /></Devices>"
            + "<PointSets><PointSet id=\"ps1\">" + fragment + "</PointSet></PointSets></SamplerConfig>");
        Assert.True(ignore.IsValidated);
        Assert.Empty(ignore.Warnings);
    }

    [Fact]
    public void Unconsumed_attribute_declared_only_in_a_point_template_is_reported()
    {
        // 模板也是「声明」：只写在模板里、被点位引用时同样必须告警（DetectUnconsumed 只扫 PointSets 就会漏）
        var config = Load(Shell(
            "<Points><Point id=\"p\" address=\"0\" template=\"tpl\" /></Points>",
            topLevel: "<PointTemplates><Point id=\"tpl\" range=\"0..100\" /></PointTemplates>"));

        Assert.True(config.IsValidated);
        Assert.Contains(config.Warnings, w => w.IndexOf("Point@range", StringComparison.Ordinal) >= 0);
    }

    [Fact]
    public void Unconsumed_attribute_declared_only_in_a_device_template_is_reported()
    {
        var config = Load(Shell("<Points><Point id=\"p\" address=\"0\" /></Points>",
            topLevel: "<DeviceTemplates><Device id=\"dtpl\" generateDiagnostics=\"true\" /></DeviceTemplates>"));

        Assert.Contains(config.Warnings, w => w.IndexOf("generateDiagnostics", StringComparison.Ordinal) >= 0);
    }

    // ═══════════════ 13b. 未实现的链路变体（findings W79） ═══════════════

    [Theory]
    [InlineData("udp")]
    [InlineData("ascii")]
    public void Unimplemented_transport_variants_are_reported_at_load(string variant)
    {
        // findings W79：udp/ascii 是「枚举合法但未实现」——Scheduler.ParseVariant 把两者都收敛到 TCP/MBAP 通道，
        // 此前加载期一声不响、运行期也无提示：现场按字面写 ascii 会以 MBAP 帧收发却不报错（静默行为缺口）。
        // 现在与「未实现段/未消费属性」共用同一条告警通道（unsupportedPolicy 控制），写了必须可见。
        var config = Load(Shell("<Points><Point id=\"p\" address=\"0\" intervalMs=\"200\" /></Points>",
            topLevel: Transport("variant=\"" + variant + "\"")));

        Assert.True(config.IsValidated);
        Assert.Contains(config.Warnings,
            w => w.IndexOf("variant=\"" + variant + "\"", StringComparison.Ordinal) >= 0
                 && w.IndexOf("t2", StringComparison.Ordinal) >= 0);
    }

    [Fact]
    public void Unimplemented_transport_variant_follows_all_three_unsupported_policies()
    {
        const string points = "<Points><Point id=\"p\" address=\"0\" intervalMs=\"200\" /></Points>";
        const string body = "<Transports><Transport id=\"tcp1\" host=\"127.0.0.1\" variant=\"ascii\" /></Transports>"
            + "<Devices><Device id=\"d1\" transport=\"tcp1\" pointSet=\"ps1\" /></Devices>"
            + "<PointSets><PointSet id=\"ps1\">" + points + "</PointSet></PointSets>";

        // warn（默认）：加载成功 + 进 Warnings
        var warn = Load(Shell(points, topLevel: Transport("variant=\"ascii\"")));
        Assert.True(warn.IsValidated);
        Assert.Contains(warn.Warnings, w => w.IndexOf("variant=\"ascii\"", StringComparison.Ordinal) >= 0);

        // error：拒绝加载（与未实现段同一口径）
        AssertReports(LoadError(
            "<SamplerConfig schemaVersion=\"3.0\" unsupportedPolicy=\"error\">" + body + "</SamplerConfig>"),
            "variant=\"ascii\"", "unsupportedPolicy=error");

        // ignore：静默加载（刻意的静默通道，不是遗漏）
        var ignore = Load(
            "<SamplerConfig schemaVersion=\"3.0\" unsupportedPolicy=\"ignore\">" + body + "</SamplerConfig>");
        Assert.True(ignore.IsValidated);
        Assert.Empty(ignore.Warnings);
    }

    [Theory]
    [InlineData("tcp")]
    [InlineData("rtuovertcp")]
    [InlineData("rtu")]
    public void Implemented_transport_variants_do_not_warn(string variant)
    {
        // 不误伤：真实现的三个变体（含串口 rtu）一个告警都不许有
        var config = Load(Shell("<Points><Point id=\"p\" address=\"0\" intervalMs=\"200\" /></Points>",
            topLevel: Transport("variant=\"" + variant + "\"")));

        Assert.Empty(config.Warnings);
    }

    // ═══════════════ 13c. 失败加载也带告警（findings W70） ═══════════════

    [Fact]
    public void Failed_load_still_carries_the_warnings_it_collected()
    {
        // findings W70：加载因错误失败时，本次收集到的告警同样要能拿到（此前只带 Errors，
        // 告警随半成品配置丢弃 → 宿主「改完错误才发现还有告警」要跑两轮）。
        // 一份配置同时含 1 条错误（点表内 id 重复，CGV-5）+ 1 条告警（Point@range 未消费）。
        var ex = LoadError(Shell(
            "<Points>"
            + "<Point id=\"p\" address=\"0\" range=\"0..100\" />"
            + "<Point id=\"p\" address=\"1\" />"
            + "</Points>"));

        Assert.Contains(ex.Errors, e => e.IndexOf("重复", StringComparison.Ordinal) >= 0);
        Assert.Contains(ex.Warnings, w => w.IndexOf("Point@range", StringComparison.Ordinal) >= 0);
        Assert.Contains(ex.Warnings, w => w.IndexOf("尚未实现", StringComparison.Ordinal) >= 0);
    }

    [Fact]
    public void Failed_load_warnings_follow_unsupported_policy_too()
    {
        // 同一份「1 错误 + 1 未实现属性」的配置，只换 HostConfig@unsupportedPolicy
        const string body = "<Transports><Transport id=\"tcp1\" host=\"127.0.0.1\" /></Transports>"
            + "<Devices><Device id=\"d1\" transport=\"tcp1\" pointSet=\"ps1\" /></Devices>"
            + "<PointSets><PointSet id=\"ps1\"><Points>"
            + "<Point id=\"p\" address=\"0\" range=\"0..100\" /><Point id=\"p\" address=\"1\" />"
            + "</Points></PointSet></PointSets>";

        // error 策略：未实现项本身变成错误，Warnings 为空（不是「错误被塞进告警」）
        var forced = Assert.Throws<ConfigValidationException>(() => Load(
            "<SamplerConfig schemaVersion=\"3.0\" unsupportedPolicy=\"error\">" + body + "</SamplerConfig>"));
        Assert.Contains(forced.Errors, e => e.IndexOf("Point@range", StringComparison.Ordinal) >= 0);
        Assert.Empty(forced.Warnings);

        // ignore 策略：告警通道整体静默，异常只带错误
        var ignored = Assert.Throws<ConfigValidationException>(() => Load(
            "<SamplerConfig schemaVersion=\"3.0\" unsupportedPolicy=\"ignore\">" + body + "</SamplerConfig>"));
        Assert.Empty(ignored.Warnings);
    }

    [Fact]
    public void Xml_level_failures_carry_an_empty_warning_list_not_null()
    {
        // 连 XML 都没解析出来时：Warnings 是空列表（宿主不必判空），Errors 仍是唯一一条
        var missing = Path.Combine(Path.GetTempPath(), "ss_missing_" + Guid.NewGuid().ToString("N") + ".xml");
        var ex = Assert.Throws<ConfigValidationException>(() => SamplerConfigLoader.LoadFromXml(missing));

        Assert.Single(ex.Errors);
        Assert.Empty(ex.Warnings);
    }

    // ═══════════════ 14. i18n（ADR D39：只加载选中的那一份） ═══════════════

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ss_cfg_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>i18n 用例骨架：name 文本里含 <c>${KEY}</c>（花括号），所以拼串而不是 string.Format。</summary>
    private static string I18nShell(string files, string nameText = "${T_NAME}")
        => "<SamplerConfig schemaVersion=\"3.0\">"
           + "<Global language=\"zh_CN\" fallbackLanguage=\"en_US\" />"
           + "<I18n><Files>" + files + "</Files></I18n>"
           + "<Transports><Transport id=\"tcp1\" host=\"127.0.0.1\" /></Transports>"
           + "<Devices><Device id=\"d1\" transport=\"tcp1\" pointSet=\"ps1\" /></Devices>"
           + "<PointSets><PointSet id=\"ps1\"><Points>"
           + "<Point id=\"p\" address=\"0\" name=\"" + nameText + "\" /></Points></PointSet></PointSets>"
           + "</SamplerConfig>";

    [Fact]
    public void Only_the_selected_language_file_is_loaded_even_when_fallback_exists_on_disk()
    {
        var dir = TempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "zh.i18n"), "T_NAME = \"中文名\"\n");
            File.WriteAllText(Path.Combine(dir, "en.i18n"), "T_NAME = \"English\"\nT_ONLY_EN = \"只有英文\"\n");

            var config = Load(I18nShell(
                "<File lang=\"zh_CN\" path=\"zh.i18n\" /><File lang=\"en_US\" path=\"en.i18n\" />"), dir);

            Assert.Equal("中文名", config.PointSets[0].Points[0].Name);
            // 回退语言文件**没有**被一并加载（否则后加载的会覆盖主语言）
            Assert.Null(config.I18n.Resolve("T_ONLY_EN"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void I18n_key_missing_from_the_selected_file_is_a_located_load_error()
    {
        // key 缺失不是「静默保留 ${}」：被 name/message 引用的 key 必须在选中的语言文件里存在（CGV-4）
        var dir = TempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "zh.i18n"), "OTHER = \"x\"\n");

            var ex = Assert.Throws<ConfigValidationException>(() => Load(
                I18nShell("<File lang=\"zh_CN\" path=\"zh.i18n\" />"), dir));

            Assert.Contains(ex.Errors, e => e.IndexOf("ps1/p.name", StringComparison.Ordinal) >= 0
                && e.IndexOf("i18n key 不存在", StringComparison.Ordinal) >= 0);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Missing_i18n_file_is_skipped_and_referenced_keys_become_located_errors()
    {
        var dir = TempDir();
        try
        {
            var ex = Assert.Throws<ConfigValidationException>(() => Load(
                I18nShell("<File lang=\"zh_CN\" path=\"missing.i18n\" />"), dir));

            Assert.Contains(ex.Errors, e => e.IndexOf("i18n key 不存在", StringComparison.Ordinal) >= 0);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Unreferenced_i18n_keys_do_not_need_a_file_at_all()
    {
        // 没有 ${KEY} 引用时，i18n 文件缺失完全无关紧要（语言文件不参与校验）
        var config = Load(Shell("<Points><Point id=\"p\" address=\"0\" /></Points>",
            topLevel: "<I18n><Files><File lang=\"zh_CN\" path=\"no_such_file.i18n\" /></Files></I18n>"));

        Assert.True(config.IsValidated);
        Assert.Empty(config.Warnings);
    }

    // ═══════════════ 15. 模型完备性（Bits / Slices / DependsOn / 模板 / 引用定位） ═══════════════

    [Fact]
    public void Bits_children_carry_parent_id_and_never_get_alarms()
    {
        var config = Load(Shell("<Points><Point id=\"st\" address=\"0\">"
            + "<Alarm type=\"high\" limit=\"100\" />"
            + "<Bits><Bit index=\"0\" name=\"run\" />"
            + "<Field from=\"4\" to=\"7\" name=\"mode\"><Map><Item key=\"1\">自动</Item></Map></Field></Bits>"
            + "</Point></Points>"));

        var parent = config.PointSets[0].Points.Single(p => p.Id == "st");
        var children = config.PointSets[0].Points.Where(p => p.ParentPointId != null).ToList();

        Assert.Equal(2, children.Count);
        Assert.Equal(new[] { "st.mode", "st.run" }, children.Select(c => c.Id).OrderBy(x => x, StringComparer.Ordinal).ToArray());
        Assert.All(children, c => Assert.Equal("st", c.ParentPointId));
        Assert.Equal(RuntimeDataType.Bool, children.Single(c => c.Id == "st.run").DataType);      // 单一位 → bool
        var modeChild = children.Single(c => c.Id == "st.mode");
        Assert.Equal(RuntimeDataType.UInt16, modeChild.DataType);                                 // 位域 → 无符号小整数
        Assert.Equal("4-7", modeChild.BitRange);
        Assert.All(children, c => Assert.Empty(c.Alarms));      // 子点位只取值，不做业务判断
        Assert.Single(parent.Alarms);                            // 整字点位保留自己的报警
        var field = parent.Bits!.Single(b => b.Name == "mode");  // 枚举 Map 留在整字点位的 Bits 上
        Assert.Equal("自动", field.Map["1"]);
        Assert.Equal(4, field.From);
        Assert.Equal(7, field.To);
    }

    [Fact]
    public void Slices_are_parsed_with_the_sum_as_the_effective_length()
    {
        var config = Load(Shell("<Points><Point id=\"s\" address=\"0\" dataType=\"uint16\">"
            + "<Slices><Slice address=\"0\" length=\"2\" /><Slice address=\"10\" length=\"3\" /></Slices></Point></Points>"));

        var point = config.PointSets[0].Points.Single();
        Assert.Equal(2, point.Slices!.Count);
        Assert.Equal(10, point.Slices[1].Address);

        // 有效字长 = 片段长度之和；一次请求要覆盖的跨度 = [最小片段地址, 最大片段末地址)
        var runtime = new RuntimePoint(point, new DeviceConfig { Id = "d1", UnitId = 1, Transport = "tcp1", PointSetId = "ps1" });
        Assert.Equal(5, runtime.Length);
        Assert.Equal(0, runtime.SpanStart);
        Assert.Equal(13, runtime.SpanEnd);
    }

    [Fact]
    public void Depends_on_point_ref_is_parsed_and_dangling_reference_is_located()
    {
        var ok = Load(Shell("<Points><Point id=\"a\" address=\"0\" /></Points>"
            + "<Calculated><Point id=\"c\"><Expression>P('a') + 1</Expression>"
            + "<DependsOn><PointRef>a</PointRef></DependsOn></Point></Calculated>"));
        Assert.Equal(new[] { "a" }, ok.PointSets[0].Calculated.Single().DependsOn);

        AssertReports(LoadError(Shell("<Points />"
                + "<Calculated><Point id=\"c\"><Expression>1</Expression>"
                + "<DependsOn><PointRef>ghost</PointRef></DependsOn></Point></Calculated>")),
            "DependsOn", "不存在的点位 ghost");
    }

    [Fact]
    public void Every_reference_layer_failure_is_a_located_config_error_not_an_nre()
    {
        var ex = LoadError("<SamplerConfig schemaVersion=\"3.0\">"
            + "<Transports><Transport host=\"127.0.0.1\" /></Transports>"                       // Transport 缺 id
            + "<Devices><Device id=\"d1\" transport=\"tcp1\" pointSet=\"ps1\" template=\"nope\" /></Devices>"
            + "<PointSets><PointSet id=\"ps1\"><Blocks><Block id=\"b\" start=\"0\" />"          // Block 缺 count
            + "</Blocks><Points><Point id=\"p\" address=\"0\" template=\"ghost\" /></Points>"
            + "</PointSet></PointSets>"
            + "<PointTemplates><Point /></PointTemplates>"                                      // 模板缺 id
            + "</SamplerConfig>");

        AssertReports(ex, "Transport 缺少 id", "不存在的设备模板 nope", "不存在的模板 ghost",
            "缺少 id/start/count", "PointTemplate 缺少 id");
    }

    [Fact]
    public void Duplicate_ids_at_every_level_are_reported()
    {
        var ex = LoadError("<SamplerConfig schemaVersion=\"3.0\">"
            + "<Transports><Transport id=\"tcp1\" host=\"127.0.0.1\" /><Transport id=\"tcp1\" host=\"127.0.0.2\" /></Transports>"
            + "<Devices><Device id=\"d1\" transport=\"tcp1\" pointSet=\"ps1\" unitId=\"1\" />"
            + "<Device id=\"d1\" transport=\"tcp1\" pointSet=\"ps1\" unitId=\"2\" /></Devices>"
            + "<PointSets><PointSet id=\"ps1\"><Points><Point id=\"p\" address=\"0\" /><Point id=\"p\" address=\"1\" /></Points></PointSet></PointSets>"
            + "</SamplerConfig>");

        AssertReports(ex, "Transport id 重复", "Device id 重复", "点位 id 重复");
    }

    [Fact]
    public void Point_template_values_are_validated_as_if_written_on_the_point()
    {
        // 模板带入的非法值必须报出来（否则就是「写了不生效」的隐身通道）
        AssertReports(LoadError(Shell("<Points><Point id=\"p\" address=\"0\" template=\"tpl\" /></Points>",
                topLevel: "<PointTemplates><Point id=\"tpl\" dataType=\"bogus\" /></PointTemplates>")),
            "dataType 非法值");
    }

    // ═══════════════ 16. 畸形 XML ═══════════════

    [Fact]
    public void Empty_file_non_xml_content_and_a_missing_path_are_all_config_errors()
    {
        var dir = TempDir();
        try
        {
            var empty = Path.Combine(dir, "empty.xml");
            File.WriteAllText(empty, string.Empty);
            var emptyError = Assert.Throws<ConfigValidationException>(() => SamplerConfigLoader.LoadFromXml(empty));
            Assert.Contains(emptyError.Errors, e => e.Contains("不是合法 XML"));

            var binary = Path.Combine(dir, "binary.xml");
            File.WriteAllBytes(binary, new byte[] { 0x00, 0x01, 0xFF, 0xFE, 0x7F });
            var binaryError = Assert.Throws<ConfigValidationException>(() => SamplerConfigLoader.LoadFromXml(binary));
            Assert.Contains(binaryError.Errors, e => e.Contains("不是合法 XML"));

            var missing = Assert.Throws<ConfigValidationException>(
                () => SamplerConfigLoader.LoadFromXml(Path.Combine(dir, "missing.xml")));
            Assert.Contains(missing.Errors, e => e.Contains("配置文件读不到"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Unknown_elements_are_load_errors_and_unknown_attributes_go_through_the_policy()
    {
        // findings D53 / CGV-34：拼错的段名与属性名此前**静默忽略**（<Ponits> 打错 → 点表零点位、零错误零告警，
        // 宿主拿到一个没有任何点位的设备）。现在：未知**元素**是加载错误（结构性错误，一定读不到）；
        // 未知**属性**走 HostConfig@unsupportedPolicy（warn → Warnings / error → 拒绝 / ignore → 静默）。
        var typo = Assert.Throws<ConfigValidationException>(
            () => Load(Shell("<Ponits><Point id=\"p\" address=\"0\" /></Ponits>")));

        Assert.Contains(typo.Errors, e => e.Contains("Ponits") && e.Contains("未知元素"));

        // 默认 policy=warn：属性名拼错不拦加载，但必须留下可见告警（不再静默）
        var typoAttribute = Load(Shell("<Points><Point id=\"p\" address=\"0\" intervallMs=\"500\" /></Points>"));
        Assert.True(typoAttribute.IsValidated);
        Assert.Null(typoAttribute.PointSets[0].Points[0].IntervalMs);        // 打错的属性确实不生效
        Assert.Contains(typoAttribute.Warnings, w => w.Contains("intervallMs") && w.Contains("未知属性"));

        // unsupportedPolicy=error：未知属性同样拒绝加载
        var strict = Assert.Throws<ConfigValidationException>(() => Load(
            "<SamplerConfig schemaVersion=\"3.0\" unsupportedPolicy=\"error\">"
            + "<Global><Polling defaultIntervalMs=\"1000\" requestTimeoutMs=\"500\" /></Global>"
            + "<Transports><Transport id=\"tcp1\" host=\"127.0.0.1\" /></Transports>"
            + "<Devices><Device id=\"d1\" transport=\"tcp1\" pointSet=\"ps1\" /></Devices>"
            + "<PointSets><PointSet id=\"ps1\"><Points>"
            + "<Point id=\"p\" address=\"0\" intervallMs=\"500\" /></Points></PointSet></PointSets>"
            + "</SamplerConfig>"));
        Assert.Contains(strict.Errors, e => e.Contains("intervallMs") && e.Contains("unsupportedPolicy=error"));

        // 未知元素不受 policy 影响：元素名拼错永远是错误
        var sectionTypo = Assert.Throws<ConfigValidationException>(
            () => Load(Shell("<Points><Point id=\"p\" address=\"0\" /></Points>")
                       .Replace("</SamplerConfig>", "<Storagex /></SamplerConfig>")));
        Assert.Contains(sectionTypo.Errors, e => e.Contains("Storagex") && e.Contains("未知元素"));
    }

    [Fact]
    public void Deeply_nested_unknown_elements_are_reported_once_without_crashing_the_loader()
    {
        // 未知元素只报它自己（不深入子树），所以 200 层嵌套也只会得到一条错误——既不刷屏也不递归爆栈
        var deep = new XElement("Ponits");
        var cursor = deep;
        for (var i = 0; i < 200; i++)
        {
            var child = new XElement("Level" + i);
            cursor.Add(child);
            cursor = child;
        }

        var root = XDocument.Parse(Shell("<Points><Point id=\"p\" address=\"0\" /></Points>")).Root!;
        root.Elements("PointSets").Elements("PointSet").First().Add(deep);

        var ex = Assert.Throws<ConfigValidationException>(
            () => SamplerConfigLoader.Load(new XDocument(root), Directory.GetCurrentDirectory()));

        var reported = ex.Errors.Where(e => e.Contains("未知元素")).ToList();
        Assert.Single(reported);
        Assert.Contains("Ponits", reported[0]);
    }


    [Fact]
    public void Document_without_a_root_element_reports_one_clear_error_without_throwing_raw_xml_errors()
    {
        // XDocument 允许「有声明没根」的构造方式（XDocument.Parse("") 会抛 XmlException），
        // 这里直接构造无根文档，验证 ParseAndValidate 的 root == null 分支
        var ex = Assert.Throws<ConfigValidationException>(
            () => SamplerConfigLoader.Load(new XDocument(), Directory.GetCurrentDirectory()));

        Assert.Contains(ex.Errors, e => e.Contains("XML 无根元素"));
    }
}
