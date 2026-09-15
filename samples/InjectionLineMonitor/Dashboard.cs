using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using SuperSampler.Abstractions.Facade;
using SuperSampler.Abstractions.Values;

namespace InjectionLineMonitor;

/// <summary>
/// 轮询显示：每 --every 秒一屏关键点位表（值 + 质量 + 坏值原因 + 数据年龄）。
/// 这是现场最常看的画面，也是「框架给了什么」与「宿主还得补什么」的分界线：
/// 框架给三元组（值/质量/时间戳），宿主自己决定怎么排版、多旧算陈旧、怎么置灰。
/// </summary>
internal sealed class Dashboard
{
    private sealed class Row
    {
        public string DeviceId = string.Empty;
        public string PointId = string.Empty;
        public string Label = string.Empty;
    }

    private readonly IDeviceManager _manager;
    private readonly PointCatalog _catalog;
    private readonly EventJournal _journal;
    private readonly Stopwatch _clock;
    private readonly int _staleAfterMs;
    private readonly List<Row> _rows = new List<Row>();
    private long _screens;

    public Dashboard(IDeviceManager manager, PointCatalog catalog, EventJournal journal, Stopwatch clock, int staleAfterMs)
    {
        _manager = manager;
        _catalog = catalog;
        _journal = journal;
        _clock = clock;
        _staleAfterMs = staleAfterMs;

        Add("IM-01", "im01.stage", "注塑阶段");
        Add("IM-01", "im01.statusWord", "状态字");
        Add("IM-01", "im01.cycleTime", "当前周期");
        Add("IM-01", "im01.goodParts", "合格品数");
        Add("IM-01", "im01.rejectParts", "次品数");
        Add("IM-01", "im01.barrel1Temp", "料筒1段温");
        Add("IM-01", "im01.moldTemp", "模具温度");
        Add("IM-01", "im01.oilTemp", "液压油温");
        Add("IM-01", "im01.injectPressure", "射胶压力");
        Add("IM-01", "im01.energyTotal", "累计能耗");
        Add("IM-01", "im01.moldTempDev", "模温偏差(计算点)");
        Add("IM-01", "im01.faultAlarm", "注塑机报警位");

        Add("GW-LINE-B", "mtc01.actualTemp", "模温机温度");
        Add("GW-LINE-B", "mtc01.pumpFlow", "模温机流量");
        Add("GW-LINE-B", "dry01.dryTemp", "干燥温度");
        Add("GW-LINE-B", "dry01.dewPoint", "露点");
        Add("GW-LINE-B", "dry01.materialLevel", "料位");
        Add("GW-LINE-B", "rob01.cycleTime", "机械手节拍时间");
        Add("GW-LINE-B", "rob01.totalPicks", "累计取件");

        Add("EM-01", "em01.activePower", "总有功功率");
        Add("EM-01", "em01.powerFactor", "功率因数");
        Add("EM-01", "em01.totalEnergy", "累计电能");

        Add("ENV-01", "env01.roomTemp", "车间温度");
        Add("ENV-01", "env01.humidity", "湿度");
        Add("ENV-01", "env01.co2", "CO2");
        Add("ENV-01", "env01.highTempAlarm", "高温报警位");
        Add("ENV-01", "env01.plantLoad", "车间负载率(计算点)");
    }

    private void Add(string deviceId, string pointId, string label)
    {
        _rows.Add(new Row { DeviceId = deviceId, PointId = pointId, Label = label });
    }

    /// <summary>
    /// 采样一次：把所有点位的当前质量喂给统计（用于退出时的「各点位质量分布」）。
    /// 注意 GetValueDetail 只读缓存、不发通讯，可以放心高频调用。
    /// </summary>
    public void Capture()
    {
        foreach (var deviceId in _catalog.DeviceIds)
        {
            foreach (var point in _catalog.PointsOf(deviceId))
            {
                var value = _manager.GetValueDetail(deviceId, point.PointId);
                _journal.RecordQualitySample(point.Key, deviceId, point.PointId, value);
            }
        }
    }

    public string Render()
    {
        _screens++;
        var now = DateTimeOffset.UtcNow;
        var text = new StringBuilder(4096);

        text.AppendLine("══════════════════════════════════════════════════════════════════════════════════════════");
        text.AppendLine("  屏 #" + _screens.ToString(CultureInfo.InvariantCulture)
                        + "   T+" + _clock.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + "s"
                        + "   本地时间 " + DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture)
                        + "   质量非 Good 或超过 " + (_staleAfterMs / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + "s 未刷新会标 !! / ~");
        text.AppendLine("══════════════════════════════════════════════════════════════════════════════════════════");
        text.AppendLine("  " + "点位".PadRight(34) + "值".PadRight(24) + "质量".PadRight(20) + "年龄".PadRight(9) + "说明");

        foreach (var row in _rows)
        {
            text.AppendLine(RenderRow(row, now));
        }

        var active = _journal.ActiveAlarms;
        text.AppendLine("  ── 激活报警：" + (active.Count == 0 ? "无" : string.Join(" | ", active)));

        text.Append("  事件计数：值 " + _journal.ValueEvents.ToString(CultureInfo.InvariantCulture)
                    + "  错误 " + _journal.ErrorEvents.ToString(CultureInfo.InvariantCulture)
                    + "  写 " + _journal.Writes.ToString(CultureInfo.InvariantCulture)
                    + "  （明细见 CSV）");
        return text.ToString();
    }

    private string RenderRow(Row row, DateTimeOffset now)
    {
        var detail = _manager.GetValueDetail(row.DeviceId, row.PointId);
        var reference = row.DeviceId + "/" + row.PointId;

        var formatted = detail.IsGood
            ? _manager.GetValue(row.DeviceId, row.PointId)
            : "--（质量非 Good）";

        var ageText = detail.Timestamp == DateTimeOffset.MinValue
            ? "-"
            : (now - detail.Timestamp).TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + "s";

        var stale = detail.IsGood
                    && detail.Timestamp != DateTimeOffset.MinValue
                    && (now - detail.Timestamp).TotalMilliseconds > _staleAfterMs;

        var marker = !detail.IsGood ? "!! " : stale ? "~  " : "   ";
        var quality = detail.Quality + (string.IsNullOrEmpty(detail.Reason) ? string.Empty : "(" + detail.Reason + ")");
        if (stale) quality += " 数据陈旧";

        var note = row.Label + (detail.IsGood ? string.Empty : "  [原始值 " + (detail.Value?.ToString() ?? "null") + "]");

        return marker + reference.PadRight(31)
               + Trim(formatted, 22).PadRight(24)
               + Trim(quality, 18).PadRight(20)
               + ageText.PadRight(9)
               + note;
    }

    private static string Trim(string text, int width)
        => text.Length <= width ? text : text.Substring(0, width - 1) + "…";
}
