using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;

namespace InjectionLineMonitor;

/// <summary>一次进程资源采样（长稳判据用：线程只增不减 = 泄漏，托管堆持续上升 = 泄漏）。</summary>
internal sealed class ResourceSample
{
    public double AtSeconds;
    public int Threads;
    public int Handles;
    public double ManagedHeapMb;
    public long ValueEvents;
    public long ErrorEvents;
    public string HostTime = string.Empty;
}

/// <summary>进程资源采样器：每 30 秒一次（线程数/句柄数/GC 后托管堆）。</summary>
internal sealed class ResourceSampler : IDisposable
{
    private readonly Process _process = Process.GetCurrentProcess();
    private readonly EventJournal _journal;
    private readonly Stopwatch _clock;
    private readonly List<ResourceSample> _samples = new List<ResourceSample>();
    private DateTime _lastSampleUtc = DateTime.MinValue;

    public ResourceSampler(EventJournal journal, Stopwatch clock)
    {
        _journal = journal;
        _clock = clock;
    }

    public IReadOnlyList<ResourceSample> Samples => _samples;

    /// <summary>到点（缺省 30s）就采样一次；返回是否真的采了。</summary>
    public ResourceSample? SampleIfDue(double intervalSeconds, CsvBundle csv)
    {
        var now = DateTime.UtcNow;
        if (_lastSampleUtc != DateTime.MinValue && (now - _lastSampleUtc).TotalSeconds < intervalSeconds)
        {
            return null;
        }

        _lastSampleUtc = now;
        _process.Refresh();

        var sample = new ResourceSample
        {
            AtSeconds = _clock.Elapsed.TotalSeconds,
            Threads = _process.Threads.Count,
            Handles = _process.HandleCount,
            // 强制 GC 后取托管堆：要的是「存活对象」而不是「还没回收的垃圾」
            ManagedHeapMb = GC.GetTotalMemory(true) / (1024.0 * 1024.0),
            ValueEvents = _journal.ValueEvents,
            ErrorEvents = _journal.ErrorEvents,
            HostTime = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
        };

        _samples.Add(sample);

        csv.Resources.Row(CsvSink.Time(DateTimeOffset.Now),
            sample.AtSeconds.ToString("0.0", CultureInfo.InvariantCulture),
            sample.Threads.ToString(CultureInfo.InvariantCulture),
            sample.Handles.ToString(CultureInfo.InvariantCulture),
            sample.ManagedHeapMb.ToString("0.00", CultureInfo.InvariantCulture),
            sample.ValueEvents.ToString(CultureInfo.InvariantCulture),
            sample.ErrorEvents.ToString(CultureInfo.InvariantCulture));

        return sample;
    }

    public void Dispose() => _process.Dispose();
}

/// <summary>退出汇总：现场长稳判据就是这张表。</summary>
internal static class RunReport
{
    public static string Build(
        string stopReason,
        string configPath,
        string opsSource,
        double seconds,
        EventJournal journal,
        OpsRunner ops,
        ResourceSampler sampler,
        CsvBundle csv,
        SuperSampler.Core.Events.InProcessEventBus bus)
    {
        var text = new StringBuilder(8192);

        text.AppendLine("╔══════════════════════════════════════════════════════════════════════════════════════╗");
        text.AppendLine("║ 运行汇总（退出原因：" + Pad(stopReason, 66) + "）║");
        text.AppendLine("╚══════════════════════════════════════════════════════════════════════════════════════╝");
        text.AppendLine("  配置     : " + configPath);
        text.AppendLine("  操作脚本 : " + opsSource);
        text.AppendLine("  运行时长 : " + seconds.ToString("0.0", CultureInfo.InvariantCulture) + "s");
        text.AppendLine();
        text.AppendLine("── 宿主操作动作（脚本侧统计）───────────────────────────────────────────────");
        text.AppendLine("  普通写 " + ops.WriteAttempts.ToString(CultureInfo.InvariantCulture)
                        + " 次  点动写 " + ops.PulseAttempts.ToString(CultureInfo.InvariantCulture)
                        + " 次  报警确认 " + ops.AckAttempts.ToString(CultureInfo.InvariantCulture)
                        + " 次（其中 NotPending " + ops.AckNotPending.ToString(CultureInfo.InvariantCulture) + "）"
                        + "  查看 " + ops.ShowCount.ToString(CultureInfo.InvariantCulture) + " 次");
        text.AppendLine("  VerifyMismatch 次数：" + ops.VerifyMismatches.ToString(CultureInfo.InvariantCulture)
                        + "（写成功但回读值与写入值不同的次数；框架不自动重试，须人工判断）"
                        + "  宿主侧动作异常 " + ops.ActionErrors.ToString(CultureInfo.InvariantCulture) + " 次");
        text.AppendLine();

        text.AppendLine("── 事件计数 ────────────────────────────────────────────────────────────────");
        text.Append(journal.Counters());
        text.AppendLine();
        text.AppendLine("  事件总线 : 发出 " + bus.Metrics.TotalEmitted.ToString(CultureInfo.InvariantCulture)
                        + "  丢弃 " + bus.Metrics.TotalDropped.ToString(CultureInfo.InvariantCulture)
                        + "  订阅者异常 " + bus.Metrics.TotalFaults.ToString(CultureInfo.InvariantCulture)
                        + "  活跃订阅 " + bus.Metrics.ActiveSubscriptions.ToString(CultureInfo.InvariantCulture));
        foreach (var info in journal.Subscriptions)
        {
            text.AppendLine("             · " + Pad(info.Label, 34)
                            + " 投递 " + info.Handle.Delivered.ToString(CultureInfo.InvariantCulture)
                            + " 丢弃 " + info.Handle.Dropped.ToString(CultureInfo.InvariantCulture)
                            + " 异常 " + info.Handle.Faults.ToString(CultureInfo.InvariantCulture));
        }

        text.AppendLine();
        AppendQuality(text, journal);
        text.AppendLine();
        AppendTransitions(text, journal);
        text.AppendLine();
        AppendAlarms(text, journal);
        text.AppendLine();
        AppendResources(text, sampler);
        text.AppendLine();
        text.AppendLine("── 落盘文件 ────────────────────────────────────────────────────────────────");
        text.AppendLine("  values.csv    值变化事件（每点每次变化一行）");
        text.AppendLine("  alarms.csv    报警三态（Raised/Cleared/Acknowledged）");
        text.AppendLine("  writes.csv    写审计事件（框架发出，含四态）");
        text.AppendLine("  errors.csv    错误族事件（含分类/错误码/结构化上下文）");
        text.AppendLine("  ops.csv       宿主操作脚本结果（含 WriteResult 的 VerifyMismatch 与 Readback）");
        text.AppendLine("  resources.csv 每 30s 的进程资源采样");
        return text.ToString();
    }

    private static void AppendQuality(StringBuilder text, EventJournal journal)
    {
        text.AppendLine("── 质量分布（每显示周期采样一次，全部点位）──────────────────────────────────");

        var snapshot = journal.QualitySnapshot();
        if (snapshot.Count == 0)
        {
            text.AppendLine("  无采样（运行时间太短？）");
            return;
        }

        long good = 0, uncertain = 0, bad = 0, offline = 0;
        foreach (var stats in snapshot.Values)
        {
            good += stats.Good;
            uncertain += stats.Uncertain;
            bad += stats.Bad;
            offline += stats.Offline;
        }

        var total = good + uncertain + bad + offline;
        text.AppendLine("  全部点位合计：采样 " + total.ToString(CultureInfo.InvariantCulture)
                        + " 点次  Good " + Pct(good, total)
                        + "  Uncertain " + Pct(uncertain, total)
                        + "  Bad " + Pct(bad, total)
                        + "  Offline " + Pct(offline, total));

        var degraded = snapshot
            .Where(p => p.Value.Bad > 0 || p.Value.Offline > 0 || p.Value.Uncertain > 0)
            .OrderByDescending(p => p.Value.Bad + p.Value.Offline + p.Value.Uncertain)
            .ToList();

        if (degraded.Count == 0)
        {
            text.AppendLine("  运行期间所有点位始终 Good");
            return;
        }

        text.AppendLine("  出现非 Good 的点位（" + degraded.Count.ToString(CultureInfo.InvariantCulture) + " 个）：");
        foreach (var pair in degraded.Take(40))
        {
            text.AppendLine("    " + pair.Key.PadRight(38) + pair.Value.Distribution());
        }

        if (degraded.Count > 40)
        {
            text.AppendLine("    ... 其余 " + (degraded.Count - 40).ToString(CultureInfo.InvariantCulture) + " 个点位见 resources/values CSV");
        }
    }

    private static void AppendTransitions(StringBuilder text, EventJournal journal)
    {
        var transitions = journal.QualityTransitions;
        text.AppendLine("── 质量跃迁（断线/恢复证据，共 " + transitions.Count.ToString(CultureInfo.InvariantCulture) + " 条）────────────");
        if (transitions.Count == 0)
        {
            text.AppendLine("  运行期间无质量跃迁");
            return;
        }

        foreach (var line in transitions.Skip(Math.Max(0, transitions.Count - 40)))
        {
            text.AppendLine("  " + line);
        }
    }

    private static void AppendAlarms(StringBuilder text, EventJournal journal)
    {
        var alarms = journal.RecentAlarms;
        text.AppendLine("── 报警三态流水（共 " + alarms.Count.ToString(CultureInfo.InvariantCulture) + " 条）──────────────────────────────");
        if (alarms.Count == 0)
        {
            text.AppendLine("  运行期间无报警事件");
        }
        else
        {
            foreach (var line in alarms)
            {
                text.AppendLine("  " + line);
            }
        }

        var active = journal.ActiveAlarms;
        text.AppendLine("  结束时仍在激活：" + (active.Count == 0 ? "无" : string.Join(", ", active)));
    }

    private static void AppendResources(StringBuilder text, ResourceSampler sampler)
    {
        text.AppendLine("── 进程资源（每 30s 采样，长稳判据）────────────────────────────────────────");
        var samples = sampler.Samples;
        if (samples.Count == 0)
        {
            text.AppendLine("  运行时间不足 30s，未采样");
            return;
        }

        text.AppendLine("  时间     运行(s)  线程  句柄   GC后托管堆   值事件   错误事件");
        foreach (var sample in samples)
        {
            text.AppendLine("  " + sample.HostTime.PadRight(9)
                            + sample.AtSeconds.ToString("0.0", CultureInfo.InvariantCulture).PadLeft(7)
                            + sample.Threads.ToString(CultureInfo.InvariantCulture).PadLeft(6)
                            + sample.Handles.ToString(CultureInfo.InvariantCulture).PadLeft(6)
                            + ("  " + sample.ManagedHeapMb.ToString("0.00", CultureInfo.InvariantCulture) + " MB").PadLeft(12)
                            + sample.ValueEvents.ToString(CultureInfo.InvariantCulture).PadLeft(9)
                            + sample.ErrorEvents.ToString(CultureInfo.InvariantCulture).PadLeft(10));
        }

        var first = samples[0];
        var last = samples[samples.Count - 1];
        text.AppendLine("  首末差：线程 " + (last.Threads - first.Threads).ToString("+#;-#;0", CultureInfo.InvariantCulture)
                        + "  句柄 " + (last.Handles - first.Handles).ToString("+#;-#;0", CultureInfo.InvariantCulture)
                        + "  托管堆 " + (last.ManagedHeapMb - first.ManagedHeapMb).ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture) + " MB");
    }

    private static string Pct(long part, long total)
        => total == 0 ? "-" : (part * 100.0 / total).ToString("0.0", CultureInfo.InvariantCulture) + "%";

    private static string Pad(string text, int width)
        => text.Length >= width ? text.Substring(0, width) : text.PadRight(width);
}
