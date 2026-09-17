using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using SuperSampler.Core.Config;
using Xunit;

namespace SuperSampler.UnitTests.Config;

/// <summary>
/// 脚本配置（<c>Global/Script</c> 与 <c>Point/Script</c>）的解析与校验测试（ADR D41 / CGV-32 / CGV-33）：
/// 属性进入模型、缺省值正确、非法值一律报错（language 只认 js、timeoutMs &gt; 0、onError 只认 markBad、
/// 未知/已删除属性报错）；计算点必须恰有 Expression 或 Script 之一。
/// </summary>
public class ScriptConfigTests
{
    private static SamplerConfiguration Load(string inner)
        => SamplerConfigLoader.Load(
            XDocument.Parse("<HostConfig schemaVersion=\"3.0\">" + inner + "</HostConfig>"),
            Directory.GetCurrentDirectory());

    private static ConfigValidationException LoadError(string inner)
        => Assert.Throws<ConfigValidationException>(() => Load(inner));

    private const string Base =
        "<Transports><Transport id=\"tcp1\" host=\"x\" /></Transports>"
        + "<Devices><Device id=\"d1\" transport=\"tcp1\" pointSet=\"ps1\" /></Devices>";

    private static string WithPoint(string point)
        => Base + "<PointSets><PointSet id=\"ps1\"><Points>" + point + "</Points></PointSet></PointSets>";

    private static string WithCalculated(string point)
        => Base + "<PointSets><PointSet id=\"ps1\"><Points><Point id=\"p\" address=\"0\" /></Points>"
           + "<Calculated>" + point + "</Calculated></PointSet></PointSets>";

    private static PointConfig Point(string point)
    {
        var config = Load(WithPoint(point));
        return Assert.Single(config.PointSets[0].Points);
    }

    // ─────────────── Global/Script ───────────────

    [Fact]
    public void Global_script_defaults_are_50ms_mark_bad()
    {
        var config = Load(Base + "<PointSets><PointSet id=\"ps1\" /></PointSets>");

        Assert.Equal(50, config.Global.ScriptTimeoutMs);
        Assert.Equal("markBad", config.Global.ScriptOnError);
    }

    [Fact]
    public void Global_script_attributes_are_parsed()
    {
        var config = Load("<Global><Script timeoutMs=\"120\" onError=\"markBad\" /></Global>"
            + Base + "<PointSets><PointSet id=\"ps1\" /></PointSets>");

        Assert.Equal(120, config.Global.ScriptTimeoutMs);
        Assert.Equal("markBad", config.Global.ScriptOnError);
    }

    [Theory]
    [InlineData("<Global><Script language=\"js\" /></Global>", "不支持 language")]
    [InlineData("<Global><Script timeoutMs=\"0\" /></Global>", "必须 > 0")]
    [InlineData("<Global><Script timeoutMs=\"-5\" /></Global>", "必须 > 0")]
    [InlineData("<Global><Script timeoutMs=\"abc\" /></Global>", "不是合法整数")]
    [InlineData("<Global><Script onError=\"keepLast\" /></Global>", "onError")]
    [InlineData("<Global><Script onError=\"defaultValue\" /></Global>", "onError")]
    [InlineData("<Global><Script maxMemoryMb=\"8\" /></Global>", "maxMemoryMb 已删除")]
    [InlineData("<Global><Script bogus=\"1\" /></Global>", "未知属性")]
    public void Global_script_invalid_values_are_rejected(string global, string fragment)
    {
        var ex = LoadError(global + Base + "<PointSets><PointSet id=\"ps1\" /></PointSets>");

        Assert.Contains(ex.Errors, e => e.Contains(fragment));
    }

    [Fact]
    public void Global_script_must_not_carry_script_body()
    {
        var ex = LoadError("<Global><Script>return 1;</Script></Global>"
            + Base + "<PointSets><PointSet id=\"ps1\" /></PointSets>");

        Assert.Contains(ex.Errors, e => e.Contains("不得写脚本正文"));
    }

    // ─────────────── Point/Script ───────────────

    [Fact]
    public void Point_script_body_and_attributes_are_parsed()
    {
        var point = Point("<Point id=\"p\" address=\"0\"><Script language=\"js\" timeoutMs=\"30\" onError=\"markBad\">"
                          + "rawValue * 0.1</Script></Point>");

        Assert.True(point.HasScript);
        Assert.Equal("rawValue * 0.1", point.Script);
        Assert.Equal("js", point.ScriptLanguage);
        Assert.Equal(30, point.ScriptTimeoutMs);
        Assert.Equal("markBad", point.ScriptOnError);
    }

    [Fact]
    public void Point_script_language_defaults_to_js_and_timeout_stays_null_for_inheritance()
    {
        var point = Point("<Point id=\"p\" address=\"0\"><Script>rawValue</Script></Point>");

        Assert.Equal("js", point.ScriptLanguage);
        Assert.Null(point.ScriptTimeoutMs);   // null = 运行期取 Global/Script@timeoutMs
        Assert.Null(point.ScriptOnError);
    }

    [Fact]
    public void Point_without_script_has_no_script()
    {
        Assert.False(Point("<Point id=\"p\" address=\"0\" />").HasScript);
    }

    [Theory]
    [InlineData("<Script language=\"csharp\">return 1;</Script>", "language")]
    [InlineData("<Script language=\"javascript\">return 1;</Script>", "language")]
    [InlineData("<Script timeoutMs=\"0\">return 1;</Script>", "必须 > 0")]
    [InlineData("<Script onError=\"keepLast\">return 1;</Script>", "onError")]
    [InlineData("<Script maxMemoryMb=\"16\">return 1;</Script>", "maxMemoryMb 已删除")]
    [InlineData("<Script bogus=\"1\">return 1;</Script>", "未知属性")]
    [InlineData("<Script>   </Script>", "脚本正文为空")]
    public void Point_script_invalid_values_are_rejected(string script, string fragment)
    {
        var ex = LoadError(WithPoint("<Point id=\"p\" address=\"0\">" + script + "</Point>"));

        Assert.Contains(ex.Errors, e => e.Contains(fragment));
    }

    // ─────────────── 计算点：Expression / Script 恰有其一（CGV-33） ───────────────

    [Fact]
    public void Calculated_point_with_script_is_accepted()
    {
        var config = Load(WithCalculated("<Point id=\"c\"><Script>P('p') * 2</Script></Point>"));
        var calculated = Assert.Single(config.PointSets[0].Calculated);

        Assert.True(calculated.IsCalculated);
        Assert.True(calculated.HasScript);
        Assert.Null(calculated.Expression);
    }

    [Theory]
    [InlineData("<Point id=\"c\"><Expression>P('p')</Expression><Script>P('p')</Script></Point>", "只能写一个")]
    [InlineData("<Point id=\"c\"></Point>", "必须写 <Expression> 或 <Script>")]
    public void Calculated_point_must_have_exactly_one_form(string point, string fragment)
    {
        var ex = LoadError(WithCalculated(point));

        Assert.Contains(ex.Errors, e => e.Contains(fragment));
    }

    // ─────────────── 脚本与其它字段共存 ───────────────

    [Fact]
    public void Script_on_block_point_is_parsed_too()
    {
        var config = Load(Base + "<PointSets><PointSet id=\"ps1\"><Blocks>"
            + "<Block id=\"b1\" start=\"0\" count=\"4\"><Point id=\"bp\" address=\"0\">"
            + "<Script>rawValue + 1</Script></Point></Block></Blocks></PointSet></PointSets>");

        var point = Assert.Single(config.PointSets[0].Blocks[0].Points);
        Assert.Equal("rawValue + 1", point.Script);
    }
}
