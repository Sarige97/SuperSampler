using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Net.Sockets;
using System.Threading;

namespace SuperSampler.Drivers.Modbus.Wire;

/// <summary>线路帧格式：MBAP（Modbus TCP）或 RTU（unit + PDU + CRC16）。</summary>
public enum WireFormat
{
    /// <summary>Modbus TCP：MBAP 头（事务号/协议号/长度/单元号）。</summary>
    Tcp = 0,

    /// <summary>RTU 帧：从站号 + PDU + CRC16（串口与 RTU-over-TCP 共用）。</summary>
    Rtu = 1,
}

/// <summary>通道变体。</summary>
public enum ChannelVariant
{
    /// <summary>Modbus TCP（MBAP 帧）。</summary>
    Tcp = 0,

    /// <summary>RTU over TCP（RTU 帧走 TCP 流）。</summary>
    RtuOverTcp = 1,

    /// <summary>本地串口 RTU。</summary>
    Serial = 2,
}

/// <summary>Modbus 通讯异常（协议层）。Code 为 Modbus 异常码（0x01~0x0B）。</summary>
public sealed class ModbusProtocolException : Exception
{
    public ModbusProtocolException(byte code)
        : base($"从站返回异常码 0x{code:X2}")
    {
        Code = code;
    }

    /// <summary>Modbus 异常码。</summary>
    public byte Code { get; }
}

/// <summary>通信失败（连接断开、超时、帧错）。区别于协议异常：它是链路级错误。</summary>
public sealed class ModbusIoException : Exception
{
    public ModbusIoException(string message, Exception? inner = null, bool isTimeout = false)
        : base(message, inner)
    {
        IsTimeout = isTimeout;
    }

    /// <summary>true 表示超时类失败（瞬时，可重试）；false 表示链路级失败（应交重连）。</summary>
    public bool IsTimeout { get; }
}

/// <summary>
/// 一条物理链路上的 Modbus 通道。
/// 同一通道严格串行（内部加锁）：Modbus 是请求应答式，并发会让应答错序；
/// 调试工具与轮询共用通道即由此保证「排队执行、不插队」。
/// 断线在下次 Execute 时惰性重连，重试由上层 Master 控制。
/// </summary>
public sealed class ModbusChannel : IDisposable
{
    private readonly ChannelVariant _variant;
    private readonly TransportLike _options;
    private readonly object _gate = new();

    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private SerialPort? _serial;
    private ushort _transactionId;
    private bool _opened;

    /// <summary>构造通道。variant 决定帧格式与底层介质。</summary>
    public ModbusChannel(ChannelVariant variant, TransportLike options)
    {
        _variant = variant;
        _options = options;
    }

    /// <summary>通道是否已建立连接。</summary>
    public bool IsOpen
    {
        get
        {
            lock (_gate) return _opened;
        }
    }

    /// <summary>
    /// 发送一个 PDU（功能码+数据），返回应答 PDU。内部完成拆帧、CRC 校验、事务号校验。
    /// </summary>
    /// <exception cref="ModbusProtocolException">从站返回异常码。</exception>
    /// <exception cref="ModbusIoException">连接、超时、帧错。</exception>
    public byte[] Execute(byte[] pdu, byte unitId, int timeoutMs)
    {
        if (pdu == null || pdu.Length == 0) throw new ModbusIoException("PDU 为空");

        lock (_gate)
        {
            EnsureOpen();

            var wireFormat = _variant == ChannelVariant.Tcp ? WireFormat.Tcp : WireFormat.Rtu;
            ushort txnId = 0;
            var request = wireFormat == WireFormat.Tcp
                ? BuildTcpAdu(pdu, unitId, out txnId)
                : BuildRtuAdu(pdu, unitId);

            try
            {
                WriteAll(request);
                return wireFormat == WireFormat.Tcp
                    ? ReadTcpResponse(unitId, txnId, timeoutMs)
                    : ReadRtuResponse(pdu[0], unitId, timeoutMs);
            }
            catch (ModbusIoException)
            {
                Close(); // 链路级失败：断开，下次惰性重连
                throw;
            }
            finally
            {
                if (_options.GapMs > 0) Thread.Sleep(_options.GapMs);
            }
        }
    }

    /// <summary>关闭连接。</summary>
    public void Close()
    {
        lock (_gate)
        {
            TryClose(() => _stream?.Close());
            TryClose(() => _tcp?.Close());
            TryClose(() => _serial?.Close());
            _stream = null;
            _tcp = null;
            _serial = null;
            _opened = false;
        }
    }

    void IDisposable.Dispose() => Close();

    private static void TryClose(Action action)
    {
        try
        {
            action();
        }
        catch
        {
            // 关闭时的次生异常一律忽略
        }
    }

    // ─────────────── 连接 ───────────────

    private void EnsureOpen()
    {
        if (_opened) return;

        switch (_variant)
        {
            case ChannelVariant.Tcp:
            case ChannelVariant.RtuOverTcp:
                EnsureTcp();
                break;

            case ChannelVariant.Serial:
                EnsureSerial();
                break;

            default:
                throw new ModbusIoException($"不支持的通道变体 {_variant}");
        }

        _opened = true;
    }

    private void EnsureTcp()
    {
        if (string.IsNullOrEmpty(_options.Host)) throw new ModbusIoException("Transport 未配置 host");

        var tcp = new TcpClient();
        try
        {
            var connect = tcp.ConnectAsync(_options.Host!, _options.Port);
            if (!connect.Wait(_options.ConnectTimeoutMs))
            {
                tcp.Close();
                throw new ModbusIoException($"连接 {_options.Host}:{_options.Port} 超时", isTimeout: true);
            }

            tcp.ReceiveTimeout = 200;   // 单次读块短超时，总超时由外层按截止时间控制
            tcp.SendTimeout = _options.RequestTimeoutMs;
            _tcp = tcp;
            _stream = tcp.GetStream();
        }
        catch (ModbusIoException)
        {
            throw;
        }
        catch (Exception ex)
        {
            tcp.Close();
            throw new ModbusIoException($"连接 {_options.Host}:{_options.Port} 失败：{ex.Message}", ex);
        }
    }

    private void EnsureSerial()
    {
        if (string.IsNullOrEmpty(_options.PortName)) throw new ModbusIoException("Transport 未配置 portName");

        try
        {
            var port = new SerialPort(
                _options.PortName,
                _options.BaudRate,
                ParseParity(_options.Parity),
                _options.DataBits,
                ParseStopBits(_options.StopBits))
            {
                // findings W13：流控/DTR/RTS/读写超时改为配置驱动
                Handshake = ParseHandshake(_options.Handshake),
                DtrEnable = _options.DtrEnable,
                RtsEnable = _options.RtsEnable,
                ReadTimeout = Math.Max(1, _options.ReadTimeoutMs),
                WriteTimeout = Math.Max(1, _options.WriteTimeoutMs),
            };
            port.Open();
            _serial = port;
        }
        catch (Exception ex)
        {
            throw new ModbusIoException($"打开串口 {_options.PortName} 失败：{ex.Message}", ex);
        }
    }

    private static Parity ParseParity(string text)
    {
        return text switch
        {
            "even" => Parity.Even,
            "odd" => Parity.Odd,
            "mark" => Parity.Mark,
            "space" => Parity.Space,
            _ => Parity.None,
        };
    }

    private static StopBits ParseStopBits(string text)
    {
        return text switch
        {
            "two" => StopBits.Two,
            "onepointfive" => StopBits.OnePointFive,
            _ => StopBits.One,
        };
    }

    private static Handshake ParseHandshake(string text)
    {
        return text switch
        {
            "xonxoff" => Handshake.XOnXOff,
            "rtscts" => Handshake.RequestToSend,
            "dtrdsr" => Handshake.RequestToSendXOnXOff,
            _ => Handshake.None,
        };
    }

    // ─────────────── 发送 ───────────────

    private byte[] BuildTcpAdu(byte[] pdu, byte unitId, out ushort txnId)
    {
        txnId = ++_transactionId;
        var adu = new byte[7 + pdu.Length];
        adu[0] = (byte)(txnId >> 8);
        adu[1] = (byte)(txnId & 0xFF);
        adu[2] = 0; // 协议号高字节
        adu[3] = 0; // 协议号低字节
        adu[4] = (byte)((pdu.Length + 1) >> 8);
        adu[5] = (byte)((pdu.Length + 1) & 0xFF);
        adu[6] = unitId;
        Array.Copy(pdu, 0, adu, 7, pdu.Length);
        return adu;
    }

    private static byte[] BuildRtuAdu(byte[] pdu, byte unitId)
    {
        var adu = new byte[3 + pdu.Length];
        adu[0] = unitId;
        Array.Copy(pdu, 0, adu, 1, pdu.Length);
        var crc = ModbusCrc16.Compute(adu, 0, adu.Length - 2);
        adu[adu.Length - 2] = (byte)(crc & 0xFF);        // CRC 低字节在前
        adu[adu.Length - 1] = (byte)((crc >> 8) & 0xFF);
        return adu;
    }

    private void WriteAll(byte[] bytes)
    {
        if (_stream != null)
        {
            _stream.Write(bytes, 0, bytes.Length);
            _stream.Flush();
            return;
        }

        if (_serial != null)
        {
            _serial.DiscardInBuffer();
            _serial.Write(bytes, 0, bytes.Length);
            return;
        }

        throw new ModbusIoException("通道未打开");
    }

    // ─────────────── 接收 ───────────────

    private byte[] ReadTcpResponse(byte unitId, ushort txnId, int timeoutMs)
    {
        var header = ReadExact(_stream!, 7, timeoutMs);
        var responseTxn = (ushort)((header[0] << 8) | header[1]);
        if (responseTxn != txnId) throw new ModbusIoException($"事务号不匹配：期望 {txnId}，收到 {responseTxn}");

        var length = (header[4] << 8) | header[5];
        if (length < 2 || length > 260) throw new ModbusIoException($"非法 MBAP 长度 {length}");

        // D14：MBAP 长度域 = 单元号 1 + PDU N，7 字节头已含单元号（header[6]），
        // 剩余 length-1 字节即为纯 PDU，首字节是功能码而非单元号（集成实测抓出）。
        if (header[6] != unitId) throw new ModbusIoException($"从站号不匹配：期望 {unitId}，收到 {header[6]}");

        var pdu = ReadExact(_stream!, length - 1, timeoutMs);
        ValidateFunctionByte(pdu);
        return pdu;
    }

    private byte[] ReadRtuResponse(byte requestFunction, byte unitId, int timeoutMs)
    {
        // RTU 无长度头：按功能码推导应答总长
        var adu = ReadRtuAdu(requestFunction, timeoutMs);
        if (adu[0] != unitId) throw new ModbusIoException($"从站号不匹配：期望 {unitId}，收到 {adu[0]}");

        var expectedCrc = ModbusCrc16.Compute(adu, 0, adu.Length - 2);
        var actualCrc = adu[adu.Length - 2] | (adu[adu.Length - 1] << 8);
        if (expectedCrc != actualCrc) throw new ModbusIoException("CRC 校验失败");

        var pdu = new byte[adu.Length - 3];
        Array.Copy(adu, 1, pdu, 0, pdu.Length);
        ValidateFunctionByte(pdu);
        return pdu;
    }

    private byte[] ReadRtuAdu(byte requestFunction, int timeoutMs)
    {
        // 先读 3 字节（unit, fc, 第三字节），再按 fc 推导剩余长度
        var head = ReadBytes(3, timeoutMs);
        var function = head[1];

        int remaining;
        if ((function & 0x80) != 0)
        {
            remaining = 2;                      // 异常应答：code(1) + crc(2)
        }
        else if (function is 1 or 2 or 3 or 4)
        {
            remaining = head[2] + 2;            // byteCount(已读) + 数据 + CRC
        }
        else if (function is 5 or 6 or 15 or 16)
        {
            remaining = 5;                      // 地址2 + 值/数量2 + CRC2
        }
        else
        {
            remaining = 0;                      // 未知功能码：读尽静默期，交上层报错
            DrainSilence(timeoutMs);
        }

        var rest = remaining > 0 ? ReadBytes(remaining, timeoutMs) : Array.Empty<byte>();
        var adu = new byte[3 + rest.Length];
        Array.Copy(head, adu, 3);
        Array.Copy(rest, 0, adu, 3, rest.Length);
        return adu;
    }

    private void DrainSilence(int timeoutMs)
    {
        var deadline = Environment.TickCount + Math.Min(timeoutMs, 200);
        while (Environment.TickCount < deadline)
        {
            if (!ReadOne(20, out _)) break;
        }
    }

    private byte[] ReadExact(NetworkStream stream, int count, int timeoutMs)
    {
        var buffer = new byte[count];
        var deadline = Environment.TickCount + timeoutMs;
        var offset = 0;
        while (offset < count)
        {
            if (!ReadOne(deadline - Environment.TickCount, out var b))
                throw new ModbusIoException("应答超时或连接断开", isTimeout: true);

            buffer[offset++] = b;
        }

        return buffer;
    }

    private byte[] ReadBytes(int count, int timeoutMs)
    {
        var buffer = new byte[count];
        var deadline = Environment.TickCount + timeoutMs;
        var offset = 0;
        while (offset < count)
        {
            if (!ReadOne(deadline - Environment.TickCount, out var b))
                throw new ModbusIoException("应答超时或连接断开", isTimeout: true);

            buffer[offset++] = b;
        }

        return buffer;
    }

    /// <summary>读单字节；流模式轮询 DataAvailable，串口模式靠 ReadTimeout。超时返回 false。</summary>
    private bool ReadOne(int timeoutMs, out byte value)
    {
        value = 0;
        if (timeoutMs <= 0) return false;

        if (_stream != null)
        {
            if (!_stream.CanRead) throw new ModbusIoException("连接已断开");

            var deadline = Environment.TickCount + timeoutMs;
            while (Environment.TickCount < deadline)
            {
                if (_stream.DataAvailable)
                {
                    var single = new byte[1];
                    var read = _stream.Read(single, 0, 1);
                    if (read == 1)
                    {
                        value = single[0];
                        return true;
                    }

                    throw new ModbusIoException("连接已断开");
                }

                Thread.Sleep(1);
            }

            return false;
        }

        if (_serial != null)
        {
            try
            {
                value = (byte)_serial.ReadByte();
                return true;
            }
            catch (TimeoutException)
            {
                return false;
            }
        }

        throw new ModbusIoException("通道未打开");
    }

    private static void ValidateFunctionByte(byte[] pdu)
    {
        if (pdu.Length == 0) throw new ModbusIoException("空应答");

        if ((pdu[0] & 0x80) != 0)
        {
            if (pdu.Length < 2) throw new ModbusIoException("异常应答缺少异常码");
            throw new ModbusProtocolException(pdu[1]);
        }
    }
}

/// <summary>
/// 通道参数抽象：驱动层不依赖 Core 的配置类型，保持依赖方向（驱动 ← Core）。
/// </summary>
public sealed class TransportLike
{
    public string? Host { get; init; }
    public int Port { get; init; } = 502;
    public string? PortName { get; init; }
    public int BaudRate { get; init; } = 9600;
    public int DataBits { get; init; } = 8;
    public string Parity { get; init; } = "none";
    public string StopBits { get; init; } = "one";
    public int ConnectTimeoutMs { get; init; } = 3000;
    public int RequestTimeoutMs { get; init; } = 1000;
    public int GapMs { get; init; }

    // findings W13：串口流控与读写超时（此前未接线）
    public string Handshake { get; init; } = "none";
    public bool DtrEnable { get; init; }
    public bool RtsEnable { get; init; }
    public int ReadTimeoutMs { get; init; } = 500;
    public int WriteTimeoutMs { get; init; } = 500;
}

/// <summary>Modbus CRC16（多项式 0xA001，即反向 0x8005）。查表实现。</summary>
public static class ModbusCrc16
{
    private static readonly ushort[] Table = BuildTable();

    private static ushort[] BuildTable()
    {
        var table = new ushort[256];
        for (var i = 0; i < 256; i++)
        {
            var value = (ushort)i;
            for (var bit = 0; bit < 8; bit++)
            {
                if ((value & 1) != 0)
                {
                    value = (ushort)((value >> 1) ^ 0xA001);
                }
                else
                {
                    value >>= 1;
                }
            }

            table[i] = value;
        }

        return table;
    }

    /// <summary>计算 CRC16，初值 0xFFFF，低字节在前发送。</summary>
    public static ushort Compute(byte[] data, int offset, int count)
    {
        ushort crc = 0xFFFF;
        for (var i = offset; i < offset + count; i++)
        {
            crc = (ushort)((crc >> 8) ^ Table[(crc ^ data[i]) & 0xFF]);
        }

        return crc;
    }
}
