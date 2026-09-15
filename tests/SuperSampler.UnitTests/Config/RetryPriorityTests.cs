using System;
using System.IO;
using System.Xml.Linq;
using SuperSampler.Core.Config;
using Xunit;

namespace SuperSampler.UnitTests.Config;

/// <summary>
/// 重试参数三级优先级：设备 &lt;Retry&gt; &gt; 链路 &lt;Retry&gt; &gt; Global/Retry（findings W5/W14 接线）。
/// 解析在加载期折算进 DeviceConfig，运行时按设备取（SamplerEngine.RetryCount）。
/// </summary>
public class RetryPriorityTests
{
    private static SamplerConfiguration Cfg(string global = "", string transport = "", string device = "")
        => SamplerConfigLoader.Load(
            XDocument.Parse(
                "<HostConfig schemaVersion=\"3.0\">"
                + global
                + "<ScanGroups><ScanGroup id=\"normal\" /></ScanGroups>"
                + "<Transports><Transport id=\"tcp1\" host=\"x\">" + transport + "</Transport></Transports>"
                + "<PointSets><PointSet id=\"ps1\"><Points><Point id=\"p\" address=\"0\" /></Points></PointSet></PointSets>"
                + "<Devices><Device id=\"d1\" transport=\"tcp1\" pointSet=\"ps1\">" + device + "</Device></Devices>"
                + "</HostConfig>"),
            Directory.GetCurrentDirectory());

    private static DeviceConfig D0(SamplerConfiguration c) => c.Devices[0];

    [Fact]
    public void Global_retry_applies_when_nothing_else_is_declared()
    {
        var c = Cfg(global: "<Global><Retry count=\"7\" intervalMs=\"700\" /></Global>");

        Assert.Equal(7, D0(c).RetryCount);
        Assert.Equal(700, D0(c).RetryIntervalMs);
    }

    [Fact]
    public void Transport_retry_overrides_global()
    {
        var c = Cfg(
            global: "<Global><Retry count=\"7\" intervalMs=\"700\" /></Global>",
            transport: "<Retry count=\"6\" intervalMs=\"600\" />");

        Assert.Equal(6, D0(c).RetryCount);
        Assert.Equal(600, D0(c).RetryIntervalMs);
    }

    [Fact]
    public void Device_retry_overrides_transport_and_global()
    {
        var c = Cfg(
            global: "<Global><Retry count=\"7\" intervalMs=\"700\" /></Global>",
            transport: "<Retry count=\"6\" intervalMs=\"600\" />",
            device: "<Retry count=\"5\" intervalMs=\"500\" />");

        Assert.Equal(5, D0(c).RetryCount);
        Assert.Equal(500, D0(c).RetryIntervalMs);
    }

    [Fact]
    public void Device_retry_overrides_only_the_attribute_it_declares()
    {
        var c = Cfg(
            global: "<Global><Retry count=\"7\" intervalMs=\"700\" /></Global>",
            device: "<Retry count=\"5\" />");

        Assert.Equal(5, D0(c).RetryCount);
        Assert.Equal(700, D0(c).RetryIntervalMs); // intervalMs 未写 → 沿用 Global
    }

    [Fact]
    public void Device_template_retry_participates_in_priority()
    {
        var c = SamplerConfigLoader.Load(
            XDocument.Parse(
                "<HostConfig schemaVersion=\"3.0\">"
                + "<Global><Retry count=\"7\" intervalMs=\"700\" /></Global>"
                + "<ScanGroups><ScanGroup id=\"normal\" /></ScanGroups>"
                + "<Transports><Transport id=\"tcp1\" host=\"x\" /></Transports>"
                + "<DeviceTemplates><Device id=\"tpl\" transport=\"tcp1\" pointSet=\"ps1\"><Retry count=\"4\" intervalMs=\"400\" /></Device></DeviceTemplates>"
                + "<PointSets><PointSet id=\"ps1\"><Points><Point id=\"p\" address=\"0\" /></Points></PointSet></PointSets>"
                + "<Devices><Device id=\"d1\" template=\"tpl\" /></Devices>"
                + "</HostConfig>"),
            Directory.GetCurrentDirectory());

        Assert.Equal(4, D0(c).RetryCount);
        Assert.Equal(400, D0(c).RetryIntervalMs);
    }

    [Fact]
    public void Transport_retry_block_without_count_keeps_transport_default()
    {
        // 已记录的语义细节：链路 <Retry> 一旦出现即整体接管（HasRetryOverride），
        // 未写的 count 取链路默认 2，而不是回落 Global。
        var c = Cfg(
            global: "<Global><Retry count=\"7\" intervalMs=\"700\" /></Global>",
            transport: "<Retry intervalMs=\"600\" />");

        Assert.Equal(2, D0(c).RetryCount);
        Assert.Equal(600, D0(c).RetryIntervalMs);
    }
}
