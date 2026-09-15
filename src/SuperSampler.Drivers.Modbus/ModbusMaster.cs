using System;
using SuperSampler.Drivers.Modbus.Wire;

namespace SuperSampler.Drivers.Modbus;

/// <summary>失败种类：None 成功；Timeout 瞬时超时；LinkDown 链路失败；Protocol 设备异常码。</summary>
public enum ModbusFailureKind
{
    None = 0,
    Timeout = 1,
    LinkDown = 2,
    Protocol = 3,
}

/// <summary>一次 Modbus 交互的结果对象：成功给寄存器，失败给种类与异常码。不抛业务异常。</summary>
public sealed class ModbusReply
{
    public bool Success { get; private set; }
    public ushort[] Registers { get; private set; } = Array.Empty<ushort>();
    public ModbusFailureKind Kind { get; private set; }
    public byte ExceptionCode { get; private set; }
    public string Message { get; private set; } = string.Empty;
    public long ElapsedMs { get; private set; }

    /// <summary>原始应答 PDU（成功时；失败时为 null）。供解析回调使用。</summary>
    public byte[]? Pdu { get; private set; }

    public static ModbusReply Ok(ushort[] registers, long elapsedMs)
    {
        return new ModbusReply
        {
            Success = true,
            Registers = registers,
            ElapsedMs = elapsedMs,
        };
    }

    public static ModbusReply Fail(ModbusFailureKind kind, byte code, string message, long elapsedMs)
    {
        return new ModbusReply
        {
            Success = false,
            Kind = kind,
            ExceptionCode = code,
            Message = message,
            ElapsedMs = elapsedMs,
        };
    }

    /// <summary>挂上原始应答 PDU。</summary>
    public ModbusReply WithPdu(byte[] pdu)
    {
        Pdu = pdu;
        return this;
    }

    /// <summary>挂上解析出的寄存器。</summary>
    public ModbusReply WithRegisters(ushort[] registers)
    {
        Registers = registers;
        return this;
    }

    /// <summary>解析回调在发现应答不合规时调用：把成功翻转为失败。</summary>
    public void Fail(ModbusFailureKind kind, byte code, string message)
    {
        Success = false;
        Kind = kind;
        ExceptionCode = code;
        Message = message;
    }
}

/// <summary>
/// 主站抽象缝：写管道与调度器依赖此接口而非具体类，
/// 单元测试可注入假链路做确定性断言（findings B1）。
/// </summary>
public interface IModbusLink : IDisposable
{
    /// <summary>通道是否已连接。</summary>
    bool IsOpen { get; }

    /// <summary>读位区或寄存器区。</summary>
    ModbusReply Read(DataArea area, int address, int count, byte unitId, int timeoutMs, int retries, int retryIntervalMs);

    /// <summary>写单个线圈/寄存器。</summary>
    ModbusReply WriteSingle(DataArea area, int address, ushort value, byte unitId, int timeoutMs, int retries, int retryIntervalMs);

    /// <summary>写多个线圈/寄存器。</summary>
    ModbusReply WriteMulti(DataArea area, int address, ushort[] values, byte unitId, int timeoutMs, int retries, int retryIntervalMs);
}

/// <summary>
/// Modbus 主站：PDU 级读写的重试与分类。
/// 只管「这次请求成没成、失败属于哪类」，不认识点位；错误上下文由上层（引擎）补齐。
/// 重试策略按 docs/02 第 2.2 节：超时与瞬时异常码（05/06/0A）重试；
/// 永久码（01/02/03/04/08）绝不重试；链路失败不重试（交重连/下轮）。
/// </summary>
public sealed partial class ModbusMaster : IModbusLink, IDisposable
{
    private const byte FcReadCoils = 0x01;
    private const byte FcReadDiscrete = 0x02;
    private const byte FcReadHolding = 0x03;
    private const byte FcReadInput = 0x04;
    private const byte FcWriteSingleCoil = 0x05;
    private const byte FcWriteSingleRegister = 0x06;
    private const byte FcWriteMultiCoils = 0x0F;
    private const byte FcWriteMultiRegisters = 0x10;

    private readonly ModbusChannel _channel;

    /// <summary>构造主站。一条通道一个实例，通道内严格串行。</summary>
    public ModbusMaster(ChannelVariant variant, TransportLike options)
    {
        _channel = new ModbusChannel(variant, options);
    }

    /// <summary>通道是否已连接。</summary>
    public bool IsOpen => _channel.IsOpen;

    /// <inheritdoc />
    public void Dispose() => ((IDisposable)_channel).Dispose();

    internal ModbusReply Execute(byte[] pdu, byte unitId, int timeoutMs, int retries, int retryIntervalMs)
    {
        var start = Environment.TickCount;
        var attempt = 0;

        while (true)
        {
            attempt++;
            try
            {
                var response = _channel.Execute(pdu, unitId, timeoutMs);
                return ModbusReply.Ok(Array.Empty<ushort>(), Environment.TickCount - start).WithPdu(response);
            }
            catch (ModbusProtocolException ex)
            {
                // 永久码绝不重试；瞬时码（05/06/0A）按重试预算重发
                if (attempt > retries || !IsTransientCode(ex.Code))
                {
                    return ModbusReply.Fail(ModbusFailureKind.Protocol, ex.Code, ex.Message, Elapsed(start));
                }

                Sleep(retryIntervalMs);
            }
            catch (ModbusIoException ex) when (ex.IsTimeout)
            {
                if (attempt > retries)
                {
                    return ModbusReply.Fail(ModbusFailureKind.Timeout, 0, ex.Message, Elapsed(start));
                }

                Sleep(retryIntervalMs);
            }
            catch (ModbusIoException ex)
            {
                // 链路级失败：不重试，直接止损（docs/02 第 5.1 节「一次失败只交给一层」）
                return ModbusReply.Fail(ModbusFailureKind.LinkDown, 0, ex.Message, Elapsed(start));
            }
        }
    }

    /// <summary>瞬时异常码（可重试）：05 确认、06 从站忙、0A 网关路径不可用。</summary>
    public static bool IsTransientCode(byte code)
    {
        return code is 0x05 or 0x06 or 0x0A;
    }

    /// <summary>执行请求并在成功后做应答解析；解析可以把成功翻转为失败。</summary>
    private ModbusReply Execute(byte[] pdu, byte unitId, int timeoutMs, int retries, int retryIntervalMs, Action<ModbusReply> parse)
    {
        var reply = Execute(pdu, unitId, timeoutMs, retries, retryIntervalMs);
        if (reply.Success) parse(reply);
        return reply;
    }

    private static void Sleep(int ms)
    {
        if (ms > 0) System.Threading.Thread.Sleep(ms);
    }

    private static long Elapsed(int startTicks) => Environment.TickCount - startTicks >= 0
        ? Environment.TickCount - startTicks
        : 0;
}
