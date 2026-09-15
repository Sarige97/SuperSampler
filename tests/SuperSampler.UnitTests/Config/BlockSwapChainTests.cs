using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using SuperSampler.Core.Config;
using SuperSampler.Core.Runtime;
using Xunit;

namespace SuperSampler.UnitTests.Config;

/// <summary>
/// D27 / ADR D38：块内点位的 swap 兜底链必须是
/// `Point@swap > Block@swap（仅块显式声明）> PointSet/Defaults@swap > Device@swap > Global@swap`。
/// 回归背景：块未声明 swap 时曾强塞 word（CDAB），把块内所有多字点位解错
/// （累计电能读成天文数字、float64 读成 -7e-197），宿主实测才暴露。
/// </summary>
public class BlockSwapChainTests
{
    private const string ConfigTemplate = """
        <SamplerConfig schemaVersion="3.0">
          <ScanGroups><ScanGroup id="normal" /></ScanGroups>
          <Transports><Transport id="tcp1" host="127.0.0.1" /></Transports>
          <Devices><Device id="d1" __DEV__ /></Devices>
          __SET__
        </SamplerConfig>
        """;

    private static SamplerConfiguration Load(string pointSetXml,
        string deviceAttrs = "transport=\"tcp1\" pointSet=\"ps1\"")
        => SamplerConfigLoader.Load(
            XDocument.Parse(ConfigTemplate.Replace("__DEV__", deviceAttrs).Replace("__SET__", pointSetXml)),
            Directory.GetCurrentDirectory());

    private const string BlockNoSwapNoDefaults = """
        <PointSets><PointSet id="ps1">
          <Blocks><Block id="b1" area="holding" start="0" count="8">
            <Point id="p32" address="0" dataType="uint32" />
          </Block></Blocks>
        </PointSet></PointSets>
        """;

    [Fact]
    public void Block_without_swap_does_not_declare_swap_for_its_points()
    {
        var cfg = Load(BlockNoSwapNoDefaults);
        var point = cfg.PointSets.Single().Blocks.Single().Points.Single();  // 块内点位挂在 Block.Points

        // 未声明 → 交给运行期按 Device → Global 兜底
        Assert.False(point.HasSwapDeclared);
    }

    [Fact]
    public void Pointset_defaults_win_over_undeclared_block()
    {
        var cfg = Load("""
            <PointSets><PointSet id="ps1">
              <Defaults swap="none" />
              <Blocks><Block id="b1" area="holding" start="0" count="8">
                <Point id="p32" address="0" dataType="uint32" />
              </Block></Blocks>
            </PointSet></PointSets>
            """);

        var point = cfg.PointSets.Single().Blocks.Single().Points.Single();  // 块内点位挂在 Block.Points
        var runtime = new RuntimePoint(point, cfg.Devices.Single());

        Assert.True(point.HasSwapDeclared);              // Defaults 声明了
        Assert.Equal(SwapMode.None, runtime.Swap);       // 且必须是 none，不能是块缺省的 word
    }

    [Fact]
    public void Explicit_block_swap_applies_to_block_points()
    {
        var cfg = Load("""
            <PointSets><PointSet id="ps1">
              <Defaults swap="none" />
              <Blocks><Block id="b1" area="holding" start="0" count="8" swap="word">
                <Point id="p32" address="0" dataType="uint32" />
              </Block></Blocks>
            </PointSet></PointSets>
            """);

        var point = cfg.PointSets.Single().Blocks.Single().Points.Single();  // 块内点位挂在 Block.Points
        var runtime = new RuntimePoint(point, cfg.Devices.Single());

        Assert.True(point.HasSwapDeclared);
        Assert.Equal(SwapMode.Word, runtime.Swap);       // 窄作用域（块显式）优先于点表缺省
    }

    [Fact]
    public void Point_declared_swap_still_beats_block_swap()
    {
        var cfg = Load("""
            <PointSets><PointSet id="ps1">
              <Blocks><Block id="b1" area="holding" start="0" count="8" swap="word">
                <Point id="p32" address="0" dataType="uint32" swap="byte" />
              </Block></Blocks>
            </PointSet></PointSets>
            """);

        var runtime = new RuntimePoint(cfg.PointSets.Single().Blocks.Single().Points.Single(), cfg.Devices.Single());
        Assert.Equal(SwapMode.Byte, runtime.Swap);
    }

    [Fact]
    public void Device_swap_applies_when_nothing_narrower_is_declared()
    {
        // 块与点表都没声明 → 落到设备级（ADR D38 运行期解析）
        var cfg = Load(BlockNoSwapNoDefaults, "transport=\"tcp1\" pointSet=\"ps1\" swap=\"byte\"");

        var runtime = new RuntimePoint(cfg.PointSets.Single().Blocks.Single().Points.Single(), cfg.Devices.Single());
        Assert.Equal(SwapMode.Byte, runtime.Swap);
    }

    // ─────────────── D28：Scale@mode="linear" 不该被当成未实现告警 ───────────────

    [Fact]
    public void Scale_mode_linear_is_not_reported_as_unimplemented()
    {
        var cfg = Load("""
            <PointSets><PointSet id="ps1"><Points>
              <Point id="p" address="0" dataType="float32"><Scale mode="linear" factor="2" /></Point>
            </Points></PointSet></PointSets>
            """);

        Assert.DoesNotContain(cfg.Warnings, w => w.Contains("Scale@mode"));
    }

    [Fact]
    public void Scale_mode_twopoint_is_still_reported()
    {
        var cfg = Load("""
            <PointSets><PointSet id="ps1"><Points>
              <Point id="p" address="0" dataType="float32"><Scale mode="twopoint" factor="2" /></Point>
            </Points></PointSet></PointSets>
            """);

        Assert.Contains(cfg.Warnings, w => w.Contains("Scale@mode") && w.Contains("twopoint"));
    }
}