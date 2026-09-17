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
/// 第五~七步（位映射展开 / Slices 并入自动分组 / 字符串 padding+left）+ 两个小尾巴
/// （<c>&lt;DependsOn&gt;&lt;PointRef&gt;</c> 成环、<c>bitRange</c> 范围）的**加载期**行为与校验。
/// 规则编号 CGV-27（位映射）、CGV-28（Slices 跨度）、CGV-29（字符串口径）、
/// CGV-30（bitRange 范围）、CGV-31（DependsOn 与跨点表成环）。
/// 运行期行为（轮询取值、子点位可写不报警、Slices 一次请求拼值）见 Runtime/BitMapSliceRuntimeTests。
/// </summary>
public class BitMapSlicesStringTests
{
    private static SamplerConfiguration Load(string pointSetInner, string global = "")
        => SamplerConfigLoader.Load(XDocument.Parse(Wrap(pointSetInner, global)), Directory.GetCurrentDirectory());

    private static ConfigValidationException LoadError(string pointSetInner, string global = "")
        => Assert.Throws<ConfigValidationException>(() => Load(pointSetInner, global));

    private static string Message(ConfigValidationException ex) => string.Join(" | ", ex.Errors);

    private static string Wrap(string pointSetInner, string global = "")
        => "<SamplerConfig schemaVersion=\"3.0\">" + global
           + "<Transports><Transport id=\"tcp1\" host=\"127.0.0.1\" /></Transports>"
           + "<Devices><Device id=\"d1\" transport=\"tcp1\" pointSet=\"ps1\" unitId=\"1\" /></Devices>"
           + "<PointSets><PointSet id=\"ps1\">" + pointSetInner + "</PointSet></PointSets></SamplerConfig>";

    private static string Points(string points) => "<Points>" + points + "</Points>";

    private static PointConfig Find(SamplerConfiguration config, string id)
        => config.PointSets[0].Points.Single(p => p.Id == id);

    /// <summary>状态字点位：bit0 单一位 + bit4-7 位域（带枚举映射）——位映射展开的标准形态。</summary>
    private const string StateWordWithBits =
        "<Point id=\"s\" address=\"3\" dataType=\"uint16\"><Bits>"
        + "<Bit index=\"0\" name=\"running\" text=\"运行\"/>"
        + "<Field from=\"4\" to=\"7\" name=\"alarmCode\" text=\"报警码\">"
        + "<Map><Item key=\"1\">油温过高</Item><Item key=\"2\">模具超温</Item></Map>"
        + "</Field></Bits></Point>";

    // ═══════════════ A. 位映射展开（模型层） ═══════════════

    [Fact]
    public void Cgv27_bits_expand_into_child_points_and_keep_the_integer_point()
    {
        var config = Load(Points(StateWordWithBits));

        // 整数值点位保留（两种都能用）+ 两个子点位
        Assert.Equal(new[] { "s", "s.running", "s.alarmCode" }, config.PointSets[0].Points.Select(p => p.Id).ToArray());

        var word = Find(config, "s");
        Assert.Equal(RuntimeDataType.UInt16, word.DataType);
        Assert.Equal(2, word.Bits!.Count);
        Assert.Null(word.ParentPointId);

        var running = Find(config, "s.running");
        Assert.Equal(RuntimeDataType.Bool, running.DataType);
        Assert.Equal(0, running.Bit);
        Assert.Equal(3, running.Address);                 // 与整字点位同址
        Assert.Equal("运行", running.Name);                // text → 显示名
        Assert.Equal("s", running.ParentPointId);          // 标记展开来源
        Assert.Empty(running.Alarms);                      // 子点位不挂报警（框架只负责取值）

        var alarmCode = Find(config, "s.alarmCode");
        Assert.Equal(RuntimeDataType.UInt16, alarmCode.DataType);
        Assert.Equal("4-7", alarmCode.BitRange);
        Assert.Equal("报警码", alarmCode.Name);
        Assert.Equal("s", alarmCode.ParentPointId);
        Assert.Empty(alarmCode.Alarms);
        Assert.Equal("油温过高", alarmCode.Format!.Map["1"]);
        Assert.Equal("模具超温", alarmCode.Format.Map["2"]);
    }

    [Fact]
    public void Cgv27_bits_on_block_point_expand_into_that_block()
    {
        var config = Load("<Blocks><Block id=\"b1\" area=\"holding\" start=\"0\" count=\"8\">"
                          + StateWordWithBits
                          + "</Block></Blocks><Points />");

        var block = config.PointSets[0].Blocks[0];
        Assert.Equal(new[] { "s", "s.running", "s.alarmCode" }, block.Points.Select(p => p.Id).ToArray());
        Assert.Empty(config.PointSets[0].Points);
    }

    [Fact]
    public void Cgv27_child_point_inherits_writability_from_the_integer_point()
    {
        var config = Load(Points(StateWordWithBits.Replace("dataType=\"uint16\"", "dataType=\"uint16\" access=\"readwrite\"")));

        Assert.True(Find(config, "s").IsWritable);
        Assert.True(Find(config, "s.running").IsWritable);
        Assert.True(Find(config, "s.alarmCode").IsWritable);
    }

    [Fact]
    public void Cgv27_duplicate_bit_between_two_entries_is_rejected()
    {
        var ex = LoadError(Points(
            "<Point id=\"s\" address=\"3\" dataType=\"uint16\"><Bits>"
            + "<Bit index=\"0\" name=\"a\"/><Field from=\"0\" to=\"3\" name=\"b\"/>"
            + "</Bits></Point>"));

        var message = Message(ex);
        Assert.Contains("重复声明了位 0", message);
        Assert.Contains("Bits/Bit index=0", message);     // 位映射展开出来的位声明要标出来源
        Assert.Contains("Bits/Field 0-3", message);
        Assert.Contains("s.a", message);
        Assert.Contains("s.b", message);
    }

    [Fact]
    public void Cgv27_bit_index_beyond_fifteen_is_rejected()
    {
        var ex = LoadError(Points("<Point id=\"s\" address=\"3\"><Bits><Bit index=\"16\" name=\"a\"/></Bits></Point>"));

        Assert.Contains("位索引非法", Message(ex));
        Assert.Contains("s", Message(ex));
    }

    [Fact]
    public void Cgv27_field_from_greater_than_to_is_rejected()
    {
        var ex = LoadError(Points("<Point id=\"s\" address=\"3\"><Bits><Field from=\"7\" to=\"4\" name=\"a\"/></Bits></Point>"));

        Assert.Contains("位索引非法", Message(ex));
        Assert.Contains("Field 7-4", Message(ex));
    }

    [Fact]
    public void Cgv27_child_id_conflicting_with_existing_point_is_rejected()
    {
        var ex = LoadError(Points(
            StateWordWithBits + "<Point id=\"s.running\" address=\"9\" dataType=\"bool\"/>"));

        var message = Message(ex);
        Assert.Contains("子点位 id s.running 与现有点位冲突", message);
        Assert.Contains("位映射展开", message);
    }

    [Fact]
    public void Cgv27_bits_together_with_bitrange_on_same_point_is_rejected()
    {
        var ex = LoadError(Points(
            "<Point id=\"s\" address=\"3\" bitRange=\"4-7\"><Bits><Bit index=\"0\" name=\"a\"/></Bits></Point>"));

        Assert.Contains("已声明 bit/bitRange 但又有 <Bits>", Message(ex));
    }

    [Fact]
    public void Cgv27_bits_on_multiword_point_is_rejected()
    {
        var ex = LoadError(Points(
            "<Point id=\"s\" address=\"3\" dataType=\"uint32\"><Bits><Bit index=\"0\" name=\"a\"/></Bits></Point>"));

        Assert.Contains("只能挂在单字整数点位上", Message(ex));
    }

    // ═══════════════ B. Slices 跨度（CGV-28） ═══════════════

    [Fact]
    public void Cgv28_slice_span_beyond_group_limit_is_rejected_with_span_and_limit()
    {
        var ex = LoadError(Points(
            "<Point id=\"e\" address=\"0\" dataType=\"uint32\">"
            + "<Slices><Slice address=\"0\" length=\"1\"/><Slice address=\"200\" length=\"2\"/></Slices></Point>"));

        var message = Message(ex);
        Assert.Contains("片段太分散", message);
        Assert.Contains("跨度 202", message);              // 最大末地址 202 - 最小地址 0
        Assert.Contains("地址组上限 125", message);         // 默认 groupLimitRegisters
        Assert.Contains("一次请求", message);
    }

    [Fact]
    public void Cgv28_slice_span_within_limit_loads_and_defines_effective_length()
    {
        var config = Load(Points(
            "<Point id=\"e\" address=\"0\" dataType=\"uint32\">"
            + "<Slices><Slice address=\"200\" length=\"1\"/><Slice address=\"202\" length=\"1\"/></Slices></Point>"));

        var point = Find(config, "e");
        Assert.Equal(2, point.Slices!.Count);

        // 有效字长 = 片段长度之和；一次请求要覆盖的范围 = [最小片段地址, 最大片段末地址)
        var runtime = new PointRegistry(config).GetPoint("d1", "e");
        Assert.Equal(2, runtime.Length);
        Assert.Equal(200, runtime.SpanStart);
        Assert.Equal(203, runtime.SpanEnd);
    }

    [Fact]
    public void Cgv28_group_limit_is_configurable_and_checked_against_it()
    {
        // 上限调到 300 → 同样的分散片段必须放行（证明用的是可配置的地址组上限，不是硬编码）
        var config = Load(
            Points("<Point id=\"e\" address=\"0\" dataType=\"uint32\">"
                   + "<Slices><Slice address=\"0\" length=\"1\"/><Slice address=\"200\" length=\"2\"/></Slices></Point>"),
            global: "<Global><Scheduler groupLimitRegisters=\"300\" /></Global>");

        Assert.Equal(2, Find(config, "e").Slices!.Count);
    }

    [Fact]
    public void Cgv28_zero_length_slice_is_rejected()
    {
        var ex = LoadError(Points(
            "<Point id=\"e\" address=\"0\" dataType=\"uint32\">"
            + "<Slices><Slice address=\"10\" length=\"0\"/></Slices></Point>"));

        Assert.Contains("片段长度必须大于 0", Message(ex));
    }

    // ═══════════════ C. 字符串口径（CGV-29） ═══════════════

    [Fact]
    public void Cgv29_string_padding_and_left_are_parsed()
    {
        var config = Load(Points("<Point id=\"str\" address=\"0\" dataType=\"string\" length=\"4\">"
                                 + "<String encoding=\"ascii\" padding=\"0x20\" left=\"false\" trimNull=\"false\"/></Point>"));

        var point = Find(config, "str");
        Assert.Equal(0x20, point.StringPadding);
        Assert.False(point.StringPadLeft);
        Assert.False(point.StringTrimNull);
    }

    [Fact]
    public void Cgv29_string_defaults_are_null_padding_left_aligned()
    {
        var config = Load(Points("<Point id=\"str\" address=\"0\" dataType=\"string\" length=\"4\"/>"));
        var point = Find(config, "str");

        Assert.Equal(0x00, point.StringPadding);
        Assert.True(point.StringPadLeft);
        Assert.True(point.StringTrimNull);
    }

    [Theory]
    [InlineData("0x100")]
    [InlineData("256")]
    [InlineData("-1")]
    [InlineData("abc")]
    public void Cgv29_illegal_padding_is_rejected(string padding)
    {
        var ex = LoadError(Points("<Point id=\"str\" address=\"0\" dataType=\"string\" length=\"4\">"
                                  + "<String padding=\"" + padding + "\"/></Point>"));

        Assert.Contains("padding 非法值", Message(ex));
        Assert.Contains("str", Message(ex));
    }

    [Fact]
    public void Cgv29_non_boolean_left_is_rejected()
    {
        var ex = LoadError(Points("<Point id=\"str\" address=\"0\" dataType=\"string\" length=\"4\">"
                                  + "<String left=\"yes\"/></Point>"));

        Assert.Contains("left", Message(ex));
        Assert.Contains("不是合法数值或布尔值", Message(ex));
    }

    [Fact]
    public void Cgv29_per_byte_layout_is_not_implemented_and_rejected()
    {
        var ex = LoadError(Points("<Point id=\"str\" address=\"0\" dataType=\"string\" length=\"4\">"
                                  + "<String perByte=\"true\"/></Point>"));

        Assert.Contains("perByte 未实现，请勿使用", Message(ex));
    }

    [Fact]
    public void Cgv29_legacy_byte_aligned_is_rejected()
    {
        var ex = LoadError(Points("<Point id=\"str\" address=\"0\" dataType=\"string\" length=\"4\">"
                                  + "<String byteAligned=\"true\"/></Point>"));

        Assert.Contains("byteAligned 未实现，请勿使用", Message(ex));
    }

    // ═══════════════ D1. bitRange 范围（CGV-30） ═══════════════

    [Theory]
    [InlineData("7-4")]
    [InlineData("0-16")]
    [InlineData("-1-3")]
    [InlineData("x-y")]
    [InlineData("4")]
    public void Cgv30_illegal_bitrange_is_rejected(string bitRange)
    {
        var ex = LoadError(Points("<Point id=\"p.range\" address=\"7\" bitRange=\"" + bitRange + "\"/>"));

        Assert.Contains("bitRange", Message(ex));
        Assert.Contains("p.range", Message(ex));
    }

    [Fact]
    public void Cgv30_legal_bitrange_still_loads()
    {
        var config = Load(Points("<Point id=\"p.range\" address=\"7\" bitRange=\"4-7\"/>"));

        Assert.Equal("4-7", Find(config, "p.range").BitRange);
    }

    // ═══════════════ D2. DependsOn 成环与悬空引用（CGV-31） ═══════════════

    [Fact]
    public void Cgv31_depends_on_point_ref_participates_in_cycle_detection()
    {
        // c.a 靠 <DependsOn> 指向 c.b（没有表达式），c.b 用表达式指回 c.a → 必须成环报错
        var ex = LoadError(Points("<Point id=\"p\" address=\"0\"/>")
            + "<Calculated>"
            + "<Point id=\"c.a\"><Script language=\"js\">return 1;</Script>"
            + "<DependsOn><PointRef>c.b</PointRef></DependsOn></Point>"
            + "<Point id=\"c.b\"><Expression>P('c.a') + 1</Expression></Point>"
            + "</Calculated>");

        var message = Message(ex);
        Assert.Contains("计算点循环依赖", message);
        Assert.Contains("c.a", message);
        Assert.Contains("c.b", message);
        Assert.Contains("→", message);
    }

    [Fact]
    public void Cgv31_cross_point_set_cycle_is_rejected_and_named_in_the_ring_path()
    {
        var xml = "<SamplerConfig schemaVersion=\"3.0\">"
                  + "<Transports><Transport id=\"t\" host=\"127.0.0.1\" /></Transports>"
                  + "<Devices><Device id=\"d1\" transport=\"t\" pointSet=\"ps1\" unitId=\"1\" />"
                  + "<Device id=\"d2\" transport=\"t\" pointSet=\"ps2\" unitId=\"2\" /></Devices>"
                  + "<PointSets>"
                  + "<PointSet id=\"ps1\"><Points /><Calculated>"
                  + "<Point id=\"c1\"><DependsOn><PointRef>d2/c2</PointRef></DependsOn></Point>"
                  + "</Calculated></PointSet>"
                  + "<PointSet id=\"ps2\"><Points /><Calculated>"
                  + "<Point id=\"c2\"><Expression>P('d1/c1') + 1</Expression></Point>"
                  + "</Calculated></PointSet>"
                  + "</PointSets></SamplerConfig>";

        var ex = Assert.Throws<ConfigValidationException>(
            () => SamplerConfigLoader.Load(XDocument.Parse(xml), Directory.GetCurrentDirectory()));

        var message = Message(ex);
        Assert.Contains("计算点循环依赖", message);
        Assert.Contains("ps1/c1", message);               // 环路径带点表限定（跨点表也参与判定）
        Assert.Contains("ps2/c2", message);
    }

    [Fact]
    public void Cgv31_dangling_depends_on_reference_is_rejected()
    {
        var ex = LoadError(Points("<Point id=\"p\" address=\"0\"/>")
            + "<Calculated><Point id=\"c.a\"><DependsOn><PointRef>no.such.point</PointRef></DependsOn></Point></Calculated>");

        var message = Message(ex);
        Assert.Contains("DependsOn", message);
        Assert.Contains("引用了不存在的点位 no.such.point", message);
    }

    [Fact]
    public void Cgv31_depends_on_list_is_parsed_and_acyclic_dependency_loads()
    {
        var config = Load(Points("<Point id=\"p\" address=\"0\"/><Point id=\"q\" address=\"1\"/>")
            + "<Calculated>"
            + "<Point id=\"c.a\"><Script language=\"js\">return 1;</Script>"
            + "<DependsOn><PointRef>p</PointRef><PointRef>q</PointRef></DependsOn></Point>"
            + "</Calculated>");

        var calculated = config.PointSets[0].Calculated[0];
        Assert.Equal(new[] { "p", "q" }, calculated.DependsOn.ToArray());
    }
}
