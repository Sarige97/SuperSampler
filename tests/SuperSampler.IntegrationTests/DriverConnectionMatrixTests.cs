using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Ports;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using SuperSampler.Drivers.Modbus;
using SuperSampler.Drivers.Modbus.Wire;
using Xunit;

namespace SuperSampler.IntegrationTests;

/// <summary>
/// 超时/连接层：requestTimeoutMs、connectTimeoutMs、keepalive 实设证据、连接生命周期
/// （半开恢复 / 多链路隔离 / Dispose 50 次不增长 / 同链路并发读不串数据）、
/// 混合拓扑（TCP + RTU-over-TCP 互不串数据）、串口参数映射。
/// </summary>
[Collection(DriverHarnessCollection.Name)]
public sealed class DriverConnectionMatrixTests
{
    private static ModbusMaster NewMaster(RawModbusSlave slave, int requestTimeoutMs = 500,
        int connectTimeoutMs = 1000, int gapMs = 0)
        => new(slave.IsRtu ? ChannelVariant.RtuOverTcp : ChannelVariant.Tcp,
            new TransportLike
            {
                Host = "127.0.0.1",
                Port = slave.Port,
                RequestTimeoutMs = requestTimeoutMs,
                ConnectTimeoutMs = connectTimeoutMs,
                GapMs = gapMs,
            });

    // ───────────────────────── E. 超时 ─────────────────────────

    /// <summary>E1：requestTimeoutMs 真生效——不应答的从站在超时量级内失败，且判 Timeout（连接仍在）。</summary>
    [Fact]
    public void E1_Request_timeout_is_honoured_within_its_budget()
    {
        using var slave = new RawModbusSlave { DefaultBehavior = RawModbusSlave.Fallback.Silent };
        using var master = NewMaster(slave, requestTimeoutMs: 400);

        var watch = Stopwatch.StartNew();
        var reply = master.Read(DataArea.HoldingRegister, 0, 1, 1, 400, 0, 0);
        watch.Stop();

        Assert.False(reply.Success);
        Assert.Equal(ModbusFailureKind.Timeout, reply.Kind);
        Assert.InRange(watch.ElapsedMilliseconds, 350, 1800); // 区间断言：下界卡住"真的等了"，上界容忍调度抖动
    }

    /// <summary>E2：慢从站——延迟在预算内 → 成功；超出预算 → 超时（两条对照，证明超时不是恒真）。</summary>
    [Fact]
    public void E2_Slow_slave_succeeds_inside_the_budget_and_times_out_beyond_it()
    {
        using var fastSlave = new RawModbusSlave();
        fastSlave.ReplyAfter(150, r => fastSlave.ValidReadPdu(r));
        using var fastMaster = NewMaster(fastSlave, requestTimeoutMs: 800);
        var fast = fastMaster.Read(DataArea.HoldingRegister, 0x0100, 1, 1, 800, 0, 0);
        Assert.True(fast.Success, fast.Message);

        using var slowSlave = new RawModbusSlave();
        slowSlave.ReplyAfter(900, r => slowSlave.ValidReadPdu(r));
        using var slowMaster = NewMaster(slowSlave, requestTimeoutMs: 300);
        var watch = Stopwatch.StartNew();
        var slow = slowMaster.Read(DataArea.HoldingRegister, 0x0100, 1, 1, 300, 0, 0);
        watch.Stop();

        Assert.False(slow.Success);
        Assert.Equal(ModbusFailureKind.Timeout, slow.Kind);
        Assert.InRange(watch.ElapsedMilliseconds, 250, 1800);
    }

    /// <summary>
    /// E3：connectTimeoutMs 真生效——连接不可达地址（TEST-NET-1 192.0.2.1，本环境实测丢包不 RST）
    /// 在 connectTimeoutMs 量级内失败，不挂死（有界等待）。
    /// </summary>
    [Fact]
    public void E3_Connect_timeout_is_honoured_for_an_unreachable_address()
    {
        using var master = new ModbusMaster(ChannelVariant.Tcp, new TransportLike
        {
            Host = "192.0.2.1",
            Port = 502,
            ConnectTimeoutMs = 700,
            RequestTimeoutMs = 700,
        });

        var watch = Stopwatch.StartNew();
        var reply = master.Read(DataArea.HoldingRegister, 0, 1, 1, 700, 0, 0);
        watch.Stop();

        Assert.False(reply.Success);
        Assert.Equal(ModbusFailureKind.Timeout, reply.Kind); // 连接等待超时 = 瞬时（可重试/交退避）
        Assert.InRange(watch.ElapsedMilliseconds, 600, 3500);
    }

    /// <summary>
    /// E4：keepalive 真设到 socket 上（ADR D40：空闲 10s / 间隔 3s / 探测 3 次）。
    /// 反射取已连接 socket：SO_KEEPALIVE=1、TCP_KEEPCNT=3；源码常量与文档一致。
    /// </summary>
    [Fact]
    public void E4_Tcp_keepalive_is_really_applied_to_the_socket()
    {
        using var slave = new RawModbusSlave();
        using var master = NewMaster(slave);
        Assert.True(master.Read(DataArea.HoldingRegister, 0, 1, 1, 500, 0, 0).Success);

        var socket = GetConnectedSocket(master);

        var keepAlive = socket.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive);
        var keepAliveOn = keepAlive switch
        {
            int i => i != 0,
            byte[] bytes => bytes.Any(b => b != 0),
            _ => false,
        };
        Assert.True(keepAliveOn, "SO_KEEPALIVE 必须在已连接 socket 上打开，实际=" + keepAlive);

        // TCP_KEEPCNT = 16（ws2ipdef.h）；设置值为 3（ADR D40）
        var keepCount = Convert.ToInt32(socket.GetSocketOption(SocketOptionLevel.Tcp, (SocketOptionName)16));
        Assert.Equal(3, keepCount);

        // 源码常量 = ADR D40 的 10s / 3s / 3 次（SIO_KEEPALIVE_VALS 无读回 API，用常量 + 上面的 KEEPCNT 组成证据链）
        var channelType = typeof(ModbusChannel);
        Assert.Equal(10_000, PrivateConst(channelType, "KeepAliveIdleMs"));
        Assert.Equal(3_000, PrivateConst(channelType, "KeepAliveIntervalMs"));
        Assert.Equal(3, PrivateConst(channelType, "KeepAliveRetryCount"));
        Assert.Equal(16, PrivateConst(channelType, "TcpKeepAliveRetryCountOption")); // 与 ws2ipdef.h 的 TCP_KEEPCNT 同值
    }

    private static int PrivateConst(Type type, string name)
    {
        var field = type.GetField(name, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.True(field != null, "找不到私有常量 " + name + "（重命名后请同步本用例）");
        return Convert.ToInt32(field!.GetValue(null));
    }

    private static Socket GetConnectedSocket(ModbusMaster master)
    {
        var channelField = typeof(ModbusMaster).GetField("_channel", BindingFlags.NonPublic | BindingFlags.Instance);
        var channel = channelField?.GetValue(master) as ModbusChannel;
        if (channel == null) throw new InvalidOperationException("反射取 ModbusChannel 失败（字段改名？）");

        var tcpField = typeof(ModbusChannel).GetField("_tcp", BindingFlags.NonPublic | BindingFlags.Instance);
        var tcp = tcpField?.GetValue(channel) as TcpClient;
        if (tcp == null || !tcp.Connected) throw new InvalidOperationException("反射取已连接 TcpClient 失败");
        return tcp.Client;
    }

    // ───────────────────────── F. 连接生命周期 ─────────────────────────

    /// <summary>
    /// F1：半开（accept 但应吞掉全部请求）→ 超时；失败后连接被关闭（不假死），
    /// 通道恢复后下一次请求自动重连成功。
    /// </summary>
    [Fact]
    public void F1_Half_open_blackhole_times_out_closes_and_recovers()
    {
        using var slave = new RawModbusSlave { DefaultBehavior = RawModbusSlave.Fallback.Silent };
        using var master = NewMaster(slave, requestTimeoutMs: 300);

        var first = master.Read(DataArea.HoldingRegister, 0, 1, 1, 300, 0, 0);
        Assert.Equal(ModbusFailureKind.Timeout, first.Kind);
        Assert.False(master.IsOpen); // 链路级失败必须关闭连接，下次惰性重连（ADR D37/D40）

        slave.DefaultBehavior = RawModbusSlave.Fallback.ReplyValid;
        var second = master.Read(DataArea.HoldingRegister, 0, 1, 1, 800, 0, 0);
        Assert.True(second.Success, second.Message);
        Assert.Equal(new ushort[] { 0x0000 }, second.Registers);
    }

    /// <summary>F2：多链路互不影响——停掉链路 2 的监听，链路 1 连续读照常成功。</summary>
    [Fact]
    public void F2_A_dead_link_does_not_disturb_another_link()
    {
        using var alive = new RawModbusSlave();
        using var dying = new RawModbusSlave();
        using var aliveMaster = NewMaster(alive, requestTimeoutMs: 400);
        using var dyingMaster = NewMaster(dying, requestTimeoutMs: 400);

        Assert.True(aliveMaster.Read(DataArea.HoldingRegister, 1, 1, 1, 400, 0, 0).Success);
        Assert.True(dyingMaster.Read(DataArea.HoldingRegister, 1, 1, 1, 400, 0, 0).Success);

        dying.Dispose(); // 链路 2 死亡（监听关闭 + 已有连接全部切断）

        var dead = dyingMaster.Read(DataArea.HoldingRegister, 1, 1, 1, 400, 0, 0);
        Assert.False(dead.Success);
        Assert.Equal(ModbusFailureKind.LinkDown, dead.Kind);

        for (var i = 0; i < 3; i++)
        {
            var ok = aliveMaster.Read(DataArea.HoldingRegister, (ushort)(100 + i), 1, 1, 400, 0, 0);
            Assert.True(ok.Success, "另一条链路必须照常工作：" + ok.Message);
            Assert.Equal((ushort)(100 + i), ok.Registers[0]);
        }
    }

    /// <summary>
    /// F3：连续起停 50 次（建连→读→Dispose）→ 进程句柄数与线程数不增长
    /// （每轮泄漏 1 个 socket 会 +45 句柄，阈值 30 可抓住）。
    /// </summary>
    [Fact]
    public void F3_Fifty_connect_read_dispose_cycles_do_not_leak_handles_or_threads()
    {
        using var slave = new RawModbusSlave();

        for (var i = 0; i < 5; i++) RunCycle(slave); // 预热（JIT / 线程池 / DNS 缓存）

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Thread.Sleep(250);

        var process = Process.GetCurrentProcess();
        process.Refresh();
        var handles0 = process.HandleCount;
        var threads0 = process.Threads.Count;

        for (var i = 0; i < 45; i++) RunCycle(slave);

        // 线程/socket 的 OS 句柄由托管对象终结器释放：先回收再采样（否则量到的是终结滞后，不是泄漏）
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        Thread.Sleep(400);
        process.Refresh();
        var handles1 = process.HandleCount;
        var threads1 = process.Threads.Count;

        Assert.True(slave.ActiveConnections == 0, "从站侧仍挂着 " + slave.ActiveConnections + " 条连接（连接未关闭）");
        Assert.True(handles1 - handles0 <= 30,
            "45 轮起停后句柄增长过多：" + handles0 + " → " + handles1 + "（+" + (handles1 - handles0) + "）");
        Assert.True(threads1 - threads0 <= 8,
            "45 轮起停后线程增长过多：" + threads0 + " → " + threads1);
    }

    private static void RunCycle(RawModbusSlave slave)
    {
        using var master = NewMaster(slave, requestTimeoutMs: 400);
        var reply = master.Read(DataArea.HoldingRegister, 0, 1, 1, 400, 0, 0);
        Assert.True(reply.Success, reply.Message);
    }

    /// <summary>
    /// F4：同一 ModbusMaster 被 8 线程并发读——通道内部严格串行（从站侧并发度恒 1）、
    /// 每个线程拿到的都是自己地址的数据（不串）、不抛异常。
    /// </summary>
    [Fact]
    public void F4_Concurrent_reads_on_one_master_are_serialized_and_never_cross_talk()
    {
        using var slave = new RawModbusSlave();
        using var master = NewMaster(slave, requestTimeoutMs: 3000);

        const int workers = 8;
        const int perWorker = 20;
        var errors = new ConcurrentBag<Exception>();
        var mismatches = new ConcurrentBag<string>();
        var threads = new List<Thread>();

        for (var t = 0; t < workers; t++)
        {
            var id = t;
            var thread = new Thread(() =>
            {
                try
                {
                    for (var i = 0; i < perWorker; i++)
                    {
                        var address = 1000 + (id * 16);
                        var reply = master.Read(DataArea.HoldingRegister, address, 2, 1, 3000, 0, 0);
                        if (!reply.Success)
                        {
                            errors.Add(new InvalidOperationException("线程 " + id + " 读失败：" + reply.Message));
                            return;
                        }

                        if (reply.Registers.Length != 2 || reply.Registers[0] != address || reply.Registers[1] != address + 1)
                        {
                            mismatches.Add("线程 " + id + " 地址 " + address + " 收到 " +
                                           string.Join(",", reply.Registers.Select(r => r.ToString())));
                        }
                    }
                }
                catch (Exception ex)
                {
                    errors.Add(ex);
                }
            })
            {
                IsBackground = true,
                Name = "driver-read-" + id,
            };
            threads.Add(thread);
            thread.Start();
        }

        foreach (var thread in threads) thread.Join(TimeSpan.FromSeconds(60));

        Assert.Empty(errors);
        Assert.Empty(mismatches);
        Assert.Equal(workers * perWorker, slave.RequestCount);
        Assert.Equal(1, slave.MaxConcurrentRequests); // 一条通道严格串行，应答绝不交叠
    }

    // ───────────────────────── H9. 混合拓扑互不串数据 ─────────────────────────

    /// <summary>
    /// H9：同一次运行里 TCP 链路 + RTU-over-TCP 链路并发采集（各自多请求），
    /// 用不同数据偏置证明确实各拿各的数据（串数据即失败）。
    /// </summary>
    [Fact]
    public void H9_Tcp_and_rtu_links_in_one_run_never_cross_talk()
    {
        using var tcpSlave = new RawModbusSlave { ReadBias = 0x0000 };
        using var rtuSlave = new RawModbusSlave(rtuOverTcp: true) { ReadBias = 0x4000 };
        using var tcpMaster = NewMaster(tcpSlave, requestTimeoutMs: 2000);
        using var rtuMaster = NewMaster(rtuSlave, requestTimeoutMs: 2000);

        var errors = new ConcurrentBag<Exception>();
        var mismatches = new ConcurrentBag<string>();

        void RunLink(ModbusMaster master, int bias, int unitId)
        {
            try
            {
                for (var i = 0; i < 30; i++)
                {
                    var address = 200 + i;
                    var reply = master.Read(DataArea.HoldingRegister, address, 2, (byte)unitId, 2000, 0, 0);
                    if (!reply.Success)
                    {
                        errors.Add(new InvalidOperationException("读失败：" + reply.Message));
                        return;
                    }

                    var expected = (ushort)(address + bias);
                    if (reply.Registers[0] != expected || reply.Registers[1] != expected + 1)
                    {
                        mismatches.Add("期望 " + expected.ToString("X4") + " 实收 " +
                                       string.Join(",", reply.Registers.Select(r => r.ToString("X4"))));
                    }
                }
            }
            catch (Exception ex)
            {
                errors.Add(ex);
            }
        }

        var tcpThread = new Thread(() => RunLink(tcpMaster, 0x0000, 1)) { IsBackground = true };
        var rtuThread = new Thread(() => RunLink(rtuMaster, 0x4000, 9)) { IsBackground = true };
        tcpThread.Start();
        rtuThread.Start();
        tcpThread.Join(TimeSpan.FromSeconds(60));
        rtuThread.Join(TimeSpan.FromSeconds(60));

        Assert.Empty(errors);
        Assert.Empty(mismatches);
        Assert.Equal(30, tcpSlave.RequestCount);
        Assert.Equal(30, rtuSlave.RequestCount);
        Assert.All(tcpSlave.Requests, r => Assert.Equal(1, r.UnitId));
        Assert.All(rtuSlave.Requests, r => Assert.Equal(9, r.UnitId));
    }

    // ───────────────────────── G. 串口参数映射 ─────────────────────────

    /// <summary>G1：串口字符串枚举 → System.IO.Ports 枚举的映射（反射调私有解析函数）。</summary>
    [Theory]
    [InlineData("none", Parity.None)]
    [InlineData("even", Parity.Even)]
    [InlineData("odd", Parity.Odd)]
    [InlineData("mark", Parity.Mark)]
    [InlineData("space", Parity.Space)]
    [InlineData("unknown", Parity.None)]
    public void G1_Parity_mapping_is_exact(string text, Parity expected)
        => Assert.Equal(expected, InvokePrivate<Parity>("ParseParity", text));

    [Theory]
    [InlineData("one", StopBits.One)]
    [InlineData("two", StopBits.Two)]
    [InlineData("onepointfive", StopBits.OnePointFive)]
    [InlineData("unknown", StopBits.One)]
    public void G1b_Stop_bits_mapping_is_exact(string text, StopBits expected)
        => Assert.Equal(expected, InvokePrivate<StopBits>("ParseStopBits", text));

    [Theory]
    [InlineData("none", Handshake.None)]
    [InlineData("xonxoff", Handshake.XOnXOff)]
    [InlineData("rtscts", Handshake.RequestToSend)]
    [InlineData("dtrdsr", Handshake.RequestToSendXOnXOff)]
    [InlineData("unknown", Handshake.None)]
    public void G1c_Handshake_mapping_is_exact(string text, Handshake expected)
        => Assert.Equal(expected, InvokePrivate<Handshake>("ParseHandshake", text));

    private static T InvokePrivate<T>(string method, string argument)
    {
        var info = typeof(ModbusChannel).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.True(info != null, "找不到私有方法 " + method + "（改名后请同步本用例）");
        var result = info!.Invoke(null, new object[] { argument });
        return (T)result!;
    }

    /// <summary>
    /// G2：串口打不开 → 以失败应答上报（ModbusIoException 在驱动边界被转成 LinkDown 回复，
    /// 裸 IO 异常绝不逃逸），消息带端口名。
    /// </summary>
    [Fact]
    public void G2_Missing_serial_port_is_reported_as_a_wrapped_io_error()
    {
        using var master = new ModbusMaster(ChannelVariant.Serial, new TransportLike
        {
            PortName = "COM256",
            BaudRate = 9600,
            RequestTimeoutMs = 300,
        });

        var exception = Record.Exception(() => master.Read(DataArea.HoldingRegister, 0, 1, 1, 300, 0, 0));
        Assert.Null(exception); // 不允许裸异常逃逸

        var reply = master.Read(DataArea.HoldingRegister, 0, 1, 1, 300, 0, 0);
        Assert.False(reply.Success);
        Assert.Equal(ModbusFailureKind.LinkDown, reply.Kind);
        Assert.Contains("COM256", reply.Message);
    }

    /// <summary>G2b：串口未配置 portName / TCP 未配置 host → 明确失败应答（消息点明缺什么）。</summary>
    [Fact]
    public void G2b_Missing_transport_fields_are_explicit_io_errors()
    {
        using var serial = new ModbusMaster(ChannelVariant.Serial, new TransportLike { RequestTimeoutMs = 300 });
        var serialReply = serial.Read(DataArea.HoldingRegister, 0, 1, 1, 300, 0, 0);
        Assert.False(serialReply.Success);
        Assert.Equal(ModbusFailureKind.LinkDown, serialReply.Kind);
        Assert.Contains("portName", serialReply.Message);

        using var tcp = new ModbusMaster(ChannelVariant.Tcp, new TransportLike { Port = 502, RequestTimeoutMs = 300 });
        var tcpReply = tcp.Read(DataArea.HoldingRegister, 0, 1, 1, 300, 0, 0);
        Assert.False(tcpReply.Success);
        Assert.Equal(ModbusFailureKind.LinkDown, tcpReply.Kind);
        Assert.Contains("host", tcpReply.Message);
    }

    /// <summary>G3：TransportLike 的串口默认值（配置未写时落到 SerialPort 的取值）。</summary>
    [Fact]
    public void G3_Serial_defaults_are_the_documented_ones()
    {
        var transport = new TransportLike();
        Assert.Equal(9600, transport.BaudRate);
        Assert.Equal(8, transport.DataBits);
        Assert.Equal("none", transport.Parity);
        Assert.Equal("one", transport.StopBits);
        Assert.Equal("none", transport.Handshake);
        Assert.False(transport.DtrEnable);
        Assert.False(transport.RtsEnable);
        Assert.Equal(500, transport.ReadTimeoutMs);
        Assert.Equal(500, transport.WriteTimeoutMs);
    }
}
