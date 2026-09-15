using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using SuperSampler.Core.Config;
using Xunit;

namespace SuperSampler.UnitTests.Config;

/// <summary>
/// 字段级「未接线 / 非法值 / 未落实规则」的证据测试。
/// 第四轮（findings W 系列接线 + C 机制）后，本文件锁定的是**修复后的正确行为**：
///   ① 仍未接线的字段（写了仍被静默忽略）；
///   ② 未实现功能段按 HostConfig@unsupportedPolicy 显式化（warn/error/ignore）；
///   ③ 非法枚举值必须报错（不再静默回落）；
///   ④ 尚未落实的校验规则（仍是已知缺口）。
/// 矩阵与状态见 _testplan/field-coverage.md，缺陷编号见 _testplan/findings.md。
/// </summary>
public class FieldCoverageGapsTests
{
    // ─────────────── 构造器 ───────────────

    private static SamplerConfiguration Load(string inner)
        => SamplerConfigLoader.Load(XDocument.Parse("<HostConfig schemaVersion=\"3.0\">" + inner + "</HostConfig>"), Directory.GetCurrentDirectory());

    private static ConfigValidationException LoadError(string inner)
        => Assert.Throws<ConfigValidationException>(() => Load(inner));

    private const string Base =
        "<ScanGroups><ScanGroup id=\"normal\" /></ScanGroups>"
        + "<Transports><Transport id=\"tcp1\" host=\"x\" /></Transports>"
        + "<Devices><Device id=\"d1\" transport=\"tcp1\" pointSet=\"ps1\" /></Devices>";

    private const string PointSet =
        "<PointSets><PointSet id=\"ps1\"><Points><Point id=\"p\" address=\"0\" /></Points></PointSet></PointSets>";

    /// <summary>基础配置 + 顶部附加段（Global/Diagnostics/Drivers/…）。</summary>
    private static SamplerConfiguration WithTop(string top) => Load(Base + top + PointSet);

    /// <summary>基础配置 + 单点位（可带属性/子元素）。</summary>
    private static SamplerConfiguration WithPoint(string attrs = "", string child = "")
    {
        var pt = child.Length == 0
            ? "<Point id=\"p\" address=\"0\" " + attrs + " />"
            : "<Point id=\"p\" address=\"0\" " + attrs + ">" + child + "</Point>";
        return Load(Base + "<PointSets><PointSet id=\"ps1\"><Points>" + pt + "</Points></PointSet></PointSets>");
    }

    private static PointConfig P0(SamplerConfiguration c) => c.PointSets[0].Points[0];
    private static DeviceConfig D0(SamplerConfiguration c) => c.Devices[0];
    private static GlobalOptions G(SamplerConfiguration c) => c.Global;

    /// <summary>哨兵断言：整段被静默吸收，其余模型不受影响。</summary>
    private static void Absorbed(SamplerConfiguration c)
    {
        var p = Assert.Single(c.PointSets[0].Points);
        Assert.Equal("p", p.Id);
    }

    private static GapCase C(string name, SamplerConfiguration cfg, Action<SamplerConfiguration> check)
        => new() { Name = name, Config = cfg, Check = check };

    private static IEnumerable<object[]> Cases(Func<IEnumerable<GapCase>> factory)
    {
        foreach (var g in factory()) yield return new object[] { g };
    }

    public sealed class GapCase
    {
        public string Name = string.Empty;
        public SamplerConfiguration Config = null!;
        public Action<SamplerConfiguration> Check = _ => { };
        public override string ToString() => Name;
    }

    // ─────────────── ① 仍未接线的字段：写了仍被忽略 ───────────────

    [Theory]
    [MemberData(nameof(Unwired))]
    public void Declared_but_unwired_field_is_ignored(GapCase c) => c.Check(c.Config);

    public static IEnumerable<object[]> Unwired => Cases(UnwiredCases);

    private static IEnumerable<GapCase> UnwiredCases()
    {
        // ── Global@nullText / Global@swap 已接线（ADR D38），正向断言见 GlobalNullTextAndSwapAreWired ──

        // ── Global 子段中仍未接线的部分 ──
        yield return C("Global/Polling@gapMs 未解析", WithTop("<Global><Polling gapMs=\"33\" /></Global>"),
            c => Assert.Equal(1000, G(c).DefaultRateMs));
        yield return C("Global/Reconnect 整段未解析", WithTop("<Global><Reconnect enabled=\"false\" initialDelayMs=\"1\" maxDelayMs=\"2\" keepAliveMs=\"3\" /></Global>"), Absorbed);
        yield return C("Global/Scheduler@maxConcurrent/@strictOrder 未解析（上限三项已接线）",
            WithTop("<Global><Scheduler maxConcurrent=\"8\" maxRegistersPerRead=\"60\" maxBitsPerRead=\"500\" strictOrder=\"false\" /></Global>"),
            c => { Assert.Equal(60, G(c).MaxRegistersPerRead); Assert.Equal(500, G(c).MaxBitsPerRead); });
        yield return C("Global/Script@maxMemoryMb 未解析（timeoutMs/onError 已接线）",
            WithTop("<Global><Script timeoutMs=\"7\" maxMemoryMb=\"8\" onError=\"keepLast\" /></Global>"),
            c => { Assert.Equal(7, G(c).ScriptTimeoutMs); Assert.Equal("keepLast", G(c).ScriptOnError); });

        // ── I18n ──
        yield return C("I18n@default/@reloadOnChange 未解析（只读 Files/File）", WithTop("<I18n default=\"zh_CN\" reloadOnChange=\"false\" />"), Absorbed);

        // ── ScanGroups ──
        yield return C("ScanGroup/Trigger 未解析", WithTop("<ScanGroups><ScanGroup id=\"od\" mode=\"onDemand\"><Trigger>ui1</Trigger></ScanGroup></ScanGroups>"),
            c => Assert.Equal("ondemand", c.ScanGroups[1].Mode, ignoreCase: true));

        // ── Transports ──
        yield return C("Transport@driver/@maxConcurrent 未解析（串口六项已接线）",
            WithTop("<Transports><Transport id=\"t2\" driver=\"modbus\" maxConcurrent=\"4\" portName=\"COM3\" handshake=\"rts\" dtr=\"true\" rts=\"true\" readTimeoutMs=\"9\" writeTimeoutMs=\"8\" /></Transports>"),
            c =>
            {
                Assert.Equal("rts", c.Transports[1].Handshake);
                Assert.True(c.Transports[1].DtrEnable);
                Assert.Equal(9, c.Transports[1].ReadTimeoutMs);
                Assert.Equal(8, c.Transports[1].WriteTimeoutMs);
            });
        yield return C("Transport/Reconnect 整段未解析", WithTop("<Transports><Transport id=\"t2\"><Reconnect enabled=\"false\" /></Transport></Transports>"), Absorbed);

        // ── Devices ──
        yield return C("Device@desc 未解析（generateDiagnostics 已按 unsupportedPolicy 告警）",
            WithTop("<Devices><Device id=\"d2\" unitId=\"2\" transport=\"tcp1\" pointSet=\"ps1\" desc=\"x\" generateDiagnostics=\"false\" /></Devices>"),
            c => { Assert.Equal(2, c.Devices[1].UnitId); Assert.Empty(c.Warnings); }); // generateDiagnostics=false 不告警
    }

    // ─────────────── ② 未实现功能段：按 unsupportedPolicy 显式化（C 机制） ───────────────

    [Theory]
    [MemberData(nameof(Warned))]
    public void Unimplemented_section_is_reported_as_warning(GapCase c) => c.Check(c.Config);

    public static IEnumerable<object[]> Warned => Cases(WarnedCases);

    private static IEnumerable<GapCase> WarnedCases()
    {
        yield return C("Drivers 段 → 告警", WithTop("<Drivers><Driver id=\"d\" type=\"modbus\" enabled=\"false\"><Config><Item key=\"k\" value=\"v\" /></Config></Driver></Drivers>"),
            Warn);
        yield return C("Storage 段 → 告警", WithTop("<Storage><History enabled=\"true\" provider=\"sqlite\" retentionDays=\"30\" /><Events enabled=\"true\" provider=\"file\" /></Storage>"),
            Warn);
        yield return C("Ui 段 → 告警", WithTop("<Ui startScreen=\"s1\"><Screen id=\"s1\" title=\"t\" /></Ui>"), Warn);
        yield return C("Users 段 → 告警", WithTop("<Users auditWrites=\"true\"><Role id=\"op\" name=\"操作员\" /></Users>"), Warn);
        yield return C("Commands 段 → 告警", WithTop("<Commands><Command id=\"c1\"><Steps><Step action=\"write\" point=\"p\" value=\"1\" /></Steps></Command></Commands>"), Warn);
        yield return C("Diagnostics/Trace → 告警", WithTop("<Diagnostics allowRawAccess=\"true\"><Trace enabled=\"true\" ringSize=\"10\" /></Diagnostics>"), Warn);
        yield return C("Diagnostics/Events → 告警", WithTop("<Diagnostics><Events enabled=\"true\" queueSize=\"100\" /></Diagnostics>"), Warn);
        yield return C("Diagnostics/Recovery → 告警", WithTop("<Diagnostics><Recovery autoRecover=\"true\" retryOfflineMs=\"10000\" /></Diagnostics>"), Warn);
        yield return C("Point/History → 告警", WithPoint("", "<History enabled=\"true\" mode=\"deadband\" retentionDays=\"30\" />"), Warn);
        yield return C("Point/Script → 告警", WithPoint("", "<Script language=\"csharp\">return 1;</Script>"), Warn);
        yield return C("Point/Tags → 告警", WithPoint("", "<Tags><Tag>k</Tag></Tags>"), Warn);
        yield return C("Point/String@padding/@byteAligned → 告警", WithPoint("dataType=\"string\"", "<String padding=\"0x20\" byteAligned=\"false\" />"), Warn);
        yield return C("Point/Scale@mode → 告警", WithPoint("", "<Scale mode=\"twopoint\" factor=\"2\" />"), Warn);
        yield return C("AlarmClass 展示属性 → 告警", WithTop("<AlarmClasses><AlarmClass id=\"CRIT\" name=\"严重\" color=\"#f00\" sound=\"s.wav\" escalateAfterMs=\"5000\" /></AlarmClasses>"), Warn);
        yield return C("Device@generateDiagnostics=true → 告警",
            WithTop("<Devices><Device id=\"d2\" unitId=\"2\" transport=\"tcp1\" pointSet=\"ps1\" generateDiagnostics=\"true\" /></Devices>"), Warn);
        yield return C("Device/Simulate → 告警",
            WithTop("<Devices><Device id=\"d2\" unitId=\"2\" transport=\"tcp1\" pointSet=\"ps1\"><Simulate profile=\"ramp\" /></Device></Devices>"), Warn);
    }

    private static Action<SamplerConfiguration> Warn => c =>
    {
        var w = Assert.Single(c.Warnings);
        Assert.Contains("尚未实现", w);
        Assert.Contains("unsupportedPolicy=warn", w);
    };

    // ─────────────── ③ unsupportedPolicy：warn（默认）/ error / ignore ───────────────

    private const string WithDrivers = "<Drivers><Driver id=\"d\" type=\"modbus\" /></Drivers>";

    [Fact]
    public void Unsupported_policy_warn_records_warning_and_loads()
    {
        var c = Load(Base + WithDrivers + PointSet);

        Assert.Equal("warn", c.UnsupportedPolicy);
        Assert.Single(c.Warnings);
        Assert.Contains("Drivers", c.Warnings[0]);
        Assert.Single(c.PointSets[0].Points); // 配置本身照常可用
    }

    [Fact]
    public void Unsupported_policy_error_fails_load()
    {
        var ex = Assert.Throws<ConfigValidationException>(() => SamplerConfigLoader.Load(
            XDocument.Parse("<HostConfig schemaVersion=\"3.0\" unsupportedPolicy=\"error\">" + Base + WithDrivers + PointSet + "</HostConfig>"),
            Directory.GetCurrentDirectory()));

        Assert.Contains(ex.Errors, e => e.Contains("Drivers") && e.Contains("unsupportedPolicy=error"));
    }

    [Fact]
    public void Unsupported_policy_ignore_is_silent()
    {
        var c = SamplerConfigLoader.Load(
            XDocument.Parse("<HostConfig schemaVersion=\"3.0\" unsupportedPolicy=\"ignore\">" + Base + WithDrivers + PointSet + "</HostConfig>"),
            Directory.GetCurrentDirectory());

        Assert.Equal("ignore", c.UnsupportedPolicy);
        Assert.Empty(c.Warnings);
        Assert.Single(c.PointSets[0].Points);
    }

    // ─────────────── ④ 非法枚举/数值：必须报错（不再静默回落） ───────────────

    [Theory]
    [MemberData(nameof(Invalid))]
    public void Invalid_value_is_rejected(string inner, string expectedFragment)
    {
        var ex = LoadError(inner);
        Assert.Contains(ex.Errors, e => e.Contains(expectedFragment));
    }

    public static IEnumerable<object[]> Invalid => InvalidCases();

    private static IEnumerable<object[]> InvalidCases()
    {
        yield return Row(PointOnly("area=\"bogus\""), "area 非法值");
        yield return Row(PointOnly("dataType=\"bogus\""), "dataType 非法值");
        yield return Row(PointOnly("swap=\"bogus\""), "swap 非法值");
        yield return Row(PointOnly("access=\"bogus\""), "access 非法值");
        yield return Row(PointOnly("range=\"abc\""), "range 非法值");
        yield return Row(Top("<Transports><Transport id=\"t2\" variant=\"bogus\" /></Transports>"), "variant 非法值");
        yield return Row(Top("<Transports><Transport id=\"t2\" parity=\"bogus\" /></Transports>"), "parity 非法值");
        yield return Row(Top("<Transports><Transport id=\"t2\" stopBits=\"bogus\" /></Transports>"), "stopBits 非法值");
        yield return Row(Top("<ScanGroups><ScanGroup id=\"x\" mode=\"bogus\" /></ScanGroups>"), "mode 非法值");
    }

    private static object[] Row(string inner, string fragment) => new object[] { inner, fragment };

    private static string Top(string top) => Base + top + PointSet;

    private static string PointOnly(string attrs)
        => Base + "<PointSets><PointSet id=\"ps1\"><Points><Point id=\"p\" address=\"0\" " + attrs + " /></Points></PointSet></PointSets>";

    // ─────────────── ⑤ 校验规则清单里仍未落实的规则 ───────────────

    [Theory]
    [MemberData(nameof(Unenforced))]
    public void Unenforced_rule_does_not_throw(GapCase c) => c.Check(c.Config);

    public static IEnumerable<object[]> Unenforced => Cases(UnenforcedCases);

    private static IEnumerable<GapCase> UnenforcedCases()
    {
        yield return C("规则10：low 报警高于 high 报警不报错", WithPoint("", "<Alarm id=\"h\" type=\"high\" limit=\"10\" /><Alarm id=\"l\" type=\"low\" limit=\"90\" />"),
            c => Assert.Equal(2, P0(c).Alarms.Count));
        yield return C("规则9：计算点循环引用不报错",
            Load(Base + "<PointSets><PointSet id=\"ps1\"><Points /><Calculated>"
                + "<Point id=\"c1\"><Expression>P('c2')</Expression></Point>"
                + "<Point id=\"c2\"><Expression>P('c1')</Expression></Point>"
                + "</Calculated></PointSet></PointSets>"),
            c => Assert.Equal(2, c.PointSets[0].Calculated.Count));
        yield return C("规则1：Write@permission 指向不存在的角色不报错", WithPoint("access=\"write\"", "<Write permission=\"ghost\" />"),
            c => Assert.Equal("ghost", P0(c).Write!.Permission));
        yield return C("规则3：Command id 重复不报错",
            WithTop("<Commands><Command id=\"c1\"><Steps><Step point=\"p\" /></Steps></Command><Command id=\"c1\"><Steps><Step point=\"p\" /></Steps></Command></Commands>"),
            Absorbed);
    }

    // ─────────────── ⑥ 已修复的结构性缺陷（断言修复后行为） ───────────────

    [Fact]
    public void Point_template_reference_is_validated()
    {
        var ex = Assert.Throws<ConfigValidationException>(() => WithPoint("template=\"nope\""));
        Assert.Contains(ex.Errors, e => e.Contains("不存在的模板"));
    }

    [Fact]
    public void Device_template_reference_is_validated()
    {
        // W36：此前指向不存在的设备模板被静默忽略
        var ex = LoadError(Base.Replace("pointSet=\"ps1\" />", "pointSet=\"ps1\" template=\"nope\" />") + PointSet);
        Assert.Contains(ex.Errors, e => e.Contains("不存在的设备模板"));
    }

    [Fact]
    public void Malformed_numeric_attribute_is_wrapped_as_config_error()
    {
        // W32：裸 FormatException 已包装为 ConfigValidationException
        var ex = Assert.Throws<ConfigValidationException>(() => WithPoint("bit=\"x\""));
        // 文案已增强为「点位+属性」定位（见 V3SampleConfigTests）：仍须是可读的配置错误，而非裸 FormatException
        Assert.Contains(ex.Errors, e => e.Contains("不是合法数值或布尔值"));
    }

    [Fact]
    public void Missing_schema_version_is_rejected()
    {
        // W1：schemaVersion 必填
        var ex = Assert.Throws<ConfigValidationException>(() => SamplerConfigLoader.Load(
            XDocument.Parse("<HostConfig>" + Base + PointSet + "</HostConfig>"), Directory.GetCurrentDirectory()));
        Assert.Contains(ex.Errors, e => e.Contains("schemaVersion"));
    }

    [Fact]
    public void Unsupported_schema_version_is_rejected()
    {
        var ex = Assert.Throws<ConfigValidationException>(() => SamplerConfigLoader.Load(
            XDocument.Parse("<HostConfig schemaVersion=\"2.4\">" + Base + PointSet + "</HostConfig>"), Directory.GetCurrentDirectory()));
        Assert.Contains(ex.Errors, e => e.Contains("不受支持"));
    }

    [Fact]
    public void Future_3x_schema_version_is_accepted()
    {
        var c = SamplerConfigLoader.Load(
            XDocument.Parse("<HostConfig schemaVersion=\"3.9\">" + Base + PointSet + "</HostConfig>"), Directory.GetCurrentDirectory());
        Assert.Single(c.PointSets[0].Points);
    }

    [Fact]
    public void AllowRawAccess_is_read_without_a_Global_element()
    {
        // W33：LoadGlobal 此前在缺少 <Global> 时提前 return，同级的 <Diagnostics> 被一并跳过
        var c = Load("<ScanGroups><ScanGroup id=\"normal\" /></ScanGroups>"
            + "<Diagnostics allowRawAccess=\"true\" />"
            + "<PointSets><PointSet id=\"ps1\"><Points><Point id=\"p\" address=\"0\" /></Points></PointSet></PointSets>");

        Assert.True(c.Global.AllowRawAccess);
    }

    [Fact]
    public void Coil_block_of_2000_bits_is_accepted()
    {
        // W34：块上限按区区分（位区 2000 / 寄存器区 125）
        var c = Load(Base + "<PointSets><PointSet id=\"ps1\"><Blocks><Block id=\"b1\" area=\"coil\" start=\"0\" count=\"2000\" /></Blocks><Points /></PointSet></PointSets>");

        Assert.Equal(2000, c.PointSets[0].Blocks[0].Count);
    }

    [Fact]
    public void Register_block_over_125_registers_is_rejected()
    {
        var ex = LoadError(Base + "<PointSets><PointSet id=\"ps1\"><Blocks><Block id=\"b1\" start=\"0\" count=\"126\" /></Blocks><Points /></PointSet></PointSets>");

        Assert.Contains(ex.Errors, e => e.Contains("寄存器区 125"));
    }

    [Fact]
    public void GlobalNullTextAndSwapAreWired()
    {
        // D38 / W37：这两个属性此前「声明但从不读取」（写了等于没写）
        var c = WithTop("<Global nullText=\"N/A\" swap=\"none\" />");

        Assert.Equal("N/A", G(c).NullText);
        Assert.Equal(SwapMode.None, G(c).DefaultSwap);
    }

    // ─────────────── ⑦ i18n 资源加载与替换 ───────────────

    [Fact]
    public void I18n_file_is_loaded_and_keys_substituted_into_text()
    {
        var path = Path.Combine(Path.GetTempPath(), "ss_i18n_" + Guid.NewGuid().ToString("N") + ".i18n");
        File.WriteAllText(path, "# 注释行\nGREET = \"你好\"\nUNIT_C = \"\"\n", Encoding.UTF8);
        try
        {
            var c = Load("<I18n><Files><File lang=\"zh_CN\" path=\"" + path + "\" /></Files></I18n>"
                + Base
                + "<PointSets><PointSet id=\"ps1\"><Points><Point id=\"p\" address=\"0\" name=\"${GREET}\" /></Points></PointSet></PointSets>");

            Assert.Equal("你好", c.PointSets[0].Points[0].Name);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
