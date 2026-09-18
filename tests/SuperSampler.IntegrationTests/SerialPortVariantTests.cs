using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
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
/// 原生串口 RTU（<c>variant="rtu"</c>）真串口协议栈验证。
/// 手段：虚拟串口对（VSPD COM1⇄COM2 或 com0com）+ pyserial 从站
/// （`_simulator_design/tools/serial_rtu_slave.py`）——框架 `rtu` 变体真正 open 一个 COM 口，
/// 走完整 SerialPort 栈（端口配置 / 读写字节 / 坏帧 / 超时退避），补上"RTU 帧层已验、串口栈未跑"的空白。
/// RS485 电气 / 半双工方向切换（W80）仍属真硬件项，不在本测试范围。
///
/// 门控：无虚拟串口对时跳过（CI 不受影响）。环境变量：
///   SUPERSAMPLER_SERIAL_COM    = "COM1,COM2"（框架侧端口, 从站侧端口）
///   SUPERSAMPLER_SERIAL_SLAVE  = serial_rtu_slave.py 路径（缺省按仓库相对位置探测）
/// </summary>
[Collection("serial-virtual-ports")]
public sealed class SerialPortVariantTests : IDisposable
{
    private const string XmlTpl =
        "<HostConfig schemaVersion=\"3.0\">"
        + "<Global language=\"zh_CN\" fallbackLanguage=\"en_US\" nullText=\"--\" swap=\"none\">"
        + "<Polling defaultIntervalMs=\"500\" requestTimeoutMs=\"1500\" />"
        + "<Reconnect enabled=\"true\" delays=\"500,1000\" manualRetry=\"true\" offlineQuality=\"offline\" />"
        + "<Quality onCommError=\"bad\" onCommErrorValue=\"null\" />"
        + "</Global>"
        + "<Transports><Transport id=\"ser\" variant=\"rtu\" enabled=\"true\" portName=\"{0}\" "
        + "baudRate=\"{1}\" parity=\"{2}\" stopBits=\"{3}\" gapMs=\"30\" /></Transports>"
        + "<Devices><Device id=\"IM01\" transport=\"ser\" unitId=\"1\" pointSet=\"ps\" /></Devices>"
        + "<PointSets><PointSet id=\"ps\"><Defaults area=\"input\" dataType=\"uint16\" swap=\"none\" /><Points>"
        + "<Point id=\"alarm\" area=\"discrete\" address=\"3\" dataType=\"bool\" />"
        + "<Point id=\"alarmCode\" area=\"input\" address=\"1\" />"
        + "<Point id=\"barrelTemp\" area=\"input\" address=\"30\" dataType=\"int16\"><Scale factor=\"0.1\" /></Point>"
        + "<Point id=\"moldTemp\" area=\"input\" address=\"35\" dataType=\"int16\"><Scale factor=\"0.1\" /></Point>"
        + "<Point id=\"target\" area=\"holding\" address=\"1\" access=\"readwrite\"><Write verify=\"true\" /></Point>"
        + "<Point id=\"setTemp\" area=\"holding\" address=\"2\" dataType=\"int16\" access=\"readwrite\"><Scale factor=\"0.1\" /><Write verify=\"true\" /></Point>"
        + "</Points></PointSet></PointSets>"
        + "</HostConfig>";

    private readonly string _frameworkCom;
    private readonly string _slaveCom;
    private readonly string _slaveScript;
    private readonly string _slaveDir;
    private SamplerEngine? _engine;
    private Process? _slave;

    public SerialPortVariantTests()
    {
        var comEnv = Environment.GetEnvironmentVariable("SUPERSAMPLER_SERIAL_COM");
        _frameworkCom = comEnv?.Split(',')[0].Trim() ?? string.Empty;
        _slaveCom = comEnv?.Split(',')[1].Trim() ?? string.Empty;

        _slaveScript = Environment.GetEnvironmentVariable("SUPERSAMPLER_SERIAL_SLAVE") ?? string.Empty;
        if (string.IsNullOrEmpty(_slaveScript))
        {
            // 仓库相对探测：tools 在 SuperSampler 的上级目录 _simulator_design
            var repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
            var probe = Path.GetFullPath(Path.Combine(repo, "_simulator_design", "tools", "serial_rtu_slave.py"));
            if (File.Exists(probe)) _slaveScript = probe;
        }

        _slaveDir = Path.GetDirectoryName(_slaveScript) ?? string.Empty;
    }

    public void Dispose()
    {
        StopSlave();
        _engine?.Dispose();
    }

    private bool Available
        => !string.IsNullOrEmpty(_frameworkCom) && !string.IsNullOrEmpty(_slaveCom) &&
           !string.IsNullOrEmpty(_slaveScript) && File.Exists(_slaveScript);

    private string Python => Environment.GetEnvironmentVariable("SUPERSAMPLER_PYTHON") ?? "python";

    private void StartSlave(int baud = 9600, string parity = "N", string stop = "1")
    {
        var psi = new ProcessStartInfo(Python)
        {
            WorkingDirectory = _slaveDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentListAdd(_slaveScript);
        psi.ArgumentListAdd(_slaveCom);
        psi.ArgumentListAdd(baud.ToString());
        psi.ArgumentListAdd(parity);
        psi.ArgumentListAdd(stop);
        _slave = Process.Start(psi);
    }

    private void StopSlave()
    {
        if (_slave != null && !_slave.HasExited)
        {
            _slave.Kill();
            _slave.WaitForExit(3000);
        }

        _slave = null;
    }

    private void StartEngine(int baud = 9600, string parity = "none", string stop = "one")
    {
        var xml = string.Format(XmlTpl, _frameworkCom, baud, parity, stop);
        var config = SamplerConfigLoader.Load(XDocument.Parse(xml), AppContext.BaseDirectory);
        _engine = new SamplerEngine(config);
        _engine.Start();
    }

    private static bool WaitUntil(Func<bool> condition, int timeoutMs, string because)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            Thread.Sleep(25);
        }

        return condition();
    }

    private PointValue Detail(string pointId) => _engine!.GetValueDetail("IM01", pointId);

    // ═══════════════ 1. 端口打开 + 参数映射 + 轮询读值 ═══════════════

    [SkippableFact]
    public void Rtu_variant_opens_the_serial_port_and_polls_good_values()
    {
        Skip.If(!Available, "未配置虚拟串口对（SUPERSAMPLER_SERIAL_COM + SUPERSAMPLER_SERIAL_SLAVE），跳过");

        StartSlave();
        StartEngine();

        Assert.True(WaitUntil(() => Detail("barrelTemp").IsGood, 8000, "温度点应在几拍内采到好值"));
        var temp = Detail("barrelTemp");
        Assert.True(temp.IsGood && temp.Value != null && Convert.ToDouble(temp.Value) > 0,
            "料筒温度应 > 0：实际 " + temp);
        Assert.True(Detail("alarmCode").IsGood, "报警码应可读");
        Assert.True(Detail("alarm").IsGood, "离散输入报警应可读");
        Assert.NotNull(_engine!.GetValueAge("IM01", "barrelTemp"));   // 成功采集过
    }

    // ═══════════════ 2. 参数真的落位（不同波特率/校验/停止位下仍能通） ═══════════════

    [SkippableFact]
    public void Rtu_configured_baud_parity_stopbits_are_honoured()
    {
        Skip.If(!Available, "未配置虚拟串口对，跳过");

        // 用非默认参数：从站按 19200/E/2 起；若框架没按配置设置端口参数，两端速率不符必然失败
        StartSlave(baud: 19200, parity: "E", stop: "2");
        StartEngine(baud: 19200, parity: "even", stop: "two");

        Assert.True(WaitUntil(() => Detail("barrelTemp").IsGood, 8000,
            "19200/even/two 参数下应正常通讯（框架真的按配置设置了端口参数）"));
    }

    // ═══════════════ 3. 写 + verify 回读 + 超范围 Rejected ═══════════════

    [SkippableFact]
    public async System.Threading.Tasks.Task Rtu_write_with_verify_readback_and_out_of_range_rejected()
    {
        Skip.If(!Available, "未配置虚拟串口对，跳过");

        StartSlave();
        StartEngine();
        Assert.True(WaitUntil(() => Detail("barrelTemp").IsGood, 8000, "等待就绪"));

        // 写设定温度 240.0℃（int16 ×0.1 → raw 2400）
        var wr = await _engine!.SetValueAsync("IM01", "setTemp", 240.0);
        Assert.True(wr.Outcome == WriteOutcome.Succeeded,
            "写应成功：Outcome=" + wr.Outcome + " Error=" + (wr.Error != null ? wr.Error.Code + "/" + wr.Error.MessageKey : "无") +
            " Readback=" + wr.Readback + " VerifyMismatch=" + wr.VerifyMismatch);
        Assert.False(wr.VerifyMismatch, "回读应一致：" + wr);

        Assert.True(WaitUntil(() =>
        {
            var v = Detail("setTemp");
            return v.IsGood && v.Value != null && Math.Abs(Convert.ToDouble(v.Value) - 240.0) < 1.0;
        }, 4000, "写后缓存应回读为 240.0"));

        // 超范围写 → Rejected（未发通讯）
        var wr2 = await _engine.SetValueAsync("IM01", "target", 99999.0);
        Assert.Equal(WriteOutcome.Rejected, wr2.Outcome);
    }

    // ═══════════════ 4. 坏 CRC 应答 → 链路错 → 自动恢复 ═══════════════

    [SkippableFact]
    public void Rtu_bad_crc_response_is_classified_and_the_link_recovers()
    {
        Skip.If(!Available, "未配置虚拟串口对，跳过");

        StartSlave();
        StartEngine();
        Assert.True(WaitUntil(() => Detail("barrelTemp").IsGood, 8000, "等待就绪"));

        var errors = new ComplexStubFixture.EventCollector<IErrorEvent>(_engine!.Bus);
        var flag = Path.Combine(_slaveDir, "badcrc.flag");
        File.WriteAllText(flag, "1");

        // 注入的坏 CRC 应答应在数拍内被识别为链路/协议错误
        Assert.True(WaitUntil(() => errors.Count > 0, 6000, "坏 CRC 应触发错误事件"));
        Assert.True(WaitUntil(() => Detail("barrelTemp").IsGood, 8000,
            "坏帧后应自动恢复为 Good（不串数据、不崩）"));
        Assert.False(File.Exists(flag), "标志文件应被从站消费掉");
    }

    // ═══════════════ 5. 从站停止 → 超时退避 → 从站重启 → 自动恢复 ═══════════════

    [SkippableFact]
    public void Rtu_slave_restart_recovers_after_timeout_backoff()
    {
        Skip.If(!Available, "未配置虚拟串口对，跳过");

        StartSlave();
        StartEngine();
        Assert.True(WaitUntil(() => Detail("barrelTemp").IsGood, 8000, "等待就绪"));

        StopSlave();   // 拔掉从站 → 串口在但无人应答 → 读超时 → 设备级退避
        Assert.True(WaitUntil(() => !Detail("barrelTemp").IsGood, 8000,
            "从站消失后值应离开 Good（超时退避）"));

        StartSlave();  // 从站回来
        Assert.True(WaitUntil(() => Detail("barrelTemp").IsGood, 12000,
            "从站重启后应按退避节奏自动恢复为 Good"));
    }
}
