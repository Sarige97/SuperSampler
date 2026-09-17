using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using SuperSampler.Core.Config;
using SuperSampler.Core.Runtime;
using Xunit;

namespace SuperSampler.UnitTests.Config;

/// <summary>
/// SYS-1（docs/07 §5）：工作区里的真实样例配置 <c>Config/上位机配置.v3.xml</c> 必须能加载且 **0 错误**。
/// 该文件在仓库外（属于工作区资产），找不到时显式跳过，避免在纯克隆环境里假失败。
/// 同时锁定两类回归：
/// 1) 数值/布尔属性写错时必须给出「点位 + 属性」定位，而不是只有裸 FormatException 文本；
/// 2) digital 报警不该带 limit（v3 样例曾因此无法加载）。
/// </summary>
public class V3SampleConfigTests
{
    private static string? FindWorkspaceFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    [SkippableFact]
    public void Workspace_v3_sample_config_loads_without_errors()
    {
        var path = FindWorkspaceFile(Path.Combine("Config", "上位机配置.v3.xml"));
        Skip.If(path == null, "工作区资产 Config/上位机配置.v3.xml 不存在（纯克隆环境），跳过");

        var config = SamplerConfigLoader.Load(XDocument.Load(path!), Path.GetDirectoryName(path!)!);

        Assert.NotNull(config);
        Assert.NotEmpty(config.Devices);
        Assert.NotEmpty(config.Transports);
        Assert.NotEmpty(config.PointSets);
        // 允许「尚未实现」类告警（Storage/Ui/Users/Commands 等按 unsupportedPolicy=warn 记录），
        // 但不允许出现别的告警来源——真实样例配置不该有"我没写错但你说不清"的问题
        foreach (var warning in config.Warnings)
        {
            Assert.Contains("尚未实现", warning);
        }
    }

    [SkippableFact]
    public void Workspace_v3_sample_config_expands_bits_and_keeps_slices_semantics()
    {
        var path = FindWorkspaceFile(Path.Combine("Config", "上位机配置.v3.xml"));
        Skip.If(path == null, "工作区资产 Config/上位机配置.v3.xml 不存在（纯克隆环境），跳过");

        var config = SamplerConfigLoader.Load(XDocument.Load(path!), Path.GetDirectoryName(path!)!);
        var set = config.PointSets.Single(s => s.Id == "Mold01_Points");
        var all = set.Points.Concat(set.Blocks.SelectMany(b => b.Points)).ToList();

        // 位映射示例：整字点位保留 + 展开出的子点位（mold.stateWord.running / .alarmCode）
        Assert.Contains(all, p => p.Id == "mold.stateWord");
        Assert.Contains(all, p => p.Id == "mold.stateWord.running" && p.Bit == 0);
        Assert.Contains(all, p => p.Id == "mold.stateWord.alarmCode" && p.BitRange == "4-7");
        Assert.All(all.Where(p => p.ParentPointId != null), p => Assert.Empty(p.Alarms));

        // Slices 示例：累计电能拆两段 → 有效字长 = 片段长度之和、一次请求范围覆盖两段
        var energy = all.Single(p => p.Id == "mold.energyTotal");
        Assert.Equal(2, energy.Slices!.Count);
        var runtime = new PointRegistry(config).GetPoint("InjectMoldMachine01", "mold.energyTotal");
        Assert.Equal(2, runtime.Length);
        Assert.Equal(200, runtime.SpanStart);
        Assert.Equal(203, runtime.SpanEnd);

        // 字符串补齐口径示例：空格补齐 + 靠左
        var model = all.Single(p => p.Id == "mold.modelName");
        Assert.Equal(0x20, model.StringPadding);
        Assert.True(model.StringPadLeft);
    }

    [Fact]
    public void Malformed_numeric_attribute_names_the_point_and_attribute()
    {
        var xml = """
            <SamplerConfig schemaVersion="3.0">
              <Transports><Transport id="tcp1" host="127.0.0.1" /></Transports>
              <Devices><Device id="d1" transport="tcp1" pointSet="ps1" /></Devices>
              <PointSets><PointSet id="ps1"><Points>
                <Point id="bad.alarm" address="0" dataType="bool"><Alarm type="digital" limit="true" /></Point>
              </Points></PointSet></PointSets>
            </SamplerConfig>
            """;

        var ex = Assert.Throws<ConfigValidationException>(
            () => SamplerConfigLoader.Load(XDocument.Parse(xml), Directory.GetCurrentDirectory()));

        var message = string.Join(" | ", ex.Errors);
        Assert.Contains("bad.alarm", message);   // 点位定位
        Assert.Contains("limit", message);       // 属性定位
    }

    [Fact]
    public void Digital_alarm_without_limit_loads_fine()
    {
        var xml = """
            <SamplerConfig schemaVersion="3.0">
              <Transports><Transport id="tcp1" host="127.0.0.1" /></Transports>
              <Devices><Device id="d1" transport="tcp1" pointSet="ps1" /></Devices>
              <PointSets><PointSet id="ps1"><Points>
                <Point id="ok.alarm" address="0" dataType="bool"><Alarm type="digital" latch="true" ackRequired="true" /></Point>
              </Points></PointSet></PointSets>
            </SamplerConfig>
            """;

        var config = SamplerConfigLoader.Load(XDocument.Parse(xml), Directory.GetCurrentDirectory());
        var point = config.PointSets.Single().Points.Single();
        Assert.Equal("digital", point.Alarms.Single().Type);
    }
}