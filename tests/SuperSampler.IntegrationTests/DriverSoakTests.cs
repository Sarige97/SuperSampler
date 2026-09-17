using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using SuperSampler.Abstractions.Errors;
using SuperSampler.Abstractions.Events;
using SuperSampler.Abstractions.Values;
using SuperSampler.Core.Runtime;
using Xunit;

namespace SuperSampler.IntegrationTests;

/// <summary>
/// 并发长稳：≥20 从站（本用例 25 台，四条链路：TCP 三条 + RTU-over-TCP 一条）持续采集，
/// 期间在轮换目标上交替注入断路器故障（silent / refuse / halfopen / drop+delay），
/// 周期性采样进程线程数、句柄数、托管堆、事件计数、丢弃计数、好点位数与最大陈旧度。
///
/// 时长：默认 5 分钟冒烟（保证全量回归可承受）；长稳验收用环境变量
/// <c>SUPERSAMPLER_SOAK_MINUTES=30</c> 跑满 30 分钟（报告中的 30 分钟数据即由此产生）。
/// 采样 CSV 落 <c>_testplan/模块重测-驱动混沌-长稳采样.csv</c>（每次运行覆盖）。
/// </summary>
[Collection(ComplexStubCollection.Name)]
public sealed class DriverSoakTests
{
    private readonly ComplexStubFixture _stub;

    public DriverSoakTests(ComplexStubFixture stub) => _stub = stub;

    private const int PortP1 = ComplexStubFixture.PublicBasePort;       // 2502 → unit 1-4
    private const int PortP2 = ComplexStubFixture.PublicBasePort + 1;   // 2503 → unit 1-4
    private const int PortP3 = ComplexStubFixture.PublicBasePort + 2;   // 2504 → unit 5-15
    private const int PortP4 = ComplexStubFixture.PublicBasePort + 3;   // 2505 → unit 1-4/20/21（RTU）

    /// <summary>采样 CSV（仓库根 _testplan；经 ToolsDir 反推，避免相对层级写错）。</summary>
    private string CsvPath => Path.GetFullPath(Path.Combine(_stub.ToolsDir, "..", "..", "_testplan",
        "模块重测-驱动混沌-长稳采样.csv"));

    private static double SoakMinutes()
    {
        var raw = Environment.GetEnvironmentVariable("SUPERSAMPLER_SOAK_MINUTES");
        if (!string.IsNullOrWhiteSpace(raw) &&
            double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var minutes) &&
            minutes > 0)
        {
            return minutes;
        }

        return 5;
    }

    private sealed class SampleRow
    {
        public int ElapsedSec;
        public int Threads;
        public int Handles;
        public long ManagedBytes;
        public int Gen0;
        public int Gen1;
        public int Gen2;
        public long Emitted;
        public long Dropped;
        public int Errors;
        public int BackoffEntered;
        public int BackoffRecovered;
        public int GoodPoints;
        public int TotalPoints;
        public double WorstAgeSec;
        public int FaultsInjected;

        public string ToCsv()
            => string.Join(",", new[]
            {
                ElapsedSec.ToString(CultureInfo.InvariantCulture),
                Threads.ToString(CultureInfo.InvariantCulture),
                Handles.ToString(CultureInfo.InvariantCulture),
                ManagedBytes.ToString(CultureInfo.InvariantCulture),
                Gen0.ToString(CultureInfo.InvariantCulture),
                Gen1.ToString(CultureInfo.InvariantCulture),
                Gen2.ToString(CultureInfo.InvariantCulture),
                Emitted.ToString(CultureInfo.InvariantCulture),
                Dropped.ToString(CultureInfo.InvariantCulture),
                Errors.ToString(CultureInfo.InvariantCulture),
                BackoffEntered.ToString(CultureInfo.InvariantCulture),
                BackoffRecovered.ToString(CultureInfo.InvariantCulture),
                GoodPoints.ToString(CultureInfo.InvariantCulture),
                TotalPoints.ToString(CultureInfo.InvariantCulture),
                WorstAgeSec.ToString("F1", CultureInfo.InvariantCulture),
                FaultsInjected.ToString(CultureInfo.InvariantCulture),
            });
    }

    [SkippableFact]
    public async Task Soak_TwentyFiveSlaves_WithAlternatingDisconnects_StayHealthy()
    {
        _stub.RequireBreaker();
        _stub.RequireRtu();
        await _stub.ClearAllAsync();

        var duration = TimeSpan.FromMinutes(SoakMinutes());
        var faultInterval = TimeSpan.FromSeconds(Math.Max(12, Math.Min(75, duration.TotalSeconds / 6)));
        var faultHold = TimeSpan.FromSeconds(Math.Max(2, Math.Min(6, faultInterval.TotalSeconds / 4)));

        var plan = BuildFaultPlan();
        var byLink = new Dictionary<int, int>();

        var devices = new StringBuilder();
        var slaves = new List<(string Id, int Port, int Unit)>();
        foreach (var unit in new[] { 1, 2, 3, 4 }) AddSlave("t1", PortP1, unit);
        foreach (var unit in new[] { 1, 2, 3, 4 }) AddSlave("t2", PortP2, unit);
        // unit 5-14：SLOW-500（unit14）以内都在超时预算里；unit15 = SLOW-2000 是「设计上 2s 才回」的慢台，
        // 超时预算 1200ms 下必然判超时（不是缺陷），长稳刻意排除并在报告里写明。
        foreach (var unit in Enumerable.Range(5, 10)) AddSlave("t3", PortP3, unit);
        foreach (var unit in new[] { 1, 2, 3, 4, 20, 21 }) AddSlave("t4", PortP4, unit);

        void AddSlave(string transport, int port, int unit)
        {
            var id = transport + "u" + unit;
            devices.Append(ComplexStubFixture.Device(id, transport, unit, "ps"));
            slaves.Add((id, port, unit));
        }

        Assert.True(slaves.Count >= 20, "长稳要求 ≥20 从站，实际 " + slaves.Count);

        var pointSets =
            "<PointSet id=\"ps\"><Defaults swap=\"abcd\" /><Points>" +
            "<Point id=\"hv\" address=\"0\" dataType=\"uint16\" area=\"holding\" />" +
            "<Point id=\"iv\" address=\"0\" dataType=\"uint16\" area=\"input\" />" +
            "</Points></PointSet>";

        var xml = "<SamplerConfig schemaVersion=\"3.0\">" +
                  "<Global><Retry count=\"0\" intervalMs=\"20\" />" +
                  "<Polling defaultIntervalMs=\"800\" requestTimeoutMs=\"1200\" />" +
                  "<Quality onCommError=\"bad\" onCommErrorValue=\"null\" />" +
                  "<Reconnect enabled=\"true\" delays=\"2000,5000,10000\" manualRetry=\"true\" />" +
                  "</Global>" +
                  "<Transports>" +
                  ComplexStubFixture.Transport("t1", PortP1, "tcp", 1200) +
                  ComplexStubFixture.Transport("t2", PortP2, "tcp", 1200) +
                  ComplexStubFixture.Transport("t3", PortP3, "tcp", 1200) +
                  ComplexStubFixture.Transport("t4", PortP4, "rtuovertcp", 1200) +
                  "</Transports>" +
                  "<Devices>" + devices + "</Devices>" +
                  "<PointSets>" + pointSets + "</PointSets>" +
                  "</SamplerConfig>";

        using var engine = new SamplerEngine(_stub.LoadConfig(xml));
        using var errors = new ComplexStubFixture.EventCollector<IErrorEvent>(engine.Bus);
        using var entered = new ComplexStubFixture.EventCollector<BackoffEnteredEvent>(engine.Bus);
        using var recovered = new ComplexStubFixture.EventCollector<BackoffRecoveredEvent>(engine.Bus);

        engine.Start();

        var rows = new List<SampleRow>();
        var process = Process.GetCurrentProcess();
        var started = DateTime.UtcNow;
        var deadline = started + duration;
        var nextSampleAt = started.AddSeconds(3);
        var nextFaultAt = started.AddSeconds(Math.Min(10, faultInterval.TotalSeconds / 2));
        DateTime? clearFaultAt = null;
        var faultIndex = 0;
        var faultsInjected = 0;

        void Sample()
        {
            process.Refresh();
            var good = 0;
            var worstAge = 0.0;
            var total = 0;
            foreach (var (id, _, _) in slaves)
            {
                foreach (var point in new[] { "hv", "iv" })
                {
                    total++;
                    var value = engine.GetValueDetail(id, point);
                    if (value.Quality == PointQuality.Good) good++;
                    var age = engine.GetValueAge(id, point);
                    if (age.HasValue && age.Value.TotalSeconds > worstAge) worstAge = age.Value.TotalSeconds;
                }
            }

            var metrics = engine.Bus.Metrics;
            rows.Add(new SampleRow
            {
                ElapsedSec = (int)(DateTime.UtcNow - started).TotalSeconds,
                Threads = process.Threads.Count,
                Handles = process.HandleCount,
                ManagedBytes = GC.GetTotalMemory(false),
                Gen0 = GC.CollectionCount(0),
                Gen1 = GC.CollectionCount(1),
                Gen2 = GC.CollectionCount(2),
                Emitted = metrics.TotalEmitted,
                Dropped = metrics.TotalDropped,
                Errors = errors.Count,
                BackoffEntered = entered.Count,
                BackoffRecovered = recovered.Count,
                GoodPoints = good,
                TotalPoints = total,
                WorstAgeSec = worstAge,
                FaultsInjected = faultsInjected,
            });
        }

        try
        {
            while (DateTime.UtcNow < deadline)
            {
                if (clearFaultAt.HasValue && DateTime.UtcNow >= clearFaultAt.Value)
                {
                    await _stub.ClearAllAsync();
                    clearFaultAt = null;
                    nextFaultAt = DateTime.UtcNow + faultInterval;
                }

                if (!clearFaultAt.HasValue && DateTime.UtcNow >= nextFaultAt &&
                    DateTime.UtcNow + faultHold + TimeSpan.FromSeconds(2) < deadline)
                {
                    var (port, unit, command) = plan[faultIndex % plan.Count];
                    faultIndex++;
                    var ack = await _stub.InjectAsync(command.Replace("{P}", port.ToString(CultureInfo.InvariantCulture)));
                    Assert.True(ack.Ok, "故障注入失败：" + ack);
                    byLink[port] = byLink.TryGetValue(port, out var n) ? n + 1 : 1;
                    faultsInjected++;
                    clearFaultAt = DateTime.UtcNow + faultHold;
                }

                if (DateTime.UtcNow >= nextSampleAt)
                {
                    Sample();
                    nextSampleAt = DateTime.UtcNow.AddSeconds(30);
                }

                await Task.Delay(200);
            }

            // 收尾：清故障 → 等全部点位恢复
            await _stub.ClearAllAsync();
            var allGoodDeadline = DateTime.UtcNow.AddSeconds(90);
            var badDetails = string.Empty;
            while (DateTime.UtcNow < allGoodDeadline)
            {
                var bad = slaves.Where(s =>
                    engine.GetValueDetail(s.Id, "hv").Quality != PointQuality.Good ||
                    engine.GetValueDetail(s.Id, "iv").Quality != PointQuality.Good).ToArray();
                if (bad.Length == 0)
                {
                    badDetails = string.Empty;
                    break;
                }

                badDetails = string.Join("; ", bad.Take(8).Select(s =>
                    s.Id + "/" + s.Port + ":" + engine.GetValueDetail(s.Id, "hv").Quality + "/" +
                    engine.GetValueDetail(s.Id, "iv").Quality + "/" + (engine.GetValueDetail(s.Id, "hv").Reason ?? "-")));
                await Task.Delay(500);
            }

            Sample();
            Assert.True(string.IsNullOrEmpty(badDetails), "收尾后仍有非 Good 点位：" + badDetails);
        }
        finally
        {
            await _stub.ClearAllAsync();
        }

        // 先把采样数据落盘（判定失败也留证据），再做判定
        WriteCsv(CsvPath, rows, duration);

        // ───────────── 判定 ─────────────
        Assert.True(rows.Count >= Math.Max(3, (int)(duration.TotalSeconds / 60)), "采样点太少：" + rows.Count);
        // 故障周期 = faultInterval + faultHold（默认 75s + 6s ≈ 80s）→ 每分钟约 0.75 次
        Assert.True(faultsInjected >= Math.Max(3, (int)(duration.TotalMinutes / 2)), "故障注入次数太少：" + faultsInjected);
        var expectedLinks = duration.TotalMinutes >= 8 ? 4 : Math.Min(4, faultsInjected);
        Assert.True(byLink.Count == expectedLinks, "四条链路必须都被交替断线，实际：" +
                                                   string.Join(",", byLink.Select(kv => kv.Key + "×" + kv.Value)));
        Assert.All(byLink.Values, count => Assert.True(count >= 1, "每条链路至少一次故障"));

        // ① 无异常退出：25 台从站全部恢复 Good（写入 CSV 前的最后一次采样）
        var last = rows[rows.Count - 1];
        Assert.Equal(last.TotalPoints, last.GoodPoints);
        Assert.True(last.WorstAgeSec < 30, "恢复后最大陈旧度 " + last.WorstAgeSec + "s 过大");

        // ② 线程/句柄无线性增长
        var first = rows[0];
        var peakThreads = rows.Max(r => r.Threads);
        var peakHandles = rows.Max(r => r.Handles);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        await Task.Delay(500);
        process.Refresh();
        var finalThreads = process.Threads.Count;
        var finalHandles = process.HandleCount;

        Assert.True(peakThreads - first.Threads <= 20,
            "线程数增长过多：首 " + first.Threads + " / 峰 " + peakThreads + " / 末 " + finalThreads);
        Assert.True(finalThreads - first.Threads <= 8,
            "线程数未回落：首 " + first.Threads + " / 末 " + finalThreads);
        Assert.True(finalHandles - first.Handles <= 80,
            "句柄数未回落：首 " + first.Handles + " / 峰 " + peakHandles + " / 末 " + finalHandles);

        // ③ 托管堆无持续单调增长（首尾均值对比，容忍抖动）
        var head = rows.Take(Math.Min(5, rows.Count)).Average(r => (double)r.ManagedBytes);
        var tail = rows.Skip(Math.Max(0, rows.Count - 5)).Average(r => (double)r.ManagedBytes);
        Assert.True(tail <= head + 30 * 1024 * 1024,
            "托管堆首尾均值增长过多：首 " + (head / 1024 / 1024).ToString("F1") + "MB → 末 " +
            (tail / 1024 / 1024).ToString("F1") + "MB");

        // ④ 故障与恢复事件都真实发生（计数可读、丢弃自洽）
        Assert.True(last.Errors > 0, "故障期间必须有错误事件");
        Assert.True(last.BackoffEntered > 0, "必须进入过退避");
        Assert.True(last.BackoffRecovered > 0, "必须发生过退避恢复");
        Assert.True(last.Dropped <= last.Emitted, "丢弃数不应超过发出数");
        Assert.True(engine.Bus.Metrics.TotalFaults == 0, "事件总线不应有故障");
    }

    private static List<(int Port, int Unit, string Command)> BuildFaultPlan()
        => new()
        {
            (PortP1, 2, "{\"port\":{P},\"unit\":2,\"action\":\"silent\"}"),
            (PortP2, 3, "{\"port\":{P},\"unit\":3,\"action\":\"drop\",\"percent\":50,\"direction\":\"both\"}"),
            (PortP3, 8, "{\"port\":{P},\"unit\":8,\"action\":\"delay\",\"ms\":250,\"jitter\":250,\"direction\":\"resp\"}"),
            (PortP4, 2, "{\"port\":{P},\"unit\":2,\"action\":\"silent\"}"),
            (PortP1, 0, "{\"port\":{P},\"action\":\"offline\",\"mode\":\"refuse\",\"seconds\":2}"),
            (PortP2, 0, "{\"port\":{P},\"action\":\"offline\",\"mode\":\"halfopen\",\"seconds\":2}"),
            (PortP3, 12, "{\"port\":{P},\"unit\":12,\"action\":\"silent\"}"),
            (PortP4, 0, "{\"port\":{P},\"action\":\"offline\",\"mode\":\"refuse\",\"seconds\":2}"),
        };

    private static void WriteCsv(string csvPath, IReadOnlyList<SampleRow> rows, TimeSpan duration)
    {
        var directory = Path.GetDirectoryName(csvPath);
        if (directory != null) Directory.CreateDirectory(directory);

        var text = new StringBuilder();
        text.AppendLine("# durationMinutes=" + duration.TotalMinutes.ToString("F1", CultureInfo.InvariantCulture) +
                        " started=" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        text.AppendLine("elapsedSec,threads,handles,managedBytes,gen0,gen1,gen2,emitted,dropped,errors," +
                        "backoffEntered,backoffRecovered,goodPoints,totalPoints,worstAgeSec,faultsInjected");
        foreach (var row in rows) text.AppendLine(row.ToCsv());
        File.WriteAllText(csvPath!, text.ToString(), new UTF8Encoding(false));
    }
}
