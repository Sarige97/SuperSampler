using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using SuperSampler.Core.Config;
using SuperSampler.Core.Runtime;

namespace InjectionLineMonitor;

/// <summary>
/// 启动时的配置解析摘要。现场排障第一屏就看这个：
/// 设备/点位/扫描组/警告条数对不上，后面所有数据都不要信。
/// </summary>
internal static class LineSummary
{
    public static string Build(SamplerConfiguration config, PointCatalog catalog, string configPath)
    {
        var text = new StringBuilder(2048);

        text.AppendLine("── 配置解析摘要 ─────────────────────────────────────────────────────────────");
        text.AppendLine("  配置文件   : " + configPath);
        text.AppendLine("  工程       : " + ConsoleOut.Show(config.Meta.ProjectName)
                                     + "  修订 " + config.Meta.Revision.ToString(CultureInfo.InvariantCulture)
                                     + "  作者 " + ConsoleOut.Show(config.Meta.Author));
        text.AppendLine("  schema     : 3.x  unsupportedPolicy=" + config.UnsupportedPolicy);
        text.AppendLine("  语言/坏值  : " + config.Global.Language + "/" + config.Global.FallbackLanguage
                                     + "  nullText=\"" + config.Global.NullText + "\"  全局 swap=" + config.Global.DefaultSwap);

        // 每一类可枚举的东西都数一遍：加载器把「未实现」记进 Warnings，宿主负责显示与落盘
        var all = catalog.AllPoints().ToList();
        var enabled = all.Count(p => p.Enabled);
        var writable = all.Count(p => p.IsWritable);
        var calculated = all.Count(p => p.IsCalculated);
        var alarms = all.Sum(p => p.Source.Alarms.Count);

        text.AppendLine("  规模       : 链路 " + config.Transports.Count.ToString(CultureInfo.InvariantCulture)
                                     + "  设备 " + config.Devices.Count.ToString(CultureInfo.InvariantCulture)
                                     + "  点表 " + config.PointSets.Count.ToString(CultureInfo.InvariantCulture)
                                     + "  点位 " + enabled.ToString(CultureInfo.InvariantCulture)
                                     + "/" + all.Count.ToString(CultureInfo.InvariantCulture)
                                     + "（可写 " + writable.ToString(CultureInfo.InvariantCulture)
                                     + " / 计算点 " + calculated.ToString(CultureInfo.InvariantCulture)
                                     + " / 报警 " + alarms.ToString(CultureInfo.InvariantCulture) + "）");
        text.AppendLine("  通信策略   : 超时 " + config.Global.RequestTimeoutMs.ToString(CultureInfo.InvariantCulture) + "ms"
                                     + "  重试 " + config.Global.RetryCount.ToString(CultureInfo.InvariantCulture)
                                     + "x" + config.Global.RetryIntervalMs.ToString(CultureInfo.InvariantCulture) + "ms"
                                     + "  断线质量=" + config.Global.OnCommError + "/" + config.Global.OnCommErrorValue
                                     + "  staleAfter=" + config.Global.StaleAfterMs.ToString(CultureInfo.InvariantCulture) + "ms"
                                     + "  原始读写=" + (config.Global.AllowRawAccess ? "开" : "关"));

        text.AppendLine();
        text.AppendLine("  扫描组     : " + string.Join("  ",
            config.ScanGroups.Select(g => g.Id + "[" + g.Mode + " "
                + (g.Mode == "poll" ? g.RateMs.ToString(CultureInfo.InvariantCulture) + "ms" : "-")
                + (g.JitterMs > 0 ? "+" + g.JitterMs.ToString(CultureInfo.InvariantCulture) + "ms抖动" : string.Empty) + "]")));

        text.AppendLine("  链路       : " + string.Join("  ",
            config.Transports.Select(t => t.Id + "[" + t.Variant + " " + ConsoleOut.Show(t.Host) + ":"
                + t.Port.ToString(CultureInfo.InvariantCulture) + " to=" + t.RequestTimeoutMs.ToString(CultureInfo.InvariantCulture) + "ms"
                + (t.HasRetryOverride ? " retry=" + t.RetryCount.ToString(CultureInfo.InvariantCulture) : string.Empty) + "]")));

        text.AppendLine();
        text.AppendLine("  设备清单：");
        foreach (var device in config.Devices)
        {
            var points = catalog.PointsOf(device.Id).ToList();
            var blocks = catalog.BlockIdsOf(device.Id).ToList();
            var groups = string.Join("/", points.Select(p => p.ScanGroup).Distinct().OrderBy(g => g, StringComparer.Ordinal));

            text.AppendLine("    " + device.Id.PadRight(12)
                + " " + ConsoleOut.Show(device.Name).PadRight(10)
                + " transport=" + device.Transport.PadRight(7)
                + " unitId=" + device.UnitId.ToString(CultureInfo.InvariantCulture).PadRight(3)
                + " 点位=" + points.Count.ToString(CultureInfo.InvariantCulture).PadRight(4)
                + " 可写=" + points.Count(p => p.IsWritable).ToString(CultureInfo.InvariantCulture).PadRight(3)
                + " 扫描组=" + groups.PadRight(18)
                + (blocks.Count > 0 ? " 块=" + string.Join(",", blocks) : " 块=无（全散点）"));

            // 网关下多从站：把点位实际落在哪个从站打出来，这是最容易配错的地方
            var units = points.Where(p => !p.IsCalculated).Select(p => p.UnitId).Distinct().OrderBy(u => u).ToList();
            if (units.Count > 1)
            {
                text.AppendLine("                  └ 点位级 unitId 覆盖：" + string.Join(", ",
                    units.Select(u => "unit" + u.ToString(CultureInfo.InvariantCulture) + "×"
                        + points.Count(p => !p.IsCalculated && p.UnitId == u).ToString(CultureInfo.InvariantCulture))));
            }
        }

        text.AppendLine();
        var warnings = config.Warnings;
        text.AppendLine("  Warnings   : " + warnings.Count.ToString(CultureInfo.InvariantCulture) + " 条"
                                     + (warnings.Count == 0 ? "（加载器未发现未实现字段）" : string.Empty));
        foreach (var warning in warnings.Take(10))
        {
            text.AppendLine("      ! " + warning);
        }

        if (warnings.Count > 10)
        {
            text.AppendLine("      ... 其余 " + (warnings.Count - 10).ToString(CultureInfo.InvariantCulture) + " 条见 Config.Warnings");
        }

        text.Append("────────────────────────────────────────────────────────────────────────────");
        return text.ToString();
    }
}
