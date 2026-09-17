using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using SuperSampler.Abstractions.Errors;
using SuperSampler.Abstractions.Events;
using SuperSampler.Abstractions.Facade;
using SuperSampler.Abstractions.Values;
using SuperSampler.Core.Config;
using SuperSampler.Core.Runtime;
using Xunit;

namespace SuperSampler.IntegrationTests;

/// <summary>
/// ComplexStub 系列集成测试的集合定义：整组测试共用一份夹具、彼此串行
/// （夹具会启动复杂工业桩镜像与可编程断路器，二者都是进程级共享资源，不能并行）。
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ComplexStubCollection : ICollectionFixture<ComplexStubFixture>
{
    public const string Name = "ComplexStub";
}

/// <summary>
/// 真链路集成测试夹具：复杂工业桩镜像（complex_sim.py，4 端口 / 25 从站 / 1029 点）+ 可编程断路器
/// （breaker.py，公共口 2502-2505 → 内部口 16002-16005）。
///
/// 行为约定：
/// *. 内部口（16002-16005）已有实例在听 → 直接复用，不抢不起（不杀别人的进程）；
/// *. 公共口（2502-2505，避开现有 13 项集成测试用的 1502/1503）无人听或已有受控实例 → 自起断路器；
/// *. 端口 2502-2505 被非受控实例占用 → C4 组显式跳过并给出提示，绝不静默；
/// *. 只清理本夹具自己拉起的进程（Dispose）。
/// </summary>
public sealed class ComplexStubFixture : IDisposable
{
    /// <summary>断路器公共口基址（2502-2505；1502/1503 已被 modbus_tcp_sim.py 占用，必须避开）。</summary>
    public const int PublicBasePort = 2502;

    /// <summary>端口数：P1/P2/P3 为 TCP、P4 为 rtuOverTcp。</summary>
    public const int PortCount = 4;

    private const int SimP1 = 16002;
    private const int SimP2 = 16003;
    private const int SimP3 = 16004;
    private const int SimP4 = 16005;

    private readonly object _gate = new();
    private readonly List<Process> _owned = new();
    private readonly Dictionary<int, StringBuilder> _stdout = new();
    private readonly string _workDir;
    private readonly string _python;
    private string? _ctlPath;
    private string? _ackPath;
    private string? _simLogPath;
    private int _seq = 7000;

    /// <summary>_simulator_design/tools 目录（complex_sim.py / breaker.py / complex_ports.json 所在）。</summary>
    public string ToolsDir { get; }

    /// <summary>镜像（内部口）是否可用。</summary>
    public bool StubAvailable { get; }

    /// <summary>断路器（公共口）是否可用；false 时 C4 组跳过。</summary>
    public bool BreakerAvailable { get; }

    /// <summary>RTU-over-TCP 口（16005）是否可用；false 时 badcrc 子项跳过。</summary>
    public bool RtuAvailable { get; }

    /// <summary>镜像不可用原因（可直接展示给使用者）。</summary>
    public string StubUnavailableReason { get; }

    /// <summary>断路器不可用原因。</summary>
    public string BreakerUnavailableReason { get; }

    /// <summary>镜像请求日志是否可用（自起镜像才有 --log；外部实例可能没有）。</summary>
    public bool SimLogAvailable => _simLogPath != null;

    /// <summary>断路器结构化日志路径（JSONL）；断路器不可用时为 null。</summary>
    public string? BreakerLogPath { get; private set; }

    public ComplexStubFixture()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "supersampler_complexstub_" + Process.GetCurrentProcess().Id);
        Directory.CreateDirectory(_workDir);

        ToolsDir = ResolveToolsDir();
        _python = Environment.GetEnvironmentVariable("SUPERSAMPLER_PYTHON") ?? "python";

        (StubAvailable, StubUnavailableReason) = EnsureStub();
        (BreakerAvailable, BreakerUnavailableReason) = EnsureBreaker();
        RtuAvailable = StubAvailable && Probe(SimP4);
    }

    // ─────────────────────────── 环境探测与启动 ───────────────────────────

    private static string ResolveToolsDir()
    {
        var explicitDir = Environment.GetEnvironmentVariable("SUPERSAMPLER_STUB_TOOLS");
        if (!string.IsNullOrWhiteSpace(explicitDir) && File.Exists(Path.Combine(explicitDir!, "complex_sim.py")))
        {
            return explicitDir!;
        }

        // 从测试程序集目录向上找仓库根（SuperSampler.sln），再用仓库外的 _simulator_design/tools
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var tools = Path.Combine(dir.FullName, "..", "_simulator_design", "tools");
            if (File.Exists(Path.Combine(tools, "complex_sim.py"))) return Path.GetFullPath(tools);

            var direct = Path.Combine(dir.FullName, "_simulator_design", "tools");
            if (File.Exists(Path.Combine(direct, "complex_sim.py"))) return Path.GetFullPath(direct);

            dir = dir.Parent;
        }

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "_simulator_design", "tools"));
    }

    private (bool ok, string reason) EnsureStub()
    {
        var script = Path.Combine(ToolsDir, "complex_sim.py");
        if (!File.Exists(script))
        {
            return (false, $"找不到 {script}；请设置环境变量 SUPERSAMPLER_STUB_TOOLS 指向 _simulator_design/tools");
        }

        if (Probe(SimP1) && Probe(SimP2) && Probe(SimP3))
        {
            return (true, "复用已在运行的复杂桩镜像");
        }

        _simLogPath = Path.Combine(_workDir, "complex_sim.log");
        var args = new List<string> { script, "--host", "127.0.0.1", "--log", _simLogPath };
        if (!TryStartPython(args, out var process, out var error))
        {
            _simLogPath = null;
            return (false,
                "无法启动复杂桩镜像（" + error + "）。请手动执行：cd " + ToolsDir + " && python complex_sim.py");
        }

        lock (_gate) _owned.Add(process!);
        if (!WaitForPorts(new[] { SimP1, SimP2, SimP3 }, TimeSpan.FromSeconds(15), out var missing))
        {
            var stdout = DrainStdout(process!);
            var state = process!.HasExited ? "进程已退出(exit=" + process.ExitCode + ")" : "进程仍在运行";
            return (false,
                $"复杂桩镜像未就绪：端口 {missing} 未监听（{state}）。stdout/stderr: {stdout}。" +
                "常见原因：端口被 mbserver 或遗留脚本进程占用（可先跑 kill_mbserver_orphans.ps1），或 python 不在 PATH（可设 SUPERSAMPLER_PYTHON）");
        }

        return (true, "本夹具已拉起复杂桩镜像");
    }

    private (bool ok, string reason) EnsureBreaker()
    {
        var script = Path.Combine(ToolsDir, "breaker.py");
        var mapSource = Path.Combine(ToolsDir, "complex_ports.json");
        if (!File.Exists(script) || !File.Exists(mapSource))
        {
            return (false, $"找不到 {script} 或 {mapSource}");
        }

        var publicPorts = PublicPorts().ToArray();
        if (publicPorts.Any(p => Probe(p)))
        {
            // 公共口有占用：可能是上一次未清理的 breaker.py，也可能被别的进程占着。
            // 不抢端口（外部实例的控制文件路径未知，无法安全驱动），显式跳过 C4。
            return (false,
                $"公共口 {publicPorts[0]}-{publicPorts[3]} 已被占用（可能是上一次遗留的 breaker.py）。" +
                "请先停掉占用进程（taskkill /F /IM python.exe 或关掉对应窗口）后重跑；" +
                "本夹具会把公共口错开到 2502-2505，避免与 1502/1503 的现有集成测试冲突");
        }

        var mapPath = WriteBreakerMap(mapSource);
        _ctlPath = Path.Combine(_workDir, "breaker_ctl.json");
        _ackPath = Path.Combine(_workDir, "breaker_ctl.ack.json");
        BreakerLogPath = Path.Combine(_workDir, "breaker.log");

        var args = new List<string>
        {
            script, "--map", mapPath, "--control", _ctlPath, "--log", BreakerLogPath,
            "--no-console", "--quiet", "--interval", "0.1",
            "--rules", Path.Combine(_workDir, "no_such_rules.json"),
        };
        if (!TryStartPython(args, out var process, out var error))
        {
            return (false, "无法启动断路器（" + error + "）");
        }

        lock (_gate) _owned.Add(process!);
        if (!WaitForPorts(publicPorts, TimeSpan.FromSeconds(15), out var missing))
        {
            var stdout = DrainStdout(process!);
            var state = process!.HasExited ? "进程已退出(exit=" + process.ExitCode + ")" : "进程仍在运行";
            return (false,
                $"断路器未就绪：公共口 {missing} 未监听（{state}）。stdout/stderr: {stdout}。" +
                "常见原因：公共口被别的进程占用（可能是上次遗留的 breaker.py 或 mbserver），请先停掉占用者");
        }

        // 端口在听 ≠ 控制通路可用：再走一遍真实控制文件，确认能拿到回执
        // （这里刻意绕过 InjectAsync 的可用性守卫——此时代理刚起、BreakerAvailable 还没落值）
        try
        {
            var probeSeq = Interlocked.Increment(ref _seq);
            var ack = SendBreakerCommandsAsync(
                "{\"seq\":" + probeSeq + ",\"commands\":[{\"port\":" + PublicBasePort + ",\"action\":\"status\"}]}",
                probeSeq, TimeSpan.FromSeconds(6)).GetAwaiter().GetResult();
            if (!ack.Ok)
            {
                return (false, "断路器不响应控制文件（回执 ok=false）：" + ack.Raw);
            }
        }
        catch (Exception ex)
        {
            return (false, "断路器控制通路不可用：" + ex.GetType().Name + "：" + ex.Message);
        }

        return (true, "本夹具已拉起断路器（公共口 " + publicPorts[0] + "-" + publicPorts[3] + "）");
    }

    private string WriteBreakerMap(string sourcePath)
    {
        var text = File.ReadAllText(sourcePath, Encoding.UTF8);
        var doc = MiniJson.AsObject(MiniJson.Parse(text));
        var ports = doc == null ? null : MiniJson.AsArray(doc["ports"]);
        if (ports == null)
        {
            throw new InvalidOperationException("complex_ports.json 结构异常：缺少 ports 数组");
        }

        var sb = new StringBuilder();
        sb.Append("{\"ports\":[");
        for (var i = 0; i < ports.Count; i++)
        {
            var port = MiniJson.AsObject(ports[i])!;
            if (i > 0) sb.Append(',');
            sb.Append('{');
            sb.Append("\"id\":\"").Append(port.Str("id")).Append("\",");
            sb.Append("\"transport\":\"").Append(port.Str("transport")).Append("\",");
            sb.Append("\"internal_port\":").Append(port.Int("internal_port")).Append(',');
            sb.Append("\"public_port\":").Append(PublicBasePort + i).Append(',');
            sb.Append("\"slaves\":[");
            var slaves = MiniJson.AsArray(port["slaves"]) ?? new List<object?>();
            for (var j = 0; j < slaves.Count; j++)
            {
                var slave = MiniJson.AsObject(slaves[j])!;
                if (j > 0) sb.Append(',');
                sb.Append("{\"unit\":").Append(slave.Int("unit"))
                    .Append(",\"name\":\"").Append(slave.Str("name")).Append("\"}");
            }

            sb.Append("]}");
        }

        sb.Append("]}");
        var path = Path.Combine(_workDir, "breaker_map.json");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        return path;
    }

    private bool TryStartPython(List<string> args, out Process? process, out string error)
    {
        process = null;
        error = string.Empty;
        try
        {
            var psi = new ProcessStartInfo(_python)
            {
                WorkingDirectory = ToolsDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            foreach (var arg in args) psi.ArgumentListAdd(arg);

            var started = Process.Start(psi);
            if (started == null)
            {
                error = "Process.Start 返回 null";
                return false;
            }

            started.OutputDataReceived += (_, e) => AppendStdout(started, e.Data);
            started.ErrorDataReceived += (_, e) => AppendStdout(started, e.Data);
            started.BeginOutputReadLine();
            started.BeginErrorReadLine();
            process = started;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.GetType().Name + ": " + ex.Message;
            return false;
        }
    }

    private void AppendStdout(Process process, string? line)
    {
        if (line == null) return;
        lock (_gate)
        {
            if (!_stdout.TryGetValue(process.Id, out var sb))
            {
                sb = new StringBuilder();
                _stdout[process.Id] = sb;
            }

            if (sb.Length < 4000) sb.AppendLine(line);
        }
    }

    private string DrainStdout(Process process)
    {
        lock (_gate)
        {
            return _stdout.TryGetValue(process.Id, out var sb) ? sb.ToString().Trim() : "(无输出)";
        }
    }

    // ─────────────────────────── 端口探测 ───────────────────────────

    private static bool Probe(int port, int timeoutMs = 400)
    {
        try
        {
            using var client = new TcpClient();
            return client.ConnectAsync("127.0.0.1", port).Wait(timeoutMs) && client.Connected;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private bool WaitForPorts(IReadOnlyList<int> ports, TimeSpan timeout, out string missing)
    {
        var deadline = DateTime.UtcNow + timeout;
        missing = string.Empty;
        while (DateTime.UtcNow < deadline)
        {
            var notUp = ports.Where(p => !Probe(p)).ToArray();
            if (notUp.Length == 0)
            {
                missing = string.Empty;
                return true;
            }

            missing = string.Join(",", notUp);
            Thread.Sleep(200);
        }

        return false;
    }

    // ─────────────────────────── 端口基址 ───────────────────────────

    /// <summary>公共口（经断路器）：2502 / 2503 / 2504 / 2505。</summary>
    public IReadOnlyList<int> PublicPorts() => Enumerable.Range(0, PortCount).Select(i => PublicBasePort + i).ToArray();

    /// <summary>P1-LINE-A 内部口（产线 A：IM/MTC/DRY/ROB，unit 1-4）。</summary>
    public int SimPortP1 => SimP1;

    /// <summary>P2-LINE-B 内部口（产线 B，unit 1-4）。</summary>
    public int SimPortP2 => SimP2;

    /// <summary>P3-VERIFY 内部口（总电表/环境/四字序/只读/写保护/边界/慢响应 unit 5-15）。</summary>
    public int SimPortP3 => SimP3;

    /// <summary>P4-RTU-LINE 内部口（rtuOverTcp，unit 1-4 + 20/21）。</summary>
    public int SimPortP4 => SimP4;

    // ─────────────────────────── 显式跳过 ───────────────────────────

    /// <summary>镜像不可用 → 跳过并给出可操作提示。</summary>
    public void RequireStub()
    {
        Skip.If(!StubAvailable,
            "复杂桩镜像不可用：" + StubUnavailableReason +
            "。请先执行：cd " + ToolsDir + " && python complex_sim.py（内部口 " + SimP1 + "-" + SimP4 + "）");
    }

    /// <summary>断路器不可用 → 跳过 C4 组。</summary>
    public void RequireBreaker()
    {
        RequireStub();
        Skip.If(!BreakerAvailable, "可编程断路器不可用：" + BreakerUnavailableReason +
                                  "。请先执行：cd " + ToolsDir + " && python breaker.py --map <改好公共口的 complex_ports.json>");
    }

    /// <summary>RTU 口（16005）不可用 → 跳过 badcrc 子项。</summary>
    public void RequireRtu()
    {
        RequireStub();
        Skip.If(!RtuAvailable, "复杂桩未启用 RTU-over-TCP 口 " + SimP4 + "（启动时加了 --no-rtu？）");
    }

    /// <summary>镜像请求日志不可用 → 跳过「零通讯/请求次数」类断言。</summary>
    public void RequireSimLog()
    {
        RequireStub();
        Skip.If(!SimLogAvailable,
            "镜像请求日志不可用（复用的是外部实例，未带 --log 启动）。" +
            "如需验证「零通讯 / 每周期请求数」，请停掉外部实例让夹具自起，或手动加 --log 启动");
    }

    // ─────────────────────────── 断路器控制 ───────────────────────────

    /// <summary>一次断路器控制回执。</summary>
    public sealed class BreakerAck
    {
        public BreakerAck(int seq, bool ok, string raw, IReadOnlyList<Dictionary<string, object?>> results)
        {
            Seq = seq;
            Ok = ok;
            Raw = raw;
            Results = results;
        }

        public int Seq { get; }
        public bool Ok { get; }
        public string Raw { get; }
        public IReadOnlyList<Dictionary<string, object?>> Results { get; }

        public override string ToString() => "seq=" + Seq + " ok=" + Ok + " " + Raw;
    }

    /// <summary>写入控制文件并等待回执（seq 单调递增，保证每条命令都被执行一次）。</summary>
    public Task<BreakerAck> InjectAsync(TimeSpan timeout, params string[] commands)
    {
        RequireBreaker();
        var seq = Interlocked.Increment(ref _seq);
        var json = "{\"seq\":" + seq + ",\"commands\":[" + string.Join(",", commands) + "]}";
        return SendBreakerCommandsAsync(json, seq, timeout);
    }

    /// <summary>注入单条命令（默认 8s 超时）。</summary>
    public Task<BreakerAck> InjectAsync(string command, TimeSpan? timeout = null)
        => InjectAsync(timeout ?? TimeSpan.FromSeconds(8), command);

    private async Task<BreakerAck> SendBreakerCommandsAsync(string json, int seq, TimeSpan timeout)
    {
        if (_ctlPath == null || _ackPath == null) throw new InvalidOperationException("断路器控制文件路径未初始化");

        File.WriteAllText(_ctlPath, json, new UTF8Encoding(false));
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var ack = ReadAck(_ackPath);
            if (ack != null && ack.Int("seq") == seq)
            {
                var results = MiniJson.AsArray(ack["results"]) ?? new List<object?>();
                var parsed = results.Select(r => MiniJson.AsObject(r) ?? new Dictionary<string, object?>())
                    .ToList();
                return new BreakerAck(seq, ack.Bool("ok") ?? false, ack.Raw, parsed);
            }

            await Task.Delay(50).ConfigureAwait(false);
        }

        throw new TimeoutException("等待断路器回执超时（seq=" + seq + "，文件 " + _ackPath + "）");
    }

    private static AckDoc? ReadAck(string ackPath)
    {
        try
        {
            if (!File.Exists(ackPath)) return null;
            var text = File.ReadAllText(ackPath, Encoding.UTF8);
            var obj = MiniJson.AsObject(MiniJson.Parse(text));
            return obj == null ? null : new AckDoc(obj, text);
        }
        catch (IOException)
        {
            return null; // 正在被断路器替换
        }
        catch (MiniJson.JsonException)
        {
            return null; // 半截文件
        }
    }

    private sealed class AckDoc
    {
        public AckDoc(Dictionary<string, object?> fields, string raw)
        {
            Fields = fields;
            Raw = raw;
        }

        public Dictionary<string, object?> Fields { get; }
        public string Raw { get; }

        public object? this[string key] => Fields.TryGetValue(key, out var value) ? value : null;

        public int? Int(string key) => Fields.Int(key);
        public bool? Bool(string key) => Fields.Bool(key);
    }

    /// <summary>清除所有端口的故障并恢复在线（每条注入测试的开头与结尾都该调一次）。</summary>
    public Task<BreakerAck> ClearAllAsync()
    {
        var commands = PublicPorts().Select(p => "{\"port\":" + p + ",\"action\":\"clear\"}").ToArray();
        return InjectAsync(TimeSpan.FromSeconds(10), commands);
    }

    /// <summary>恢复某端口（或某端口某从站）在线。注意：只清离线状态，不清 drop/delay/badcrc 之类故障规则。</summary>
    public Task<BreakerAck> OnlineAsync(int publicPort, int? unit = null)
        => InjectAsync("{\"port\":" + publicPort + ",\"action\":\"online\"" + (unit.HasValue ? ",\"unit\":" + unit.Value : "") + "}");

    /// <summary>清除某端口（或某从站）的全部故障规则并恢复在线。</summary>
    public Task<BreakerAck> ClearAsync(int publicPort, int? unit = null)
        => InjectAsync("{\"port\":" + publicPort + ",\"action\":\"clear\"" + (unit.HasValue ? ",\"unit\":" + unit.Value : "") + "}");

    /// <summary>读断路器结构化日志（逐行 JSON）。</summary>
    public IReadOnlyList<Dictionary<string, object?>> ReadBreakerLog()
    {
        var path = BreakerLogPath;
        if (path == null || !File.Exists(path)) return Array.Empty<Dictionary<string, object?>>();

        var records = new List<Dictionary<string, object?>>();
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.Length == 0) continue;
                try
                {
                    var obj = MiniJson.AsObject(MiniJson.Parse(line));
                    if (obj != null) records.Add(obj);
                }
                catch (MiniJson.JsonException)
                {
                    // 半截行：忽略
                }
            }
        }
        catch (IOException)
        {
            // 日志文件正被写：返回已读到的
        }

        return records;
    }

    /// <summary>统计断路器日志中某端口的 frame 记录数（可选方向 / unit）。</summary>
    public int CountBreakerFrames(int publicPort, string? direction = null, int? unit = null)
        => ReadBreakerLog().Count(r =>
            r.Str("event") == "frame"
            && r.Int("port") == publicPort
            && (direction == null || r.Str("dir") == direction)
            && (unit == null || r.Int("unit") == unit));

    /// <summary>等断路器日志里出现满足条件的记录。</summary>
    public async Task<bool> WaitBreakerLogAsync(Func<Dictionary<string, object?>, bool> predicate, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (ReadBreakerLog().Any(predicate)) return true;
            await Task.Delay(100).ConfigureAwait(false);
        }

        return false;
    }

    // ─────────────────────────── 镜像请求日志 ───────────────────────────

    /// <summary>镜像请求日志行数（外部实例未带 --log 时为 -1）。</summary>
    public int CountSimLogLines(int port, int unit, string? function = null)
    {
        if (_simLogPath == null || !File.Exists(_simLogPath)) return -1;

        var marker = "port=" + port + " unit=" + unit;
        var count = 0;
        try
        {
            using var stream = new FileStream(_simLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.IndexOf(marker, StringComparison.Ordinal) < 0) continue;
                if (function != null && line.IndexOf("fc=" + function, StringComparison.Ordinal) < 0) continue;
                count++;
            }
        }
        catch (IOException)
        {
        }

        return count;
    }

    // ─────────────────────────── 配置 / 引擎辅助 ───────────────────────────

    /// <summary>把 XML 文本加载为已校验配置（相对路径以夹具工作目录为基准）。</summary>
    public SamplerConfiguration LoadConfig(string xml)
        => SamplerConfigLoader.Load(XDocument.Parse(xml), _workDir);

    /// <summary>取一个独立工作子目录（i18n 文件等测试产物）。</summary>
    public string WorkSubDir(string name)
    {
        var path = Path.Combine(_workDir, name);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>生成 &lt;Point&gt; 元素；attrs 为附加属性串（如 mode / intervalMs / enabled / unitId / swap），inner 为子元素串。</summary>
    public static string Point(string id, int address, string dataType, string attrs = "", string inner = "")
        => "<Point id=\"" + id + "\" address=\"" + address + "\" dataType=\"" + dataType + "\" " + attrs + ">" + inner + "</Point>";

    /// <summary>生成带 alarm 子元素的 Point。</summary>
    public static string PointWithAlarm(string id, int address, string dataType, string alarmInner, string attrs = "")
        => Point(id, address, dataType, attrs, "<Alarm " + alarmInner + " />");

    /// <summary>生成 &lt;Transport&gt;（tcp / rtuOverTcp）。</summary>
    public static string Transport(string id, int port, string variant = "tcp", int requestTimeoutMs = 800)
        => "<Transport id=\"" + id + "\" host=\"127.0.0.1\" port=\"" + port + "\" variant=\"" + variant +
           "\" connectTimeoutMs=\"1500\" requestTimeoutMs=\"" + requestTimeoutMs + "\" />";

    /// <summary>生成 &lt;Device&gt;。</summary>
    public static string Device(string id, string transport, int unitId, string pointSet, string attrs = "")
        => "<Device id=\"" + id + "\" transport=\"" + transport + "\" unitId=\"" + unitId + "\" pointSet=\"" + pointSet + "\" " + attrs + " />";

    // ─────────────────────────── 断言辅助 ───────────────────────────

    /// <summary>等点位质量到达期望值；超时抛 TimeoutException（带最后一次的质量与原因）。</summary>
    public static async Task<PointValue> WaitQualityAsync(IDeviceManager manager, string deviceId, string pointId,
        PointQuality expected, int timeoutMs = 8000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var value = manager.GetValueDetail(deviceId, pointId);
            if (value.Quality == expected) return value;
            await Task.Delay(50).ConfigureAwait(false);
        }

        var last = manager.GetValueDetail(deviceId, pointId);
        throw new TimeoutException("点位 " + deviceId + "/" + pointId + " 未在 " + timeoutMs + "ms 内变为 " + expected +
                                   "（实际=" + last.Quality + "，reason=" + (last.Reason ?? "null") + "）");
    }

    /// <summary>等点位 Good。</summary>
    public static Task<PointValue> WaitGoodAsync(IDeviceManager manager, string deviceId, string pointId, int timeoutMs = 8000)
        => WaitQualityAsync(manager, deviceId, pointId, PointQuality.Good, timeoutMs);

    /// <summary>等点位 Good 且值满足条件；返回该值。</summary>
    public static async Task<T> WaitValueAsync<T>(IDeviceManager manager, string deviceId, string pointId,
        Func<T, bool> predicate, int timeoutMs = 8000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var value = manager.GetValueDetail(deviceId, pointId);
            if (value.Quality == PointQuality.Good && value.TryGetValue<T>(out var typed) && predicate(typed))
            {
                return typed;
            }

            await Task.Delay(50).ConfigureAwait(false);
        }

        var last = manager.GetValueDetail(deviceId, pointId);
        throw new TimeoutException("点位 " + deviceId + "/" + pointId + " 未在 " + timeoutMs + "ms 内满足条件（实际=" + last + "）");
    }

    /// <summary>取点位值并断言类型。</summary>
    public static T GetAs<T>(PointValue value)
    {
        Assert.True(value.TryGetValue<T>(out var typed),
            "值类型不符：期望 " + typeof(T).Name + "，实际 " + (value.Value?.GetType().Name ?? "null") + "（" + value + "）");
        return typed;
    }

    /// <summary>轮询等待条件成立。</summary>
    public static async Task<bool> WaitUntilAsync(Func<bool> condition, int timeoutMs, int pollMs = 50)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(pollMs).ConfigureAwait(false);
        }

        return condition();
    }

    /// <summary>阻塞式等待（断路器命令之后的短暂静默期用）。</summary>
    public static void Sleep(int ms) => Thread.Sleep(ms);

    // ─────────────────────────── 事件订阅辅助 ───────────────────────────

    /// <summary>线程安全的事件收集器：订阅某类事件，可在测试里等待/断言。</summary>
    public sealed class EventCollector<TEvent> : IDisposable where TEvent : IEvent
    {
        private readonly List<TEvent> _bodies = new();
        private readonly List<long> _seqs = new();
        private readonly object _gate = new();
        private readonly ISubscription _subscription;

        public EventCollector(IEventBus bus, DeliveryMode mode = DeliveryMode.Queued,
            OverflowPolicy overflow = OverflowPolicy.DropOldest, int? capacity = null)
        {
            _subscription = bus.Subscribe<TEvent>(envelope =>
            {
                lock (_gate)
                {
                    _bodies.Add(envelope.Body);
                    _seqs.Add(envelope.Seq);
                }
            }, mode, overflow, capacity);
        }

        public IReadOnlyList<TEvent> Items
        {
            get { lock (_gate) return _bodies.ToArray(); }
        }

        public int Count
        {
            get { lock (_gate) return _bodies.Count; }
        }

        public async Task<bool> WaitCountAsync(int atLeast, int timeoutMs = 8000)
            => await WaitUntilAsync(() => Count >= atLeast, timeoutMs).ConfigureAwait(false);

        /// <summary>等一条满足条件的事件出现。</summary>
        public async Task<bool> WaitAsync(Func<TEvent, bool> predicate, int timeoutMs = 8000)
            => await WaitUntilAsync(() => Items.Any(predicate), timeoutMs).ConfigureAwait(false);

        public void Dispose() => _subscription.Dispose();
    }

    // ─────────────────────────── 释放 ───────────────────────────

    public void Dispose()
    {
        List<Process> processes;
        lock (_gate)
        {
            processes = _owned.ToList();
            _owned.Clear();
        }

        foreach (var process in processes)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                    process.WaitForExit(3000);
                }
            }
            catch (Exception)
            {
                // 忽略：退出竞态
            }
            finally
            {
                try { process.Dispose(); } catch (Exception) { }
            }
        }

        try
        {
            if (Directory.Exists(_workDir)) Directory.Delete(_workDir, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>断言辅助短别名（测试里高频使用，避免每处都写完整类名）。</summary>
internal static class F
{
    public static T GetAs<T>(PointValue value) => ComplexStubFixture.GetAs<T>(value);
}

/// <summary>ProcessStartInfo.ArgumentList 在 net462 不存在，用等价的转义追加。</summary>
internal static class ProcessStartInfoExtensions
{
    public static void ArgumentListAdd(this ProcessStartInfo info, string argument)
    {
        if (info.Arguments.Length > 0) info.Arguments += " ";
        info.Arguments += argument.IndexOf(' ') >= 0 || argument.IndexOf('"') >= 0
            ? "\"" + argument.Replace("\"", "\\\"") + "\""
            : argument;
    }
}

/// <summary>极小 JSON 解析器（只为本夹具解析断路器回执/日志，避免引入依赖）。</summary>
internal static class MiniJson
{
    public sealed class JsonException : Exception
    {
        public JsonException(string message) : base(message) { }
    }

    public static object? Parse(string text)
    {
        var index = 0;
        var value = ParseValue(text, ref index);
        return value;
    }

    public static Dictionary<string, object?>? AsObject(object? value) => value as Dictionary<string, object?>;

    public static List<object?>? AsArray(object? value) => value as List<object?>;

    private static object? ParseValue(string text, ref int index)
    {
        SkipWhitespace(text, ref index);
        if (index >= text.Length) return null;

        switch (text[index])
        {
            case '{': return ParseObject(text, ref index);
            case '[': return ParseArray(text, ref index);
            case '"': return ParseString(text, ref index);
            case 't':
                Expect(text, ref index, "true");
                return true;
            case 'f':
                Expect(text, ref index, "false");
                return false;
            case 'n':
                Expect(text, ref index, "null");
                return null;
            default: return ParseNumber(text, ref index);
        }
    }

    private static Dictionary<string, object?> ParseObject(string text, ref int index)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        index++; // {
        while (true)
        {
            SkipWhitespace(text, ref index);
            if (index >= text.Length) throw new JsonException("对象未闭合");
            if (text[index] == '}')
            {
                index++;
                return result;
            }

            var key = ParseString(text, ref index);
            SkipWhitespace(text, ref index);
            if (index >= text.Length || text[index] != ':') throw new JsonException("对象缺少冒号");
            index++;
            result[key] = ParseValue(text, ref index);
            SkipWhitespace(text, ref index);
            if (index < text.Length && text[index] == ',')
            {
                index++;
                continue;
            }

            if (index < text.Length && text[index] == '}')
            {
                index++;
                return result;
            }

            throw new JsonException("对象分隔符异常");
        }
    }

    private static List<object?> ParseArray(string text, ref int index)
    {
        var result = new List<object?>();
        index++; // [
        while (true)
        {
            SkipWhitespace(text, ref index);
            if (index >= text.Length) throw new JsonException("数组未闭合");
            if (text[index] == ']')
            {
                index++;
                return result;
            }

            result.Add(ParseValue(text, ref index));
            SkipWhitespace(text, ref index);
            if (index < text.Length && text[index] == ',')
            {
                index++;
                continue;
            }

            if (index < text.Length && text[index] == ']')
            {
                index++;
                return result;
            }

            throw new JsonException("数组分隔符异常");
        }
    }

    private static string ParseString(string text, ref int index)
    {
        SkipWhitespace(text, ref index);
        if (index >= text.Length || text[index] != '"') throw new JsonException("期望字符串");
        index++;
        var sb = new StringBuilder();
        while (index < text.Length)
        {
            var c = text[index++];
            if (c == '"') return sb.ToString();
            if (c != '\\')
            {
                sb.Append(c);
                continue;
            }

            if (index >= text.Length) break;
            var esc = text[index++];
            switch (esc)
            {
                case '"': sb.Append('"'); break;
                case '\\': sb.Append('\\'); break;
                case '/': sb.Append('/'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case 'u':
                    if (index + 4 > text.Length) throw new JsonException("\\u 转义不完整");
                    var code = int.Parse(text.Substring(index, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    sb.Append((char)code);
                    index += 4;
                    break;
                default: throw new JsonException("未知转义 \\" + esc);
            }
        }

        throw new JsonException("字符串未闭合");
    }

    private static object ParseNumber(string text, ref int index)
    {
        var start = index;
        while (index < text.Length && (char.IsDigit(text[index]) || text[index] == '-' || text[index] == '+'
                                       || text[index] == '.' || text[index] == 'e' || text[index] == 'E'))
        {
            index++;
        }

        var slice = text.Substring(start, index - start);
        if (slice.Length == 0) throw new JsonException("期望数值");
        return double.Parse(slice, CultureInfo.InvariantCulture);
    }

    private static void Expect(string text, ref int index, string literal)
    {
        if (index + literal.Length > text.Length ||
            string.CompareOrdinal(text, index, literal, 0, literal.Length) != 0)
        {
            throw new JsonException("期望字面量 " + literal);
        }

        index += literal.Length;
    }

    private static void SkipWhitespace(string text, ref int index)
    {
        while (index < text.Length && (text[index] == ' ' || text[index] == '\t' || text[index] == '\r' || text[index] == '\n'))
        {
            index++;
        }
    }
}

/// <summary>JSON 记录的取值辅助（数值统一由 double 转换）。</summary>
internal static class JsonRecordExtensions
{
    public static string? Str(this Dictionary<string, object?> record, string key)
        => record.TryGetValue(key, out var value) ? value as string : null;

    public static int? Int(this Dictionary<string, object?> record, string key)
    {
        if (!record.TryGetValue(key, out var value) || value == null) return null;
        return value switch
        {
            double d => (int)Math.Round(d),
            int i => i,
            long l => (int)l,
            string s => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : (int?)null,
            _ => null,
        };
    }

    public static bool? Bool(this Dictionary<string, object?> record, string key)
    {
        if (!record.TryGetValue(key, out var value) || value == null) return null;
        return value switch
        {
            bool b => b,
            string s => bool.TryParse(s, out var parsed) ? parsed : (bool?)null,
            _ => null,
        };
    }
}
