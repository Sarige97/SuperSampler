using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using SuperSampler.Core.Config;
using SuperSampler.Core.Runtime;
using Xunit;

namespace SuperSampler.UnitTests.Config;

/// <summary>
/// 配置加载收口矩阵（findings D53 / D66 / D68~D72 与自查新增规则的正反例）。
/// <para>
/// 口径：**加载期必须把所有矛盾报全**（docs/11 §二 两阶段加载）；「运行期才炸」或「静默给错值」都是缺陷。
/// 本文件覆盖本轮新增/补齐的规则（CGV-34 词汇、CGV-35 区与宽度、CGV-36 报警、CGV-37 枚举穷举、
/// CGV-38 数值边界），与既有类分工：
/// <list type="bullet">
/// <item><c>LoadTimeValidationTests</c>：CGV-8~33 的既有规则；</item>
/// <item><c>AlarmLoadValidationTests</c>：D68~D72 的逐条正反例；</item>
/// <item><c>ConfigLoadMatrixTests</c>：两阶段加载、DetectUnconsumed、畸形 XML；</item>
/// <item>本类：**新规则的全景正反例 + 「一次报全」的最强用例 + 阶段二闸门**。</item>
/// </list>
/// </para>
/// </summary>
public class ConfigClosureMatrixTests
{
    // ═══════════════ 夹具 ═══════════════

    private const string Shell = """
        <SamplerConfig schemaVersion="3.0"{0}>
          <Global{1}>
            <Polling defaultIntervalMs="1000" requestTimeoutMs="500" />
            {2}
          </Global>
          {3}
          <Transports><Transport id="tcp1" host="127.0.0.1" /></Transports>
          <Devices><Device id="d1" transport="tcp1" pointSet="ps1" unitId="1" /></Devices>
          <PointSets><PointSet id="ps1">{4}</PointSet></PointSets>
        </SamplerConfig>
        """;

    private static string Xml(string points, string globalInner = "", string topLevel = "",
        string rootAttrs = "", string globalAttrs = "")
        => Shell.Replace("{0}", rootAttrs).Replace("{1}", globalAttrs)
            .Replace("{2}", globalInner).Replace("{3}", topLevel)
            .Replace("{4}", "<Points>" + points + "</Points>");

    /// <summary>把内容直接放进 &lt;PointSet&gt;（块 / 计算点 / 散点混排时用）。</summary>
    private static string XmlSets(string pointSetContent, string globalInner = "", string topLevel = "",
        string rootAttrs = "", string globalAttrs = "")
        => Shell.Replace("{0}", rootAttrs).Replace("{1}", globalAttrs)
            .Replace("{2}", globalInner).Replace("{3}", topLevel).Replace("{4}", pointSetContent);

    private static SamplerConfiguration Load(string xml)
        => SamplerConfigLoader.Load(XDocument.Parse(xml), Directory.GetCurrentDirectory());

    private static string ErrorsOf(string xml)
    {
        var ex = Assert.Throws<ConfigValidationException>(() => Load(xml));
        return string.Join(" | ", ex.Errors);
    }

    // ═══════════════ 一、一次报全（≥8 处互不相关的矛盾）═══════════════

    [Fact]
    public void Every_rule_family_reports_in_one_load_with_a_location()
    {
        // 一份配置里塞 14+ 处不同矛盾，横跨全部新规则族。加载期必须一次报全，且每条都能指回
        // 「哪个实体 + 哪个属性/子元素」——宿主改一遍配置就能全部修掉，不用「改一条跑一次」。
        // unsupportedPolicy=error 让「未知属性」也进错误列表（默认 warn 时它只进 Warnings，见下一条用例）。
        var xml = Xml(
            points:
            "<Point id=\"p1\" address=\"0\" area=\"input\" access=\"write\" />"              // CGV-35 只读区可写
            + "<Point id=\"p2\" address=\"1\" area=\"coil\" dataType=\"float64\" />"          // CGV-35 位区多字
            + "<Point id=\"p3\" address=\"2\" dataType=\"bogus\" />"                          // CGV-22 非法枚举
            + "<Point id=\"p4\" address=\"3\" bit=\"20\" />"                                  // CGV-38 bit 越界
            + "<Point id=\"p5\" address=\"4\"><Alarm type=\"foo\" limit=\"1\" /></Point>"     // CGV-36 未知报警类型
            + "<Point id=\"p6\" address=\"5\"><Alarm type=\"high\" /></Point>"                // CGV-36 缺 limit
            + "<Point id=\"p7\" address=\"6\" intervallMs=\"500\" />"                         // CGV-34 未知属性
            + "<Ponits><Point id=\"p8\" address=\"7\" /></Ponits>"                            // CGV-34 未知元素
            + "<Point id=\"p9\" address=\"65535\" dataType=\"uint32\" />",                    // CGV-18 越出区容量
            globalInner: "<Quality onCommError=\"bogus\" onCommErrorValue=\"keep\" />"
                         + "<Scheduler ignoreGap=\"-1\" />",
            rootAttrs: " unsupportedPolicy=\"error\"",
            topLevel: "<Transports><Transport id=\"tcp2\" host=\"x\" handshake=\"rts\" port=\"0\" /></Transports>"
                      + "<Devices><Device id=\"d2\" transport=\"missing\" pointSet=\"nope\" unitId=\"300\" /></Devices>");

        var ex = Assert.Throws<ConfigValidationException>(() => Load(xml));
        var message = string.Join(" | ", ex.Errors);

        // 每条矛盾都要有自己的定位与原因（横跨 7 个规则族）
        Assert.Contains("点位 ps1/p1", message);                     // 只读区可写
        Assert.Contains("只读区", message);
        Assert.Contains("点位 ps1/p2", message);                     // 位区多字
        Assert.Contains("位区", message);
        Assert.Contains("dataType 非法值", message);                  // CGV-22
        Assert.Contains("bit=20", message);                          // CGV-38
        Assert.Contains("type=\"foo\" 非法", message);                // CGV-36
        Assert.Contains("缺少 limit", message);
        Assert.Contains("未知属性 intervallMs", message);              // CGV-34（属性 → 按 policy=error）
        Assert.Contains("未知元素 <Ponits>", message);                 // CGV-34（元素 → 恒为错误）
        Assert.Contains("地址+长度越出", message);                     // CGV-18
        Assert.Contains("onCommError=\"bogus\" 非法", message);        // CGV-37
        Assert.Contains("onCommErrorValue=\"keep\" 非法", message);
        Assert.Contains("ignoreGap=-1", message);                    // CGV-38
        Assert.Contains("handshake=\"rts\" 非法", message);
        Assert.Contains("port=0", message);
        Assert.Contains("unitId=300", message);
        Assert.Contains("不存在的链路 missing", message);
        Assert.Contains("不存在的点表 nope", message);

        Assert.True(ex.Errors.Count >= 14, "一次加载必须报出全部矛盾，实际只报出 " + ex.Errors.Count + " 条："
            + Environment.NewLine + message);
    }

    [Fact]
    public void Unknown_attributes_do_not_block_a_load_that_has_no_other_errors()
    {
        // 未知**属性**默认按 unsupportedPolicy=warn：加载通过 + Warnings 可见（不再静默）
        var warnOnly = Load(Xml("<Point id=\"p\" address=\"0\" intervallMs=\"500\" />"));

        Assert.True(warnOnly.IsValidated);
        Assert.Contains(warnOnly.Warnings, w => w.Contains("intervallMs") && w.Contains("未知属性"));
        Assert.Null(warnOnly.PointSets[0].Points[0].IntervalMs);
    }

    [Fact]
    public void Errors_never_cross_contaminate_a_previously_valid_load()
    {
        // 阶段二闸门：有错就不交付（抛异常，宿主拿不到配置对象）；
        // 紧接着的合法配置必须照常加载并 IsValidated（错误列表不得跨次残留）
        Assert.Throws<ConfigValidationException>(() => Load(
            Xml("<Point id=\"p\" address=\"0\" area=\"input\" access=\"write\" />")));

        var good = Load(Xml("<Point id=\"p\" address=\"0\" />"));

        Assert.True(good.IsValidated);
        Assert.Empty(good.Warnings);
        Assert.Single(good.PointSets[0].Points);
    }

    [Fact]
    public void A_manual_configuration_can_never_be_used_by_the_runtime()
    {
        // 闸门（ConfigGuard.RequireValidated）：IsValidated 的 setter 是 internal，只有 Load 能置位；
        // 手工拼的半成品配置与「加载失败」等价——运行时对象一个都构造不出来
        var halfBaked = new SamplerConfiguration();

        Assert.Contains("未通过加载期全量校验",
            Assert.Throws<InvalidOperationException>(() => new PointRegistry(halfBaked)).Message);
        Assert.Contains("未通过加载期全量校验",
            Assert.Throws<InvalidOperationException>(() => new SamplerEngine(halfBaked)).Message);
    }

    // ═══════════════ 二、D53 / CGV-34：词汇（未知元素/属性）═══════════════

    [Fact]
    public void Unknown_elements_are_load_errors_in_the_point_section()
    {
        var message = ErrorsOf(Xml("<Ponits><Point id=\"p\" address=\"0\" /></Ponits>"));
        Assert.Contains("未知元素 <Ponits>", message);
        Assert.Contains("PointSet", message);          // 定位含所在层级

        Assert.Contains("未知元素 <Ponit>", ErrorsOf(XmlSets(
            "<Points><Point id=\"p\" address=\"0\" /><Ponit id=\"q\" /></Points>")));

        // 正例（不误伤）：已知元素照常加载
        Assert.True(Load(Xml("<Point id=\"p\" address=\"0\" area=\"holding\" />")).IsValidated);
    }

    [Fact]
    public void Unknown_elements_are_reported_at_every_level_including_templates()
    {
        // Global 段里的段名拼错
        var message = ErrorsOf(Xml("<Point id=\"p\" address=\"0\" />",
            globalInner: "<Pollng defaultIntervalMs=\"1000\" />"));
        Assert.Contains("未知元素 <Pollng>", message);

        // 模板段（PointTemplates）里的未知元素同样是错误
        Assert.Contains("未知元素 <Ponits>", ErrorsOf(Xml(
            "<Point id=\"p\" address=\"0\" />",
            topLevel: "<PointTemplates><Ponits /></PointTemplates>")));

        // 模板段（PointTemplates）里的未知属性同样按 policy 告警（此前只扫实例段 → 静默）
        var templateAttr = Load(Xml(
            "<Point id=\"p\" address=\"0\" />",
            topLevel: "<PointTemplates><Point id=\"tpl\" address=\"1\" intervallMs=\"500\" /></PointTemplates>"));
        Assert.Contains(templateAttr.Warnings, w => w.Contains("intervallMs") && w.Contains("未知属性"));

        // 点位模板里未知属性 + 只有模板被引用（属性会经模板落到真实点位上）
        var usedTemplate = Load(Xml(
            "<Point id=\"p\" address=\"0\" template=\"tpl\" />",
            topLevel: "<PointTemplates><Point id=\"tpl\" intervallMs=\"500\" /></PointTemplates>"));
        Assert.Null(usedTemplate.PointSets[0].Points[0].IntervalMs);   // 打错的属性确实不生效
        Assert.Contains(usedTemplate.Warnings, w => w.Contains("未知属性 intervallMs"));
    }

    [Fact]
    public void A_known_element_in_the_wrong_place_is_a_load_error()
    {
        // 已知元素放在不承载它的父节点下同样「不会被读取」——必须报出来（否则又是静默）
        var message = ErrorsOf(XmlSets(
            "<Points><Block id=\"b\" start=\"0\" count=\"4\"><Point id=\"bp\" address=\"0\" /></Block></Points>"));

        Assert.Contains("不能出现在", message);
        Assert.Contains("Block", message);
        Assert.Contains("Points", message);
    }

    [Fact]
    public void Unknown_attributes_follow_the_unsupported_policy()
    {
        const string typo = "<Point id=\"p\" address=\"0\" intervallMs=\"500\" />";

        // warn（默认）：加载通过 + Warnings 可见
        var warn = Load(Xml(typo));
        Assert.True(warn.IsValidated);
        Assert.Contains(warn.Warnings, w => w.Contains("未知属性 intervallMs"));

        // error：拒绝加载
        Assert.Contains("unsupportedPolicy=error",
            ErrorsOf(Xml(typo, rootAttrs: " unsupportedPolicy=\"error\"")));

        // ignore：静默（既无错误也无告警）
        var ignore = Load(Xml(typo, rootAttrs: " unsupportedPolicy=\"ignore\""));
        Assert.True(ignore.IsValidated);
        Assert.Empty(ignore.Warnings);
    }

    [Fact]
    public void Unsupported_policy_value_itself_is_validated()
    {
        Assert.Contains("unsupportedPolicy=\"bogus\" 非法",
            ErrorsOf(Xml("<Point id=\"p\" address=\"0\" />", rootAttrs: " unsupportedPolicy=\"bogus\"")));
    }

    [Fact]
    public void Comment_is_allowed_anywhere_and_unimplemented_sections_keep_free_form_content()
    {
        // <Comment> 是全文档通用注释元素（加载器不读），未实现段的内部结构不校验（整段按 policy 告警）
        var config = Load(XmlSets(
            "<Points><Point id=\"p\" address=\"0\"><Comment>说明</Comment></Point></Points>",
            topLevel: "<Ui startScreen=\"s1\"><Screen id=\"s1\"><Widget type=\"lamp\" x=\"0\" /></Screen></Ui>"
                      + "<Storage><History provider=\"sqlite\" /><Events provider=\"file\" /></Storage>"));

        Assert.True(config.IsValidated);
        Assert.NotEmpty(config.Warnings);
        Assert.All(config.Warnings, w => Assert.Contains("尚未实现", w));
    }

    // ═══════════════ 三、D66 / CGV-35：只读区与位区宽度 ═══════════════

    [Theory]
    [InlineData("input", "read", null)]              // 正例：只读区只读
    [InlineData("discrete", "read", null)]
    [InlineData("input", "write", "只读区")]
    [InlineData("discrete", "write", "只读区")]
    [InlineData("input", "readwrite", "只读区")]
    public void Read_only_areas_must_not_be_writable(string area, string access, string? expected)
    {
        var xml = Xml($"""<Point id="p" address="0" area="{area}" access="{access}" />""");

        if (expected == null)
        {
            Assert.True(Load(xml).IsValidated);
            return;
        }

        var message = ErrorsOf(xml);
        Assert.Contains("点位 ps1/p", message);
        Assert.Contains(expected, message);
    }

    [Fact]
    public void Readonly_area_violation_is_also_caught_when_access_comes_from_defaults()
    {
        // access 经 PointSet/Defaults 继承而来同样是「可写点位落在只读区」
        var message = ErrorsOf(
            "<SamplerConfig schemaVersion=\"3.0\">"
            + "<Global><Polling defaultIntervalMs=\"1000\" requestTimeoutMs=\"500\" /></Global>"
            + "<Transports><Transport id=\"tcp1\" host=\"127.0.0.1\" /></Transports>"
            + "<Devices><Device id=\"d1\" transport=\"tcp1\" pointSet=\"ps1\" /></Devices>"
            + "<PointSets><PointSet id=\"ps1\"><Defaults area=\"input\" access=\"write\" />"
            + "<Points><Point id=\"p\" address=\"0\" /></Points></PointSet></PointSets>"
            + "</SamplerConfig>");

        Assert.Contains("点位 ps1/p", message);
        Assert.Contains("只读区", message);
    }

    [Theory]
    [InlineData("""<Point id="p" address="0" area="coil" dataType="bool" />""", null)]
    [InlineData("""<Point id="p" address="0" area="coil" access="readwrite" dataType="uint16" />""", null)]
    [InlineData("""<Point id="p" address="0" area="coil" dataType="int32" />""", "位区")]
    [InlineData("""<Point id="p" address="0" area="coil" dataType="string" length="2" />""", "位区")]
    [InlineData("""<Point id="p" address="0" area="discrete" dataType="float64" />""", "位区")]
    [InlineData("""<Point id="p" address="0" area="coil" dataType="raw" length="2" />""", "位区")]
    [InlineData("""<Point id="p" address="0" area="discrete" dataType="datetime"><DateTime format="unixsec" /></Point>""", "位区")]
    [InlineData("""<Point id="p" address="0" area="holding" dataType="string" length="2" />""", null)]
    public void Bit_areas_must_not_declare_multi_word_points(string point, string? expected)
    {
        var xml = Xml(point);

        if (expected == null)
        {
            Assert.True(Load(xml).IsValidated);
            return;
        }

        var message = ErrorsOf(xml);
        Assert.Contains(expected, message);
        Assert.Contains("点位 ps1/p", message);
    }

    [Fact]
    public void Multi_word_bcd_in_a_bit_area_is_rejected_as_well()
    {
        // BCD digits=8 → 2 字，位区同样拒绝（digits=4 → 1 字，位区仍属「多字类型」）
        Assert.Contains("位区", ErrorsOf(Xml(
            """<Point id="p" address="0" area="coil" dataType="bcd"><Bcd digits="8" /></Point>""")));
    }

    [Fact]
    public void Multi_word_points_in_bit_areas_are_also_caught_inside_blocks()
    {
        // 块区取块声明的区：holding 块里的 uint32 合法
        Assert.True(Load(XmlSets(
            "<Blocks><Block id=\"b\" start=\"0\" count=\"4\">"
            + "<Point id=\"bp\" address=\"0\" dataType=\"uint32\" /></Block></Blocks>")).IsValidated);

        // 显式声明为位区的块里出现多字点位 → 拒绝
        var coilBlock = ErrorsOf(XmlSets(
            "<Blocks><Block id=\"b\" area=\"coil\" start=\"0\" count=\"8\">"
            + "<Point id=\"bp\" address=\"0\" dataType=\"uint32\" /></Block></Blocks>"));

        Assert.Contains("位区", coilBlock);
        Assert.Contains("点位 ps1/bp", coilBlock);
    }

    // ═══════════════ 四、D68~D72 / CGV-36：报警规则在模板与块内同样生效 ═══════════════

    [Fact]
    public void Alarm_rules_apply_to_template_provided_alarms()
    {
        // 报警写在点位模板里同样会被展开到真实点位——缺 limit 必须在模板层就报出来
        var message = ErrorsOf(XmlSets(
            "<Points><Point id=\"p\" address=\"0\" template=\"tpl\" /></Points>",
            topLevel: "<PointTemplates><Point id=\"tpl\"><Alarm id=\"a\" type=\"high\" /></Point></PointTemplates>"));

        Assert.Contains("缺少 limit", message);
    }

    [Fact]
    public void Alarm_rules_apply_inside_blocks_and_calculated_points()
    {
        var block = ErrorsOf(XmlSets(
            "<Blocks><Block id=\"b\" start=\"0\" count=\"4\">"
            + "<Point id=\"bp\" address=\"0\"><Alarm type=\"high\" limit=\"1\" deadband=\"-1\" /></Point></Block></Blocks>"));
        Assert.Contains("deadband=-1", block);
        Assert.Contains("点位 ps1/bp", block);

        var calculated = ErrorsOf(
            "<SamplerConfig schemaVersion=\"3.0\">"
            + "<Global><Polling defaultIntervalMs=\"1000\" requestTimeoutMs=\"500\" /></Global>"
            + "<Transports><Transport id=\"tcp1\" host=\"127.0.0.1\" /></Transports>"
            + "<Devices><Device id=\"d1\" transport=\"tcp1\" pointSet=\"ps1\" /></Devices>"
            + "<PointSets><PointSet id=\"ps1\"><Calculated>"
            + "<Point id=\"c\"><Expression>2+2</Expression><Alarm type=\"high\" limit=\"1\" /></Point>"
            + "</Calculated></PointSet></PointSets></SamplerConfig>");
        Assert.Contains("计算点不得挂", calculated);
    }

    // ═══════════════ 五、CGV-37：枚举穷举（本轮补齐的静默回落面）═══════════════

    [Theory]
    [InlineData("area")]
    [InlineData("dataType")]
    [InlineData("swap")]
    [InlineData("access")]
    public void Defaults_level_enums_are_validated(string attribute)
    {
        var message = ErrorsOf(
            "<SamplerConfig schemaVersion=\"3.0\">"
            + "<Global><Polling defaultIntervalMs=\"1000\" requestTimeoutMs=\"500\" /></Global>"
            + "<Transports><Transport id=\"tcp1\" host=\"127.0.0.1\" /></Transports>"
            + "<Devices><Device id=\"d1\" transport=\"tcp1\" pointSet=\"ps1\" /></Devices>"
            + "<PointSets><PointSet id=\"ps1\"><Defaults " + attribute + "=\"bogus\" />"
            + "<Points><Point id=\"p\" address=\"0\" /></Points></PointSet></PointSets></SamplerConfig>");

        Assert.Contains("PointSet ps1/Defaults", message);
        Assert.Contains(attribute + "=\"bogus\" 非法", message);
    }

    [Theory]
    [InlineData("area=\"bogus\"", "area")]
    [InlineData("swap=\"bogus\"", "swap")]
    public void Block_level_enums_are_validated(string fragment, string attribute)
    {
        var message = ErrorsOf(XmlSets(
            $"<Blocks><Block id=\"b\" {fragment} start=\"0\" count=\"4\"><Point id=\"bp\" address=\"0\" /></Block></Blocks>"));

        Assert.Contains("Block b", message);
        Assert.Contains(attribute + "=\"bogus\" 非法", message);
    }

    [Fact]
    public void Global_and_device_swap_enums_are_validated()
    {
        Assert.Contains("Global：swap=\"bogus\" 非法", ErrorsOf(Xml(
            "<Point id=\"p\" address=\"0\" />", globalAttrs: " swap=\"bogus\"")));

        Assert.Contains("Device d1：swap=\"bogus\" 非法", ErrorsOf(
            "<SamplerConfig schemaVersion=\"3.0\">"
            + "<Global swap=\"none\"><Polling defaultIntervalMs=\"1000\" requestTimeoutMs=\"500\" /></Global>"
            + "<Transports><Transport id=\"tcp1\" host=\"127.0.0.1\" /></Transports>"
            + "<Devices><Device id=\"d1\" transport=\"tcp1\" pointSet=\"ps1\" swap=\"bogus\" /></Devices>"
            + "<PointSets><PointSet id=\"ps1\"><Points><Point id=\"p\" address=\"0\" /></Points></PointSet></PointSets>"
            + "</SamplerConfig>"));
    }

    [Fact]
    public void Quality_transport_format_and_datetime_enums_are_validated()
    {
        Assert.Contains("onCommErrorValue=\"bogus\" 非法", ErrorsOf(Xml(
            "<Point id=\"p\" address=\"0\" />", globalInner: "<Quality onCommErrorValue=\"bogus\" />")));

        Assert.Contains("handshake=\"bogus\" 非法", ErrorsOf(Xml(
            "<Point id=\"p\" address=\"0\" />",
            topLevel: "<Transports><Transport id=\"tcp2\" host=\"x\" handshake=\"bogus\" /></Transports>")));

        Assert.Contains("mapOn=\"bogus\" 非法", ErrorsOf(Xml(
            "<Point id=\"p\" address=\"0\"><Format mapOn=\"bogus\" /></Point>")));

        Assert.Contains("mode=\"bogus\" 非法", ErrorsOf(Xml(
            "<Point id=\"p\" address=\"0\"><Scale><Clamp mode=\"bogus\" low=\"0\" high=\"1\" /></Scale></Point>")));

        Assert.Contains("format=\"bogus\" 非法", ErrorsOf(Xml(
            "<Point id=\"p\" address=\"0\" dataType=\"datetime\"><DateTime format=\"bogus\" /></Point>")));
    }

    [Fact]
    public void Every_legal_enum_value_is_still_accepted()
    {
        // 正例（不误伤）：各枚举的合法取值照常加载
        var config = Load(Xml(
            "<Point id=\"p\" address=\"0\" area=\"coil\" dataType=\"bool\" swap=\"dcba\" access=\"readwrite\">"
            + "<Format mapOn=\"raw\" /><DateTime format=\"plc6\" /><Bcd digits=\"4\" />"
            + "<Scale mode=\"linear\"><Clamp mode=\"both\" low=\"0\" high=\"10\" /></Scale></Point>",
            globalInner: "<Quality onCommError=\"uncertain\" onCommErrorValue=\"null\" />",
            globalAttrs: " swap=\"cdab\"",
            topLevel: "<Transports><Transport id=\"tcp2\" host=\"x\" handshake=\"dtrdsr\" parity=\"even\" stopBits=\"two\" variant=\"rtu\" /></Transports>"
                      + "<Devices><Device id=\"d2\" transport=\"tcp2\" pointSet=\"ps1\" swap=\"badc\" /></Devices>"));

        Assert.True(config.IsValidated);
        Assert.Empty(config.Warnings);
        Assert.Equal(RuntimeArea.Coil, config.PointSets[0].Points[0].Area);
    }

    // ═══════════════ 六、CGV-38：数值边界 ═══════════════

    [Theory]
    [InlineData("bit=\"16\"", "bit=16")]
    [InlineData("bit=\"-1\"", "bit=-1")]
    [InlineData("length=\"0\"", "length=0")]
    [InlineData("length=\"-2\"", "length=-2")]
    [InlineData("unitId=\"300\"", "unitId=300")]
    [InlineData("unitId=\"-1\"", "unitId=-1")]
    public void Point_level_numeric_bounds_are_validated(string fragment, string expected)
    {
        var message = ErrorsOf(Xml($"""<Point id="p" address="0" {fragment} />"""));

        Assert.Contains("点位 ps1/p", message);
        Assert.Contains(expected, message);
    }

    [Theory]
    [InlineData("<Format decimals=\"-1\" />", "decimals=-1")]
    [InlineData("<Format decimals=\"16\" />", "decimals=16")]
    [InlineData("<Write pulseMs=\"-1\" />", "pulseMs=-1")]
    public void Nested_numeric_bounds_are_validated(string child, string expected)
    {
        var message = ErrorsOf(Xml($"""<Point id="p" address="0">{child}</Point>"""));

        Assert.Contains("点位 ps1/p", message);
        Assert.Contains(expected, message);
    }

    [Fact]
    public void Write_range_with_min_above_max_is_rejected()
    {
        var message = ErrorsOf(Xml("""<Point id="p" address="0"><Write min="10" max="1" /></Point>"""));

        Assert.Contains("min=10 > max=1", message);
    }

    [Fact]
    public void Bcd_digits_and_transport_numbers_are_validated()
    {
        Assert.Contains("digits=0", ErrorsOf(Xml(
            """<Point id="p" address="0" dataType="bcd"><Bcd digits="0" /></Point>""")));

        var transport = ErrorsOf(Xml("<Point id=\"p\" address=\"0\" />",
            topLevel: "<Transports><Transport id=\"tcp2\" host=\"x\" port=\"0\" baudRate=\"0\" dataBits=\"9\" "
                      + "connectTimeoutMs=\"0\" gapMs=\"-1\" readTimeoutMs=\"-1\" writeTimeoutMs=\"-1\" />"
                      + "</Transports>"));

        Assert.Contains("port=0", transport);
        Assert.Contains("baudRate=0", transport);
        Assert.Contains("dataBits=9", transport);
        Assert.Contains("connectTimeoutMs=0", transport);
        Assert.Contains("gapMs=-1", transport);
        Assert.Contains("readTimeoutMs=-1", transport);
        Assert.Contains("writeTimeoutMs=-1", transport);

        var global = ErrorsOf(Xml("<Point id=\"p\" address=\"0\" />",
            globalInner: "<Quality staleAfterMs=\"-1\" /><Retry count=\"-1\" intervalMs=\"-1\" />"));

        Assert.Contains("staleAfterMs=-1", global);
        Assert.Contains("count=-1", global);
        Assert.Contains("intervalMs=-1", global);

        Assert.Contains("start=-1", ErrorsOf(XmlSets(
            "<Blocks><Block id=\"b\" start=\"-1\" count=\"4\"><Point id=\"bp\" address=\"0\" /></Block></Blocks>")));
    }

    [Fact]
    public void Non_finite_numbers_in_value_processing_are_rejected()
    {
        // NaN/±Inf 写在配置里 = 该点位的值恒为坏/不确定（ADR D32 只在**解码结果**上降级，配置层直接拒绝）
        Assert.Contains("factor=NaN", ErrorsOf(Xml(
            """<Point id="p" address="0"><Scale factor="NaN" /></Point>""")));
        Assert.Contains("offset=INF", ErrorsOf(Xml(
            """<Point id="p" address="0"><Scale offset="INF" /></Point>""")));
        Assert.Contains("high=-INF", ErrorsOf(Xml(
            """<Point id="p" address="0"><Scale><Clamp mode="both" low="0" high="-INF" /></Scale></Point>""")));
        Assert.Contains("max=INF", ErrorsOf(Xml(
            """<Point id="p" address="0" access="write"><Write max="INF" /></Point>""")));
        Assert.Contains("端值必须是有限数值", ErrorsOf(Xml(
            """<Point id="p" address="0" range="0..NaN" />""")));
        Assert.Contains("limit=NaN", ErrorsOf(Xml(
            """<Point id="p" address="0"><Alarm type="high" limit="NaN" /></Point>""")));
    }

    [Fact]
    public void Legal_boundary_numbers_are_accepted()
    {
        // 边界正例：0 与上限值都合法（只有越界才报）
        var config = Load(Xml(
            "<Point id=\"p\" address=\"0\" bit=\"15\" length=\"1\" unitId=\"255\" dataType=\"uint16\" access=\"write\">"
            + "<Format decimals=\"15\" /><Write min=\"1\" max=\"1\" pulseMs=\"0\" /></Point>",
            globalInner: "<Quality staleAfterMs=\"0\" /><Retry count=\"0\" intervalMs=\"0\" /><Scheduler ignoreGap=\"0\" />",
            topLevel: "<Transports><Transport id=\"tcp2\" host=\"x\" port=\"65535\" baudRate=\"1\" dataBits=\"5\" "
                      + "connectTimeoutMs=\"1\" gapMs=\"0\" readTimeoutMs=\"0\" writeTimeoutMs=\"0\" /></Transports>"));

        Assert.True(config.IsValidated);
        Assert.Empty(config.Warnings);
    }
}
