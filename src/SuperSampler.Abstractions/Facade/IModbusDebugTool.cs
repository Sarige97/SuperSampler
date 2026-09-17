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

/// <summary>手动重试结果三态（ADR D40）。</summary>
public enum ManualRetryOutcome
{
    /// <summary>重试成功：该目标的退避队列已归零（下次失败从队列第一项重来）。</summary>
    Succeeded = 0,

    /// <summary>重试失败：退避继续，档位已加深一档（按 <c>Global/Reconnect@delays</c> 继续等）。</summary>
    Failed = 1,

    /// <summary>手动重试被配置关闭（<c>Global/Reconnect@manualRetry=false</c>）：未发出任何通讯，退避未被改动。</summary>
    NotSupported = 2,
}

/// <summary>
/// 手动重试结果对象。与写结果同风格：业务失败用结果对象表达，不抛异常
/// （只有编程错误——未知 id——才抛 <see cref="KeyNotFoundException"/>）。
/// </summary>
/// <param name="Outcome">三态结果。</param>
/// <param name="InterruptedBackoff">本次调用是否真的打断了正在进行的退避等待（true = 调用前该目标在退避中）。</param>
/// <param name="Error">失败时的错误信息；成功与被拒绝时为 null。</param>
public sealed record ManualRetryResult(ManualRetryOutcome Outcome, bool InterruptedBackoff, ErrorInfo? Error);

/// <summary>
/// 调试工具：三条能力。
/// 1) 物理层：绕过点表语义按 (area, address) 直接读写 —— 返回原始寄存器，不做任何解析；
/// 2) 语义层：手动触发点表定义的读取（onDemand 点、强制刷新、整块读）；
/// 3) 运维层：手动重试（单设备 / 整条链路，ADR D40）—— 打断退避、立即重试，**不受 allowRawAccess 门禁**
///    （它是运维动作，不是裸读写；绕过写保护的是 1) 的原始写，生产环境必须关）。
/// 约束：
/// - 物理层受配置 Diagnostics@allowRawAccess 开关控制（默认关闭）；
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

    /// <summary>
    /// 手动触发一个设备上**全部** <c>mode="onDemand"</c> 的块与点位：整批读一遍，结果写入实时缓存。
    /// 语义：一次调用把该设备所有 onDemand 的块（各一次请求）与 onDemand 的散点组
    /// （按自动分组规则合并、必要的切分也在同一次调用内背靠背完成）全部刷一遍；
    /// <c>auto</c> / <c>once</c> 项不在范围内——单点、单块刷新请用
    /// <see cref="TriggerReadAsync"/> / <see cref="TriggerBlockReadAsync"/>。
    /// 只读 0 个 onDemand 项时返回 0，不算错误。
    /// </summary>
    /// <returns>本次成功刷新的点位数（读失败窗口内的点位不计入）。</returns>
    /// <exception cref="KeyNotFoundException">deviceId 不存在。</exception>
    Task<int> TriggerOnDemandReadAsync(string deviceId, CancellationToken ct = default);

    /// <summary>
    /// **手动重试单台设备**（ADR D40）：立刻打断该设备当前的退避等待，立即按其轮询计划采集一次
    /// （每个 auto 桶各一次 + 每个未完成的 once 项各一次；onDemand 不在范围内，请用
    /// <see cref="TriggerOnDemandReadAsync"/>）。
    /// <para>
    /// 语义：成功一次 → 该设备与所属链路的退避队列**一起归零**；失败 → 退避继续（档位加深一档）。
    /// 设备没有 auto/once 项时不做任何通讯，返回 <see cref="ManualRetryOutcome.Succeeded"/>。
    /// </para>
    /// <para>
    /// **不受 <c>Diagnostics@allowRawAccess</c> 门禁**：它是运维动作（现场「立刻再试一次」），
    /// 不是裸读写；原始读写才绕过写保护，必须留在门禁之后。
    /// 该调用与后台轮询并发安全：通道内部严格串行，状态机加锁，两者不会互相破坏。
    /// </para>
    /// </summary>
    /// <exception cref="KeyNotFoundException">deviceId 不存在。</exception>
    Task<ManualRetryResult> RetryDeviceAsync(string deviceId, CancellationToken ct = default);

    /// <summary>
    /// **手动重试整条链路**（ADR D40）：清空该链路与链路上全部从站的退避等待 →
    /// **关闭连接并立即重连**（对每个参与轮询的设备各按计划尝试一次采集）。
    /// <para>
    /// 语义：任一设备成功即刷新其点位；全部成功 → 该链路队列归零；有失败 → 整体返回
    /// <see cref="ManualRetryOutcome.Failed"/>（失败设备的退避按层级继续）。
    /// 手动重试关闭（<c>Global/Reconnect@manualRetry=false</c>）时返回
    /// <see cref="ManualRetryOutcome.NotSupported"/>，不发任何通讯。
    /// 同样不受 <c>Diagnostics@allowRawAccess</c> 门禁。
    /// </para>
    /// </summary>
    /// <exception cref="KeyNotFoundException">transportId 不存在。</exception>
    Task<ManualRetryResult> RetryLinkAsync(string transportId, CancellationToken ct = default);
}
