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
          <ScanGroups><ScanGroup id="normal" /></ScanGroups>
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
          <ScanGroups><ScanGroup id="normal" /></ScanGroups>
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
          <ScanGroups><ScanGroup id="normal" /></ScanGroups>
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
          <ScanGroups><ScanGroup id="normal" /></ScanGroups>
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
          <ScanGroups><ScanGroup id="normal" /></ScanGroups>
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
          <ScanGroups><ScanGroup id="normal" /></ScanGroups>
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
            // ${KEY} 在加载期代入；Defaults 的 dataType/scanGroup 链式继承
            Assert.Equal("主温度", point.Name);
            Assert.Equal("℃", point.Unit);
            Assert.Equal(RuntimeDataType.Float32, point.DataType);
            Assert.Equal("fast", point.ScanGroup);
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
          <ScanGroups><ScanGroup id="fast" rateMs="500" /></ScanGroups>
          <Transports><Transport id="tcp1" host="x" /></Transports>
          <Devices><Device id="d1" transport="tcp1" pointSet="ps1" /></Devices>
          <PointSets>
            <PointSet id="ps1">
              <Defaults scanGroup="fast" dataType="float32" />
              <Points>
                <Point id="p1" address="0" name="${PT_NAME}" unit="${UNIT_C}" />
              </Points>
            </PointSet>
          </PointSets>
        </SamplerConfig>
        """;
}
