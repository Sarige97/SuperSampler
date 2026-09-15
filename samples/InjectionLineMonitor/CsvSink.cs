using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace InjectionLineMonitor;

/// <summary>
/// 最简 CSV 落盘器。为什么宿主自己写：框架只发事件、不落盘（ADR D1），
/// 现场要的是「事后能对账的文件」，所以每一个宿主都得有一个这样的东西。
/// 线程安全：事件泵线程 + 操作脚本线程都会写。
/// </summary>
internal sealed class CsvSink : IDisposable
{
    private readonly object _gate = new object();
    private readonly StreamWriter _writer;
    private int _disposed;

    public CsvSink(string path, params string[] header)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // UTF-8 with BOM：现场用 Excel 直接双击打开 CSV 时不乱码。
        // FileShare.Read：允许别人同时读（tail/Excel），但不允许第二个宿主写同一个文件——
        // 那种情况会抛 IOException，这里换成能直接看懂的提示（现场最常见的原因是上一个实例还在跑）。
        try
        {
            var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            _writer = new StreamWriter(stream, new UTF8Encoding(true));
        }
        catch (IOException ex)
        {
            throw new IOException("CSV 文件无法写入：" + path + "（是否另一个宿主实例还在运行？）：" + ex.Message, ex);
        }

        _writer.WriteLine(string.Join(",", header));
    }

    /// <summary>写一行。字段里的逗号/引号/换行会被转义。</summary>
    public void Row(params string?[] fields)
    {
        var line = new StringBuilder(128);
        for (var i = 0; i < fields.Length; i++)
        {
            if (i > 0) line.Append(',');
            line.Append(Escape(fields[i]));
        }

        lock (_gate)
        {
            if (_disposed != 0) return;
            _writer.WriteLine(line.ToString());
        }
    }

    /// <summary>把时间统一成 ISO 8601 带毫秒的本地时间串，便于人工比对现场记录。</summary>
    public static string Time(DateTimeOffset value)
        => value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);

    /// <summary>数值/对象转字符串，保持 invariant（避免中文区域小数点变逗号）。</summary>
    public static string Text(object? value)
        => value == null ? string.Empty : Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;

    public void Flush()
    {
        lock (_gate)
        {
            if (_disposed != 0) return;
            _writer.Flush();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed != 0) return;
            _disposed = 1;
            _writer.Flush();
            _writer.Dispose();
        }
    }

    private static string Escape(string? field)
    {
        if (string.IsNullOrEmpty(field)) return string.Empty;

        var text = field!;
        if (text.IndexOf(',') < 0 && text.IndexOf('"') < 0 && text.IndexOf('\n') < 0 && text.IndexOf('\r') < 0)
        {
            return text;
        }

        return "\"" + text.Replace("\"", "\"\"") + "\"";
    }
}

/// <summary>一次运行用到的全部 CSV 汇（值/报警/写审计/错误/资源/脚本动作）。</summary>
internal sealed class CsvBundle : IDisposable
{
    private readonly List<CsvSink> _all;

    public CsvBundle(string directory)
    {
        Directory.CreateDirectory(directory);

        Values = new CsvSink(System.IO.Path.Combine(directory, "values.csv"),
            "seq", "host_time", "device", "point", "quality", "value", "reason");

        Alarms = new CsvSink(System.IO.Path.Combine(directory, "alarms.csv"),
            "seq", "host_time", "state", "alarm_id", "device", "point", "type", "limit", "value", "priority", "user", "message");

        Writes = new CsvSink(System.IO.Path.Combine(directory, "writes.csv"),
            "seq", "host_time", "device", "point", "value", "user", "outcome", "message");

        Errors = new CsvSink(System.IO.Path.Combine(directory, "errors.csv"),
            "seq", "host_time", "event_type", "error_class", "code", "source", "level", "message_key", "context");

        Ops = new CsvSink(System.IO.Path.Combine(directory, "ops.csv"),
            "host_time", "at_s", "index", "command", "target", "arg", "outcome", "verify_mismatch", "readback", "readback_quality", "detail");

        Resources = new CsvSink(System.IO.Path.Combine(directory, "resources.csv"),
            "host_time", "at_s", "threads", "handles", "managed_heap_mb", "value_events", "error_events");

        _all = new List<CsvSink> { Values, Alarms, Writes, Errors, Ops, Resources };
    }

    public CsvSink Values { get; }

    public CsvSink Alarms { get; }

    public CsvSink Writes { get; }

    public CsvSink Errors { get; }

    public CsvSink Ops { get; }

    public CsvSink Resources { get; }

    public void FlushAll()
    {
        foreach (var sink in _all) sink.Flush();
    }

    public void Dispose()
    {
        foreach (var sink in _all) sink.Dispose();
    }
}
