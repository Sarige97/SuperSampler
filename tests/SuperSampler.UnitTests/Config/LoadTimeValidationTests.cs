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
/// 加载期全量校验（docs/11 §二，采集范围改造第一步）。
/// 每条新增规则至少一个「故意写错」的用例：断言抛 <see cref="ConfigValidationException"/>
/// 且错误文案能定位到**点位/属性**与**原因关键词**（不是只断言"抛了异常"）；
/// 另配反向用例（合法配置不报错）与「多个错误一次报出」用例。
/// 规则编号见 Config/配置字段说明.md 第 16 节。
/// </summary>
public class LoadTimeValidationTests
{
    private static SamplerConfiguration Load(string xml)
        => SamplerConfigLoader.Load(XDocument.Parse(xml), Directory.GetCurrentDirectory());

    private static ConfigValidationException LoadError(string xml)
        => Assert.Throws<ConfigValidationException>(() => Load(xml));

    /// <summary>把 message 单行错误合并成便于断言的字符串。</summary>
    private static string Message(ConfigValidationException ex) => string.Join(" | ", ex.Errors);

    private const string Base =
        "<Transports><Transport id=\"tcp1\" host=\"127.0.0.1\" /></Transports>"
        + "<Devices><Device id=\"d1\" transport=\"tcp1\" pointSet=\"ps1\" /></Devices>";

    /// <summary>基础配置（Global 可选）+ 点表内容。</summary>
    private static string Wrap(string pointSetInner, string global = "")
        => "<SamplerConfig schemaVersion=\"3.0\">" + global + Base
           + "<PointSets><PointSet id=\"ps1\">" + pointSetInner + "</PointSet></PointSets></SamplerConfig>";

    private static string Points(string points) => "<Points>" + points + "</Points>";

    private static string Block(string blockAttrs, string points)
        => "<Blocks><Block id=\"b1\" " + blockAttrs + ">" + points + "</Block></Blocks><Points />";

    // ═══════════════ CGV-16：块内点位地址不连续（无法用一次请求覆盖）═══════════════

    [Fact]
    public void Cgv16_block_point_hole_is_rejected_with_block_and_points()
    {
        var ex = LoadError(Wrap(Block("area=\"holding\" start=\"0\" count=\"16\"",
            "<Point id=\"p.zero\" address=\"0\" />"
            + "<Point id=\"p.two\" address=\"2\" />")));

        var message = Message(ex);
        Assert.Contains("块内点位地址不连续", message);
        Assert.Contains("Block b1", message);         // 哪个块
        Assert.Contains("PointSet ps1", message);     // 哪个点表
        Assert.Contains("p.zero", message);           // 断点前一个点位
        Assert.Contains("p.two", message);            // 断点后一个点位
        Assert.Contains("1 个地址空洞", message);      // 原因：中间空了 1 个地址
    }

    [Fact]
    public void Cgv16_block_point_hole_is_accepted_when_within_ignore_gap()
    {
        // 忽略间隔数（Global/Scheduler@ignoreGap）= 1：中间空一个地址仍算“能一次请求覆盖”
        var cfg = Load(Wrap(
            Block("area=\"holding\" start=\"0\" count=\"16\"",
                "<Point id=\"p.zero\" address=\"0\" />"
                + "<Point id=\"p.two\" address=\"2\" />"),
            global: "<Global><Scheduler ignoreGap=\"1\" /></Global>"));

        Assert.Equal(2, cfg.PointSets[0].Blocks[0].Points.Count);
    }

    [Fact]
    public void Cgv16_contiguous_block_points_are_accepted()
    {
        // 反向用例 1：块内点位严格连续（含多字点位跨 1..2）→ 不报错
        var cfg = Load(Wrap(Block("area=\"holding\" start=\"0\" count=\"16\"",
            "<Point id=\"p.a\" address=\"0\" />"
            + "<Point id=\"p.b\" address=\"1\" dataType=\"uint32\" />"
            + "<Point id=\"p.c\" address=\"3\" />")));

        Assert.Equal(new[] { "p.a", "p.b", "p.c" }, cfg.PointSets[0].Blocks[0].Points.Select(p => p.Id));
    }

    [Fact]
    public void Cgv16_overlapping_block_points_are_not_a_hole()
    {
        // 反向用例 1b：整字 raw 包住内部寄存器（地址重叠）不算空洞
        var cfg = Load(Wrap(Block("area=\"holding\" start=\"0\" count=\"4\"",
            "<Point id=\"blk.raw\" address=\"0\" dataType=\"raw\" length=\"4\" />"
            + "<Point id=\"blk.u16\" address=\"2\" />")));

        Assert.Equal(2, cfg.PointSets[0].Blocks[0].Points.Count);
    }

    // ═══════════════ CGV-17：同一寄存器同一位被重复声明 ═══════════════

    [Fact]
    public void Cgv17_duplicate_bit_on_same_register_is_rejected()
    {
        var ex = LoadError(Wrap(Points(
            "<Point id=\"p.bit0a\" address=\"10\" dataType=\"bool\" bit=\"0\" />"
            + "<Point id=\"p.bit0b\" address=\"10\" dataType=\"bool\" bit=\"0\" />")));

        var message = Message(ex);
        Assert.Contains("重复声明了位 0", message);
        Assert.Contains("地址 10", message);       // 哪个地址
        Assert.Contains("p.bit0a", message);       // 哪两个点位
        Assert.Contains("p.bit0b", message);
        Assert.Contains("bit=0", message);         // 哪个属性
    }

    [Fact]
    public void Cgv17_overlapping_bit_ranges_are_rejected()
    {
        var ex = LoadError(Wrap(Points(
            "<Point id=\"p.rangeA\" address=\"7\" bitRange=\"4-7\" />"
            + "<Point id=\"p.rangeB\" address=\"7\" bitRange=\"6-9\" />")));

        var message = Message(ex);
        Assert.Contains("p.rangeA", message);
        Assert.Contains("p.rangeB", message);
        Assert.Contains("位 6-7", message);        // 相交的位区间
        Assert.Contains("bitRange=4-7", message);
        Assert.Contains("bitRange=6-9", message);
    }

    [Fact]
    public void Cgv17_single_bit_overlapping_bit_range_is_rejected()
    {
        var ex = LoadError(Wrap(Points(
            "<Point id=\"p.one\" address=\"3\" dataType=\"bool\" bit=\"5\" />"
            + "<Point id=\"p.many\" address=\"3\" bitRange=\"4-7\" />")));

        Assert.Contains("位 5", Message(ex));
    }

    [Fact]
    public void Cgv17_same_address_different_bits_are_accepted()
    {
        // 反向用例 2：同址多位不重叠（含整字点位与位点位并存）→ 现有设计，不许误报
        var cfg = Load(Wrap(Points(
            "<Point id=\"p.word\" address=\"10\" />"
            + "<Point id=\"p.bit0\" address=\"10\" dataType=\"bool\" bit=\"0\" />"
            + "<Point id=\"p.bit1\" address=\"10\" dataType=\"bool\" bit=\"1\" />"
            + "<Point id=\"p.bits4to7\" address=\"10\" bitRange=\"4-7\" />"
            + "<Point id=\"p.bits8to15\" address=\"10\" bitRange=\"8-15\" />")));

        Assert.Equal(5, cfg.PointSets[0].Points.Count);
    }

    [Fact]
    public void Cgv17_same_bit_on_different_registers_is_accepted()
    {
        var cfg = Load(Wrap(Points(
            "<Point id=\"p.a\" address=\"10\" dataType=\"bool\" bit=\"0\" />"
            + "<Point id=\"p.b\" address=\"11\" dataType=\"bool\" bit=\"0\" />")));

        Assert.Equal(2, cfg.PointSets[0].Points.Count);
    }

    // ═══════════════ CGV-18：地址 + 长度越出区容量（65536）═══════════════

    [Fact]
    public void Cgv18_address_plus_length_beyond_area_capacity_is_rejected()
    {
        var ex = LoadError(Wrap(Points(
            "<Point id=\"p.edge\" address=\"65535\" dataType=\"uint32\" />")));

        var message = Message(ex);
        Assert.Contains("p.edge", message);                 // 哪个点位
        Assert.Contains("越出", message);
        Assert.Contains("65536", message);                  // 容量
        Assert.Contains("65535 + 2 = 65537", message);      // 地址 + 有效长度
    }

    [Fact]
    public void Cgv18_last_register_of_area_is_accepted()
    {
        var cfg = Load(Wrap(Points("<Point id=\"p.last\" address=\"65535\" dataType=\"uint16\" />")));

        Assert.Equal(65535, cfg.PointSets[0].Points[0].Address);
    }

    [Fact]
    public void Cgv18_negative_address_is_rejected()
    {
        var ex = LoadError(Wrap(Points("<Point id=\"p.neg\" address=\"-1\" />")));

        Assert.Contains("p.neg", Message(ex));
        Assert.Contains("地址非法", Message(ex));
    }

    [Fact]
    public void Cgv18_slice_beyond_capacity_is_rejected()
    {
        var ex = LoadError(Wrap(Points(
            "<Point id=\"p.slices\" address=\"0\" dataType=\"raw\">"
            + "<Slices><Slice address=\"65530\" length=\"8\" /></Slices>"
            + "</Point>")));

        var message = Message(ex);
        Assert.Contains("p.slices", message);
        Assert.Contains("Slices/Slice", message);
        Assert.Contains("65530", message);
    }

    // ═══════════════ CGV-15：计算点循环依赖 ═══════════════

    [Fact]
    public void Cgv15_direct_cycle_reports_the_ring_path()
    {
        var ex = LoadError(Wrap("<Points /><Calculated>"
            + "<Point id=\"c.a\"><Expression>P('c.b')</Expression></Point>"
            + "<Point id=\"c.b\"><Expression>P('c.a')</Expression></Point>"
            + "</Calculated>"));

        var message = Message(ex);
        Assert.Contains("计算点循环依赖", message);
        Assert.Contains("c.a", message);
        Assert.Contains("c.b", message);
        Assert.Contains("→", message);              // 环路径 A → B → A
    }

    [Fact]
    public void Cgv15_indirect_cycle_is_rejected()
    {
        var ex = LoadError(Wrap("<Points /><Calculated>"
            + "<Point id=\"c.a\"><Expression>P('c.b') + 1</Expression></Point>"
            + "<Point id=\"c.b\"><Expression>P('c.c') + 1</Expression></Point>"
            + "<Point id=\"c.c\"><Expression>P('c.a') + 1</Expression></Point>"
            + "</Calculated>"));

        var message = Message(ex);
        Assert.Contains("计算点循环依赖", message);
        Assert.Contains("c.a", message);
        Assert.Contains("c.b", message);
        Assert.Contains("c.c", message);
    }

    [Fact]
    public void Cgv15_self_reference_is_rejected()
    {
        var ex = LoadError(Wrap("<Points /><Calculated>"
            + "<Point id=\"c.self\"><Expression>P('c.self')</Expression></Point>"
            + "</Calculated>"));

        Assert.Contains("计算点循环依赖", Message(ex));
        Assert.Contains("c.self", Message(ex));
    }

    [Fact]
    public void Cgv15_acyclic_calculated_chain_is_accepted()
    {
        // 反向用例：链式依赖（不成环）必须放行
        var cfg = Load(Wrap(Points(
            "<Point id=\"p.t\" address=\"0\" dataType=\"int16\" />"
            + "<Point id=\"p.s\" address=\"1\" dataType=\"int16\" />")
            + "<Calculated>"
            + "<Point id=\"c.diff\"><Expression>P('p.t') - P('p.s')</Expression></Point>"
            + "<Point id=\"c.abs\"><Expression>P('c.diff') + 1</Expression></Point>"
            + "</Calculated>"));

        Assert.Equal(2, cfg.PointSets[0].Calculated.Count);
    }

    // ═══════════════ CGV-14：报警限值大小关系 ═══════════════

    [Fact]
    public void Cgv14_high_not_below_highHigh_is_rejected()
    {
        var ex = LoadError(Wrap(Points(
            "<Point id=\"p.temp\" address=\"0\" dataType=\"int16\">"
            + "<Alarm id=\"h\" type=\"high\" limit=\"90\" />"
            + "<Alarm id=\"hh\" type=\"highHigh\" limit=\"80\" />"
            + "</Point>")));

        var message = Message(ex);
        Assert.Contains("p.temp", message);                    // 哪个点位
        Assert.Contains("报警限值大小关系不成立", message);
        Assert.Contains("high=90", message);
        Assert.Contains("highHigh=80", message);
    }

    [Fact]
    public void Cgv14_lowLow_not_below_low_is_rejected()
    {
        var ex = LoadError(Wrap(Points(
            "<Point id=\"p.temp\" address=\"0\" dataType=\"int16\">"
            + "<Alarm type=\"lowLow\" limit=\"-5\" />"
            + "<Alarm type=\"low\" limit=\"-5\" />"
            + "</Point>")));

        var message = Message(ex);
        Assert.Contains("lowLow=-5", message);
        Assert.Contains("low=-5", message);
        Assert.Contains("lowLow < low", message);
    }

    [Fact]
    public void Cgv14_low_not_below_high_is_rejected()
    {
        var ex = LoadError(Wrap(Points(
            "<Point id=\"p.temp\" address=\"0\" dataType=\"int16\">"
            + "<Alarm type=\"low\" limit=\"90\" />"
            + "<Alarm type=\"high\" limit=\"10\" />"
            + "</Point>")));

        Assert.Contains("low=90", Message(ex));
        Assert.Contains("high=10", Message(ex));
    }

    [Fact]
    public void Cgv14_ordered_limits_are_accepted()
    {
        // 反向用例：lowLow < low < high < highHigh 是合法配置
        var cfg = Load(Wrap(Points(
            "<Point id=\"p.temp\" address=\"0\" dataType=\"int16\">"
            + "<Alarm type=\"lowLow\" limit=\"-10\" />"
            + "<Alarm type=\"low\" limit=\"0\" />"
            + "<Alarm type=\"high\" limit=\"100\" />"
            + "<Alarm type=\"highHigh\" limit=\"200\" />"
            + "</Point>")));

        Assert.Equal(4, cfg.PointSets[0].Points[0].Alarms.Count);
    }

    [Fact]
    public void Cgv14_digital_alarm_without_limit_is_ignored()
    {
        var cfg = Load(Wrap(Points(
            "<Point id=\"p.flag\" address=\"0\" dataType=\"bool\">"
            + "<Alarm type=\"digital\" latch=\"true\" />"
            + "</Point>")));

        Assert.Single(cfg.PointSets[0].Points[0].Alarms);
    }

    // ═══════════════ 一次性报全（不是遇到第一个就停）═══════════════

    [Fact]
    public void Multiple_contradictions_are_reported_in_one_load()
    {
        var ex = LoadError(Wrap(
            Block("area=\"holding\" start=\"0\" count=\"16\"",
                "<Point id=\"p.zero\" address=\"0\" />"
                + "<Point id=\"p.two\" address=\"2\" />")
            + Points(
                "<Point id=\"p.bitA\" address=\"20\" dataType=\"bool\" bit=\"0\" />"
                + "<Point id=\"p.bitB\" address=\"20\" dataType=\"bool\" bit=\"0\" />"
                + "<Point id=\"p.edge\" address=\"65535\" dataType=\"uint32\" />"
                + "<Point id=\"p.alarm\" address=\"30\" dataType=\"int16\">"
                + "<Alarm type=\"lowLow\" limit=\"-5\" /><Alarm type=\"low\" limit=\"-5\" />"
                + "</Point>")));

        // 四条不同规则的问题在同一次加载里全部报出，且每条都带定位
        Assert.True(ex.Errors.Count >= 4, "一次加载必须报出全部矛盾，实际：" + Environment.NewLine + Message(ex));

        Assert.Contains(ex.Errors, e => e.Contains("块内点位地址不连续") && e.Contains("Block b1") && e.Contains("p.two"));
        Assert.Contains(ex.Errors, e => e.Contains("重复声明了位 0") && e.Contains("p.bitA") && e.Contains("p.bitB"));
        Assert.Contains(ex.Errors, e => e.Contains("越出") && e.Contains("p.edge"));
        Assert.Contains(ex.Errors, e => e.Contains("报警限值大小关系不成立") && e.Contains("p.alarm"));
    }

    [Fact]
    public void Parse_errors_and_rule_errors_are_collected_together()
    {
        // 旧实现：解析期撞上第一处非法数值就以 FormatException 中断，后面的矛盾一条都看不到
        var ex = LoadError(Wrap(Points(
            "<Point id=\"p.badint\" address=\"10\" bit=\"x\" />"
            + "<Point id=\"p.badbool\" address=\"11\" enabled=\"maybe\" />"
            + "<Point id=\"p.bitA\" address=\"12\" dataType=\"bool\" bit=\"0\" />"
            + "<Point id=\"p.bitB\" address=\"12\" dataType=\"bool\" bit=\"0\" />")));

        var message = Message(ex);
        Assert.Contains("p.badint", message);        // 点位 + 属性定位
        Assert.Contains("bit", message);
        Assert.Contains("p.badbool", message);
        Assert.Contains("enabled", message);
        Assert.Contains("重复声明了位 0", message);    // 解析错误之后照常继续收集
    }

    [Fact]
    public void Device_attribute_error_does_not_stop_other_checks()
    {
        var ex = Assert.Throws<ConfigValidationException>(() => Load(
            "<SamplerConfig schemaVersion=\"3.0\">" + Base.Replace(
                "<Device id=\"d1\" transport=\"tcp1\" pointSet=\"ps1\" />",
                "<Device id=\"d1\" transport=\"tcp1\" pointSet=\"ps1\" unitId=\"one\" />")
            + "<PointSets><PointSet id=\"ps1\">" + Points(
                "<Point id=\"p.bitA\" address=\"12\" dataType=\"bool\" bit=\"0\" />"
                + "<Point id=\"p.bitB\" address=\"12\" dataType=\"bool\" bit=\"0\" />")
            + "</PointSet></PointSets></SamplerConfig>"));

        var message = Message(ex);
        Assert.Contains("Device d1", message);
        Assert.Contains("unitId", message);
        Assert.Contains("重复声明了位 0", message);
    }

    [Fact]
    public void Malformed_plc_address_does_not_stop_point_set_validation()
    {
        // 旧实现：CGV-9 复查 PLC 前缀时对同一属性再取一次值，遇到 address="abc" 直接抛，
        // 整个 PointSets 段（含同表其它点位的矛盾）全部丢失
        var ex = LoadError(Wrap(Points(
            "<Point id=\"p.plc\" address=\"abc\" addrFormat=\"plc\" area=\"input\" />"
            + "<Point id=\"p.bitA\" address=\"12\" dataType=\"bool\" bit=\"0\" />"
            + "<Point id=\"p.bitB\" address=\"12\" dataType=\"bool\" bit=\"0\" />")));

        var message = Message(ex);
        Assert.Contains("p.plc", message);                     // 地址本身报错（带定位）
        Assert.Contains("address", message);
        Assert.Contains("重复声明了位 0", message);              // 后面的规则照常跑
        Assert.DoesNotContain(ex.Errors, e => e.Contains("PointSets："));   // 段级兜底没被触发
    }

    [Fact]
    public void Malformed_generateDiagnostics_is_reported_once_without_killing_the_stage()
    {
        var ex = Assert.Throws<ConfigValidationException>(() => Load(
            "<SamplerConfig schemaVersion=\"3.0\">" + Base.Replace(
                "<Device id=\"d1\" transport=\"tcp1\" pointSet=\"ps1\" />",
                "<Device id=\"d1\" transport=\"tcp1\" pointSet=\"ps1\" generateDiagnostics=\"maybe\" />")
            + "<Storage><History enabled=\"true\" /></Storage>"
            + "<PointSets><PointSet id=\"ps1\">" + Points("<Point id=\"p1\" address=\"0\" />")
            + "</PointSet></PointSets></SamplerConfig>"));

        Assert.Single(ex.Errors, e => e.Contains("generateDiagnostics"));       // 只报一次，且带定位
        Assert.DoesNotContain(ex.Errors, e => e.Contains("未实现段检测"));       // 未实现段检测段没被异常打断
    }

    // ═══════════════ 两阶段：零错误才交付可用配置，运行时对象只认已校验配置 ═══════════════

    [Fact]
    public void Loaded_config_is_marked_validated()
    {
        var cfg = Load(Wrap(Points("<Point id=\"p1\" address=\"0\" />")));

        Assert.True(cfg.IsValidated);
    }

    [Fact]
    public void Failed_load_throws_with_all_errors_instead_of_returning_a_config()
    {
        var ex = LoadError(Wrap(Points("<Point id=\"p.edge\" address=\"65535\" dataType=\"uint32\" />")));

        // 校验不通过就没有可用配置对象 → 宿主不可能据此构造运行时对象
        Assert.NotEmpty(ex.Errors);
        Assert.Contains("配置校验失败", ex.Message);
    }

    [Fact]
    public void Engine_rejects_config_that_did_not_pass_validation()
    {
        // 宿主手工拼的半成品配置（IsValidated 只可能由加载器置位）必须被挡在构造之前
        var halfBaked = new SamplerConfiguration();

        var ex = Assert.Throws<InvalidOperationException>(() => new SamplerEngine(halfBaked));

        Assert.Contains("未通过加载期全量校验", ex.Message);
    }

    [Fact]
    public void Registry_rejects_config_that_did_not_pass_validation()
    {
        var halfBaked = new SamplerConfiguration();

        var ex = Assert.Throws<InvalidOperationException>(() => new PointRegistry(halfBaked));

        Assert.Contains("未通过加载期全量校验", ex.Message);
    }

    [Fact]
    public void Engine_accepts_config_that_passed_validation()
    {
        var cfg = Load(Wrap(Points("<Point id=\"p1\" address=\"0\" />")));

        using var engine = new SamplerEngine(cfg);

        Assert.Equal("--", engine.GetValue("d1", "p1"));   // 未采集 → 坏值占位（Global@nullText 默认）
    }

    [Fact]
    public void Malformed_xml_file_is_reported_as_config_error()
    {
        var path = Path.Combine(Path.GetTempPath(), "ss_badxml_" + Guid.NewGuid().ToString("N") + ".xml");
        File.WriteAllText(path, "<SamplerConfig schemaVersion=\"3.0\"><Broken>");
        try
        {
            var ex = Assert.Throws<ConfigValidationException>(() => SamplerConfigLoader.LoadFromXml(path));

            Assert.Contains("不是合法 XML", Message(ex));
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ═══════════════ CGV-19：间隔与地址组上限非法 ═══════════════
    // 调度节拍 50ms 也是最小间隔：小于它（含 ≤0）即报错；否则会在运行期被静默夹住。

    [Fact]
    public void Cgv19_point_interval_below_tick_is_rejected()
    {
        var ex = LoadError(Wrap(Points("<Point id=\"p\" address=\"0\" intervalMs=\"49\" />")));

        var message = Message(ex);
        Assert.Contains("intervalMs=49", message);
        Assert.Contains("最小 50ms", message);
    }

    [Fact]
    public void Cgv19_zero_interval_is_rejected()
    {
        var ex = LoadError(Wrap(Points("<Point id=\"p\" address=\"0\" intervalMs=\"0\" />")));

        Assert.Contains("intervalMs=0", Message(ex));
    }

    [Fact]
    public void Cgv19_block_interval_below_tick_is_rejected()
    {
        var ex = LoadError(Wrap(Block("start=\"0\" count=\"4\" intervalMs=\"10\"", "<Point id=\"p1\" address=\"0\" />")));

        var message = Message(ex);
        Assert.Contains("Block ps1/b1", message);
        Assert.Contains("intervalMs=10", message);
    }

    [Fact]
    public void Cgv19_global_default_interval_below_tick_is_rejected()
    {
        var ex = LoadError(Wrap(Points("<Point id=\"p\" address=\"0\" />"),
            global: "<Global><Polling defaultIntervalMs=\"20\" /></Global>"));

        Assert.Contains("defaultIntervalMs=20", Message(ex));
    }

    [Fact]
    public void Cgv19_non_positive_group_limit_is_rejected()
    {
        var ex = LoadError(Wrap(Points("<Point id=\"p\" address=\"0\" />"),
            global: "<Global><Scheduler groupLimitRegisters=\"0\" groupLimitBits=\"-1\" /></Global>"));

        var message = Message(ex);
        Assert.Contains("groupLimitRegisters=0", message);
        Assert.Contains("groupLimitBits=-1", message);
    }

    [Fact]
    public void Cgv19_min_interval_50_is_accepted()
    {
        // 边界：正好 50ms（一个调度节拍）必须放行
        var cfg = Load(Wrap(Points("<Point id=\"p\" address=\"0\" intervalMs=\"50\" />")));

        Assert.Equal(50, cfg.PointSets[0].Points[0].IntervalMs);
    }

    // ═══════════════ CGV-20：块内点位不得写 intervalMs / mode ═══════════════
    // 块统一决定这一段的节奏；块内点位自带节奏会被静默忽略，故直接报错。

    [Fact]
    public void Cgv20_block_point_interval_is_rejected()
    {
        var ex = LoadError(Wrap(Block("start=\"0\" count=\"4\"",
            "<Point id=\"p1\" address=\"0\" intervalMs=\"500\" />")));

        var message = Message(ex);
        Assert.Contains("不得写 intervalMs", message);
        Assert.Contains("p1", message);
    }

    [Fact]
    public void Cgv20_block_point_mode_is_rejected()
    {
        var ex = LoadError(Wrap(Block("start=\"0\" count=\"4\"",
            "<Point id=\"p1\" address=\"0\" mode=\"onDemand\" />")));

        var message = Message(ex);
        Assert.Contains("不得写 mode", message);
        Assert.Contains("p1", message);
    }

    [Fact]
    public void Cgv20_template_interval_carried_into_block_point_is_rejected()
    {
        // 点位自己没写 intervalMs，但模板带了：合并后同样算「块内点位自带节奏」
        var ex = LoadError("<SamplerConfig schemaVersion=\"3.0\">" + Base
            + "<PointTemplates><Point id=\"tpl\" intervalMs=\"500\" /></PointTemplates>"
            + "<PointSets><PointSet id=\"ps1\">"
            + Block("start=\"0\" count=\"4\"", "<Point id=\"p1\" address=\"0\" template=\"tpl\" />")
            + "</PointSet></PointSets></SamplerConfig>");

        Assert.Contains("不得写 intervalMs", Message(ex));
    }

    [Fact]
    public void Cgv20_contradictions_are_reported_together()
    {
        // 一次报全：块内点位的 intervalMs 与 mode 两条错误都要出现
        var ex = LoadError(Wrap(Block("start=\"0\" count=\"4\"",
            "<Point id=\"p1\" address=\"0\" intervalMs=\"500\" mode=\"once\" />")));

        var message = Message(ex);
        Assert.Contains("不得写 intervalMs", message);
        Assert.Contains("不得写 mode", message);
    }

    // ═══════════════ CGV-21：once 不得同时写 intervalMs ═══════════════

    [Fact]
    public void Cgv21_once_point_with_interval_is_rejected()
    {
        var ex = LoadError(Wrap(Points("<Point id=\"p\" address=\"0\" mode=\"once\" intervalMs=\"500\" />")));

        var message = Message(ex);
        Assert.Contains("mode=\"once\" 不得同时写 intervalMs", message);
        Assert.Contains("点位 ps1/p", message);
    }

    [Fact]
    public void Cgv21_once_block_with_interval_is_rejected()
    {
        var ex = LoadError(Wrap(Block("start=\"0\" count=\"4\" mode=\"once\" intervalMs=\"500\"",
            "<Point id=\"p1\" address=\"0\" />")));

        Assert.Contains("mode=\"once\" 不得同时写 intervalMs", Message(ex));
    }

    [Fact]
    public void Cgv21_once_without_interval_and_auto_with_interval_are_accepted()
    {
        // 边界：once 不带间隔、auto 带间隔都合法
        var cfg = Load(Wrap(Points("<Point id=\"once\" address=\"0\" mode=\"once\" />"
            + "<Point id=\"auto\" address=\"1\" mode=\"auto\" intervalMs=\"500\" />"
            + "<Point id=\"od\" address=\"2\" mode=\"onDemand\" />"
            + "<Point id=\"plain\" address=\"3\" />")));

        var points = cfg.PointSets[0].Points;
        Assert.Null(points.Single(p => p.Id == "once").IntervalMs);
        Assert.Equal(500, points.Single(p => p.Id == "auto").IntervalMs);
        Assert.Equal("ondemand", points.Single(p => p.Id == "od").Mode);
        Assert.Null(points.Single(p => p.Id == "plain").IntervalMs);
    }
}
