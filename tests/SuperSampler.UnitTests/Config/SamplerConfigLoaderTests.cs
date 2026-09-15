using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using SuperSampler.Core.Config;
using SuperSampler.Core.Runtime;
using Xunit;

namespace SuperSampler.UnitTests.Config;

/// <summary>配置加载与校验测试：继承、PLC 编址、模板覆盖、已实现校验规则。</summary>
public class SamplerConfigLoaderTests
{
    private static SamplerConfiguration Load(string xml)
        => SamplerConfigLoader.Load(XDocument.Parse(xml), Directory.GetCurrentDirectory());

    [Fact]
    public void Loads_minimal_config()
    {
        var cfg = Load(MINIMAL);
        Assert.Single(cfg.Transports);
        Assert.Single(cfg.Devices);
        Assert.Single(cfg.PointSets);
        Assert.Equal(1, cfg.Devices[0].UnitId);
        Assert.True(cfg.Devices[0].Enabled);
    }

    [Fact]
    public void Plc_address_40001_maps_to_holding_register_0()
    {
        var cfg = Load(MINIMAL);
        // addrFormat=plc 时 40001（1 基 PLC 编址）→ 0 基地址 0；area 不变（v1 缺口见 findings）
        var point = cfg.PointSets[0].Points[0];
        Assert.Equal(0, point.Address);
        Assert.Equal(RuntimeArea.HoldingRegister, point.Area);
    }

    [Fact]
    public void PointSet_defaults_inherit_to_points()
    {
        var cfg = Load(DEFAULTS_TEST);
        var point = cfg.PointSets[0].Points[0];
        // 未显式写 scanGroup 的点继承 Defaults.scanGroup=fast
        Assert.Equal("fast", point.ScanGroup);
    }

    [Fact]
    public void Template_attributes_are_overridden_by_instance()
    {
        var cfg = Load(TEMPLATE_TEST);
        var point = cfg.PointSets[0].Points[0];
        // 模板 dataType=uint32，实例 dataType=float32 覆盖
        Assert.Equal(RuntimeDataType.Float32, point.DataType);
    }

    [Fact]
    public void Duplicate_point_id_in_set_throws()
    {
        var ex = Assert.Throws<ConfigValidationException>(() => Load(DUP_POINT_TEST));
        Assert.Contains(ex.Errors, e => e.Contains("点位 id 重复"));
    }

    [Fact]
    public void Missing_scan_group_reference_throws()
    {
        var ex = Assert.Throws<ConfigValidationException>(() => Load(BAD_SCANGROUP_TEST));
        Assert.Contains(ex.Errors, e => e.Contains("不存在的扫描组"));
    }

    [Fact]
    public void Block_count_over_125_throws()
    {
        var ex = Assert.Throws<ConfigValidationException>(() => Load(BLOCK_TOO_BIG));
        Assert.Contains(ex.Errors, e => e.Contains("寄存器区 125"));
    }

    // ─────────────── 第二轮校验规则（一次触发一条）───────────────

    [Fact]
    public void Cgv8_explicit_length_mismatches_dataType_width()
    {
        var ex = Assert.Throws<ConfigValidationException>(() => Load(CGV8_LENGTH_MISMATCH));
        Assert.Contains(ex.Errors, e => e.Contains("位宽 2 不符"));
    }

    [Fact]
    public void Cgv9_plc_prefix_conflicts_with_area()
    {
        var ex = Assert.Throws<ConfigValidationException>(() => Load(CGV9_PLC_AREA_CONFLICT));
        Assert.Contains(ex.Errors, e => e.Contains("区段") && e.Contains("不一致"));
    }

    [Fact]
    public void Cgv10_readonly_point_with_write_is_rejected()
    {
        var ex = Assert.Throws<ConfigValidationException>(() => Load(CGV10_READ_WRITE_CONFLICT));
        Assert.Contains(ex.Errors, e => e.Contains("但声明了 Write"));
    }

    [Fact]
    public void Cgv13_alarm_priority_must_exist_in_alarm_classes()
    {
        var ex = Assert.Throws<ConfigValidationException>(() => Load(CGV13_UNKNOWN_PRIORITY));
        Assert.Contains(ex.Errors, e => e.Contains("不存在的等级 CRITICAL"));
    }

    [Fact]
    public void Known_alarm_priority_passes()
    {
        var cfg = Load(ALARM_PRIORITY_OK);
        Assert.Single(cfg.PointSets[0].Points[0].Alarms);
    }

    [Fact]
    public void I18n_key_reference_must_exist()
    {
        var ex = Assert.Throws<ConfigValidationException>(() => Load(I18N_MISSING_KEY));
        Assert.Contains(ex.Errors, e => e.Contains("i18n key 不存在：missing.key"));
    }

    [Fact]
    public void Command_step_referencing_unknown_point_throws()
    {
        var ex = Assert.Throws<ConfigValidationException>(() => Load(COMMAND_BAD_POINT));
        Assert.Contains(ex.Errors, e => e.Contains("引用的点位 ghost 不存在或不唯一"));
    }

    [Fact]
    public void Command_step_referencing_existing_point_passes()
    {
        var cfg = Load(COMMAND_GOOD_POINT);
        Assert.Single(cfg.Devices);
    }

    private const string CGV8_LENGTH_MISMATCH = """
        <SamplerConfig schemaVersion="3.0">
          <ScanGroups>
          <ScanGroup id="normal" />
          </ScanGroups>
          <Transports><Transport id="tcp1" host="x" /></Transports>
          <Devices><Device id="d1" transport="tcp1" pointSet="ps1" /></Devices>
          <PointSets>
            <PointSet id="ps1">
              <Points>
                <Point id="p1" address="0" dataType="uint32" length="3" />
              </Points>
            </PointSet>
          </PointSets>
        </SamplerConfig>
        """;

    private const string CGV9_PLC_AREA_CONFLICT = """
        <SamplerConfig schemaVersion="3.0">
          <ScanGroups>
          <ScanGroup id="normal" />
          </ScanGroups>
          <Transports><Transport id="tcp1" host="x" /></Transports>
          <Devices><Device id="d1" transport="tcp1" pointSet="ps1" /></Devices>
          <PointSets>
            <PointSet id="ps1">
              <Points>
                <Point id="p1" address="40001" addrFormat="plc" area="inputregister" />
              </Points>
            </PointSet>
          </PointSets>
        </SamplerConfig>
        """;

    private const string CGV10_READ_WRITE_CONFLICT = """
        <SamplerConfig schemaVersion="3.0">
          <ScanGroups>
          <ScanGroup id="normal" />
          </ScanGroups>
          <Transports><Transport id="tcp1" host="x" /></Transports>
          <Devices><Device id="d1" transport="tcp1" pointSet="ps1" /></Devices>
          <PointSets>
            <PointSet id="ps1">
              <Points>
                <Point id="p1" address="0" access="read">
                  <Write min="0" max="100" />
                </Point>
              </Points>
            </PointSet>
          </PointSets>
        </SamplerConfig>
        """;

    private const string CGV13_UNKNOWN_PRIORITY = """
        <SamplerConfig schemaVersion="3.0">
          <AlarmClasses />
          <ScanGroups>
          <ScanGroup id="normal" />
          </ScanGroups>
          <Transports><Transport id="tcp1" host="x" /></Transports>
          <Devices><Device id="d1" transport="tcp1" pointSet="ps1" /></Devices>
          <PointSets>
            <PointSet id="ps1">
              <Points>
                <Point id="p1" address="0">
                  <Alarm type="high" limit="100" priority="CRITICAL" />
                </Point>
              </Points>
            </PointSet>
          </PointSets>
        </SamplerConfig>
        """;

    private const string ALARM_PRIORITY_OK = """
        <SamplerConfig schemaVersion="3.0">
          <AlarmClasses>
            <AlarmClass id="CRITICAL" />
          </AlarmClasses>
          <ScanGroups>
          <ScanGroup id="normal" />
          </ScanGroups>
          <Transports><Transport id="tcp1" host="x" /></Transports>
          <Devices><Device id="d1" transport="tcp1" pointSet="ps1" /></Devices>
          <PointSets>
            <PointSet id="ps1">
              <Points>
                <Point id="p1" address="0">
                  <Alarm type="high" limit="100" priority="CRITICAL" />
                </Point>
              </Points>
            </PointSet>
          </PointSets>
        </SamplerConfig>
        """;

    private const string I18N_MISSING_KEY = """
        <SamplerConfig schemaVersion="3.0">
          <ScanGroups>
          <ScanGroup id="normal" />
          </ScanGroups>
          <Transports><Transport id="tcp1" host="x" /></Transports>
          <Devices><Device id="d1" transport="tcp1" pointSet="ps1" /></Devices>
          <PointSets>
            <PointSet id="ps1">
              <Points>
                <Point id="p1" address="0" name="温度 ${missing.key} 度" />
              </Points>
            </PointSet>
          </PointSets>
        </SamplerConfig>
        """;

    private const string COMMAND_BAD_POINT = """
        <SamplerConfig schemaVersion="3.0">
          <Commands>
            <Command id="c1">
              <Steps>
                <Step point="ghost" />
              </Steps>
            </Command>
          </Commands>
          <ScanGroups>
          <ScanGroup id="normal" />
          </ScanGroups>
          <Transports><Transport id="tcp1" host="x" /></Transports>
          <Devices><Device id="d1" transport="tcp1" pointSet="ps1" /></Devices>
          <PointSets>
            <PointSet id="ps1">
              <Points>
                <Point id="p1" address="0" />
              </Points>
            </PointSet>
          </PointSets>
        </SamplerConfig>
        """;

    private const string COMMAND_GOOD_POINT = """
        <SamplerConfig schemaVersion="3.0">
          <Commands>
            <Command id="c1">
              <Steps>
                <Step point="d1/p1" />
              </Steps>
            </Command>
          </Commands>
          <ScanGroups>
          <ScanGroup id="normal" />
          </ScanGroups>
          <Transports><Transport id="tcp1" host="x" /></Transports>
          <Devices><Device id="d1" transport="tcp1" pointSet="ps1" /></Devices>
          <PointSets>
            <PointSet id="ps1">
              <Points>
                <Point id="p1" address="0" />
              </Points>
            </PointSet>
          </PointSets>
        </SamplerConfig>
        """;

    private const string MINIMAL = """
        <SamplerConfig schemaVersion="3.0">
          <Global swap="none" />
          <ScanGroups>
          <ScanGroup id="normal" />
          </ScanGroups>
          <Transports>
            <Transport id="tcp1" host="127.0.0.1" />
          </Transports>
          <Devices>
            <Device id="d1" transport="tcp1" pointSet="ps1" unitId="1" />
          </Devices>
          <PointSets>
            <PointSet id="ps1">
              <Points>
                <Point id="p1" address="40001" dataType="uint16" addrFormat="plc" />
              </Points>
            </PointSet>
          </PointSets>
        </SamplerConfig>
        """;

    private const string DEFAULTS_TEST = """
        <SamplerConfig schemaVersion="3.0">
          <ScanGroups>
            <ScanGroup id="fast" rateMs="500" />
          </ScanGroups>
          <Transports>
            <Transport id="tcp1" host="127.0.0.1" />
          </Transports>
          <Devices>
            <Device id="d1" transport="tcp1" pointSet="ps1" />
          </Devices>
          <PointSets>
            <PointSet id="ps1">
              <Defaults scanGroup="fast" dataType="uint32" />
              <Points>
                <Point id="p1" address="0" />
              </Points>
            </PointSet>
          </PointSets>
        </SamplerConfig>
        """;

    private const string TEMPLATE_TEST = """
        <SamplerConfig schemaVersion="3.0">
          <ScanGroups>
          <ScanGroup id="normal" />
          </ScanGroups>
          <Transports>
            <Transport id="tcp1" host="127.0.0.1" />
          </Transports>
          <Devices>
            <Device id="d1" transport="tcp1" pointSet="ps1" />
          </Devices>
          <PointTemplates>
            <Point id="tpl1" dataType="uint32" swap="none" unit="C" />
          </PointTemplates>
          <PointSets>
            <PointSet id="ps1">
              <Points>
                <Point id="p1" template="tpl1" address="0" dataType="float32" />
              </Points>
            </PointSet>
          </PointSets>
        </SamplerConfig>
        """;

    private const string DUP_POINT_TEST = """
        <SamplerConfig schemaVersion="3.0">
          <ScanGroups>
          <ScanGroup id="normal" />
          </ScanGroups>
          <Transports><Transport id="tcp1" host="x" /></Transports>
          <Devices><Device id="d1" transport="tcp1" pointSet="ps1" /></Devices>
          <PointSets>
            <PointSet id="ps1">
              <Points>
                <Point id="p1" address="0" />
                <Point id="p1" address="1" />
              </Points>
            </PointSet>
          </PointSets>
        </SamplerConfig>
        """;

    private const string BAD_SCANGROUP_TEST = """
        <SamplerConfig schemaVersion="3.0">
          <ScanGroups>
          <ScanGroup id="normal" />
          </ScanGroups>
          <Transports><Transport id="tcp1" host="x" /></Transports>
          <Devices><Device id="d1" transport="tcp1" pointSet="ps1" /></Devices>
          <PointSets>
            <PointSet id="ps1">
              <Points>
                <Point id="p1" address="0" scanGroup="ghost" />
              </Points>
            </PointSet>
          </PointSets>
        </SamplerConfig>
        """;

    private const string BLOCK_TOO_BIG = """
        <SamplerConfig schemaVersion="3.0">
          <ScanGroups>
          <ScanGroup id="normal" />
          </ScanGroups>
          <Transports><Transport id="tcp1" host="x" /></Transports>
          <Devices><Device id="d1" transport="tcp1" pointSet="ps1" /></Devices>
          <PointSets>
            <PointSet id="ps1">
              <Blocks>
                <Block id="b1" start="0" count="126" />
              </Blocks>
              <Points />
            </PointSet>
          </PointSets>
        </SamplerConfig>
        """;
}

/// <summary>点位注册表测试：设备/点位/块索引与查询。</summary>
public class PointRegistryTests
{
    private static PointRegistry Registry(string xml)
    {
        var cfg = SamplerConfigLoader.Load(XDocument.Parse(xml), Directory.GetCurrentDirectory());
        return new PointRegistry(cfg);
    }

    [Fact]
    public void Registers_points_and_devices()
    {
        var reg = Registry(XML);
        Assert.NotNull(reg.GetDevice("d1"));
        Assert.NotNull(reg.GetPoint("d1", "p1"));
        Assert.True(reg.TryGetPoint("d1", "p1", out _));
        Assert.False(reg.TryGetPoint("d1", "nope", out _));
    }

    [Fact]
    public void Point_key_is_device_slash_point()
    {
        var reg = Registry(XML);
        var point = reg.GetPoint("d1", "p1");
        Assert.Equal("d1/p1", point.Key);
    }

    [Fact]
    public void Missing_point_throws_key_not_found()
    {
        var reg = Registry(XML);
        Assert.Throws<KeyNotFoundException>(() => reg.GetPoint("d1", "missing"));
    }

    [Fact]
    public void Block_is_registered_and_queryable()
    {
        var reg = Registry(BLOCK_XML);
        var block = reg.GetBlock("d1", "b1");
        Assert.Equal(0, block.Start);
        Assert.Equal(10, block.Count);
    }

    private const string XML = """
        <SamplerConfig schemaVersion="3.0">
          <ScanGroups>
          <ScanGroup id="normal" />
          </ScanGroups>
          <Transports><Transport id="tcp1" host="127.0.0.1" /></Transports>
          <Devices><Device id="d1" transport="tcp1" pointSet="ps1" /></Devices>
          <PointSets>
            <PointSet id="ps1">
              <Points>
                <Point id="p1" address="0" />
              </Points>
            </PointSet>
          </PointSets>
        </SamplerConfig>
        """;

    private const string BLOCK_XML = """
        <SamplerConfig schemaVersion="3.0">
          <ScanGroups>
          <ScanGroup id="normal" />
          </ScanGroups>
          <Transports><Transport id="tcp1" host="127.0.0.1" /></Transports>
          <Devices><Device id="d1" transport="tcp1" pointSet="ps1" /></Devices>
          <PointSets>
            <PointSet id="ps1">
              <Blocks>
                <Block id="b1" start="0" count="10" />
              </Blocks>
              <Points />
            </PointSet>
          </PointSets>
        </SamplerConfig>
        """;
}