using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SuperSampler.Abstractions.Facade;
using SuperSampler.Abstractions.Values;
using SuperSampler.Core.Config;
using SuperSampler.Core.Runtime;

namespace InjectionLineMonitor;

/// <summary>
/// 操作脚本执行器：按「启动后第几秒」执行 write / pulse / ack / show / note。
/// 现场对应物：巡检流程、配方下发、交接班操作——都是带时序的固定动作，不该靠人手点。
/// 所有写操作都带 ActingUser（宿主负责认证，框架按角色授权并写审计）。
/// </summary>
internal sealed class OpsRunner
{
    private readonly string? _scriptPath;
    private readonly IDeviceManager _manager;
    private readonly PointCatalog _catalog;
    private readonly CsvBundle _csv;
    private readonly ActingUser _user;
    private readonly List<Op> _ops = new List<Op>();
    private readonly ManualResetEventSlim _stop = new ManualResetEventSlim(false);
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private Thread? _thread;

    public OpsRunner(string? scriptPath, IDeviceManager manager, PointCatalog catalog, CsvBundle csv, ActingUser user)
    {
        _scriptPath = scriptPath;
        _manager = manager;
        _catalog = catalog;
        _csv = csv;
        _user = user;
    }

    public int OpCount => _ops.Count;

    public string Source => _scriptPath == null ? "内置默认序列" : _scriptPath;

    /// <summary>宿主侧的动作统计（框架侧的写审计直方图在 EventJournal.WriteOutcomes）。</summary>
    public long WriteAttempts { get; private set; }

    public long PulseAttempts { get; private set; }

    public long VerifyMismatches { get; private set; }

    public long AckAttempts { get; private set; }

    public long AckNotPending { get; private set; }

    public long ShowCount { get; private set; }

    public long ActionErrors { get; private set; }

    public void Start()
    {
        _ops.Clear();
        if (_scriptPath == null)
        {
            BuildDefaultSequence();
        }
        else
        {
            ParseFile(_scriptPath);
        }

        _clock.Restart();
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "InjectionLineMonitor.Ops",
        };
        _thread.Start();
    }

    public void Stop()
    {
        _stop.Set();
        _thread?.Join(5000);
    }

    // ─────────────── 脚本解析 ───────────────

    private void ParseFile(string path)
    {
        if (!File.Exists(path))
        {
            throw new OptionException("操作脚本不存在：" + path);
        }

        var lines = File.ReadAllLines(path);
        for (var i = 0; i < lines.Length; i++)
        {
            var raw = lines[i];
            var text = raw.Trim();
            if (text.Length == 0 || text.StartsWith("#", StringComparison.Ordinal)) continue;

            var parts = text.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                ConsoleOut.Line("[ops] 第 " + (i + 1).ToString(CultureInfo.InvariantCulture) + " 行无法解析（缺命令）：" + text);
                continue;
            }

            double at;
            try
            {
                at = ParseTime(parts[0]);
            }
            catch (OptionException ex)
            {
                ConsoleOut.Line("[ops] 第 " + (i + 1).ToString(CultureInfo.InvariantCulture) + " 行时间非法：" + ex.Message);
                continue;
            }

            var op = new Op
            {
                AtSeconds = at,
                Index = _ops.Count + 1,
                Raw = text,
                Command = parts[1].ToLowerInvariant(),
                Target = parts.Length > 2 ? parts[2] : string.Empty,
                Arg = parts.Length > 3 ? string.Join(" ", parts.Skip(3)) : null,
            };

            _ops.Add(op);
        }
    }

    /// <summary>时间后缀：s=秒（缺省）、m=分；支持 12.5s。</summary>
    private static double ParseTime(string text)
    {
        var unit = text.Length > 0 ? char.ToLowerInvariant(text[text.Length - 1]) : 's';
        var number = unit is 's' or 'm' ? text.Substring(0, text.Length - 1) : text;
        if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var amount))
        {
            throw new OptionException("无法解析时间：" + text);
        }

        return unit == 'm' ? amount * 60.0 : amount;
    }

    /// <summary>
    /// 无 --script 时的默认动作序列：写设定值、点动、确认报警。
    /// 目标点从配置里找（第一个带 pulseMs 的点、第一个带 min/max 的可写数值点、第一个配了报警的点），
    /// 这样换成别的工程也能跑，不会写死点位名。
    /// </summary>
    private void BuildDefaultSequence()
    {
        var pulse = _catalog.AllPoints().FirstOrDefault(p => p.Write != null && p.Write.PulseMs > 0);
        var setpoint = _catalog.AllPoints().FirstOrDefault(p =>
            p.IsWritable && p.Write != null && p.Write.Min.HasValue && p.Write.Max.HasValue
            && p.Area != RuntimeArea.Coil && p.Area != RuntimeArea.DiscreteInput && p.DataType != RuntimeDataType.String);
        var alarmPoint = _catalog.AllPoints().FirstOrDefault(p => p.Source.Alarms.Count > 0);

        if (setpoint != null && setpoint.Write != null)
        {
            var min = setpoint.Write.Min ?? 0;
            var max = setpoint.Write.Max ?? 0;
            var middle = Math.Round((min + max) / 2.0, 1);
            _ops.Add(NewOp(_ops.Count + 1, 5, "write", setpoint.DeviceId + "/" + setpoint.PointId,
                middle.ToString("0.###", CultureInfo.InvariantCulture)));
        }

        if (pulse != null)
        {
            _ops.Add(NewOp(_ops.Count + 1, 15, "pulse", pulse.DeviceId + "/" + pulse.PointId, null));
        }

        if (alarmPoint != null)
        {
            _ops.Add(NewOp(_ops.Count + 1, 25, "ack", alarmPoint.DeviceId + "/" + alarmPoint.PointId, null));
        }
    }

    private static Op NewOp(int index, double at, string command, string target, string? arg)
        => new Op
        {
            Index = index,
            AtSeconds = at,
            Command = command,
            Target = target,
            Arg = arg,
            Raw = command + " " + target,
        };

    // ─────────────── 执行循环 ───────────────

    private void Loop()
    {
        double previous = 0;

        foreach (var op in _ops)
        {
            if (op.AtSeconds < previous)
            {
                ConsoleOut.Line("[" + Stamp(op.AtSeconds) + "] [ops] 第 " + op.Index.ToString(CultureInfo.InvariantCulture)
                                + " 条时间早于上一条，已按当前时刻立即执行：" + op.Raw);
            }

            previous = Math.Max(previous, op.AtSeconds);

            while (!_stop.IsSet && _clock.Elapsed.TotalSeconds < op.AtSeconds)
            {
                var remain = op.AtSeconds - _clock.Elapsed.TotalSeconds;
                if (remain > 0.05)
                {
                    _stop.Wait(TimeSpan.FromMilliseconds(Math.Min(remain * 1000, 100)));
                }
            }

            if (_stop.IsSet) return;

            try
            {
                Execute(op);
            }
            catch (Exception ex)
            {
                // 宿主脚本层绝不让异常逃逸：一条动作失败不影响后续巡检
                ConsoleOut.Line("[" + Stamp(_clock.Elapsed.TotalSeconds) + "] [ops] 第 "
                                + op.Index.ToString(CultureInfo.InvariantCulture) + " 条执行异常：" + ex.GetType().Name + ": " + ex.Message);
            }
        }
    }

    private void Execute(Op op)
    {
        switch (op.Command)
        {
            case "note":
                ConsoleOut.Line("[" + Stamp(_clock.Elapsed.TotalSeconds) + "] [ops] —— " + (op.Target + " " + (op.Arg ?? string.Empty)).Trim() + " ——");
                return;

            case "show":
                Show(op);
                return;

            case "write":
                Write(op, false);
                return;

            case "pulse":
                Write(op, true);
                return;

            case "ack":
                Ack(op);
                return;

            default:
                ConsoleOut.Line("[" + Stamp(_clock.Elapsed.TotalSeconds) + "] [ops] 未知命令：" + op.Command);
                return;
        }
    }

    private void Show(Op op)
    {
        ShowCount++;
        var point = _catalog.Resolve(op.Target);
        var detail = _manager.GetValueDetail(point.DeviceId, point.PointId);
        var formatted = detail.IsGood ? _manager.GetValue(point.DeviceId, point.PointId) : "<非 Good，门面返回 nullText>";

        ConsoleOut.Line("[" + Stamp(_clock.Elapsed.TotalSeconds) + "] [ops] show " + point.DeviceId + "/" + point.PointId
                        + " = " + formatted
                        + "  [质量 " + detail.Quality
                        + (string.IsNullOrEmpty(detail.Reason) ? string.Empty : " / " + detail.Reason)
                        + "]  原始=" + (detail.Value?.ToString() ?? "null")
                        + "  时间戳=" + detail.Timestamp.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture));

        if (!detail.IsGood)
        {
            ConsoleOut.Line("        注意：质量非 Good 时门面 GetValue 只返回全局 nullText，宿主必须用 GetValueDetail 才能看到原因");
        }
    }

    private void Write(Op op, bool pulse)
    {
        var point = _catalog.Resolve(op.Target);
        var isPulse = pulse || (point.Write != null && point.Write.PulseMs > 0);

        object? value;
        if (isPulse && op.Command == "pulse")
        {
            value = true;
        }
        else if (op.Arg == null)
        {
            ConsoleOut.Line("[" + Stamp(_clock.Elapsed.TotalSeconds) + "] [ops] write 缺少值：" + op.Raw);
            return;
        }
        else
        {
            try
            {
                value = ConvertValue(point, op.Arg);
            }
            catch (Exception ex)
            {
                ConsoleOut.Line("[" + Stamp(_clock.Elapsed.TotalSeconds) + "] [ops] 值无法转为 " + point.DataType + "：" + ex.Message);
                ActionErrors++;
                _csv.Ops.Row(CsvSink.Time(DateTimeOffset.Now), Sec(), op.Index.ToString(CultureInfo.InvariantCulture),
                    op.Command, point.DeviceId + "/" + point.PointId, op.Arg, "ArgError", string.Empty, string.Empty, string.Empty,
                    ex.Message);
                return;
            }
        }

        if (isPulse && op.Command == "pulse")
        {
            PulseAttempts++;
        }
        else
        {
            WriteAttempts++;
        }

        var watch = Stopwatch.StartNew();
        WriteResult result;
        try
        {
            result = _manager.SetValueAsync(point.DeviceId, point.PointId, value, _user).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            ActionErrors++;
            ConsoleOut.Line("[" + Stamp(_clock.Elapsed.TotalSeconds) + "] [ops] 写入调用异常：" + ex.GetType().Name + ": " + ex.Message);
            return;
        }

        watch.Stop();

        if (result.VerifyMismatch)
        {
            VerifyMismatches++;
        }

        // 回读值必须用 result.Readback 本身：门面 GetValue 读的是轮询缓存，
        // 在慢扫描组上可能是几秒前的旧值，用它当回读证据会误判（本宿主第一版就踩了这个坑）。
        var readbackText = "-";
        var readbackQuality = "-";
        if (result.Readback.HasValue)
        {
            var readback = result.Readback.Value;
            readbackQuality = readback.Quality.ToString();
            readbackText = CsvSink.Text(readback.Value)
                           + (string.IsNullOrEmpty(point.Unit) ? string.Empty : " " + point.Unit)
                           + "[" + readback.Quality + "]";
        }

        var line = "[" + Stamp(_clock.Elapsed.TotalSeconds) + "] [ops] "
                   + (isPulse ? "pulse " : "write ")
                   + point.DeviceId + "/" + point.PointId
                   + " = " + CsvSink.Text(value)
                   + " → " + result.Outcome
                   + "  verifyMismatch=" + (result.VerifyMismatch ? "True" : "False")
                   + "  readback=" + readbackText
                   + "  " + watch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + "ms"
                   + "  user=" + _user.Name + ":" + _user.RoleId;

        ConsoleOut.Line(line);

        if (result.Error != null)
        {
            ConsoleOut.Line("        error: code=" + result.Error.Code + " key=" + result.Error.MessageKey
                            + " source=" + result.Error.Source + " " + result.Error.Context);
        }

        if (result.VerifyMismatch)
        {
            ConsoleOut.Line("        注意：设备应答成功但回读值不等于写入值（设备可能自行钳位）；框架按 Succeeded 返回并置 VerifyMismatch，宿主应提示而不是自动重试");
        }

        _csv.Ops.Row(CsvSink.Time(DateTimeOffset.Now), Sec(), op.Index.ToString(CultureInfo.InvariantCulture),
            isPulse ? "pulse" : "write", point.DeviceId + "/" + point.PointId, CsvSink.Text(value),
            result.Outcome.ToString(), result.VerifyMismatch ? "True" : "False",
            readbackText, readbackQuality, result.Error?.Code ?? string.Empty);
    }

    private void Ack(Op op)
    {
        var point = _catalog.Resolve(op.Target);
        var alarmIds = op.Arg != null && op.Arg.Length > 0
            ? new List<string> { op.Arg }
            : PointCatalog.AlarmIdsOf(point);

        if (alarmIds.Count == 0)
        {
            ConsoleOut.Line("[" + Stamp(_clock.Elapsed.TotalSeconds) + "] [ops] 该点位没有配置报警，无法确认：" + point.DeviceId + "/" + point.PointId);
            return;
        }

        foreach (var alarmId in alarmIds)
        {
            AckAttempts++;
            AlarmAckResult result;
            try
            {
                result = _manager.AcknowledgeAlarmAsync(point.DeviceId, point.PointId, alarmId, _user).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                ConsoleOut.Line("[" + Stamp(_clock.Elapsed.TotalSeconds) + "] [ops] 确认调用异常：" + ex.GetType().Name + ": " + ex.Message);
                continue;
            }

            if (result.Outcome == AlarmAckOutcome.NotPending) AckNotPending++;

            var hint = result.Outcome == AlarmAckOutcome.NotPending
                ? "（该报警当前无状态记录：从未触发或已确认过；框架不发事件、也不算失败）"
                : "（AckPending 已清，报警回到可重触发状态）";

            ConsoleOut.Line("[" + Stamp(_clock.Elapsed.TotalSeconds) + "] [ops] ack " + point.DeviceId + "/" + point.PointId
                            + " alarmId=" + alarmId + " → " + result.Outcome + " " + hint
                            + " user=" + _user.Name);

            _csv.Ops.Row(CsvSink.Time(DateTimeOffset.Now), Sec(), op.Index.ToString(CultureInfo.InvariantCulture),
                "ack", point.DeviceId + "/" + point.PointId, alarmId, result.Outcome.ToString(),
                string.Empty, string.Empty, string.Empty, string.Empty);
        }
    }

    /// <summary>一条脚本动作。</summary>
    private sealed class Op
    {
        public double AtSeconds;
        public int Index;
        public string Raw = string.Empty;
        public string Command = string.Empty;
        public string Target = string.Empty;
        public string? Arg;
    }

    private string Sec() => _clock.Elapsed.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture);

    private string Stamp(double seconds) => "T+" + seconds.ToString("0.0", CultureInfo.InvariantCulture) + "s";

    /// <summary>
    /// 按点位 dataType 转换脚本里的字符串值。
    /// 为什么不直接把这个字符串交给门面：SetValueAsync 是 object? 入参，
    /// 传字符串会走 Convert.ToDouble 路径（int64/uint64 大数会丢精度，字符串点又必须保持字符串）。
    /// </summary>
    private static object ConvertValue(RuntimePoint point, string text)
    {
        var value = text.Trim();

        switch (point.DataType)
        {
            case RuntimeDataType.Bool:
                if (bool.TryParse(value, out var flag)) return flag;
                return Number(value) != 0;

            // 整型点也允许写小数文本（"28.0"）：现场工程量常带一位小数，
            // 缩放由框架按 Scale 处理，宿主只负责把文本转成目标类型。
            case RuntimeDataType.Int16: return (short)Integral(value, short.MinValue, short.MaxValue);
            case RuntimeDataType.UInt16: return (ushort)Integral(value, ushort.MinValue, ushort.MaxValue);
            case RuntimeDataType.Int32: return (int)Integral(value, int.MinValue, int.MaxValue);
            case RuntimeDataType.UInt32: return (uint)Integral(value, uint.MinValue, uint.MaxValue);
            case RuntimeDataType.Int64: return (long)Integral(value, long.MinValue, long.MaxValue);
            case RuntimeDataType.UInt64: return (ulong)Integral(value, 0, long.MaxValue);
            case RuntimeDataType.Float32: return (float)Number(value);
            case RuntimeDataType.Float64: return Number(value);

            // BCD / datetime 目前没有对应的编码实现（Encode 只覆盖数值/布尔/字符串）：
            // 传整数会按普通数字落字，读回来就不是 BCD 语义了。宿主只能读、不能可靠写。
            case RuntimeDataType.Bcd: return Integral(value, 0, long.MaxValue);
            case RuntimeDataType.DateTime: return DateTime.Parse(value, CultureInfo.InvariantCulture);

            default: return value;
        }
    }

    private static double Number(string text)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            throw new FormatException("不是合法数值：" + text);
        }

        return parsed;
    }

    private static long Integral(string text, long min, long max)
    {
        var rounded = Math.Round(Number(text), MidpointRounding.AwayFromZero);
        if (rounded < min || rounded > max)
        {
            throw new OverflowException("超出目标类型范围 [" + min + ", " + max + "]：" + text);
        }

        return (long)rounded;
    }
}
