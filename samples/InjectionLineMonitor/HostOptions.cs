using System;
using System.Globalization;

namespace InjectionLineMonitor;

/// <summary>命令行选项解析失败（宿主自己的错误，与框架无关）。</summary>
internal sealed class OptionException : Exception
{
    public OptionException(string message) : base(message)
    {
    }
}

/// <summary>
/// 宿主命令行。
///   --config &lt;path&gt;    宿主配置（缺省：当前目录的 line.xml）
///   --duration &lt;time&gt;  运行时长，如 90s / 5m / 300（缺省秒）
///   --script &lt;file&gt;    操作脚本；缺省走内置的默认操作序列
///   --csv &lt;dir&gt;        CSV 输出目录（缺省 csv）
///   --every &lt;sec&gt;      显示周期秒（缺省 2）
///   --user &lt;name:role&gt; 写入/确认所用的操作者身份（缺省 op01:Operator）
///   --help             打印用法
/// </summary>
internal sealed class HostOptions
{
    public string ConfigPath { get; private set; } = "line.xml";

    public TimeSpan Duration { get; private set; } = TimeSpan.FromSeconds(60);

    public string? ScriptPath { get; private set; }

    public string CsvDirectory { get; private set; } = "csv";

    public double DisplayEverySeconds { get; private set; } = 2.0;

    public string UserName { get; private set; } = "op01";

    public string RoleId { get; private set; } = "Operator";

    public bool ShowHelp { get; private set; }

    public static string Usage =>
        "用法: InjectionLineMonitor [--config line.xml] [--duration 90s|5m] [--script ops.txt]" + Environment.NewLine +
        "                          [--csv <目录>] [--every <秒>] [--user <名:角色>] [--help]" + Environment.NewLine +
        Environment.NewLine +
        "  --config    宿主配置路径（相对当前目录；缺省 line.xml）" + Environment.NewLine +
        "  --duration  运行时长，支持 s/m/h 后缀，纯数字按秒（缺省 60s）" + Environment.NewLine +
        "  --script    操作脚本；不写则执行内置默认序列（写设定值/点动/确认报警）" + Environment.NewLine +
        "  --csv       事件落盘目录（缺省 csv）" + Environment.NewLine +
        "  --every     点位表刷新周期，秒（缺省 2）" + Environment.NewLine +
        "  --user      写入与报警确认的操作者，格式 名:角色（缺省 op01:Operator）";

    public static HostOptions Parse(string[] args)
    {
        var options = new HostOptions();

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--help":
                case "-h":
                case "/?":
                    options.ShowHelp = true;
                    break;

                case "--config":
                    options.ConfigPath = Require(args, ref i, "--config");
                    break;

                case "--duration":
                    options.Duration = ParseDuration(Require(args, ref i, "--duration"));
                    break;

                case "--script":
                    options.ScriptPath = Require(args, ref i, "--script");
                    break;

                case "--csv":
                    options.CsvDirectory = Require(args, ref i, "--csv");
                    break;

                case "--every":
                    options.DisplayEverySeconds = ParseSeconds(Require(args, ref i, "--every"));
                    if (options.DisplayEverySeconds < 0.5) options.DisplayEverySeconds = 0.5;
                    break;

                case "--user":
                    var user = Require(args, ref i, "--user");
                    var split = user.IndexOf(':');
                    if (split <= 0)
                    {
                        throw new OptionException("--user 需要 名:角色 形式，例如 op01:Operator");
                    }

                    options.UserName = user.Substring(0, split);
                    options.RoleId = user.Substring(split + 1);
                    break;

                default:
                    throw new OptionException("无法识别的参数：" + arg);
            }
        }

        if (options.Duration <= TimeSpan.Zero)
        {
            throw new OptionException("--duration 必须大于 0");
        }

        return options;
    }

    private static string Require(string[] args, ref int index, string name)
    {
        if (index + 1 >= args.Length)
        {
            throw new OptionException(name + " 缺少参数值");
        }

        index++;
        return args[index];
    }

    /// <summary>时长解析：90s / 5m / 1h / 300（纯数字按秒）。</summary>
    private static TimeSpan ParseDuration(string text)
    {
        var value = text.Trim();
        var unit = value.Length > 0 ? char.ToLowerInvariant(value[value.Length - 1]) : 's';
        var number = unit is 's' or 'm' or 'h' ? value.Substring(0, value.Length - 1) : value;

        if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var amount))
        {
            throw new OptionException("无法解析时长：" + text);
        }

        return unit switch
        {
            'm' => TimeSpan.FromMinutes(amount),
            'h' => TimeSpan.FromHours(amount),
            _ => TimeSpan.FromSeconds(amount),
        };
    }

    private static double ParseSeconds(string text)
    {
        if (!double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            throw new OptionException("无法解析秒数：" + text);
        }

        return seconds;
    }
}
