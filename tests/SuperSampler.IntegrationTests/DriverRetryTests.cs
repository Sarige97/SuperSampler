using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using SuperSampler.Drivers.Modbus;
using SuperSampler.Drivers.Modbus.Wire;
using Xunit;

namespace SuperSampler.IntegrationTests;

/// <summary>
/// 驱动层重试分类（CHAOS-6 最小版）+ 写超时不重写（GATE-1 驱动侧）。
/// 用测试进程内的 TCP 假从站按脚本应答，不依赖外部模拟器，因此无需跳过、完全确定性。
/// 覆盖：永久码不重试、瞬时码按预算重试、瞬时后恢复、超时按预算重试、
/// 重试预算=0 时只发一次请求（绝不自动重写）、无应答与异常码两类分开上报。
/// </summary>
public sealed class DriverRetryTests : IDisposable
{
    private readonly TcpListener _listener;
    private readonly Thread _server;
    private readonly List<byte[]> _requests = new();
    private readonly Queue<Func<byte[], byte[]?>> _script = new();
    private readonly object _gate = new();
    private volatile bool _running = true;

    public DriverRetryTests()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        _server = new Thread(ServeLoop) { IsBackground = true };
        _server.Start();
    }

    private int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    // ─────────────── 假从站 ───────────────

    /// <summary>排一条应答脚本：返回 null 表示故意不应答（制造超时）。</summary>
    private void Reply(Func<byte[], byte[]?> responder)
    {
        lock (_gate) _script.Enqueue(responder);
    }

    private static byte[]? Exception(byte code) => new byte[] { 0x80 | 0x06, code };
    private static byte[]? Echo(byte[] requestPdu) => (byte[])requestPdu.Clone();

    private int RequestCount
    {
        get { lock (_gate) return _requests.Count; }
    }

    private void ServeLoop()
    {
        while (_running)
        {
            TcpClient? client = null;
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

            using (client)
            using (var stream = client.GetStream())
            {
                var header = new byte[7];
                while (_running)
                {
                    if (!TryReadExact(stream, header, 7)) break;
                    var length = (header[4] << 8) | header[5];
                    // MBAP 长度域 = 单元号(1) + PDU(N)；7 字节头已含单元号，剩余 length-1 字节即纯 PDU
                    var pdu = new byte[Math.Max(0, length - 1)];
                    if (pdu.Length > 0 && !TryReadExact(stream, pdu, pdu.Length)) break;

                    Func<byte[], byte[]?>? responder;
                    lock (_gate)
                    {
                        _requests.Add(pdu);
                        responder = _script.Count > 0 ? _script.Dequeue() : null;
                    }

                    if (responder == null) continue;   // 不应答 → 主站超时
                    var response = responder(pdu);
                    if (response == null) continue;

                    var frame = new byte[7 + response.Length];
                    Array.Copy(header, frame, 6);                    // 回显事务号/协议号
                    frame[4] = (byte)((response.Length + 1) >> 8);
                    frame[5] = (byte)((response.Length + 1) & 0xFF);
                    frame[6] = header[6];                            // 回显单元号
                    Array.Copy(response, 0, frame, 7, response.Length);
                    try
                    {
                        stream.Write(frame, 0, frame.Length);
                        stream.Flush();
                    }
                    catch (IOException)
                    {
                        break;
                    }
                }
            }
        }
    }

    private static bool TryReadExact(NetworkStream stream, byte[] buffer, int count)
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

    private ModbusReply WriteWith(int retries, int timeoutMs = 300)
    {
        using var master = new ModbusMaster(ChannelVariant.Tcp,
            new TransportLike { Host = "127.0.0.1", Port = Port, RequestTimeoutMs = timeoutMs });
        return master.WriteSingle(DataArea.HoldingRegister, 0, 5, 1, timeoutMs, retries, retryIntervalMs: 20);
    }

    // ─────────────── GATE-1：写超时不重写 ───────────────

    [Fact]
    public void Write_timeout_with_zero_retries_sends_exactly_one_request()
    {
        Reply(_ => null); // 永不应答

        var reply = WriteWith(retries: 0);

        Assert.False(reply.Success);
        Assert.Equal(ModbusFailureKind.Timeout, reply.Kind);
        Assert.Equal(1, RequestCount); // 重试预算 0 → 绝不自动重写
    }

    [Fact]
    public void Write_timeout_is_retried_only_within_configured_budget()
    {
        Reply(_ => null);
        Reply(_ => null);
        Reply(_ => null);

        var reply = WriteWith(retries: 2); // 预算 2 → 最多 3 次

        Assert.Equal(ModbusFailureKind.Timeout, reply.Kind);
        Assert.Equal(3, RequestCount);
    }

    // ─────────────── CHAOS-6：异常码分类 ───────────────

    [Theory]
    [InlineData(0x05)] // 确认（瞬时）
    [InlineData(0x06)] // 从站忙（瞬时）
    [InlineData(0x0A)] // 网关路径不可用（瞬时）
    public void Transient_exception_codes_are_retried_within_budget(byte code)
    {
        Reply(_ => Exception(code));
        Reply(_ => Exception(code));
        Reply(_ => Exception(code));

        var reply = WriteWith(retries: 2);

        Assert.False(reply.Success);
        Assert.Equal(ModbusFailureKind.Protocol, reply.Kind);
        Assert.Equal(code, reply.ExceptionCode);
        Assert.Equal(3, RequestCount); // 预算内可重试
    }

    [Theory]
    [InlineData(0x01)] // 非法功能
    [InlineData(0x02)] // 非法数据地址
    [InlineData(0x03)] // 非法数据值
    [InlineData(0x04)] // 从站设备故障
    [InlineData(0x08)] // 存储奇偶校验错
    [InlineData(0x0B)] // 网关目标设备无响应（未映射 → 按永久处理）
    public void Permanent_exception_codes_are_never_retried(byte code)
    {
        Reply(_ => Exception(code));
        Reply(_ => Exception(code));
        Reply(_ => Exception(code));

        var reply = WriteWith(retries: 2);

        Assert.False(reply.Success);
        Assert.Equal(ModbusFailureKind.Protocol, reply.Kind);
        Assert.Equal(code, reply.ExceptionCode);
        Assert.Equal(1, RequestCount); // 永久码重试无意义，量 1 次即止
    }

    [Fact]
    public void Transient_then_success_recovers_within_budget()
    {
        Reply(_ => Exception(0x06));
        Reply(Echo);

        var reply = WriteWith(retries: 2);

        Assert.True(reply.Success, $"Kind={reply.Kind} Code=0x{reply.ExceptionCode:X2} Msg={reply.Message} requests={RequestCount}");
        Assert.Equal(2, RequestCount);
    }

    [Fact]
    public void No_response_is_reported_as_timeout()
    {
        Reply(_ => null);

        var reply = WriteWith(retries: 0);

        Assert.False(reply.Success);
        Assert.Equal(ModbusFailureKind.Timeout, reply.Kind); // 无应答 → 超时
    }

    [Fact]
    public void Exception_code_is_reported_as_protocol_error()
    {
        Reply(_ => Exception(0x02));

        var reply = WriteWith(retries: 0);

        Assert.False(reply.Success);
        Assert.Equal(ModbusFailureKind.Protocol, reply.Kind); // 异常码 → 协议错，与超时分开上报
        Assert.Equal(0x02, reply.ExceptionCode);
    }

    public void Dispose()
    {
        _running = false;
        try { _listener.Stop(); } catch (SocketException) { }
        if (_server.IsAlive) _server.Join(TimeSpan.FromSeconds(2));
    }
}