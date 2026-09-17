using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using SuperSampler.Abstractions.Errors;
using SuperSampler.Abstractions.Events;
using SuperSampler.Abstractions.Values;
using SuperSampler.Core.Events;

namespace InjectionLineMonitor;

/// <summary>每个点位的质量时间分布（按显示周期采样统计）。</summary>
internal sealed class QualityStats
{
    public long Good;
    public long Uncertain;
    public long Bad;
    public long Offline;
    public PointQuality? Current;
    public long Transitions;
    public DateTimeOffset LastStamp = DateTimeOffset.MinValue;

    public long Total => Good + Uncertain + Bad + Offline;

    public void Add(PointQuality quality)
    {
        switch (quality)
        {
            case PointQuality.Good: Good++; break;
            case PointQuality.Uncertain: Uncertain++; break;
            case PointQuality.Bad: Bad++; break;
            default: Offline++; break;
        }
    }

    public string Distribution()
    {
        var total = Total;
        if (total == 0) return "-";

        var good = Good * 100.0 / total;
        var bad = Bad * 100.0 / total;
        var offline = Offline * 100.0 / total;
        var uncertain = Uncertain * 100.0 / total;

        var parts = new List<string>(4) { "Good " + pct(good) };
        if (uncertain > 0) parts.Add("Uncertain " + pct(uncertain));
        if (bad > 0) parts.Add("Bad " + pct(bad));
        if (offline > 0) parts.Add("Offline " + pct(offline));
        return "采样 " + total.ToString(CultureInfo.InvariantCulture) + " 次：" + string.Join(" / ", parts);
    }

    private static string pct(double value)
        => value.ToString("0.0", CultureInfo.InvariantCulture) + "%";
}

/// <summary>一条订阅的名字与句柄（汇总里要能看出每条订阅投递/丢弃了多少）。</summary>
internal sealed class SubscriptionInfo
{
    public SubscriptionInfo(string label, ISubscription handle)
    {
        Label = label;
        Handle = handle;
    }

    public string Label { get; }

    public ISubscription Handle { get; }
}

/// <summary>
/// 事件订阅与记账：宿主的第一职责是把框架发出来的事件变成「可对账的记录」。
/// 订阅方式覆盖了四类：值变化、报警三态、写审计、错误族（按 IErrorEvent 基接口订阅，
/// 这样将来框架新增错误类型也不会静默逃脱本宿主的统计）。
/// 落盘：每类事件一条 CSV（框架不落盘，ADR D1）。
/// </summary>
internal sealed class EventJournal : IDisposable
{
    private const int MaxPrintedErrors = 200;

    private readonly InProcessEventBus _bus;
    private readonly CsvBundle _csv;
    private readonly List<SubscriptionInfo> _subscriptions = new List<SubscriptionInfo>();

    private readonly Dictionary<string, long> _writeOutcomes = new Dictionary<string, long>(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _errorsByClass = new Dictionary<string, long>(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _errorsByCode = new Dictionary<string, long>(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _activeAlarms = new Dictionary<string, string>(StringComparer.Ordinal);
    private readonly Dictionary<string, QualityStats> _quality = new Dictionary<string, QualityStats>(StringComparer.Ordinal);
    private readonly List<string> _alarmLog = new List<string>();
    private readonly List<string> _qualityLog = new List<string>();

    private readonly object _gate = new object();
    private readonly DateTime _startedUtc = DateTime.UtcNow;

    private long _valueEvents;
    private long _valueGood;
    private long _valueUncertain;
    private long _valueBad;
    private long _valueOffline;
    private long _alarmRaised;
    private long _alarmCleared;
    private long _alarmAcked;
    private long _writes;
    private long _errors;
    private long _errorsPrinted;

    public EventJournal(InProcessEventBus bus, CsvBundle csv)
    {
        _bus = bus;
        _csv = csv;
    }

    // ─────────────── 订阅 ───────────────

    public void Subscribe()
    {
        // 值变化：高频，用 Queued + DropOldest（丢弃会计数，汇总里可见），容量给足
        _subscriptions.Add(new SubscriptionInfo("值变化 PointValueChanged",
            _bus.Subscribe<PointValueChangedEvent>(
                envelope => OnValue(envelope), DeliveryMode.Queued, OverflowPolicy.DropOldest, 16384)));

        // 报警三态：低频且不可丢，用 Wait 溢出策略，绝不丢
        _subscriptions.Add(new SubscriptionInfo("报警激活 AlarmRaised",
            _bus.Subscribe<AlarmRaisedEvent>(
                envelope => OnAlarmRaised(envelope), DeliveryMode.Queued, OverflowPolicy.Wait, 1024)));
        _subscriptions.Add(new SubscriptionInfo("报警清除 AlarmCleared",
            _bus.Subscribe<AlarmClearedEvent>(
                envelope => OnAlarmCleared(envelope), DeliveryMode.Queued, OverflowPolicy.Wait, 1024)));
        _subscriptions.Add(new SubscriptionInfo("报警确认 AlarmAcknowledged",
            _bus.Subscribe<AlarmAcknowledgedEvent>(
                envelope => OnAlarmAcknowledged(envelope), DeliveryMode.Queued, OverflowPolicy.Wait, 1024)));

        // 写审计：每条写都有，不允许丢
        _subscriptions.Add(new SubscriptionInfo("写审计 PointWritten",
            _bus.Subscribe<PointWrittenEvent>(
                envelope => OnWritten(envelope), DeliveryMode.Queued, OverflowPolicy.Wait, 4096)));

        // 错误族：按基接口订阅，新增错误类型自动纳入统计
        _subscriptions.Add(new SubscriptionInfo("错误族 IErrorEvent（基接口）",
            _bus.Subscribe<IErrorEvent>(
                envelope => OnError(envelope), DeliveryMode.Queued, OverflowPolicy.DropOldest, 8192)));

        // 事件总线自身的订阅者异常也要可见（否则「统计数字对不上」会查不出原因）
        _bus.HandlerFaulted += ex => ConsoleOut.Line("[" + Stamp() + "] [bus] 订阅者处理异常（总线已吞掉并计数）：" + ex.GetType().Name + ": " + ex.Message);
    }

    public void Dispose()
    {
        foreach (var info in _subscriptions)
        {
            info.Handle.Dispose();
        }

        _subscriptions.Clear();
    }

    // ─────────────── 计数查询（汇总用） ───────────────

    public long ValueEvents => _valueEvents;

    public long ErrorEvents => _errors;

    public long Writes => _writes;

    public IReadOnlyList<SubscriptionInfo> Subscriptions => _subscriptions;

    public IReadOnlyList<string> ActiveAlarms
    {
        get
        {
            lock (_gate)
            {
                return _activeAlarms.Values.ToList();
            }
        }
    }

    public IReadOnlyList<string> RecentAlarms
    {
        get
        {
            lock (_gate)
            {
                return _alarmLog.ToList();
            }
        }
    }

    public IReadOnlyList<string> QualityTransitions
    {
        get
        {
            lock (_gate)
            {
                return _qualityLog.ToList();
            }
        }
    }

    public Dictionary<string, QualityStats> QualitySnapshot()
    {
        lock (_gate)
        {
            return new Dictionary<string, QualityStats>(_quality, StringComparer.Ordinal);
        }
    }

    /// <summary>显示周期采样：把「此刻所有点位的质量」记进分布统计。</summary>
    public void RecordQualitySample(string key, string deviceId, string pointId, PointValue value)
    {
        lock (_gate)
        {
            if (!_quality.TryGetValue(key, out var stats))
            {
                stats = new QualityStats();
                _quality[key] = stats;
            }

            stats.Add(value.Quality);
            stats.LastStamp = value.Timestamp;
            stats.Current = value.Quality;
        }
    }

    // ─────────────── 处理器 ───────────────

    private void OnValue(IEventEnvelope<PointValueChangedEvent> envelope)
    {
        var body = envelope.Body;
        var value = body.Value;

        _valueEvents++;
        switch (value.Quality)
        {
            case PointQuality.Good: _valueGood++; break;
            case PointQuality.Uncertain: _valueUncertain++; break;
            case PointQuality.Bad: _valueBad++; break;
            default: _valueOffline++; break;
        }

        // 质量跃迁是现场最关心的证据（断线/恢复），单独記并打屏
        var key = body.DeviceId + "/" + body.PointId;
        string? transition = null;
        lock (_gate)
        {
            if (!_quality.TryGetValue(key, out var stats))
            {
                stats = new QualityStats();
                _quality[key] = stats;
            }

            // 首次拿到值不算「跃迁」（否则启动瞬间会刷一屏 ？ → Good）
            if (stats.Current.HasValue && stats.Current.Value != value.Quality)
            {
                transition = key + " " + stats.Current.Value + " → " + value.Quality
                             + (string.IsNullOrEmpty(value.Reason) ? string.Empty : "（" + value.Reason + "）")
                             + " 值=" + (value.Value?.ToString() ?? "null");
                stats.Current = value.Quality;
                stats.Transitions++;
                _qualityLog.Add(Stamp() + " " + transition);
                if (_qualityLog.Count > 200) _qualityLog.RemoveAt(0);
            }
        }

        if (transition != null)
        {
            ConsoleOut.Line("[" + Stamp() + "] [q] " + transition);
        }

        _csv.Values.Row(
            envelope.Seq.ToString(CultureInfo.InvariantCulture),
            CsvSink.Time(envelope.Time),
            body.DeviceId,
            body.PointId,
            value.Quality.ToString(),
            CsvSink.Text(value.Value),
            value.Reason ?? string.Empty);
    }

    private void OnAlarmRaised(IEventEnvelope<AlarmRaisedEvent> envelope)
    {
        var body = envelope.Body;
        _alarmRaised++;
        lock (_gate)
        {
            _activeAlarms[body.AlarmId] = body.DeviceId + "/" + body.PointId;
            _alarmLog.Add(Stamp() + " RAISED   " + body.AlarmId + " @" + body.DeviceId + "/" + body.PointId
                          + " type=" + body.AlarmType + " limit=" + CsvSink.Text(body.Limit)
                          + " value=" + CsvSink.Text(body.Value) + " priority=" + ConsoleOut.Show(body.Priority)
                          + " msg=" + ConsoleOut.Show(body.Message));
            if (_alarmLog.Count > 200) _alarmLog.RemoveAt(0);
        }

        ConsoleOut.Line("[" + Stamp() + "] [报警] RAISED  " + body.AlarmId + " " + body.DeviceId + "/" + body.PointId
                        + " type=" + body.AlarmType
                        + " limit=" + CsvSink.Text(body.Limit)
                        + " value=" + CsvSink.Text(body.Value)
                        + " priority=" + ConsoleOut.Show(body.Priority)
                        + " msg=" + ConsoleOut.Show(body.Message));

        _csv.Alarms.Row(
            envelope.Seq.ToString(CultureInfo.InvariantCulture), CsvSink.Time(envelope.Time), "Raised",
            body.AlarmId, body.DeviceId, body.PointId, body.AlarmType,
            CsvSink.Text(body.Limit), CsvSink.Text(body.Value), ConsoleOut.Show(body.Priority), string.Empty,
            ConsoleOut.Show(body.Message));
    }

    private void OnAlarmCleared(IEventEnvelope<AlarmClearedEvent> envelope)
    {
        var body = envelope.Body;
        _alarmCleared++;
        lock (_gate)
        {
            _activeAlarms.Remove(body.AlarmId);
            _alarmLog.Add(Stamp() + " CLEARED  " + body.AlarmId + " @" + body.DeviceId + "/" + body.PointId
                          + " type=" + body.AlarmType);
            if (_alarmLog.Count > 200) _alarmLog.RemoveAt(0);
        }

        ConsoleOut.Line("[" + Stamp() + "] [报警] CLEARED " + body.AlarmId + " " + body.DeviceId + "/" + body.PointId
                        + " type=" + body.AlarmType);

        _csv.Alarms.Row(
            envelope.Seq.ToString(CultureInfo.InvariantCulture), CsvSink.Time(envelope.Time), "Cleared",
            body.AlarmId, body.DeviceId, body.PointId, body.AlarmType,
            string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);
    }

    private void OnAlarmAcknowledged(IEventEnvelope<AlarmAcknowledgedEvent> envelope)
    {
        var body = envelope.Body;
        _alarmAcked++;
        lock (_gate)
        {
            _alarmLog.Add(Stamp() + " ACKED    " + body.AlarmId + " @" + body.DeviceId + "/" + body.PointId
                          + " by " + body.User);
            if (_alarmLog.Count > 200) _alarmLog.RemoveAt(0);
        }

        ConsoleOut.Line("[" + Stamp() + "] [报警] ACKED   " + body.AlarmId + " " + body.DeviceId + "/" + body.PointId
                        + " by " + body.User);

        _csv.Alarms.Row(
            envelope.Seq.ToString(CultureInfo.InvariantCulture), CsvSink.Time(envelope.Time), "Acknowledged",
            body.AlarmId, body.DeviceId, body.PointId, string.Empty,
            string.Empty, string.Empty, string.Empty, body.User, string.Empty);
    }

    private void OnWritten(IEventEnvelope<PointWrittenEvent> envelope)
    {
        var body = envelope.Body;
        _writes++;
        lock (_gate)
        {
            _writeOutcomes.TryGetValue(body.Outcome, out var count);
            _writeOutcomes[body.Outcome] = count + 1;
        }

        // 成功写不重复打屏（操作脚本已打印完整 WriteResult）；非成功一律打屏
        if (!string.Equals(body.Outcome, "Succeeded", StringComparison.Ordinal))
        {
            ConsoleOut.Line("[" + Stamp() + "] [审计] " + body.Outcome + " " + body.DeviceId + "/" + body.PointId
                            + " value=" + CsvSink.Text(body.Value) + " user=" + body.User
                            + " msg=" + ConsoleOut.Show(body.Message));
        }

        _csv.Writes.Row(
            envelope.Seq.ToString(CultureInfo.InvariantCulture), CsvSink.Time(envelope.Time),
            body.DeviceId, body.PointId, CsvSink.Text(body.Value), body.User, body.Outcome,
            ConsoleOut.Show(body.Message));
    }

    private void OnError(IEventEnvelope<IErrorEvent> envelope)
    {
        var error = envelope.Body;
        var info = error.Info;
        var errorClass = ErrorClasses.Classify(error);

        _errors++;
        lock (_gate)
        {
            _errorsByClass.TryGetValue(errorClass.ToString(), out var byClass);
            _errorsByClass[errorClass.ToString()] = byClass + 1;

            _errorsByCode.TryGetValue(info.Code, out var byCode);
            _errorsByCode[info.Code] = byCode + 1;
        }

        if (_errorsPrinted < MaxPrintedErrors)
        {
            _errorsPrinted++;
            ConsoleOut.Line("[" + Stamp() + "] [错误] " + error.GetType().Name
                            + " class=" + errorClass
                            + " code=" + info.Code
                            + " level=" + info.Level
                            + " key=" + info.MessageKey
                            + " " + info.Context);
        }
        else if (_errorsPrinted == MaxPrintedErrors)
        {
            _errorsPrinted++;
            ConsoleOut.Line("[" + Stamp() + "] [错误] 已达打屏上限 " + MaxPrintedErrors.ToString(CultureInfo.InvariantCulture)
                            + " 条，后续只计数与落 CSV");
        }

        _csv.Errors.Row(
            envelope.Seq.ToString(CultureInfo.InvariantCulture), CsvSink.Time(envelope.Time),
            error.GetType().Name, errorClass.ToString(), info.Code, info.Source.ToString(), info.Level.ToString(),
            info.MessageKey, info.Context.ToString());
    }

    // ─────────────── 汇总片段 ───────────────

    public string Counters()
    {
        var text = new StringBuilder(512);
        text.AppendLine("  值变化事件 : " + _valueEvents.ToString(CultureInfo.InvariantCulture)
                        + "（Good " + _valueGood.ToString(CultureInfo.InvariantCulture)
                        + " / Uncertain " + _valueUncertain.ToString(CultureInfo.InvariantCulture)
                        + " / Bad " + _valueBad.ToString(CultureInfo.InvariantCulture)
                        + " / Offline " + _valueOffline.ToString(CultureInfo.InvariantCulture) + "）");
        text.AppendLine("  报警事件   : 激活 " + _alarmRaised.ToString(CultureInfo.InvariantCulture)
                        + "  清除 " + _alarmCleared.ToString(CultureInfo.InvariantCulture)
                        + "  确认 " + _alarmAcked.ToString(CultureInfo.InvariantCulture)
                        + "（结束时仍在激活：" + (_activeAlarms.Count == 0 ? "无" : string.Join(", ", _activeAlarms.Values)) + "）");
        text.AppendLine("  写审计事件 : " + _writes.ToString(CultureInfo.InvariantCulture)
                        + "（" + DescribeOutcomes() + "）");
        text.AppendLine("  错误事件   : " + _errors.ToString(CultureInfo.InvariantCulture)
                        + "（" + DescribeCounts(_errorsByClass) + "）");
        text.Append("  错误码     : " + DescribeCounts(_errorsByCode));
        return text.ToString();
    }

    public string DescribeOutcomes()
    {
        var all = new[] { "Succeeded", "Failed", "Indeterminate", "Rejected" };
        var parts = all.Select(name =>
        {
            _writeOutcomes.TryGetValue(name, out var count);
            return name + " " + count.ToString(CultureInfo.InvariantCulture);
        });
        return string.Join(" / ", parts);
    }

    private static string DescribeCounts(Dictionary<string, long> counts)
        => counts.Count == 0
            ? "无"
            : string.Join(", ", counts.OrderByDescending(p => p.Value)
                .Select(p => p.Key + " " + p.Value.ToString(CultureInfo.InvariantCulture)));

    private string Stamp()
        => "T+" + (DateTime.UtcNow - _startedUtc).TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + "s";
}
