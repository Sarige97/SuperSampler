using System;
using System.IO;
using System.Xml.Linq;
using SuperSampler.Core.Config;
using Xunit;

namespace SuperSampler.UnitTests.Config;

/// <summary>
/// ADR D39：`I18n/File` 必须按 `@lang` 选文件——只加载 `Global@language`，
/// 缺则由 `Global@fallbackLanguage` 兜底；两者都无匹配才退回"全部加载"。
/// 回归背景：原实现把所有 File 无差别塞进同一目录、后写覆盖先写，
/// 于是「zh_CN 在前、en_US 在后」时中文永不生效（真实样例配置实测）。
/// </summary>
public class I18nLanguageSelectionTests
{
    private static string WriteLangFile(string dir, string name, string key, string value)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, "# test\n" + key + " = \"" + value + "\"\n");
        return path;
    }

    private static SamplerConfiguration LoadWith(string xml, string dir)
        => SamplerConfigLoader.Load(XDocument.Parse(xml), dir);

    private const string Points = """
        <PointSets><PointSet id="ps1"><Points>
          <Point id="p1" name="${T_NAME}" address="0" dataType="uint16" />
        </Points></PointSet></PointSets>
        """;

    [Fact]
    public void Primary_language_file_wins_even_when_listed_first()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ss_i18n_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            WriteLangFile(dir, "zh.i18n", "T_NAME", "中文名");
            WriteLangFile(dir, "en.i18n", "T_NAME", "English name");

            var xml = """
                <SamplerConfig schemaVersion="3.0">
                  <Global language="zh_CN" fallbackLanguage="en_US" />
                  <I18n><Files>
                    <File lang="zh_CN" path="zh.i18n" />
                    <File lang="en_US" path="en.i18n" />
                  </Files></I18n>
                  <ScanGroups><ScanGroup id="normal" /></ScanGroups>
                  <Transports><Transport id="tcp1" host="127.0.0.1" /></Transports>
                  <Devices><Device id="d1" transport="tcp1" pointSet="ps1" /></Devices>
                """ + Points + "</SamplerConfig>";

            var config = LoadWith(xml, dir);

            Assert.Equal("中文名", config.I18n.Resolve("T_NAME"));
            Assert.Equal("中文名", config.PointSets[0].Points[0].Name);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Fallback_language_is_used_when_primary_file_is_missing()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ss_i18n_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            WriteLangFile(dir, "en.i18n", "T_NAME", "English name"); // 只有 en_US 文件

            var xml = """
                <SamplerConfig schemaVersion="3.0">
                  <Global language="zh_CN" fallbackLanguage="en_US" />
                  <I18n><Files>
                    <File lang="zh_CN" path="missing_zh.i18n" />
                    <File lang="en_US" path="en.i18n" />
                  </Files></I18n>
                  <ScanGroups><ScanGroup id="normal" /></ScanGroups>
                  <Transports><Transport id="tcp1" host="127.0.0.1" /></Transports>
                  <Devices><Device id="d1" transport="tcp1" pointSet="ps1" /></Devices>
                """ + Points + "</SamplerConfig>";

            var config = LoadWith(xml, dir);

            Assert.Equal("English name", config.I18n.Resolve("T_NAME"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Unmatched_language_falls_back_to_loading_every_file()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ss_i18n_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            WriteLangFile(dir, "a.i18n", "T_NAME", "A 值");
            WriteLangFile(dir, "b.i18n", "T_ONLY_B", "B 值");

            var xml = """
                <SamplerConfig schemaVersion="3.0">
                  <Global language="fr_FR" fallbackLanguage="de_DE" />
                  <I18n><Files>
                    <File lang="zh_CN" path="a.i18n" />
                    <File lang="en_US" path="b.i18n" />
                  </Files></I18n>
                  <ScanGroups><ScanGroup id="normal" /></ScanGroups>
                  <Transports><Transport id="tcp1" host="127.0.0.1" /></Transports>
                  <Devices><Device id="d1" transport="tcp1" pointSet="ps1" /></Devices>
                """ + Points + "</SamplerConfig>";

            var config = LoadWith(xml, dir);

            // 语言都对不上 → 退回旧行为（全部加载），两个键都应可解析
            Assert.Equal("A 值", config.I18n.Resolve("T_NAME"));
            Assert.Equal("B 值", config.I18n.Resolve("T_ONLY_B"));
        }
        finally { Directory.Delete(dir, true); }
    }
}