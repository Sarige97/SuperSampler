using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using SuperSampler.Abstractions.Events;
using SuperSampler.Abstractions.Values;
using SuperSampler.Core.Config;
using SuperSampler.Core.Events;
using SuperSampler.Core.Runtime;
using SuperSampler.UnitTests.Support;
using Xunit;

namespace SuperSampler.UnitTests.Config;

/// <summary>
/// 报警的加载期校验：CGV-14 限值互斥的正反例定位，以及 findings D68~D72 收口后的完整规则
/// （未知 type / 限值型缺 limit+非有限 limit / 负 deadband / 负 delayMs / 同点位报警键冲突 /
/// 计算点不得挂报警）——全部在加载期报错，见 `Config/SamplerConfigLoader.Validation.cs`（CGV-36）。
/// </summary>
public class AlarmLoadValidationTests
{
    private const string Template = """
        <SamplerConfig schemaVersion="3.0">
          <Transports><Transport id="tcp1" host="127.0.0.1" /></Transports>
          <Devices><Device id="d1" transport="tcp1" pointSet="ps1" /></Devices>
          <PointSets><PointSet id="ps1"><Points>{0}</Points></PointSet></PointSets>
        </SamplerConfig>
        """;

    private static SamplerConfiguration Load(string points, string topLevel = "")
        => SamplerConfigLoader.Load(XDocument.Parse(
            Template.Replace("{0}", points).Replace("</SamplerConfig>", topLevel + "</SamplerConfig>")),
            Directory.GetCurrentDirectory());

    private static string ErrorsOf(string points, string topLevel = "")
    {
        var ex = Assert.Throws<ConfigValidationException>(() => Load(points, topLevel));
        return string.Join(" | ", ex.Errors);
    }

    // ═══════════════ CGV-14 正例 ═══════════════

    [Fact]
    public void Four_threshold_alarms_in_the_right_order_load_cleanly()
    {
        var cfg = Load("""
            <Point id="p" address="0">
              <Alarm id="ll" type="lowLow" limit="-10" />
              <Alarm id="l" type="low" limit="0" />
              <Alarm id="h" type="high" limit="50" />
              <Alarm id="hh" type="highHigh" limit="80" />
            </Point>
            """);

        var alarms = cfg.PointSets.Single().Points.Single().Alarms;
        Assert.Equal(new[] { "lowlow", "low", "high", "highhigh" }, alarms.Select(a => a.Type).ToArray());
    }

    // ═══════════════ CGV-14 反例：三条互斥关系各自可定位 ═══════════════

    [Theory]
    [InlineData("""<Alarm id="l" type="low" limit="90" /><Alarm id="h" type="high" limit="10" />""", "low=90", "high=10")]
    [InlineData("""<Alarm id="h" type="high" limit="90" /><Alarm id="hh" type="highHigh" limit="10" />""", "high=90", "highHigh=10")]
    [InlineData("""<Alarm id="ll" type="lowLow" limit="10" /><Alarm id="l" type="low" limit="5" />""", "lowLow=10", "low=5")]
    public void Contradictory_threshold_relations_are_rejected_at_load_with_locators(string alarms,
        string lowerText, string upperText)
    {
        var message = ErrorsOf($"""<Point id="p" address="0">{alarms}</Point>""");

        Assert.Contains("点位 ps1/p", message);
        Assert.Contains("报警限值大小关系不成立", message);
        Assert.Contains(lowerText, message);
        Assert.Contains(upperText, message);
    }

    [Fact]
    public void Equal_thresholds_are_also_rejected()
    {
        var message = ErrorsOf("""<Point id="p" address="0"><Alarm id="h" type="high" limit="10" /><Alarm id="hh" type="highHigh" limit="10" /></Point>""");

        Assert.Contains("报警限值大小关系不成立", message);
    }

    [Fact]
    public void Alarm_priority_referencing_a_missing_alarm_class_is_rejected_at_load()
    {
        var message = ErrorsOf("""<Point id="p" address="0"><Alarm id="h" type="high" limit="10" priority="P9" /></Point>""");

        Assert.Contains("不存在的等级 P9", message);
    }

    // ═══════════════ 加载期规则收口（findings D68~D71，CGV-36）═══════════════
    //
    // 这一组此前是「零错误放行 + 运行期静默失效/假清除」，本轮全部改为加载期报错——
    // 报警是「按配置保护设备」的语义，写错一条等于少一条保护，绝不能留到运行期才发现。

    [Theory]
    [InlineData("high", "limit=\"10\"")]
    [InlineData("highHigh", "limit=\"10\"")]
    [InlineData("low", "limit=\"-10\"")]
    [InlineData("lowLow", "limit=\"-10\"")]
    [InlineData("digital", "")]
    public void Every_valid_alarm_type_loads_cleanly(string type, string limit)
    {
        var cfg = Load($"""<Point id="p" address="0"><Alarm id="a" type="{type}" {limit} /></Point>""");

        Assert.Equal(type.ToLowerInvariant(), cfg.PointSets.Single().Points.Single().Alarms.Single().Type);
    }

    [Fact]
    public void An_unknown_alarm_type_is_rejected_at_load()
    {
        // findings D69：`type="foo"` 曾经零错误通过，运行期 AlarmEngine.IsCrossed 对未知类型恒 null
        // → 该报警永不触发、也不报错（宿主以为配了保护）。
        var message = ErrorsOf("""<Point id="p" address="0"><Alarm id="a" type="foo" limit="10" /></Point>""");

        Assert.Contains("点位 ps1/p", message);
        Assert.Contains("type=\"foo\" 非法", message);
        Assert.Contains("high / highHigh / low / lowLow / digital", message);
    }

    [Fact]
    public void A_threshold_alarm_without_a_limit_is_rejected_at_load()
    {
        // findings D70：限值型报警没有 limit 就永远判不出来（IsCrossed 因 !Limit.HasValue 恒 null）。
        var message = ErrorsOf("""<Point id="p" address="0"><Alarm id="a" type="high" /></Point>""");

        Assert.Contains("点位 ps1/p", message);
        Assert.Contains("缺少 limit", message);
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("INF")]
    [InlineData("-INF")]
    public void A_threshold_alarm_with_a_non_finite_limit_is_rejected_at_load(string limit)
    {
        // 非有限限值同样判不出正确报警（比较恒 false/true），必须在加载期拦
        var message = ErrorsOf($"""<Point id="p" address="0"><Alarm id="a" type="low" limit="{limit}" /></Point>""");

        Assert.Contains("limit=", message);
        Assert.Contains("必须是有限数值", message);
    }

    [Fact]
    public void Negative_deadband_is_rejected_at_load()
    {
        // findings D71：high limit=10 / deadband=-5 会把清除阈值推到 15——
        // 值 12 仍在限值之上却发清除事件（假清除，安全相关）。
        var message = ErrorsOf("""<Point id="p" address="0"><Alarm id="a" type="high" limit="10" deadband="-5" /></Point>""");

        Assert.Contains("deadband=-5", message);
        Assert.Contains("必须 ≥ 0", message);
    }

    [Fact]
    public void Negative_delay_ms_is_rejected_at_load()
    {
        // findings D71 同族：负延时等价于 0（DelayMs &gt; 0 才启用延时），语义无矛盾但仍是非法值。
        var message = ErrorsOf("""<Point id="p" address="0"><Alarm id="a" type="high" limit="10" delayMs="-5" /></Point>""");

        Assert.Contains("delayMs=-5", message);
        Assert.Contains("必须 ≥ 0 毫秒", message);
    }

    [Fact]
    public void Two_same_type_alarms_without_ids_are_rejected_at_load()
    {
        // findings D68：同点位两条 `type="high"` 都不写 id ⇒ 运行期键都是 pointId#high
        // （AlarmEngine.AlarmIdOf 的缺省键口径，ADR D9），两条报警共用一个状态 →
        // 第二条永远发不出自己的激活事件、值回落时发出「仍在限值之上」的假清除。
        var message = ErrorsOf("""<Point id="p" address="0"><Alarm type="high" limit="10" /><Alarm type="high" limit="20" /></Point>""");

        Assert.Contains("点位 ps1/p", message);
        Assert.Contains("报警键冲突", message);
        Assert.Contains("p#high", message);
    }

    [Fact]
    public void Duplicate_explicit_alarm_ids_are_also_rejected_at_load()
    {
        // 同一口径的另一面：显式写了同一个 Alarm@id 的两条报警，运行期键也相同（键 = id）
        var message = ErrorsOf("""<Point id="p" address="0"><Alarm id="h" type="high" limit="10" /><Alarm id="h" type="high" limit="20" /></Point>""");

        Assert.Contains("报警键冲突", message);
    }

    [Fact]
    public void Same_type_alarms_with_distinct_ids_are_accepted_and_addressable()
    {
        // 与上条对照（口径边界）：写了两条不同 id 就合法且可分别确认
        var cfg = Load("""<Point id="p" address="0"><Alarm id="h10" type="high" limit="10" /><Alarm id="h20" type="high" limit="20" /></Point>""");

        Assert.Equal(new[] { "h10", "h20" }, cfg.PointSets.Single().Points.Single().Alarms.Select(a => a.Id).ToArray());
    }

    [Fact]
    public void One_id_less_and_one_with_id_of_the_same_type_do_not_collide()
    {
        // 边界（不误伤）：缺 id 的键是 p#high、写了 id 的键是 h10 → 不冲突，允许
        var cfg = Load("""<Point id="p" address="0"><Alarm type="high" limit="10" /><Alarm id="h10" type="high" limit="20" /></Point>""");

        Assert.Equal(2, cfg.PointSets.Single().Points.Single().Alarms.Count);
    }

    // ═══════════════ 计算点挂报警（findings D72 → 加载期拒绝）═══════════════

    [Fact]
    public void An_alarm_on_a_calculated_point_is_rejected_at_load()
    {
        // findings D72：计算点不进采集轮次 → 报警永不评估（实测求值 Good=4、报警 0 条 = 静默无效）。
        var ex = Assert.Throws<ConfigValidationException>(() => SamplerConfigLoader.Load(XDocument.Parse(
            CalculatedShell("""<Point id="c"><Expression>2+2</Expression><Alarm id="AC" type="high" limit="1" /></Point>""")),
            Directory.GetCurrentDirectory()));

        var message = string.Join(" | ", ex.Errors);
        Assert.Contains("点位 ps1/c", message);
        Assert.Contains("计算点不得挂", message);
    }

    [Fact]
    public void A_calculated_point_without_an_alarm_loads_cleanly()
    {
        var cfg = SamplerConfigLoader.Load(XDocument.Parse(
            CalculatedShell("""<Point id="c"><Expression>2+2</Expression></Point>""")),
            Directory.GetCurrentDirectory());

        Assert.Empty(cfg.PointSets.Single().Calculated.Single().Alarms);
    }

    /// <summary>计算点要嵌在 PointSet 里（点表级的 &lt;Calculated&gt;），故单独一个骨架。</summary>
    private static string CalculatedShell(string calculated)
        => """
            <SamplerConfig schemaVersion="3.0">
              <Transports><Transport id="tcp1" host="127.0.0.1" /></Transports>
              <Devices><Device id="d1" transport="tcp1" pointSet="ps1" /></Devices>
              <PointSets><PointSet id="ps1"><Calculated>{0}</Calculated></PointSet></PointSets>
            </SamplerConfig>
            """.Replace("{0}", calculated);
}

