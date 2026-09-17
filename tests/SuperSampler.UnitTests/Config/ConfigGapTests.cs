using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using SuperSampler.Core.Config;
using SuperSampler.Core.Runtime;
using Xunit;

namespace SuperSampler.UnitTests.Config;

/// <summary>
/// G-CG 族补测（docs/07 测试计划 §3.2）：PLC 编址三区换算、模板覆盖非叠加、
/// (transport,unitId) 查重、块内覆盖规则、Defaults 链 + i18n 代入。
/// </summary>
public class ConfigGapTests
{
    private static SamplerConfiguration Load(string xml)
        => SamplerConfigLoader.Load(XDocument.Parse(xml), Directory.GetCurrentDirectory());

    // ─────────────── G-CG-1：PLC 编址四区换算 ───────────────

    [Fact]
    public void Gcg1_plc_addresses_convert_all_four_areas_once()
    {
        var cfg = Load(GCG1_PLC_AREAS);
        var points = cfg.PointSets[0].Points;

        var holding = points.Single(p => p.Id == "ph");
        Assert.Equal(RuntimeArea.HoldingRegister, holding.Area);
        Assert.Equal(99, holding.Address); // 40100 → 0 基 99，只换算一次

        var input = points.Single(p => p.Id == "pi");
        Assert.Equal(RuntimeArea.InputRegister, input.Area);
        Assert.Equal(99, input.Address); // 30100 → 99

        var discrete = points.Single(p => p.Id == "pd");
        Assert.Equal(RuntimeArea.DiscreteInput, discrete.Area);
        Assert.Equal(99, discrete.Address); // 10100 → 99

        var coil = points.Single(p => p.Id == "pc");
        Assert.Equal(RuntimeArea.Coil, coil.Area);
        Assert.Equal(99, coil.Address); // 100 → 99
    }

    const string GCG1_PLC_AREAS = """
        <SamplerConfig schemaVersion="3.0">
          <Transports><Transport id="tcp1" host="x" /></Transports>
          <Devices><Device id="d1" transport="tcp1" pointSet="ps1" /></Devices>
          <PointSets>
            <PointSet id="ps1">
              <Points>
                <Point id="ph" address="40100" addrFormat="plc" dataType="uint16" />
                <Point id="pi" address="30100" addrFormat="plc" area="input" dataType="uint16" />
                <Point id="pd" address="10100" addrFormat="plc" area="discrete" />
                <Point id="pc" address="100" addrFormat="plc" area="coil" />
              </Points>
            </PointSet>
          </PointSets>
        </SamplerConfig>
        """;

    // ─────────────── G-CG-2：模板继承是覆盖不是叠加 ───────────────

    [Fact]
    public void Gcg2_instance_overrides_one_attribute_others_retained()
    {
        var cfg = Load(GCG2_TEMPLATE_RETAIN);
        var point = cfg.PointSets[0].Points[0];
        // 实例只覆盖 swap；模板的 dataType 与 unit 保留
        Assert.Equal(SwapMode.Byte, point.Swap);
        Assert.Equal(RuntimeDataType.UInt32, point.DataType);
        Assert.Equal("C", point.Unit);
    }

    const string GCG2_TEMPLATE_RETAIN = """
        <SamplerConfig schemaVersion="3.0">
          <Transports><Transport id="tcp1" host="x" /></Transports>
          <Devices><Device id="d1" transport="tcp1" pointSet="ps1" /></Devices>
          <PointTemplates>
            <Point id="tpl1" dataType="uint32" swap="none" unit="C" />
          </PointTemplates>
          <PointSets>
            <PointSet id="ps1">
              <Points>
                <Point id="p1" template="tpl1" address="0" swap="byte" />
              </Points>
            </PointSet>
          </PointSets>
        </SamplerConfig>
        """;

    // ─────────────── G-CG-3：(transport, unitId) 查重 ───────────────

    [Fact]
    public void Gcg3_duplicate_transport_unitid_throws()
    {
        var ex = Assert.Throws<ConfigValidationException>(() => Load(GCG3_DUP_SLAVE));
        Assert.Contains(ex.Errors, e => e.Contains("声明了相同的 (transport=tcp1, unitId=1)"));
    }

    [Fact]
    public void Gcg3_disabled_device_does_not_conflict()
    {
        var cfg = Load(GCG3_DUP_SLAVE_DISABLED);
        Assert.Equal(2, cfg.Devices.Count);
    }

    const string GCG3_DUP_SLAVE = """
        <SamplerConfig schemaVersion="3.0">
          <Transports><Transport id="tcp1" host="x" /></Transports>
          <Devices>
            <Device id="d1" transport="tcp1" pointSet="ps1" unitId="1" />
            <Device id="d2" transport="tcp1" pointSet="ps1" unitId="1" />
          </Devices>
          <PointSets>
            <PointSet id="ps1"><Points><Point id="p1" address="0" /></Points></PointSet>
          </PointSets>
        </SamplerConfig>
        """;

    const string GCG3_DUP_SLAVE_DISABLED = """
        <SamplerConfig schemaVersion="3.0">
          <Transports><Transport id="tcp1" host="x" /></Transports>
          <Devices>
            <Device id="d1" transport="tcp1" pointSet="ps1" unitId="1" />
            <Device id="d2" transport="tcp1" pointSet="ps1" unitId="1" enabled="false" />
          </Devices>
          <PointSets>
            <PointSet id="ps1"><Points><Point id="p1" address="0" /></Points></PointSet>
          </PointSets>
        </SamplerConfig>
        """;

    // ─────────────── G-CG-4：块内覆盖规则 ───────────────

    [Fact]
    public void Gcg4_block_point_overriding_area_throws()
    {
        var ex = Assert.Throws<ConfigValidationException>(() => Load(GCG4_OVERRIDE_AREA));
        Assert.Contains(ex.Errors, e => e.Contains("不得覆盖 area"));
    }

    [Fact]
    public void Gcg4_block_point_may_set_swap_and_inherits_when_absent()
    {
        // 点位显式 swap 允许（D13）；未写 swap 时取块的 swap
        var cfg = Load(GCG4_SWAP_OK);
        Assert.Equal(SwapMode.Byte, cfg.PointSets[0].Blocks[0].Points.Single(p => p.Id == "p2").Swap);
        Assert.Equal(SwapMode.Word, cfg.PointSets[0].Blocks[0].Points.Single(p => p.Id == "p1").Swap);
    }

    const string GCG4_OVERRIDE_AREA = """
        <SamplerConfig schemaVersion="3.0">
          <Transports><Transport id="tcp1" host="x" /></Transports>
          <Devices><Device id="d1" transport="tcp1" pointSet="ps1" /></Devices>
          <PointSets>
            <PointSet id="ps1">
              <Blocks>
                <Block id="b1" start="0" count="10">
                  <Point id="p1" address="0" area="coil" />
                </Block>
              </Blocks>
              <Points />
            </PointSet>
          </PointSets>
        </SamplerConfig>
        """;

    const string GCG4_SWAP_OK = """
        <SamplerConfig schemaVersion="3.0">
          <Transports><Transport id="tcp1" host="x" /></Transports>
          <Devices><Device id="d1" transport="tcp1" pointSet="ps1" /></Devices>
          <PointSets>
            <PointSet id="ps1">
              <Blocks>
                <Block id="b1" start="0" count="10" swap="word">
                  <Point id="p1" address="0" />
                  <Point id="p2" address="1" swap="byte" />
                </Block>
              </Blocks>
              <Points />
            </PointSet>
          </PointSets>
        </SamplerConfig>
        """;

    // ─────────────── G-CG-5：Defaults 链 + ${i18n} 代入 ───────────────

    [Fact]
    public void Gcg5_defaults_chain_and_i18n_substitution()
    {
        // 准备 i18n 资源文件（加载器从 baseDirectory 相对解析）
        var i18nPath = Path.Combine(Directory.GetCurrentDirectory(), "test_gcg5.i18n");
        File.WriteAllText(i18nPath, "PT_NAME=主温度\nUNIT_C=℃\n# 注释行\n");
        try
        {
            var cfg = Load(GCG5_I18N_DEFAULTS);
            var point = cfg.PointSets[0].Points[0];
            // ${KEY} 在加载期代入；Defaults 的 dataType 链式继承（节奏不在 Defaults 里，见 CGV-25）
            Assert.Equal("主温度", point.Name);
            Assert.Equal("℃", point.Unit);
            Assert.Equal(RuntimeDataType.Float32, point.DataType);
            Assert.Equal(500, point.IntervalMs);   // 间隔由点位自己声明
            Assert.Equal("auto", point.Mode);
        }
        finally
        {
            File.Delete(i18nPath);
        }
    }

    const string GCG5_I18N_DEFAULTS = """
        <SamplerConfig schemaVersion="3.0">
          <I18n>
            <Files><File path="test_gcg5.i18n" /></Files>
          </I18n>
          <Transports><Transport id="tcp1" host="x" /></Transports>
          <Devices><Device id="d1" transport="tcp1" pointSet="ps1" /></Devices>
          <PointSets>
            <PointSet id="ps1">
              <Defaults dataType="float32" />
              <Points>
                <Point id="p1" address="0" name="${PT_NAME}" unit="${UNIT_C}" intervalMs="500" />
              </Points>
            </PointSet>
          </PointSets>
        </SamplerConfig>
        """;

    // ─────────────── D20（ADR D34）：BCD / datetime 的有效字长按类型参数推导 ───────────────

    private static RuntimePoint Runtime(string pointXml)
    {
        var cfg = Load(POINT_TEMPLATE.Replace("{POINT}", pointXml));
        return new RuntimePoint(cfg.PointSets[0].Points[0], cfg.Devices[0]);
    }

    private const string POINT_TEMPLATE = """
        <SamplerConfig schemaVersion="3.0">
          <Transports><Transport id="tcp1" host="x" /></Transports>
          <Devices><Device id="d1" transport="tcp1" pointSet="ps1" /></Devices>
          <PointSets><PointSet id="ps1"><Points>{POINT}</Points></PointSet></PointSets>
        </SamplerConfig>
        """;

    [Theory]
    [InlineData(4, 1)]     // 默认 digits=4
    [InlineData(1, 1)]
    [InlineData(5, 2)]     // 每寄存器 4 位十进制，不足一寄存器向上取整
    [InlineData(8, 2)]     // 电能表 8 位读数
    [InlineData(9, 3)]
    [InlineData(12, 3)]
    public void Gcg6_bcd_word_length_follows_digits(int digits, int expectedWords)
    {
        var point = Runtime("<Point id=\"p\" address=\"0\" dataType=\"bcd\"><Bcd digits=\"" + digits + "\" /></Point>");

        Assert.Equal(expectedWords, point.Length);
    }

    [Theory]
    [InlineData("plc6", 6)]     // 年/月/日/时/分/秒
    [InlineData("plc4", 4)]     // 年/月/日/时
    [InlineData("unixsec", 2)]
    [InlineData("unixms", 4)]   // 毫秒必超 32 位，故 4 字（64 位）
    public void Gcg6b_datetime_word_length_follows_format(string format, int expectedWords)
    {
        var point = Runtime("<Point id=\"p\" address=\"0\" dataType=\"datetime\"><DateTime format=\"" + format + "\" /></Point>");

        Assert.Equal(expectedWords, point.Length);
    }

    [Fact]
    public void Gcg6b2_unknown_datetime_format_is_rejected_at_load()
    {
        // CGV-37（findings D69 家族）：未知 DateTime@format 此前**静默按 plc6 解**——
        // 4 字的 unixsec 设备被当成 6 字读，量纲完全错。现在加载期直接报错。
        var ex = Assert.Throws<ConfigValidationException>(() => Load(POINT_TEMPLATE.Replace("{POINT}",
            "<Point id=\"p\" address=\"0\" dataType=\"datetime\"><DateTime format=\"custom\" /></Point>")));

        Assert.Contains("format=\"custom\" 非法", string.Join(" | ", ex.Errors));
    }

    [Fact]
    public void Gcg6c_explicit_length_consistent_with_derivation_is_accepted()
    {
        // 推导值 = 2，显式写 2 不再被 CGV-8 拒绝（并且不会退回 1 字）
        var point = Runtime("<Point id=\"p\" address=\"0\" dataType=\"bcd\" length=\"2\"><Bcd digits=\"8\" /></Point>");

        Assert.Equal(2, point.Length);
    }

    [Fact]
    public void Gcg6d_explicit_length_contradicting_derivation_is_rejected()
    {
        var ex = Assert.Throws<ConfigValidationException>(() => Load(POINT_TEMPLATE.Replace("{POINT}",
            "<Point id=\"p\" address=\"0\" dataType=\"bcd\" length=\"1\"><Bcd digits=\"8\" /></Point>")));

        Assert.Contains(ex.Errors, e => e.Contains("位宽 2 不符"));
    }

    // ─────────────── D38（ADR D38）：swap 兜底链的解析期口径 ───────────────

    [Theory]
    [InlineData("<Point id=\"p\" address=\"0\" swap=\"byte\" />", true, SwapMode.Byte)]                       // Point@swap
    [InlineData("<Point id=\"p\" address=\"0\" />", false, SwapMode.Word)]                                    // 待设备/全局兜底
    public void Gcg7_point_level_swap_marks_declared(string pointXml, bool declared, SwapMode expected)
    {
        var cfg = Load(POINT_TEMPLATE.Replace("{POINT}", pointXml));
        var point = cfg.PointSets[0].Points[0];

        Assert.Equal(expected, point.Swap);
        Assert.Equal(declared, point.HasSwapDeclared);
    }

    [Fact]
    public void Gcg7b_defaults_swap_counts_as_declared()
    {
        var cfg = Load("""
            <SamplerConfig schemaVersion="3.0">
              <Transports><Transport id="tcp1" host="x" /></Transports>
              <Devices><Device id="d1" transport="tcp1" pointSet="ps1" /></Devices>
              <PointSets><PointSet id="ps1"><Defaults swap="badc" />
                <Points><Point id="p" address="0" /></Points>
              </PointSet></PointSets>
            </SamplerConfig>
            """);
        var point = cfg.PointSets[0].Points[0];

        Assert.Equal(SwapMode.Byte, point.Swap);
        Assert.True(point.HasSwapDeclared);   // 点表缺省也是显式声明，设备级不再覆盖它
    }

    [Fact]
    public void Gcg7c_global_swap_is_read_and_is_the_last_resort()
    {
        var cfg = Load("""
            <SamplerConfig schemaVersion="3.0">
              <Global swap="dcba" nullText="N/A" />
              <Transports><Transport id="tcp1" host="x" /></Transports>
              <Devices><Device id="d1" transport="tcp1" pointSet="ps1" /></Devices>
              <PointSets><PointSet id="ps1"><Points><Point id="p" address="0" /></Points></PointSet></PointSets>
            </SamplerConfig>
            """);

        Assert.Equal(SwapMode.WordByte, cfg.Global.DefaultSwap);
        Assert.Equal("N/A", cfg.Global.NullText);
        // 点位未声明 → 运行期兜底到设备/全局（设备也没写 → Global.DefaultSwap）
        var point = new RuntimePoint(cfg.PointSets[0].Points[0], cfg.Devices[0]);
        Assert.Equal(SwapMode.WordByte, point.Swap);
    }
}
