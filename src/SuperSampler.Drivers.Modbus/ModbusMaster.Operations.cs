using System;
using SuperSampler.Drivers.Modbus.Wire;

namespace SuperSampler.Drivers.Modbus;

/// <summary>
/// <see cref="ModbusMaster"/> 的操作部分：读（01/02/03/04）、写单个（05/06）、写多个（0F/10）。
/// 应答解析通过闭包捕获请求 PDU 做回显校验。
/// </summary>
public sealed partial class ModbusMaster
{
    /// <summary>读位区（01/02）或寄存器区（03/04）。count 为位或寄存器个数。</summary>
    public ModbusReply Read(DataArea area, int address, int count, byte unitId, int timeoutMs, int retries, int retryIntervalMs)
    {
        var function = area switch
        {
            DataArea.Coil => FcReadCoils,
            DataArea.DiscreteInput => FcReadDiscrete,
            DataArea.InputRegister => FcReadInput,
            _ => FcReadHolding,
        };

        var pdu = new byte[5]
        {
            function,
            (byte)(address >> 8),
            (byte)(address & 0xFF),
            (byte)(count >> 8),
            (byte)(count & 0xFF),
        };

        return Execute(pdu, unitId, timeoutMs, retries, retryIntervalMs, reply =>
        {
            if (reply.Pdu!.Length < 2)
            {
                reply.Fail(ModbusFailureKind.Protocol, 0, "应答长度不足");
                return;
            }

            // D14 配套：ReadTcpResponse 返回 pdu 为 [fc, bc, data…]（无单元号，Modbus 标准）——
            // 字节数在 [1]，数据从 [2] 起。此前按带单元号偏移解析（数据 [3] 起）导致真链路必失败。
            var byteCount = reply.Pdu[1];
            if (reply.Pdu.Length < 2 + byteCount)
            {
                reply.Fail(ModbusFailureKind.Protocol, 0, "应答数据长度不足");
                return;
            }

            var words = new ushort[count];
            if (area is DataArea.Coil or DataArea.DiscreteInput)
            {
                // findings D106：位区应答只校验了「Pdu 长度 ≥ 2+byteCount」，没校验 byteCount 够不够
                // 放下 count 位 → 短包会在下面的循环里抛 IndexOutOfRange 裸异常逃出驱动边界（违反 D24）。
                var requiredBytes = (count + 7) / 8;
                if (byteCount < requiredBytes)
                {
                    reply.Fail(ModbusFailureKind.Protocol, 0, "应答位数据不足");
                    return;
                }

                for (var i = 0; i < count; i++)
                {
                    var bit = (reply.Pdu[2 + (i / 8)] >> (i % 8)) & 1;
                    words[i] = (ushort)bit;
                }
            }
            else
            {
                if (byteCount < count * 2)
                {
                    reply.Fail(ModbusFailureKind.Protocol, 0, "应答寄存器数据不足");
                    return;
                }

                for (var i = 0; i < count; i++)
                {
                    words[i] = (ushort)((reply.Pdu[2 + (i * 2)] << 8) | reply.Pdu[3 + (i * 2)]);
                }
            }

            reply.WithRegisters(words);
        });
    }

    /// <summary>写单个：线圈（05，value 0/1）或寄存器（06）。应答为请求回显。</summary>
    public ModbusReply WriteSingle(DataArea area, int address, ushort value, byte unitId, int timeoutMs, int retries, int retryIntervalMs)
    {
        if (area is not (DataArea.Coil or DataArea.HoldingRegister))
        {
            return ModbusReply.Fail(ModbusFailureKind.Protocol, 0, "写单个仅支持线圈与保持寄存器", 0);
        }

        var function = area == DataArea.Coil ? FcWriteSingleCoil : FcWriteSingleRegister;
        if (area == DataArea.Coil && value != 0) value = 0xFF00;

        var pdu = new byte[5]
        {
            function,
            (byte)(address >> 8),
            (byte)(address & 0xFF),
            (byte)(value >> 8),
            (byte)(value & 0xFF),
        };

        return Execute(pdu, unitId, timeoutMs, retries, retryIntervalMs, reply => ValidateEcho(pdu, reply));
    }

    /// <summary>写多个：线圈（0F，按位打包）或寄存器（10）。应答为请求前 5 字节回显。</summary>
    public ModbusReply WriteMulti(DataArea area, int address, ushort[] values, byte unitId, int timeoutMs, int retries, int retryIntervalMs)
    {
        if (values == null || values.Length == 0)
        {
            return ModbusReply.Fail(ModbusFailureKind.Protocol, 0, "写入值为空", 0);
        }

        if (area == DataArea.Coil)
        {
            var pdu = BuildWriteMultiCoilsPdu(address, values);
            return Execute(pdu, unitId, timeoutMs, retries, retryIntervalMs, reply => ValidateEcho(pdu, reply));
        }

        if (area == DataArea.HoldingRegister)
        {
            var pdu = BuildWriteMultiRegistersPdu(address, values);
            return Execute(pdu, unitId, timeoutMs, retries, retryIntervalMs, reply => ValidateEcho(pdu, reply));
        }

        return ModbusReply.Fail(ModbusFailureKind.Protocol, 0, "写多个仅支持线圈与保持寄存器", 0);
    }

    private static byte[] BuildWriteMultiCoilsPdu(int address, ushort[] values)
    {
        var byteCount = (values.Length + 7) / 8;
        var pdu = new byte[6 + byteCount];
        pdu[0] = FcWriteMultiCoils;
        pdu[1] = (byte)(address >> 8);
        pdu[2] = (byte)(address & 0xFF);
        pdu[3] = (byte)(values.Length >> 8);
        pdu[4] = (byte)(values.Length & 0xFF);
        pdu[5] = (byte)byteCount;

        for (var i = 0; i < values.Length; i++)
        {
            if (values[i] != 0)
            {
                pdu[6 + (i / 8)] |= (byte)(1 << (i % 8));
            }
        }

        return pdu;
    }

    private static byte[] BuildWriteMultiRegistersPdu(int address, ushort[] values)
    {
        var pdu = new byte[6 + (values.Length * 2)];
        pdu[0] = FcWriteMultiRegisters;
        pdu[1] = (byte)(address >> 8);
        pdu[2] = (byte)(address & 0xFF);
        pdu[3] = (byte)(values.Length >> 8);
        pdu[4] = (byte)(values.Length & 0xFF);
        pdu[5] = (byte)(values.Length * 2);

        for (var i = 0; i < values.Length; i++)
        {
            pdu[6 + (i * 2)] = (byte)(values[i] >> 8);
            pdu[7 + (i * 2)] = (byte)(values[i] & 0xFF);
        }

        return pdu;
    }

    /// <summary>写应答校验：成功应答为请求前 5 字节的回显。</summary>
    private static void ValidateEcho(byte[] requestPdu, ModbusReply reply)
    {
        if (reply.Pdu!.Length < 5)
        {
            reply.Fail(ModbusFailureKind.Protocol, 0, "写应答长度不足");
            return;
        }

        for (var i = 0; i < 5; i++)
        {
            if (reply.Pdu[i] != requestPdu[i])
            {
                reply.Fail(ModbusFailureKind.Protocol, 0, "写应答与请求回显不一致");
                return;
            }
        }
    }
}
