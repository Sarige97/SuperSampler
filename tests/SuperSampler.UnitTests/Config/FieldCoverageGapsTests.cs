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
/// 2026-09-16 复核：⑤「加载期全量校验」第一步落地，原缺口「规则9 计算点循环」「规则10 报警限值」
/// 已从「不报错」翻转为「必须报错」，用例移到 §⑤。
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
            "<Transports><Transport id=\"tcp1\" host=\"x\" /></Transports>"
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
            c => Assert.Equal(1000, G(c).DefaultIntervalMs));
        // Global/Reconnect 已于 2026-09-16 第四步整段接线（W6 关闭）：正向断言见 §⑦，非法值见 §④c
        yield return C("Global/Scheduler@maxConcurrent/@strictOrder 未解析（地址组上限与忽略间隔数已接线）",
            WithTop("<Global><Scheduler maxConcurrent=\"8\" groupLimitRegisters=\"60\" groupLimitBits=\"500\" ignoreGap=\"1\" strictOrder=\"false\" /></Global>"),
            c => { Assert.Equal(60, G(c).GroupLimitRegisters); Assert.Equal(500, G(c).GroupLimitBits); Assert.Equal(1, G(c).IgnoreGap); });
        // Global/Script 已于 2026-09-16 恢复脚本引擎时整段接线（ADR D41 接线，W8 关闭）：
        // 正向断言见 ScriptConfigTests（解析）与 ScriptDecodeTests（行为），非法值见 §④d

        // ── I18n ──
        yield return C("I18n@default/@reloadOnChange 未解析（只读 Files/File）", WithTop("<I18n default=\"zh_CN\" reloadOnChange=\"false\" />"), Absorbed);

        // ── Transports ──
        yield return C("Transport@driver/@maxConcurrent 未解析（串口六项已接线）",
            WithTop("<Transports><Transport id=\"t2\" driver=\"modbus\" maxConcurrent=\"4\" portName=\"COM3\" handshake=\"rtscts\" dtr=\"true\" rts=\"true\" readTimeoutMs=\"9\" writeTimeoutMs=\"8\" /></Transports>"),
            c =>
            {
                Assert.Equal("rtscts", c.Transports[1].Handshake);
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
        // Point/Script 与 Global/Script 已于 2026-09-16 接线（ADR D41）：不再告警，非法值走 §④d
        yield return C("Parsers 段 → 告警（脚本/自定义解码器归宿主，框架不解析）",
            WithTop("<Parsers><Parser id=\"pp\" type=\"Script\"><Script>return 1;</Script></Parser></Parsers>"), Warn);
        yield return C("Point/Tags → 告警", WithPoint("", "<Tags><Tag>k</Tag></Tags>"), Warn);
        // String@padding 已于 2026-09-16 第七步接线（正向断言见 StringPaddingAndLeft* 用例），不再告警；
        // String@perByte/@byteAligned 未实现，改成「写了即报错」（CGV-29），见 ④b。
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
        yield return Row(PointOnly("mode=\"bogus\""), "mode 非法值");
        yield return Row(Base + "<PointSets><PointSet id=\"ps1\"><Blocks><Block id=\"b1\" start=\"0\" count=\"4\" mode=\"bogus\" /></Blocks><Points /></PointSet></PointSets>", "mode 非法值");
    }

    // ─────────────── ④b 已删除/已改名的旧写法：一律报错，不做兼容（CGV-25） ───────────────

    [Theory]
    [MemberData(nameof(Removed))]
    public void Removed_old_write_up_is_rejected(string inner, string expectedFragment)
    {
        var ex = LoadError(inner);
        Assert.Contains(ex.Errors, e => e.Contains(expectedFragment));
    }

    public static IEnumerable<object[]> Removed => RemovedCases();

    private static IEnumerable<object[]> RemovedCases()
    {
        // 整段删除：命名的扫描节奏已被 intervalMs（毫秒）+ mode 取代
        yield return Row(Top("<ScanGroups><ScanGroup id=\"fast\" mode=\"poll\" rateMs=\"200\" /><ScanGroup id=\"od\" mode=\"onDemand\"><Trigger>ui1</Trigger></ScanGroup></ScanGroups>"),
            "ScanGroups 段已删除");
        // 属性删除：Device / Point 上写了 scanGroup
        yield return Row(Top("<Devices><Device id=\"d2\" unitId=\"2\" transport=\"tcp1\" pointSet=\"ps1\" scanGroup=\"fast\" /></Devices>"),
            "属性 scanGroup=\"fast\" 已删除");
        yield return Row(PointOnly("scanGroup=\"slow\""), "属性 scanGroup=\"slow\" 已删除");
        // 字段改名：旧名不再生效，必须指名道姓地报出来
        yield return Row(Top("<Global><Polling rateMs=\"500\" /></Global>"), "已改名为 defaultIntervalMs");
        yield return Row(Top("<Global><Scheduler mergeGap=\"2\" /></Global>"), "已改名为 ignoreGap");
        yield return Row(Top("<Global><Scheduler maxRegistersPerRead=\"60\" /></Global>"), "已改名为 groupLimitRegisters");
        yield return Row(Top("<Global><Scheduler maxBitsPerRead=\"600\" /></Global>"), "已改名为 groupLimitBits");
        // 不生效的继承中间层：Defaults 不承载节奏，写了就是错
        yield return Row(Base + "<PointSets><PointSet id=\"ps1\"><Defaults intervalMs=\"500\" /><Points><Point id=\"p\" address=\"0\" /></Points></PointSet></PointSets>",
            "Defaults 只承载 area/dataType/swap/unitId/access");
        yield return Row(Base + "<PointSets><PointSet id=\"ps1\"><Defaults mode=\"once\" /><Points><Point id=\"p\" address=\"0\" /></Points></PointSet></PointSets>",
            "Defaults 不承载读取模式");
        // 未实现的字符串口径：早期草案的 byteAligned 与用户决定不做的 perByte，写了直接报错（CGV-29）
        yield return Row(WithStringChild("byteAligned=\"true\""), "String@byteAligned 未实现");
        yield return Row(WithStringChild("perByte=\"true\""), "String@perByte 未实现");
    }

    /// <summary>字符串点位 + 指定 &lt;String&gt; 属性的完整配置（CGV-29 用例）。</summary>
    private static string WithStringChild(string stringAttrs)
        => Base + "<PointSets><PointSet id=\"ps1\"><Points>"
           + "<Point id=\"p\" address=\"0\" dataType=\"string\" length=\"4\">"
           + "<String " + stringAttrs + " /></Point>"
           + "</Points></PointSet></PointSets>";

    private static object[] Row(string inner, string fragment) => new object[] { inner, fragment };

    // ─────────────── ④c `Global/Reconnect`：接线后的正向值与 CGV-26 非法值 ───────────────

    [Fact]
    public void Gcg_reconnect_section_is_wired_into_model()
    {
        // 第四步（W6）：四项全部解析进模型——写了就必须生效（ADR D30）
        var c = WithTop("<Global><Reconnect enabled=\"false\" delays=\"5,10,20\" manualRetry=\"false\" offlineQuality=\"bad\" /></Global>");

        Assert.False(G(c).Reconnect.Enabled);
        Assert.Equal(new[] { 5, 10, 20 }, G(c).Reconnect.Delays);
        Assert.False(G(c).Reconnect.ManualRetry);
        Assert.Equal("bad", G(c).Reconnect.OfflineQuality);
    }

    [Fact]
    public void Gcg_reconnect_defaults_match_documented_values()
    {
        // 未写 Reconnect：默认 300,1000,3000,10000,30000 + 开启退避 + 开放手动重试 + offline 质量
        var c = WithTop("<Global><Reconnect /></Global>");

        Assert.True(G(c).Reconnect.Enabled);
        Assert.Equal(new ReconnectOptions().Delays, G(c).Reconnect.Delays);
        Assert.True(G(c).Reconnect.ManualRetry);
        Assert.Equal("offline", G(c).Reconnect.OfflineQuality);
    }

    [Fact]
    public void Gcg_reconnect_delay_queue_repeats_last_value()
    {
        // 队列语义：逐项等待，**用完后一直用最后一个值循环**
        var options = new ReconnectOptions { Delays = new[] { 100, 200 } };

        Assert.Equal(100, options.DelayAt(0));
        Assert.Equal(200, options.DelayAt(1));
        Assert.Equal(200, options.DelayAt(2));
        Assert.Equal(200, options.DelayAt(99));
    }

    [Theory]
    [MemberData(nameof(ReconnectInvalid))]
    public void Gcg_invalid_reconnect_is_rejected(string reconnect, string expectedFragment)
    {
        var ex = LoadError(Top("<Global><Reconnect " + reconnect + " /></Global>"));
        Assert.Contains(ex.Errors, e => e.Contains(expectedFragment));
    }

    public static IEnumerable<object[]> ReconnectInvalid => new[]
    {
        Row("delays=\"\"", "非法"),                                        // 空队列
        Row("delays=\"1000,,2000\"", "存在空项"),                           // 逗号之间没数值
        Row("delays=\"1000,0\"", "必须大于 0"),                             // 含 0
        Row("delays=\"1000,-5\"", "必须大于 0"),                            // 含负数
        Row("delays=\"abc\"", "不是合法整数"),                              // 非整数
        Row("offlineQuality=\"uncertain\"", "offlineQuality 非法值"),       // 只允许 offline|bad
        Row("initialDelayMs=\"1000\"", "initialDelayMs 已删除"),            // 旧属性名（CGV-26）
        Row("maxDelayMs=\"60000\"", "maxDelayMs 已删除"),
        Row("backoff=\"exponential\"", "backoff 已删除"),
        Row("keepAliveMs=\"5000\"", "keepAliveMs 已删除"),
        Row("keepAliveMode=\"app\"", "keepAliveMode 已删除"),
        Row("flushRx=\"true\"", "flushRx 已删除"),
        Row("resetTxn=\"true\"", "resetTxn 已删除"),
        Row("failInFlight=\"true\"", "failInFlight 已删除"),
        Row("resetRetry=\"true\"", "resetRetry 已删除"),
    };

    [Fact]
    public void Gcg_reconnect_delay_count_over_limit_is_rejected()
    {
        // 项数上限 32：33 项直接报错（防止拿配置当内存）
        var many = string.Join(",", Enumerable.Repeat("100", ReconnectOptions.MaxDelayCount + 1));
        var ex = LoadError(Top("<Global><Reconnect delays=\"" + many + "\" /></Global>"));

        Assert.Contains(ex.Errors, e => e.Contains("超过上限"));
    }

    [Fact]
    public void Gcg_reconnect_delay_count_at_limit_is_accepted()
    {
        var many = string.Join(",", Enumerable.Repeat("100", ReconnectOptions.MaxDelayCount));
        var c = WithTop("<Global><Reconnect delays=\"" + many + "\" /></Global>");

        Assert.Equal(ReconnectOptions.MaxDelayCount, G(c).Reconnect.Delays.Count);
    }

    // ─────────────── ④d `Global/Script` 与 `Point/Script`：接线后的正向值与 CGV-32/33 非法值 ───────────────

    [Fact]
    public void Gcg_script_sections_are_wired_into_model()
    {
        // 2026-09-16 恢复脚本引擎（ADR D41）：三段里 Global/Script 与 Point/Script 正式接线，
        // 写了就必须生效（ADR D30）——正向值进模型，行为见 ScriptDecodeTests。
        var c = Load(Base
            + "<Global><Script timeoutMs=\"120\" onError=\"markBad\" /></Global>"
            + "<PointSets><PointSet id=\"ps1\"><Points><Point id=\"p\" address=\"0\">"
            + "<Script language=\"js\" timeoutMs=\"30\">rawValue * 0.1</Script>"
            + "</Point></Points></PointSet></PointSets>");

        Assert.Equal(120, G(c).ScriptTimeoutMs);
        Assert.Equal("markBad", G(c).ScriptOnError);

        var point = P0(c);
        Assert.Equal("rawValue * 0.1", point.Script);
        Assert.Equal("js", point.ScriptLanguage);
        Assert.Equal(30, point.ScriptTimeoutMs);
    }

    [Theory]
    [MemberData(nameof(ScriptInvalid))]
    public void Gcg_invalid_script_is_rejected(string inner, string expectedFragment)
    {
        var ex = LoadError(inner);
        Assert.Contains(ex.Errors, e => e.Contains(expectedFragment));
    }

    public static IEnumerable<object[]> ScriptInvalid => new[]
    {
        Row(Top("<Global><Script language=\"js\" /></Global>"), "不支持 language"),
        Row(Top("<Global><Script timeoutMs=\"0\" /></Global>"), "必须 > 0"),
        Row(Top("<Global><Script onError=\"keepLast\" /></Global>"), "onError"),
        Row(Top("<Global><Script maxMemoryMb=\"8\" /></Global>"), "maxMemoryMb 已删除"),
        Row(PointChild("<Script language=\"csharp\">return 1;</Script>"), "language"),
        Row(PointChild("<Script timeoutMs=\"-1\">return 1;</Script>"), "必须 > 0"),
        Row(PointChild("<Script onError=\"defaultValue\">return 1;</Script>"), "onError"),
        Row(PointChild("<Script> </Script>"), "脚本正文为空"),
    };

    /// <summary>基础配置 + 单点位（带子元素）。</summary>
    private static string PointChild(string child)
        => Base + "<PointSets><PointSet id=\"ps1\"><Points>"
           + "<Point id=\"p\" address=\"0\">" + child + "</Point></Points></PointSet></PointSets>";

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
        yield return C("规则1：Write@permission 指向不存在的角色不报错", WithPoint("access=\"write\"", "<Write permission=\"ghost\" />"),
            c => Assert.Equal("ghost", P0(c).Write!.Permission));
        yield return C("规则3：Command id 重复不报错",
            WithTop("<Commands><Command id=\"c1\"><Steps><Step point=\"p\" /></Steps></Command><Command id=\"c1\"><Steps><Step point=\"p\" /></Steps></Command></Commands>"),
            Absorbed);
    }

    // ── 2026-09-16：加载期全量校验第一步落地，原缺口「规则9/规则10」改为必须报错 ──

    [Fact]
    public void Alarm_low_above_high_is_now_rejected()
    {
        // 修复前：low=90 / high=10 这种互斥阈值被放行（规则10 未落实）
        var ex = Assert.Throws<ConfigValidationException>(() => WithPoint("",
            "<Alarm id=\"h\" type=\"high\" limit=\"10\" /><Alarm id=\"l\" type=\"low\" limit=\"90\" />"));

        var message = string.Join(" | ", ex.Errors);
        Assert.Contains("点位 ps1/p", message);
        Assert.Contains("报警限值大小关系不成立", message);
        Assert.Contains("low=90", message);
        Assert.Contains("high=10", message);
    }

    [Fact]
    public void Calculated_cycle_is_now_rejected()
    {
        // 修复前：c1 引用 c2、c2 引用 c1 的环被放行（规则9 未落实），运行期永远算不出值
        var ex = Assert.Throws<ConfigValidationException>(() => Load(Base + "<PointSets><PointSet id=\"ps1\"><Points /><Calculated>"
            + "<Point id=\"c1\"><Expression>P('c2')</Expression></Point>"
            + "<Point id=\"c2\"><Expression>P('c1')</Expression></Point>"
            + "</Calculated></PointSet></PointSets>"));

        var message = string.Join(" | ", ex.Errors);
        Assert.Contains("计算点循环依赖", message);
        Assert.Contains("c1", message);
        Assert.Contains("c2", message);
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
        var c = Load("<Diagnostics allowRawAccess=\"true\" />"
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

        Assert.Contains(ex.Errors, e => e.Contains("寄存器区的地址组上限 125"));
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
