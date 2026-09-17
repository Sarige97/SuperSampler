using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Xunit;

namespace SuperSampler.IntegrationTests;

/// <summary>
/// 驱动帧层测试专用串行集合：本组用例会做进程级采样（句柄数/线程数）与并发压测，
/// 必须独占运行（DisableParallelization = true 与其它集合也不并行）。
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DriverHarnessCollection
{
    public const string Name = "DriverHarness";
}

/// <summary>从站侧记录的一次请求（原始字节 + 解析出的字段 + 到达时刻）。</summary>
public sealed class RawRequest
{
    public RawRequest(byte[] raw, byte[] pdu, byte unitId, ushort transactionId, long arrivalMs)
    {
        Raw = raw;
        Pdu = pdu;
        UnitId = unitId;
        TransactionId = transactionId;
        ArrivalMs = arrivalMs;
    }

    /// <summary>完整 ADU 原始字节。</summary>
    public byte[] Raw { get; }

    /// <summary>PDU（功能码 + 数据）。</summary>
    public byte[] Pdu { get; }

    public byte UnitId { get; }

    /// <summary>TCP 事务号（RTU 恒 0）。</summary>
    public ushort TransactionId { get; }

    /// <summary>从站启动后的相对到达时刻（毫秒）。</summary>
    public long ArrivalMs { get; }

    public byte Function => Pdu.Length > 0 ? Pdu[0] : (byte)0;

    public int Address => Pdu.Length >= 3 ? (Pdu[1] << 8) | Pdu[2] : -1;

    public int CountOrValue => Pdu.Length >= 5 ? (Pdu[3] << 8) | Pdu[4] : -1;

    public override string ToString()
        => "fc=0x" + Function.ToString("X2") + " unit=" + UnitId + " txn=" + TransactionId +
           " addr=" + Address + " cnt=" + CountOrValue + " raw=" + BitConverter.ToString(Raw);
}

/// <summary>一次连接的会话：向客户端回帧 / 拆分回帧 / 关闭 / RST。</summary>
public sealed class SlaveSession
{
    private readonly RawModbusSlave _slave;
    private readonly NetworkStream _stream;
    private readonly TcpClient _client;

    internal SlaveSession(RawModbusSlave slave, TcpClient client, NetworkStream stream)
    {
        _slave = slave;
        _client = client;
        _stream = stream;
    }

    /// <summary>原样写一段字节（用于粘包/畸形帧）。</summary>
    public void SendRaw(byte[] bytes)
    {
        _stream.Write(bytes, 0, bytes.Length);
        _stream.Flush();
    }

    /// <summary>拆成两次写（模拟 TCP 半包），中间等待 delayMs。</summary>
    public void SendSplit(byte[] bytes, int firstPartLength, int delayMs = 60)
    {
        var first = Math.Max(1, Math.Min(firstPartLength, bytes.Length - 1));
        _stream.Write(bytes, 0, first);
        _stream.Flush();
        Thread.Sleep(delayMs);
        _stream.Write(bytes, first, bytes.Length - first);
        _stream.Flush();
    }

    /// <summary>不回任何字节，直接关闭连接（FIN）。</summary>
    public void CloseGracefully() => _client.Close();

    /// <summary>以 RST 方式掐断连接。</summary>
    public void Abort()
    {
        try
        {
            _client.Client.LingerState = new LingerOption(true, 0);
        }
        catch (SocketException)
        {
        }

        _client.Close();
    }

    /// <summary>按本从站的帧格式封一个应答（TCP：MBAP；RTU：unit+PDU+CRC）。</summary>
    public byte[] Frame(byte[] pdu, byte unitId, ushort transactionId = 0)
        => _slave.BuildFrame(pdu, unitId, transactionId);

    /// <summary>回一个合法读应答：数据 = 起始地址 + i（地址推导，便于串数据断言）。</summary>
    public byte[] ReadResponse(RawRequest request)
        => _slave.BuildReadResponse(request);

    /// <summary>回一个异常码应答（fc | 0x80, code）。</summary>
    public byte[] ExceptionResponse(RawRequest request, byte code)
        => _slave.BuildFrame(new[] { (byte)(request.Function | 0x80), code }, request.UnitId, request.TransactionId);
}

/// <summary>
/// 进程内原始 Modbus 从站（TCP 或 RTU-over-TCP），脚本化应答：
/// 每个请求按 FIFO 取一条脚本执行；脚本用尽后按 <see cref="DefaultBehavior"/> 兜底。
/// 记录每个请求的原始字节与到达时刻，供「帧字节 / 间隔 / 请求次数 / 并发度」断言。
/// </summary>
public sealed class RawModbusSlave : IDisposable
{
    private readonly TcpListener _listener;
    private readonly bool _rtu;
    private readonly Thread _accept;
    private readonly object _gate = new();
    private readonly Queue<Action<RawRequest, SlaveSession>> _script = new();
    private readonly List<RawRequest> _requests = new();
    private readonly List<TcpClient> _clients = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private volatile bool _running = true;
    private int _inFlight;
    private int _maxInFlight;
    private int _connections;

    public RawModbusSlave(bool rtuOverTcp = false)
    {
        _rtu = rtuOverTcp;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        _accept = new Thread(AcceptLoop) { IsBackground = true, Name = "raw-slave-accept" };
        _accept.Start();
    }

    /// <summary>脚本用尽后的行为。</summary>
    public enum Fallback
    {
        /// <summary>回合法读/写应答（默认）。</summary>
        ReplyValid = 0,

        /// <summary>不应答（超时）。</summary>
        Silent = 1,
    }

    public Fallback DefaultBehavior { get; set; } = Fallback.ReplyValid;

    /// <summary>合法读应答的数据偏置：不同从站用不同偏置，跨链路/跨从站「串数据」一眼可辨。</summary>
    public int ReadBias { get; set; }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public bool IsRtu => _rtu;

    public int RequestCount
    {
        get { lock (_gate) return _requests.Count; }
    }

    public int ConnectionCount => Volatile.Read(ref _connections);

    /// <summary>从站侧观测到的最大并发处理请求数（>1 说明主站未串行）。</summary>
    public int MaxConcurrentRequests => Volatile.Read(ref _maxInFlight);

    /// <summary>当前仍打开的连接数（诊断用；正常应回到 0）。</summary>
    public int ActiveConnections
    {
        get { lock (_gate) return _clients.Count; }
    }

    public IReadOnlyList<RawRequest> Requests
    {
        get { lock (_gate) return _requests.ToArray(); }
    }

    /// <summary>排一条脚本（按请求顺序执行）。</summary>
    public void OnRequest(Action<RawRequest, SlaveSession> action)
    {
        lock (_gate) _script.Enqueue(action);
    }

    /// <summary>排一条「回某个 PDU」的脚本。</summary>
    public void ReplyPdu(Func<RawRequest, byte[]> pduFactory)
        => OnRequest((request, session) => session.SendRaw(session.Frame(pduFactory(request), request.UnitId, request.TransactionId)));

    /// <summary>排一条「不应答」的脚本（制造超时）。</summary>
    public void Silent()
        => OnRequest((_, _) => { });

    /// <summary>排一条「延迟 ms 后回应答」的脚本。</summary>
    public void ReplyAfter(int delayMs, Func<RawRequest, byte[]> pduFactory)
        => OnRequest((request, session) =>
        {
            Thread.Sleep(delayMs);
            session.SendRaw(session.Frame(pduFactory(request), request.UnitId, request.TransactionId));
        });

    /// <summary>排一条「应打包（split 字节处拆两次写）」的脚本。</summary>
    public void ReplySplit(int firstPartLength, Func<RawRequest, byte[]> pduFactory)
        => OnRequest((request, session) =>
        {
            var frame = session.Frame(pduFactory(request), request.UnitId, request.TransactionId);
            session.SendSplit(frame, firstPartLength);
        });

    /// <summary>排一条「本应答 + 一份相同副本一起发（粘包，残留副本毒害下一请求）」的脚本。</summary>
    public void ReplyWithDuplicate(Func<RawRequest, byte[]> pduFactory)
        => OnRequest((request, session) =>
        {
            var frame = session.Frame(pduFactory(request), request.UnitId, request.TransactionId);
            var both = new byte[frame.Length * 2];
            Array.Copy(frame, 0, both, 0, frame.Length);
            Array.Copy(frame, 0, both, frame.Length, frame.Length);
            session.SendRaw(both);
        });

    /// <summary>排一条「回一个事务号被篡改（wrongTxn）但其余完全合法的应答」的脚本。</summary>
    public void ReplyWrongTransaction(ushort wrongTxn)
        => OnRequest((request, session) =>
            session.SendRaw(session.Frame(ValidReadPdu(request), request.UnitId, wrongTxn)));

    /// <summary>排一条「回一个 unitId 被篡改的应答」的脚本。</summary>
    public void ReplyWrongUnit(byte wrongUnit)
        => OnRequest((request, session) =>
            session.SendRaw(session.Frame(ValidReadPdu(request), wrongUnit, request.TransactionId)));

    /// <summary>排一条「功能码不符（用 wrongFunction 解析请求长度）的合法形状应答」的脚本。</summary>
    public void ReplyWrongFunction(byte wrongFunction)
        => OnRequest((request, session) =>
        {
            var count = Math.Max(1, request.CountOrValue);
            var data = new byte[2 + (count * 2)];
            data[0] = wrongFunction;
            data[1] = (byte)(count * 2);
            for (var i = 0; i < count; i++)
            {
                data[2 + (i * 2)] = 0xAB;
                data[3 + (i * 2)] = 0xCD;
            }

            session.SendRaw(session.Frame(data, request.UnitId, request.TransactionId));
        });

    /// <summary>排一条「回截断帧后保持连接」的脚本。</summary>
    public void ReplyTruncated(int bytesToSend, Func<RawRequest, byte[]> pduFactory)
        => OnRequest((request, session) =>
        {
            var frame = session.Frame(pduFactory(request), request.UnitId, request.TransactionId);
            var n = Math.Min(bytesToSend, frame.Length);
            var part = new byte[n];
            Array.Copy(frame, 0, part, 0, n);
            session.SendRaw(part);
        });

    /// <summary>排一条「回截断帧后关闭连接」的脚本。</summary>
    public void ReplyTruncatedAndClose(int bytesToSend, Func<RawRequest, byte[]> pduFactory)
        => OnRequest((request, session) =>
        {
            var frame = session.Frame(pduFactory(request), request.UnitId, request.TransactionId);
            var n = Math.Min(bytesToSend, frame.Length);
            var part = new byte[n];
            Array.Copy(frame, 0, part, 0, n);
            session.SendRaw(part);
            session.CloseGracefully();
        });

    /// <summary>排一条「读到请求直接 FIN」的脚本。</summary>
    public void CloseWithoutReply()
        => OnRequest((_, session) => session.CloseGracefully());

    /// <summary>排一条「读到请求直接 RST」的脚本。</summary>
    public void AbortWithoutReply()
        => OnRequest((_, session) => session.Abort());

    /// <summary>排一条「回异常码」的脚本。</summary>
    public void ReplyException(byte code)
        => OnRequest((request, session) => session.SendRaw(session.ExceptionResponse(request, code)));

    /// <summary>排一条「原样回显请求 PDU」（写应答合法回显）。</summary>
    public void ReplyEcho()
        => OnRequest((request, session) => session.SendRaw(session.Frame(request.Pdu, request.UnitId, request.TransactionId)));

    /// <summary>排一条「回显但篡改第 5 字节（数量/值不符）」的脚本。</summary>
    public void ReplyEchoCorrupted(int byteIndex, byte newValue)
        => OnRequest((request, session) =>
        {
            var pdu = (byte[])request.Pdu.Clone();
            if (byteIndex >= 0 && byteIndex < pdu.Length) pdu[byteIndex] = newValue;
            session.SendRaw(session.Frame(pdu, request.UnitId, request.TransactionId));
        });

    /// <summary>排一条「用原始字节回」（完全自定义，包括畸形 MBAP）。</summary>
    public void ReplyRaw(Func<RawRequest, byte[]> rawFactory)
        => OnRequest((request, session) => session.SendRaw(rawFactory(request)));

    /// <summary>等从站侧累计请求数到达 atLeast。</summary>
    public bool WaitRequests(int atLeast, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount + timeoutMs;
        while (Environment.TickCount < deadline)
        {
            if (RequestCount >= atLeast) return true;
            Thread.Sleep(10);
        }

        return RequestCount >= atLeast;
    }

    /// <summary>按本从站格式封帧。</summary>
    public byte[] BuildFrame(byte[] pdu, byte unitId, ushort transactionId)
    {
        if (!_rtu)
        {
            var frame = new byte[7 + pdu.Length];
            frame[0] = (byte)(transactionId >> 8);
            frame[1] = (byte)(transactionId & 0xFF);
            frame[4] = (byte)((pdu.Length + 1) >> 8);
            frame[5] = (byte)((pdu.Length + 1) & 0xFF);
            frame[6] = unitId;
            Array.Copy(pdu, 0, frame, 7, pdu.Length);
            return frame;
        }

        var rtu = new byte[3 + pdu.Length];
        rtu[0] = unitId;
        Array.Copy(pdu, 0, rtu, 1, pdu.Length);
        var crc = TestCrc16.Compute(rtu, 0, rtu.Length - 2);
        rtu[rtu.Length - 2] = (byte)(crc & 0xFF);
        rtu[rtu.Length - 1] = (byte)((crc >> 8) & 0xFF);
        return rtu;
    }

    /// <summary>按请求形状造一个合法读应答 PDU（寄存器数据 = 起始地址 + i + <see cref="ReadBias"/>）。</summary>
    public byte[] ValidReadPdu(RawRequest request)
    {
        var fc = request.Function;
        var count = request.CountOrValue;
        if (fc is 1 or 2)
        {
            var byteCount = (count + 7) / 8;
            var pdu = new byte[2 + byteCount];
            pdu[0] = fc;
            pdu[1] = (byte)byteCount;
            for (var i = 0; i < byteCount; i++) pdu[2 + i] = 0xFF;
            return pdu;
        }

        var words = count <= 0 ? 1 : count;
        var regs = new byte[2 + (words * 2)];
        regs[0] = fc;
        regs[1] = (byte)(words * 2);
        for (var i = 0; i < words; i++)
        {
            var value = (ushort)(request.Address + i + ReadBias);
            regs[2 + (i * 2)] = (byte)(value >> 8);
            regs[3 + (i * 2)] = (byte)(value & 0xFF);
        }

        return regs;
    }

    public byte[] BuildReadResponse(RawRequest request) => BuildFrame(ValidReadPdu(request), request.UnitId, request.TransactionId);

    private void AcceptLoop()
    {
        while (_running)
        {
            TcpClient client;
            try
            {
                client = _listener.AcceptTcpClient();
            }
            catch (SocketException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            Interlocked.Increment(ref _connections);
            lock (_gate) _clients.Add(client);
            var connection = client;
            var worker = new Thread(() => Serve(connection)) { IsBackground = true, Name = "raw-slave-conn" };
            worker.Start();
        }
    }

    private void Serve(TcpClient client)
    {
        try
        {
            using (client)
            using (var stream = client.GetStream())
            {
                var session = new SlaveSession(this, client, stream);
                while (_running)
                {
                    RawRequest? request;
                    try
                    {
                        request = ReadRequest(stream);
                    }
                    catch (IOException)
                    {
                        return;
                    }
                    catch (ObjectDisposedException)
                    {
                        return;
                    }

                    if (request == null) return;

                    Action<RawRequest, SlaveSession>? action;
                    lock (_gate)
                    {
                        _requests.Add(request);
                        action = _script.Count > 0 ? _script.Dequeue() : null;
                    }

                    var inFlight = Interlocked.Increment(ref _inFlight);
                    UpdateMax(inFlight);
                    try
                    {
                        if (action != null)
                        {
                            action(request, session);
                        }
                        else if (DefaultBehavior == Fallback.ReplyValid)
                        {
                            if (request.Function is 1 or 2 or 3 or 4)
                            {
                                session.SendRaw(session.ReadResponse(request));
                            }
                            else if (request.Function is 5 or 6 or 15 or 16)
                            {
                                session.SendRaw(session.Frame(request.Pdu, request.UnitId, request.TransactionId));
                            }
                        }
                    }
                    catch (IOException)
                    {
                        return;
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _inFlight);
                    }
                }
            }
        }
        finally
        {
            lock (_gate) _clients.Remove(client);
        }
    }

    private void UpdateMax(int value)
    {
        while (true)
        {
            var current = Volatile.Read(ref _maxInFlight);
            if (value <= current) return;
            if (Interlocked.CompareExchange(ref _maxInFlight, value, current) == current) return;
        }
    }

    private RawRequest? ReadRequest(NetworkStream stream)
    {
        if (!_rtu)
        {
            var header = new byte[7];
            if (!ReadExact(stream, header, 7)) return null;
            var txn = (ushort)((header[0] << 8) | header[1]);
            var length = (header[4] << 8) | header[5];
            var pduLength = Math.Max(0, length - 1);
            var pdu = new byte[pduLength];
            if (pduLength > 0 && !ReadExact(stream, pdu, pduLength)) return null;
            return new RawRequest(Concat(header, pdu), pdu, header[6], txn, _clock.ElapsedMilliseconds);
        }

        var head = new byte[2];
        if (!ReadExact(stream, head, 2)) return null;
        var function = head[1];
        var rest = new List<byte>();
        if (function is 1 or 2 or 3 or 4 or 5 or 6)
        {
            var body = new byte[6]; // addr2 + value/count2 + crc2
            if (!ReadExact(stream, body, body.Length)) return null;
            rest.AddRange(body);
        }
        else if (function is 15 or 16)
        {
            var fixedPart = new byte[5]; // addr2 + count2 + byteCount1
            if (!ReadExact(stream, fixedPart, fixedPart.Length)) return null;
            rest.AddRange(fixedPart);
            var dataLength = fixedPart[4];
            var data = new byte[dataLength + 2];
            if (!ReadExact(stream, data, data.Length)) return null;
            rest.AddRange(data);
        }
        else
        {
            return null;
        }

        var raw = new byte[2 + rest.Count];
        raw[0] = head[0];
        raw[1] = head[1];
        rest.CopyTo(raw, 2);
        var requestPdu = new byte[raw.Length - 3];
        Array.Copy(raw, 1, requestPdu, 0, requestPdu.Length);
        return new RawRequest(raw, requestPdu, head[0], 0, _clock.ElapsedMilliseconds);
    }

    private static byte[] Concat(byte[] a, byte[] b)
    {
        var result = new byte[a.Length + b.Length];
        Array.Copy(a, 0, result, 0, a.Length);
        Array.Copy(b, 0, result, a.Length, b.Length);
        return result;
    }

    private static bool ReadExact(NetworkStream stream, byte[] buffer, int count)
    {
        var offset = 0;
        while (offset < count)
        {
            int read;
            try
            {
                read = stream.Read(buffer, offset, count - offset);
            }
            catch (IOException)
            {
                return false;
            }

            if (read <= 0) return false;
            offset += read;
        }

        return true;
    }

    public void Dispose()
    {
        _running = false;
        try
        {
            _listener.Stop();
        }
        catch (SocketException)
        {
        }

        // 关掉所有仍打开的已接受连接（否则「停掉从站」不会真正切断既有连接）
        TcpClient[] open;
        lock (_gate)
        {
            open = _clients.ToArray();
            _clients.Clear();
        }

        foreach (var client in open)
        {
            try
            {
                client.Close();
            }
            catch (Exception)
            {
                // 关闭次生异常忽略
            }
        }

        if (_accept.IsAlive) _accept.Join(TimeSpan.FromSeconds(2));
    }
}

/// <summary>测试侧独立实现的 Modbus CRC16（逐位，与驱动查表实现不同源，用作独立复算）。</summary>
public static class TestCrc16
{
    public static ushort Compute(byte[] data, int offset, int count)
    {
        ushort crc = 0xFFFF;
        for (var i = offset; i < offset + count; i++)
        {
            crc ^= data[i];
            for (var bit = 0; bit < 8; bit++)
            {
                if ((crc & 1) != 0)
                {
                    crc = (ushort)((crc >> 1) ^ 0xA001);
                }
                else
                {
                    crc >>= 1;
                }
            }
        }

        return crc;
    }
}
