using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using SuperSampler.Abstractions.Facade;
using SuperSampler.Core.Config;
using SuperSampler.Core.Runtime;

namespace InjectionLineMonitor;

/// <summary>
/// 注塑产线监视线 —— 用 SuperSampler 框架写一个「真实感」的 Console 上位机。
///
/// 它做的正是现场上位机启动后该做的九件事：
///   1. 加载配置并打印解析摘要（设备/点位/扫描组/Warnings）
///   2. 订阅框架事件（值变化/报警三态/写审计/错误族）并落 CSV
///   3. 每 N 秒轮询显示关键点位表（值+质量+坏值原因+数据年龄）
///   4. 按时间执行操作脚本（写设定值/点动/报警确认），写值带 ActingUser
///   5. 断线期间不崩不卡，质量回退可见、恢复可见
///   6. 退出时打印汇总（事件计数/质量分布/写四态直方图/进程资源）
///   7. Ctrl+C 优雅退出
/// 框架负责采集与解析，宿主负责显示、审计、落盘与操作时序 —— 两边职责不重叠。
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            return Run(args);
        }
        catch (Exception ex)
        {
            // 宿主自己的未处理异常必须留下可读证据（现场最怕「程序闪退，什么都没留下」）
            Console.Error.WriteLine("宿主异常退出：" + ex.GetType().FullName);
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine(ex.StackTrace);
            return 4;
        }
    }

    private static int Run(string[] args)
    {
        TryUseUtf8Console();

        HostOptions options;
        try
        {
            options = HostOptions.Parse(args);
        }
        catch (OptionException ex)
        {
            Console.Error.WriteLine("参数错误：" + ex.Message);
            Console.Error.WriteLine();
            Console.Error.WriteLine(HostOptions.Usage);
            return 2;
        }

        if (options.ShowHelp)
        {
            Console.WriteLine(HostOptions.Usage);
            return 0;
        }

        ConsoleOut.Line("╔══════════════════════════════════════════════════════════════════════════════════════╗");
        ConsoleOut.Line("║ InjectionLineMonitor —— 注塑产线监视线（SuperSampler 宿主样例，net46 控制台）        ║");
        ConsoleOut.Line("╚══════════════════════════════════════════════════════════════════════════════════════╝");
        ConsoleOut.Line("  操作者身份 : " + options.UserName + ":" + options.RoleId + "（写值与报警确认都带它）");
        ConsoleOut.Line("  运行时长   : " + options.Duration.TotalSeconds.ToString("0", CultureInfo.InvariantCulture) + "s"
                        + "   显示周期 " + options.DisplayEverySeconds.ToString("0.#", CultureInfo.InvariantCulture) + "s"
                        + "   CSV 目录 " + Path.GetFullPath(options.CsvDirectory));

        var configPath = Path.GetFullPath(options.ConfigPath);
        if (!File.Exists(configPath))
        {
            ConsoleOut.Line("配置文件不存在：" + configPath);
            return 2;
        }

        SamplerConfiguration config;
        try
        {
            config = SamplerConfigLoader.LoadFromXml(configPath);
        }
        catch (ConfigValidationException ex)
        {
            ConsoleOut.Line("配置校验失败（" + ex.Errors.Count.ToString(CultureInfo.InvariantCulture) + " 条）：");
            foreach (var error in ex.Errors)
            {
                ConsoleOut.Line("  · " + error);
            }

            return 3;
        }
        catch (Exception ex)
        {
            ConsoleOut.Line("配置加载失败：" + ex.GetType().Name + ": " + ex.Message);
            return 3;
        }

        var catalog = new PointCatalog(config);
        ConsoleOut.Block(LineSummary.Build(config, catalog, configPath));

        var clock = Stopwatch.StartNew();
        using var csv = new CsvBundle(options.CsvDirectory);
        using var engine = new SamplerEngine(config);
        using var journal = new EventJournal(engine.Bus, csv);

        // 先订阅、后 Start：引擎一启动就会发事件，晚订阅会漏掉第一批
        journal.Subscribe();
        ConsoleOut.Line("  已订阅     : 值变化 / 报警三态 / 写审计 / 错误族（IErrorEvent 基接口）"
                        + "  共 " + journal.Subscriptions.Count.ToString(CultureInfo.InvariantCulture) + " 条订阅");

        using var sampler = new ResourceSampler(journal, clock);
        var dashboard = new Dashboard(engine, catalog, journal, clock, config.Global.StaleAfterMs);
        var user = new ActingUser(options.UserName, options.RoleId);
        var ops = new OpsRunner(options.ScriptPath, engine, catalog, csv, user);

        var stop = new ManualResetEventSlim(false);
        var stopReason = "达到 --duration";

        Console.CancelKeyPress += (sender, eventArgs) =>
        {
            // 宿主自己处理退出：先探测再停，避免把退出信号直接甩给框架
            eventArgs.Cancel = true;
            stopReason = "Ctrl+C";
            stop.Set();
        };

        ConsoleOut.Line("  启动引擎   : " + clock.Elapsed.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture) + "s");
        engine.Start();

        ops.Start();
        ConsoleOut.Line("  操作脚本   : " + ops.Source + "（" + ops.OpCount.ToString(CultureInfo.InvariantCulture) + " 条动作）");
        ConsoleOut.Line(string.Empty);

        var nextDisplay = 2.0;
        var nextFlush = 5.0;

        while (!stop.IsSet)
        {
            var elapsed = clock.Elapsed.TotalSeconds;

            if (elapsed >= nextDisplay)
            {
                dashboard.Capture();
                ConsoleOut.Block(dashboard.Render());
                nextDisplay = elapsed + options.DisplayEverySeconds;
            }

            var sample = sampler.SampleIfDue(30.0, csv);
            if (sample != null)
            {
                ConsoleOut.Line("[" + sample.HostTime + "] [资源] T+"
                                + sample.AtSeconds.ToString("0.0", CultureInfo.InvariantCulture) + "s"
                                + "  线程 " + sample.Threads.ToString(CultureInfo.InvariantCulture)
                                + "  句柄 " + sample.Handles.ToString(CultureInfo.InvariantCulture)
                                + "  GC 后托管堆 " + sample.ManagedHeapMb.ToString("0.00", CultureInfo.InvariantCulture) + " MB");
            }

            if (elapsed >= nextFlush)
            {
                csv.FlushAll();
                nextFlush = elapsed + 5.0;
            }

            if (elapsed >= options.Duration.TotalSeconds)
            {
                stop.Set();
            }

            Thread.Sleep(100);
        }

        var ranSeconds = clock.Elapsed.TotalSeconds;
        ConsoleOut.Line(string.Empty);
        ConsoleOut.Line("[" + DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "] 停机：" + stopReason
                        + "，正在优雅停止（先停操作脚本，再停引擎轮询）…");

        ops.Stop();
        engine.Stop();

        csv.FlushAll();
        ConsoleOut.Block(RunReport.Build(stopReason, configPath, ops.Source, ranSeconds, journal, ops, sampler, csv, engine.Bus));
        return 0;
    }

    /// <summary>
    /// 控制台改 UTF-8：中文点位名与报警文本直接可读（重定向到文件时也是 UTF-8）。
    /// 某些宿主环境（无控制台/被重定向）会拒绝设置编码，失败不影响运行。
    /// </summary>
    private static void TryUseUtf8Console()
    {
        try
        {
            Console.OutputEncoding = new UTF8Encoding(false);
        }
        catch (IOException)
        {
        }
        catch (PlatformNotSupportedException)
        {
        }
    }
}
