using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using SuperSampler.Abstractions.Facade;
using SuperSampler.Core.Config;
using SuperSampler.Core.Runtime;
using SuperSampler.Drivers.Modbus;
using SuperSampler.Drivers.Modbus.Wire;
using Xunit;

namespace SuperSampler.IntegrationTests;

/// <summary>
/// 驱动重试/失败分类 + TCP/RTU 帧层正确性 + 写应答回显校验。
/// 全部用进程内原始 socket 假从站（<see cref="RawModbusSlave"/>）：脚本化畸形帧、逐请求计数、
/// 计时与原始字节断言，完全确定性、不依赖外部模拟器。
/// 依据：docs/02 §2.2（异常码分类）、§2.3（链路错与超时边界）、docs/01 §6.3、ADR D37/D40/D61。
/// </summary>
[Collection(DriverHarnessCollection.Name)]
public sealed class DriverFrameMatrixTests
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

    private static int WriteFrames(RawModbusSlave slave)
        => slave.Requests.Count(r => r.Function is 5 or 6 or 15 or 16);

    // ───────────────────────── A. 重试与失败分类 ─────────────────────────

    /// <summary>A1：瞬时码集合 = 05/06/0A（docs/02 §2.2）；01/02/03/04/08 永久，0B 按链路（不重试）。</summary>
    [Theory]
    [InlineData(0x01, false)]
    [InlineData(0x02, false)]
    [InlineData(0x03, false)]
    [InlineData(0x04, false)]
    [InlineData(0x05, true)]
    [InlineData(0x06, true)]
    [InlineData(0x08, false)]
    [InlineData(0x0A, true)]
    [InlineData(0x0B, false)]
    [InlineData(0x00, false)]
    [InlineData(0x0C, false)]
    [InlineData(0xFF, false)]
    public void A1_IsTransientCode_matches_the_documented_set(byte code, bool transient)
        => Assert.Equal(transient, ModbusMaster.IsTransientCode(code));

    /// <summary>A2：读路径上瞬时码（06 从站忙）按预算重试，预算用尽后按 Protocol 上报原码。</summary>
    [Fact]
    public void A2_Transient_code_is_retried_within_budget_on_the_read_path()
    {
        using var slave = new RawModbusSlave { DefaultBehavior = RawModbusSlave.Fallback.Silent };
        slave.ReplyException(0x06);
        slave.ReplyException(0x06);
        slave.ReplyException(0x06);

        using var master = NewMaster(slave);
        var reply = master.Read(DataArea.HoldingRegister, 0, 1, 1, 400, retries: 2, retryIntervalMs: 20);

        Assert.False(reply.Success);
        Assert.Equal(ModbusFailureKind.Protocol, reply.Kind);
        Assert.Equal(0x06, reply.ExceptionCode);
        Assert.Equal(3, slave.RequestCount); // 1 次 + 2 次重试
    }

    /// <summary>A3：永久码（01/02/03/04/08）绝不重试；即使预算 2 也只发 1 次请求。</summary>
    [Theory]
    [InlineData(0x01)]
    [InlineData(0x02)]
    [InlineData(0x03)]
    [InlineData(0x04)]
    [InlineData(0x08)]
    public void A3_Permanent_codes_are_never_retried(byte code)
    {
        using var slave = new RawModbusSlave { DefaultBehavior = RawModbusSlave.Fallback.Silent };
        slave.ReplyException(code);
        slave.ReplyException(code);
        slave.ReplyException(code);

        using var master = NewMaster(slave);
        var reply = master.Read(DataArea.HoldingRegister, 0, 1, 1, 400, retries: 2, retryIntervalMs: 20);

        Assert.False(reply.Success);
        Assert.Equal(ModbusFailureKind.Protocol, reply.Kind);
        Assert.Equal(code, reply.ExceptionCode);
        Assert.Equal(1, slave.RequestCount);
    }

    /// <summary>
    /// A4：0x0B（网关目标设备无响应）按 docs/02 §2.2 = **LinkDown / 按链路**：
    /// 不作为请求级重试（只发 1 次），分类为链路级（交链路退避/重连），而不是设备异常码。
    /// </summary>
    [Fact]
    public void A4_Gateway_target_no_response_is_classified_as_link_down_not_retried()
    {
        using var slave = new RawModbusSlave { DefaultBehavior = RawModbusSlave.Fallback.Silent };
        slave.ReplyException(0x0B);
        slave.ReplyException(0x0B);
        slave.ReplyException(0x0B);

        using var master = NewMaster(slave);
        var reply = master.Read(DataArea.HoldingRegister, 0, 1, 1, 400, retries: 2, retryIntervalMs: 20);

        Assert.False(reply.Success);
        Assert.Equal(ModbusFailureKind.LinkDown, reply.Kind); // docs/02 §2.2「0B LinkDown 按链路」
        Assert.Equal(0x0B, reply.ExceptionCode);              // 原码保留，便于诊断
        Assert.Equal(1, slave.RequestCount);                  // 不重试
    }

    /// <summary>A5a：读路径真的吃重试预算（超时也按预算重发）。</summary>
    [Fact]
    public void A5a_Read_path_consumes_the_configured_retry_budget()
    {
        using var slave = new RawModbusSlave { DefaultBehavior = RawModbusSlave.Fallback.Silent };
        using var master = NewMaster(slave, requestTimeoutMs: 200);

        var reply = master.Read(DataArea.HoldingRegister, 0, 1, 1, 200, retries: 3, retryIntervalMs: 10);

        Assert.Equal(ModbusFailureKind.Timeout, reply.Kind);
        Assert.Equal(4, slave.RequestCount); // 1 + 3
    }

    /// <summary>A5b：写路径按调用方给的重试预算执行；引擎恒传 0（D61），故预算是 0 时只发 1 帧。</summary>
    [Fact]
    public void A5b_Write_with_zero_budget_sends_exactly_one_frame_on_timeout()
    {
        using var slave = new RawModbusSlave { DefaultBehavior = RawModbusSlave.Fallback.Silent };
        using var master = NewMaster(slave, requestTimeoutMs: 200);

        var reply = master.WriteSingle(DataArea.HoldingRegister, 0, 7, 1, 200, retries: 0, retryIntervalMs: 10);

        Assert.Equal(ModbusFailureKind.Timeout, reply.Kind);
        Assert.Equal(1, WriteFrames(slave));
    }

    /// <summary>A6a：retryIntervalMs 真生效（时间证据）：两次重试间隔各 250ms → 总耗时 ≥ 500ms。</summary>
    [Fact]
    public void A6a_Retry_interval_is_really_slept_between_attempts()
    {
        using var slave = new RawModbusSlave { DefaultBehavior = RawModbusSlave.Fallback.Silent };
        slave.ReplyException(0x06);
        slave.ReplyException(0x06);
        slave.ReplyException(0x06);

        using var master = NewMaster(slave);
        var watch = Stopwatch.StartNew();
        var reply = master.Read(DataArea.HoldingRegister, 0, 1, 1, 400, retries: 2, retryIntervalMs: 250);
        watch.Stop();

        Assert.Equal(ModbusFailureKind.Protocol, reply.Kind);
        Assert.Equal(3, slave.RequestCount);
        Assert.InRange(watch.ElapsedMilliseconds, 480, 3000); // 区间断言，避免时钟抖动
    }

    /// <summary>A6b：retryIntervalMs=0 时不引入可见等待（对照 A6a，排除「耗时来自别处」）。</summary>
    [Fact]
    public void A6b_Zero_retry_interval_adds_no_visible_wait()
    {
        using var slave = new RawModbusSlave { DefaultBehavior = RawModbusSlave.Fallback.Silent };
        slave.ReplyException(0x06);
        slave.ReplyException(0x06);
        slave.ReplyException(0x06);

        using var master = NewMaster(slave);
        var watch = Stopwatch.StartNew();
        master.Read(DataArea.HoldingRegister, 0, 1, 1, 400, retries: 2, retryIntervalMs: 0);
        watch.Stop();

        Assert.Equal(3, slave.RequestCount);
        Assert.InRange(watch.ElapsedMilliseconds, 0, 250);
    }

    // ───────────────────────── B. TCP 帧层 ─────────────────────────

    /// <summary>B1：请求帧字节与调用参数一致；合法应答解出的寄存器 = 地址推导值。</summary>
    [Fact]
    public void B1_Request_bytes_and_decoded_registers_are_exact()
    {
        using var slave = new RawModbusSlave();
        using var master = NewMaster(slave);

        var reply = master.Read(DataArea.HoldingRegister, 0x1234, 3, 0x11, 400, retries: 0, retryIntervalMs: 0);

        Assert.True(reply.Success, reply.Message);
        Assert.Equal(new ushort[] { 0x1234, 0x1235, 0x1236 }, reply.Registers);
        var request = Assert.Single(slave.Requests);
        Assert.Equal(0x11, request.UnitId);
        Assert.Equal(new byte[] { 0x03, 0x12, 0x34, 0x00, 0x03 }, request.Pdu);
    }

    /// <summary>B2：半包（一帧拆两次发，间隔 80ms）必须重组成功。</summary>
    [Fact]
    public void B2_Half_packet_split_reply_is_reassembled()
    {
        using var slave = new RawModbusSlave();
        slave.ReplySplit(3, slave.ValidReadPdu); // 在 MBAP 头中间切开

        using var master = NewMaster(slave);
        var reply = master.Read(DataArea.HoldingRegister, 0x0100, 2, 1, 800, 0, 0);

        Assert.True(reply.Success, reply.Message);
        Assert.Equal(new ushort[] { 0x0100, 0x0101 }, reply.Registers);
    }

    /// <summary>
    /// B3：粘包（同一连接上一条 send 里两个完整应答）——第一个成功；
    /// 残留副本**绝不能被下一个请求当应答**：事务号不符 → 链路错，且不返回任何数据。
    /// </summary>
    [Fact]
    public void B3_Sticky_packet_leftover_is_never_served_to_the_next_request()
    {
        using var slave = new RawModbusSlave();
        slave.ReplyWithDuplicate(slave.ValidReadPdu);

        using var master = NewMaster(slave);
        var first = master.Read(DataArea.HoldingRegister, 0x0200, 2, 1, 500, 0, 0);
        Assert.True(first.Success, "第一个请求应成功");
        Assert.Equal(new ushort[] { 0x0200, 0x0201 }, first.Registers);

        var second = master.Read(DataArea.HoldingRegister, 0x0300, 2, 1, 500, 0, 0);

        Assert.False(second.Success, "残留的上一应答绝不能被当作本请求的应答");
        Assert.Equal(ModbusFailureKind.LinkDown, second.Kind);
        Assert.Empty(second.Registers);
        Assert.Contains("事务号", second.Message);
    }

    /// <summary>B4：超长帧（MBAP 长度域 300 > 上限）→ 链路错。</summary>
    [Fact]
    public void B4_Oversized_mbap_length_is_a_link_error()
    {
        using var slave = new RawModbusSlave();
        slave.ReplyRaw(request => new byte[]
        {
            (byte)(request.TransactionId >> 8), (byte)(request.TransactionId & 0xFF), 0, 0, 0x01, 0x2C, request.UnitId,
            0x03, 0x02, 0x00, 0x01, 0x00, 0x02, 0x00,
        });

        using var master = NewMaster(slave);
        var reply = master.Read(DataArea.HoldingRegister, 0, 1, 1, 500, 0, 0);

        Assert.False(reply.Success);
        Assert.Equal(ModbusFailureKind.LinkDown, reply.Kind);
        Assert.Contains("MBAP", reply.Message);
    }

    /// <summary>B5：MBAP 长度声明**偏小**（只声明 1 字节 PDU）→ 应答长度不足，判 Protocol，绝不成功。</summary>
    [Fact]
    public void B5_Declared_length_shorter_than_the_frame_is_rejected()
    {
        using var slave = new RawModbusSlave();
        slave.ReplyRaw(request =>
        {
            var valid = slave.BuildReadResponse(request);
            valid[4] = 0;   // 长度域 = 2（只 1 字节 PDU：功能码）
            valid[5] = 2;
            return valid;
        });

        using var master = NewMaster(slave);
        var reply = master.Read(DataArea.HoldingRegister, 0, 1, 1, 500, 0, 0);

        Assert.False(reply.Success);
        Assert.Equal(ModbusFailureKind.Protocol, reply.Kind);
        Assert.Contains("长度不足", reply.Message);
    }

    /// <summary>B6：MBAP 长度声明**偏大**（实际只发 6 字节、连接保持）→ 等不到剩余字节 → 超时（瞬时）。</summary>
    [Fact]
    public void B6_Declared_length_longer_than_the_frame_times_out()
    {
        using var slave = new RawModbusSlave();
        slave.ReplyRaw(request => new byte[]
        {
            (byte)(request.TransactionId >> 8), (byte)(request.TransactionId & 0xFF), 0, 0, 0x00, 0x14, request.UnitId,
            0x03, 0x02, 0x00, 0x01, 0x00, 0x02,
        });

        using var master = NewMaster(slave, requestTimeoutMs: 400);
        var watch = Stopwatch.StartNew();
        var reply = master.Read(DataArea.HoldingRegister, 0, 1, 1, 400, 0, 0);
        watch.Stop();

        Assert.False(reply.Success);
        Assert.Equal(ModbusFailureKind.Timeout, reply.Kind); // 连接仍在、只是数据没到齐
        Assert.InRange(watch.ElapsedMilliseconds, 350, 2500);
    }

    /// <summary>B7：事务号不匹配（迟到/串道的他人应答）→ 链路错，不成功、不返回数据。</summary>
    [Fact]
    public void B7_Wrong_transaction_id_is_rejected()
    {
        using var slave = new RawModbusSlave();
        slave.OnRequest((request, session) => session.SendRaw(
            session.Frame(slave.ValidReadPdu(request), request.UnitId, (ushort)(request.TransactionId + 1))));

        using var master = NewMaster(slave);
        var reply = master.Read(DataArea.HoldingRegister, 0x0400, 2, 1, 500, 0, 0);

        Assert.False(reply.Success);
        Assert.Equal(ModbusFailureKind.LinkDown, reply.Kind);
        Assert.Empty(reply.Registers);
        Assert.Contains("事务号", reply.Message);
    }

    /// <summary>B8：unitId 不匹配（应答来自别的从站）→ 链路错。</summary>
    [Fact]
    public void B8_Wrong_unit_id_is_rejected()
    {
        using var slave = new RawModbusSlave();
        slave.OnRequest((request, session) => session.SendRaw(
            session.Frame(slave.ValidReadPdu(request), (byte)(request.UnitId + 1), request.TransactionId)));

        using var master = NewMaster(slave);
        var reply = master.Read(DataArea.HoldingRegister, 0x0400, 2, 1, 500, 0, 0);

        Assert.False(reply.Success);
        Assert.Equal(ModbusFailureKind.LinkDown, reply.Kind);
        Assert.Contains("从站号不匹配", reply.Message);
    }

    /// <summary>
    /// B9：功能码不符（请求 0x03、应答 0x04，长度与数据都「合法」）——绝不能当成功解析，
    /// 否则就是把别人的应答当本请求的数据（串数据重罪）。判链路错（帧错配）。
    /// </summary>
    [Fact]
    public void B9_Mismatched_function_code_is_never_accepted_as_data()
    {
        using var slave = new RawModbusSlave();
        slave.ReplyWrongFunction(0x04);

        using var master = NewMaster(slave);
        var reply = master.Read(DataArea.HoldingRegister, 0x0100, 2, 1, 500, 0, 0);

        Assert.False(reply.Success, "功能码不符的应答绝不能解析成数据");
        Assert.Equal(ModbusFailureKind.LinkDown, reply.Kind);
        Assert.Empty(reply.Registers);
        Assert.Contains("功能码", reply.Message);
    }

    /// <summary>
    /// B10：位区应答数据不足（请求 16 位 = 2 字节，应答只给 1 字节）——
    /// 不得抛裸异常（D24 契约）、不得成功；判 Protocol（应答不可用）。
    /// </summary>
    [Fact]
    public void B10_Short_bit_area_payload_is_a_protocol_failure_not_a_raw_exception()
    {
        using var slave = new RawModbusSlave();
        slave.ReplyPdu(_ => new byte[] { 0x01, 0x01, 0x00 }); // fc=01, byteCount=1（应为 2）

        using var master = NewMaster(slave);
        ModbusReply? reply = null;
        var escaped = Record.Exception(() => reply = master.Read(DataArea.Coil, 0, 16, 1, 500, 0, 0));

        Assert.Null(escaped); // 裸异常绝不逃逸
        Assert.NotNull(reply);
        Assert.False(reply!.Success);
        Assert.Equal(ModbusFailureKind.Protocol, reply.Kind);
        Assert.Contains("数据不足", reply.Message);
    }

    /// <summary>B11：截断回复后关闭（声明 13 字节只发 5 字节 + FIN）→ 链路错，不挂死。</summary>
    [Fact]
    public void B11_Truncated_reply_followed_by_close_is_a_link_error()
    {
        using var slave = new RawModbusSlave();
        slave.ReplyTruncatedAndClose(5, slave.ValidReadPdu);

        using var master = NewMaster(slave, requestTimeoutMs: 500);
        var watch = Stopwatch.StartNew();
        var reply = master.Read(DataArea.HoldingRegister, 0, 1, 1, 500, 0, 0);
        watch.Stop();

        Assert.False(reply.Success);
        Assert.Equal(ModbusFailureKind.LinkDown, reply.Kind);
        Assert.InRange(watch.ElapsedMilliseconds, 0, 3000);
    }

    /// <summary>B12：空回复（读到请求直接 FIN）→ 链路错，不成功。</summary>
    [Fact]
    public void B12_Empty_reply_is_a_link_error()
    {
        using var slave = new RawModbusSlave();
        slave.CloseWithoutReply();

        using var master = NewMaster(slave, requestTimeoutMs: 400);
        var watch = Stopwatch.StartNew();
        var reply = master.Read(DataArea.HoldingRegister, 0, 1, 1, 400, 0, 0);
        watch.Stop();

        Assert.False(reply.Success);
        Assert.Equal(ModbusFailureKind.LinkDown, reply.Kind);
        Assert.InRange(watch.ElapsedMilliseconds, 0, 3000);
    }

    /// <summary>
    /// B13：**迟到的他人应答**——请求 A 发出后收到「另一个请求的应答」（unit/功能码/长度全对，
    /// 只有事务号是别的事务、数据也不同）。必须失败（链路错）而不是返回别人的数据；
    /// 重连后重发 A 要能拿到 A 自己的数据（证明只是拒绝错配、不是永久坏）。
    /// </summary>
    [Fact]
    public void B13_A_stale_response_of_another_request_is_never_mixed_in_and_recovery_works()
    {
        using var slave = new RawModbusSlave();

        // 第一次：回一个「别人的」应答（事务号 +7、数据 0xBEEF）——形状合法，只有事务号能识破
        slave.OnRequest((request, session) =>
        {
            var pdu = slave.ValidReadPdu(request);
            pdu[2] = 0xBE;
            pdu[3] = 0xEF;
            session.SendRaw(session.Frame(pdu, request.UnitId, (ushort)(request.TransactionId + 7)));
            session.CloseGracefully();
        });

        using var master = NewMaster(slave, requestTimeoutMs: 500);
        var stale = master.Read(DataArea.HoldingRegister, 0x0500, 2, 1, 500, 0, 0);

        Assert.False(stale.Success, "迟到的他人应答必须被拒绝");
        Assert.Equal(ModbusFailureKind.LinkDown, stale.Kind);
        Assert.Empty(stale.Registers);
        Assert.DoesNotContain((ushort)0xBEEF, stale.Registers);

        // 重连后重发：必须拿到「本次请求」的正确数据
        var fresh = master.Read(DataArea.HoldingRegister, 0x0500, 2, 1, 500, 0, 0);
        Assert.True(fresh.Success, fresh.Message);
        Assert.Equal(new ushort[] { 0x0500, 0x0501 }, fresh.Registers);
    }

    // ───────────────────────── C. RTU 帧（RTU-over-TCP） ─────────────────────────

    /// <summary>C1：RTU 请求帧字节 + 独立复算 CRC 正确；应答解出正确寄存器。</summary>
    [Fact]
    public void C1_Rtu_request_bytes_carry_a_correct_crc_and_decode_exactly()
    {
        using var slave = new RawModbusSlave(rtuOverTcp: true);
        using var master = NewMaster(slave);

        var reply = master.Read(DataArea.HoldingRegister, 0x0102, 2, 9, 500, 0, 0);

        Assert.True(reply.Success, reply.Message);
        Assert.Equal(new ushort[] { 0x0102, 0x0103 }, reply.Registers);

        var request = Assert.Single(slave.Requests);
        Assert.Equal(9, request.UnitId);
        Assert.Equal(new byte[] { 0x03, 0x01, 0x02, 0x00, 0x02 }, request.Pdu);
        var crc = TestCrc16.Compute(request.Raw, 0, request.Raw.Length - 2);
        Assert.Equal((byte)(crc & 0xFF), request.Raw[request.Raw.Length - 2]);
        Assert.Equal((byte)(crc >> 8), request.Raw[request.Raw.Length - 1]);
    }

    /// <summary>C2：坏 CRC → 链路错（CRC 校验失败）。</summary>
    [Fact]
    public void C2_Bad_crc_is_a_link_error()
    {
        using var slave = new RawModbusSlave(rtuOverTcp: true);
        slave.OnRequest((request, session) =>
        {
            var frame = session.Frame(slave.ValidReadPdu(request), request.UnitId);
            frame[frame.Length - 1] ^= 0x01; // 翻转 CRC 高位 1 比特
            session.SendRaw(frame);
        });

        using var master = NewMaster(slave);
        var reply = master.Read(DataArea.HoldingRegister, 0, 1, 1, 500, 0, 0);

        Assert.False(reply.Success);
        Assert.Equal(ModbusFailureKind.LinkDown, reply.Kind);
        Assert.Contains("CRC", reply.Message);
    }

    /// <summary>C3：RTU unitId 不符 → 链路错。</summary>
    [Fact]
    public void C3_Rtu_unit_mismatch_is_a_link_error()
    {
        using var slave = new RawModbusSlave(rtuOverTcp: true);
        slave.OnRequest((request, session) => session.SendRaw(
            session.Frame(slave.ValidReadPdu(request), (byte)(request.UnitId + 5))));

        using var master = NewMaster(slave);
        var reply = master.Read(DataArea.HoldingRegister, 0, 1, 1, 500, 0, 0);

        Assert.False(reply.Success);
        Assert.Equal(ModbusFailureKind.LinkDown, reply.Kind);
        Assert.Contains("从站号不匹配", reply.Message);
    }

    /// <summary>C4：RTU 长度不符（声明 4 字节数据只给 2 字节，连接保持）→ 等不齐 → 超时。</summary>
    [Fact]
    public void C4_Rtu_declared_length_short_of_payload_times_out()
    {
        using var slave = new RawModbusSlave(rtuOverTcp: true);
        slave.ReplyPdu(_ => new byte[] { 0x03, 0x04, 0x00, 0x01 }); // 声明 4 字节，只给 2 字节

        using var master = NewMaster(slave, requestTimeoutMs: 400);
        var reply = master.Read(DataArea.HoldingRegister, 0, 2, 1, 400, 0, 0);

        Assert.False(reply.Success);
        Assert.Equal(ModbusFailureKind.Timeout, reply.Kind);
    }

    /// <summary>C5：RTU 功能码不符 → 绝不接受，判链路错。</summary>
    [Fact]
    public void C5_Rtu_mismatched_function_code_is_rejected()
    {
        using var slave = new RawModbusSlave(rtuOverTcp: true);
        slave.ReplyPdu(request => new byte[]
        {
            0x04, 0x02, 0xAB, 0xCD, // fc=04（请的是 03）
        });

        using var master = NewMaster(slave);
        var reply = master.Read(DataArea.HoldingRegister, 0, 1, 1, 500, 0, 0);

        Assert.False(reply.Success);
        Assert.Equal(ModbusFailureKind.LinkDown, reply.Kind);
        Assert.Contains("功能码", reply.Message);
    }

    /// <summary>C6：gapMs 真生效——请求之间的帧间隔 ≥ 配置值（200ms 下界，含处理余量）。</summary>
    [Fact]
    public void C6_Gap_ms_inserts_a_visible_pause_between_requests()
    {
        using var slave = new RawModbusSlave(rtuOverTcp: true);
        using var master = NewMaster(slave, requestTimeoutMs: 500, gapMs: 250);

        Assert.True(master.Read(DataArea.HoldingRegister, 0, 1, 1, 500, 0, 0).Success);
        Assert.True(master.Read(DataArea.HoldingRegister, 1, 1, 1, 500, 0, 0).Success);

        Assert.True(slave.WaitRequests(2, 3000));
        var requests = slave.Requests;
        var gap = requests[1].ArrivalMs - requests[0].ArrivalMs;
        Assert.InRange(gap, 200, 1200);
    }

    /// <summary>C6b：对照——gapMs=0 时帧间隔远小于配置值（排除「间隔来自别处」）。</summary>
    [Fact]
    public void C6b_Without_gap_the_requests_arrive_back_to_back()
    {
        using var slave = new RawModbusSlave(rtuOverTcp: true);
        using var master = NewMaster(slave, requestTimeoutMs: 500, gapMs: 0);

        Assert.True(master.Read(DataArea.HoldingRegister, 0, 1, 1, 500, 0, 0).Success);
        Assert.True(master.Read(DataArea.HoldingRegister, 1, 1, 1, 500, 0, 0).Success);

        var requests = slave.Requests;
        Assert.True(requests.Count >= 2);
        Assert.InRange(requests[1].ArrivalMs - requests[0].ArrivalMs, 0, 160);
    }

    // ───────────────────────── D. 写应答回显校验 ─────────────────────────

    /// <summary>D1：写单（06 寄存器 / 05 线圈）回显正确 → 成功，且线上帧字节正确。</summary>
    [Fact]
    public void D1_Write_single_with_correct_echo_succeeds_and_frames_exactly()
    {
        using var slave = new RawModbusSlave();
        using var master = NewMaster(slave);

        var register = master.WriteSingle(DataArea.HoldingRegister, 0x0010, 0x1234, 3, 500, 0, 0);
        var coil = master.WriteSingle(DataArea.Coil, 0x0002, 1, 3, 500, 0, 0);

        Assert.True(register.Success, register.Message);
        Assert.True(coil.Success, coil.Message);
        Assert.Equal(2, slave.RequestCount);
        Assert.Equal(new byte[] { 0x06, 0x00, 0x10, 0x12, 0x34 }, slave.Requests[0].Pdu);
        Assert.Equal(new byte[] { 0x05, 0x00, 0x02, 0xFF, 0x00 }, slave.Requests[1].Pdu); // 线圈 true = 0xFF00
    }

    /// <summary>D2：写单回显地址/值不符 → Protocol，绝不 Succeeded。</summary>
    [Fact]
    public void D2_Write_single_echo_mismatch_is_a_protocol_failure()
    {
        using var slave = new RawModbusSlave();
        slave.ReplyEchoCorrupted(3, 0x99); // 第 4 字节（值高字节）与请求不符

        using var master = NewMaster(slave);
        var reply = master.WriteSingle(DataArea.HoldingRegister, 0x0010, 0x1234, 3, 500, 0, 0);

        Assert.False(reply.Success);
        Assert.Equal(ModbusFailureKind.Protocol, reply.Kind);
        Assert.Contains("回显", reply.Message);
    }

    /// <summary>D3：写多（10）回显数量不符 → Protocol。</summary>
    [Fact]
    public void D3_Write_multi_echo_mismatch_is_a_protocol_failure()
    {
        using var slave = new RawModbusSlave();
        slave.ReplyEchoCorrupted(4, 0x05); // 数量低字节被篡改

        using var master = NewMaster(slave);
        var reply = master.WriteMulti(DataArea.HoldingRegister, 0x0020, new ushort[] { 1, 2, 3 }, 3, 500, 0, 0);

        Assert.False(reply.Success);
        Assert.Equal(ModbusFailureKind.Protocol, reply.Kind);
        Assert.Contains("回显", reply.Message);
    }

    /// <summary>D4：写多回显正确 → 成功（含线圈多写 0F 的位打包字节）。</summary>
    [Fact]
    public void D4_Write_multi_with_correct_echo_succeeds()
    {
        using var slave = new RawModbusSlave();
        using var master = NewMaster(slave);

        var registers = master.WriteMulti(DataArea.HoldingRegister, 0x0020, new ushort[] { 0x0001, 0x0002 }, 3, 500, 0, 0);
        var coils = master.WriteMulti(DataArea.Coil, 0x0030, new ushort[] { 1, 0, 1 }, 3, 500, 0, 0);

        Assert.True(registers.Success, registers.Message);
        Assert.True(coils.Success, coils.Message);
        Assert.Equal(new byte[] { 0x10, 0x00, 0x20, 0x00, 0x02, 0x04, 0x00, 0x01, 0x00, 0x02 }, slave.Requests[0].Pdu);
        Assert.Equal(new byte[] { 0x0F, 0x00, 0x30, 0x00, 0x03, 0x01, 0x05 }, slave.Requests[1].Pdu); // 位 0/2 置位 = 0b101
    }

    // ───────────── E. 写路径恒 0 重试（D61）在真链路上的回归：引擎 → 线缆帧数 ─────────────

    private static SamplerEngine NewWriteEngine(RawModbusSlave slave, int requestTimeoutMs = 400)
    {
        var variant = slave.IsRtu ? "rtuovertcp" : "tcp";
        var xml = "<SamplerConfig schemaVersion=\"3.0\">" +
                  "<Global><Retry count=\"2\" intervalMs=\"10\" />" +
                  "<Polling defaultIntervalMs=\"150\" requestTimeoutMs=\"" + requestTimeoutMs + "\" />" +
                  "<Reconnect enabled=\"false\" />" +
                  "<Quality onCommError=\"bad\" onCommErrorValue=\"null\" /></Global>" +
                  "<Transports><Transport id=\"t\" host=\"127.0.0.1\" port=\"" + slave.Port + "\" variant=\"" + variant +
                  "\" connectTimeoutMs=\"1500\" requestTimeoutMs=\"" + requestTimeoutMs + "\" /></Transports>" +
                  "<Devices><Device id=\"d\" transport=\"t\" unitId=\"1\" pointSet=\"ps\" /></Devices>" +
                  "<PointSets><PointSet id=\"ps\"><Points>" +
                  "<Point id=\"p\" address=\"0\" dataType=\"uint16\" area=\"holding\" access=\"readwrite\" mode=\"onDemand\" />" +
                  "</Points></PointSet></PointSets>" +
                  "</SamplerConfig>";
        var config = SamplerConfigLoader.Load(XDocument.Parse(xml), Path.GetTempPath());
        var engine = new SamplerEngine(config);
        engine.Start();
        return engine;
    }

    /// <summary>
    /// E1（D61 回归，真链路帧数）：配置 Retry count=2，写超时也**只发一帧**——
    /// 写路径绝不因超时重发（GATE-1/IT-15「启动命令被执行两次」的线上证据）。
    /// </summary>
    [Fact]
    public async System.Threading.Tasks.Task E1_Engine_write_timeout_puts_exactly_one_write_frame_on_the_wire()
    {
        using var slave = new RawModbusSlave { DefaultBehavior = RawModbusSlave.Fallback.Silent };
        using var engine = NewWriteEngine(slave);
        try
        {
            var result = await engine.SetValueAsync("d", "p", 5);

            Assert.Equal(WriteOutcome.Indeterminate, result.Outcome);
            Assert.Equal(1, WriteFrames(slave)); // 重试预算 2 绝不作用于写
            // 超时回读（若有）只能是读帧，绝不出现第二帧写
            Assert.All(slave.Requests.Skip(1), r => Assert.NotEqual(0x06, r.Function));
        }
        finally
        {
            engine.Dispose();
        }
    }

    /// <summary>E2（D61 回归）：写成功路径也只发一帧，回显校验通过 → Succeeded。</summary>
    [Fact]
    public async System.Threading.Tasks.Task E2_Engine_successful_write_puts_exactly_one_write_frame_on_the_wire()
    {
        using var slave = new RawModbusSlave();
        using var engine = NewWriteEngine(slave);
        try
        {
            var result = await engine.SetValueAsync("d", "p", 5);

            Assert.Equal(WriteOutcome.Succeeded, result.Outcome);
            Assert.Equal(1, WriteFrames(slave));
            Assert.Contains(slave.Requests, r => r.Function == 0x06 && r.Address == 0);
        }
        finally
        {
            engine.Dispose();
        }
    }
}
