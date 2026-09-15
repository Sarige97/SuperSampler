using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SuperSampler.Abstractions.Errors;
using SuperSampler.Abstractions.Values;

namespace SuperSampler.Abstractions.Facade;

/// <summary>Modbus 数据区，与配置里点位的 area 对应。</summary>
public enum ModbusArea
{
    /// <summary>线圈，可读写，布尔。</summary>
    Coil = 0,

    /// <summary>离散输入，只读，布尔。</summary>
    DiscreteInput = 1,

    /// <summary>输入寄存器，只读，16 位。</summary>
    InputRegister = 2,

    /// <summary>保持寄存器，可读写，16 位。</summary>
    HoldingRegister = 3,
}

/// <summary>原始读结果：不经过任何解析，返回线上的原始寄存器。</summary>
/// <param name="Success">是否成功。</param>
/// <param name="Registers">寄存器原始值（位区每个元素为 0/1）。</param>
/// <param name="RawBytes">原始字节（大端拼接，便于人工比对报文）。</param>
/// <param name="Error">失败时的错误信息。</param>
/// <param name="ElapsedMs">耗时。</param>
public sealed record RawReadResult(bool Success, ushort[] Registers, byte[] RawBytes, ErrorInfo? Error, long ElapsedMs);

/// <summary>原始写结果。</summary>
public sealed record RawWriteResult(bool Success, ErrorInfo? Error, long ElapsedMs);

/// <summary>
/// 调试工具：两条能力。
/// 1) 物理层：绕过点表语义按 (area, address) 直接读写 —— 返回原始寄存器，不做任何解析；
/// 2) 语义层：手动触发点表定义的读取（onDemand 点、强制刷新、整块读）。
/// 约束：
/// - 受配置 Diagnostics@allowRawAccess 开关控制（默认关闭）。原始写绕过写保护（范围/联锁/权限），
///   这正是调试用途，也因此生产环境必须保持关闭；
/// - 所有请求与轮询共用同一条通道队列，排队执行，不插队、不破坏轮询顺序。
/// </summary>
public interface IModbusDebugTool
{
    /// <summary>原始读：直读一段线圈 / 离散量 / 寄存器，不做解析。</summary>
    Task<RawReadResult> RawReadAsync(string deviceId, ModbusArea area, ushort address, ushort count, CancellationToken ct = default);

    /// <summary>原始写：直写线圈 / 寄存器。Coil 与 DiscreteInput 的 values 以 0/1 表示位。</summary>
    Task<RawWriteResult> RawWriteAsync(string deviceId, ModbusArea area, ushort address, IReadOnlyList<ushort> values, CancellationToken ct = default);

    /// <summary>手动触发某个点位定义的读取（走该点的完整解析管道），结果写入实时缓存并返回。</summary>
    /// <exception cref="KeyNotFoundException">id 不存在。</exception>
    Task<PointValue> TriggerReadAsync(string deviceId, string pointId, CancellationToken ct = default);

    /// <summary>手动触发整块读取（配置里的 Block），返回本块读到的寄存器数量。</summary>
    /// <exception cref="KeyNotFoundException">blockId 不存在。</exception>
    Task<int> TriggerBlockReadAsync(string blockId, CancellationToken ct = default);
}
