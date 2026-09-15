using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SuperSampler.Abstractions.Errors;
using SuperSampler.Abstractions.Values;

namespace SuperSampler.Abstractions.Facade;

/// <summary>
/// 宿主认证后的操作者。分工：宿主负责认证（登录），框架按配置的角色做授权并写审计。
/// </summary>
/// <param name="Name">用户名（进审计记录）。</param>
/// <param name="RoleId">角色 id，对应配置 Users/Role@id。</param>
public sealed record ActingUser(string Name, string RoleId);

/// <summary>
/// 写入结果。业务失败用结果对象表达，不抛异常（docs/02 三条传播路径里的同步路径）；
/// 只有编程错误（null 参数、未知 id）才抛异常。
/// </summary>
public enum WriteOutcome
{
    /// <summary>写入成功（点位配置 verify=true 时已通过回读校验）。</summary>
    Succeeded = 0,

    /// <summary>明确失败：设备拒绝或通讯错误。可安全重试。</summary>
    Failed = 1,

    /// <summary>不确定：超时或应答异常，写入可能已生效。绝不自动重试，须回读确认。</summary>
    Indeterminate = 2,

    /// <summary>被拒绝：范围 / 联锁 / 权限校验未通过，未发出任何通讯。</summary>
    Rejected = 3,
}

/// <summary>写入的结果对象。</summary>
/// <param name="Outcome">四态结果。</param>
/// <param name="Error">失败或不确定时的错误信息；成功与拒绝时可能为 null（拒绝原因见日志/事件）。</param>
/// <param name="Readback">回读值：verify=true 的点位写入后回读所得，或 Indeterminate 后的回读结果。</param>
/// <param name="VerifyMismatch">
/// 仅当 verify=true 且通讯成功时置位：设备应答成功，但回读值与写入值不一致
/// （如设备自行钳位）。通讯确实成功，故 Outcome 仍为 Succeeded；
/// 宿主应据此在界面明确提示「实际值 X 与写入值不同」，且**不要自动重试**。
/// </param>
public sealed record WriteResult(
    WriteOutcome Outcome,
    ErrorInfo? Error,
    PointValue? Readback,
    bool VerifyMismatch = false);

/// <summary>
/// 设备管理器：宿主日常使用的便捷门面。
/// 寻址主键是 (deviceId, pointId)：deviceId 指向一个从站（链路 + 从站号），
/// pointId 在该设备的点表内唯一。三层身份见 docs/04 第 1 节。
/// </summary>
public interface IDeviceManager
{
    /// <summary>
    /// 解析后的显示字符串：缩放、枚举映射、前后缀、i18n 均已完成。
    /// 质量非 Good 一律返回全局 nullText，绝不把旧值当好值显示。
    /// </summary>
    /// <exception cref="KeyNotFoundException">deviceId 或 pointId 不存在 —— 配置写错应尽早暴露。</exception>
    string GetValue(string deviceId, string pointId);

    /// <summary>
    /// 完整三元组 {值, 质量, 时间戳}。读实时缓存，不发起任何通讯，可高频调用。
    /// 需要按质量分支处理（置灰、判联锁）的逻辑用这个，不要用 <see cref="GetValue"/>。
    /// </summary>
    /// <exception cref="KeyNotFoundException">id 不存在。</exception>
    PointValue GetValueDetail(string deviceId, string pointId);

    /// <summary>
    /// 基础写入，走完整写管道：范围校验 → 联锁 → 权限 → 下发 →（verify=true 时）回读。
    /// 无 user 重载以框架内置身份 Local 记审计；接入宿主登录后请改用带 user 的重载。
    /// </summary>
    /// <exception cref="KeyNotFoundException">id 不存在。</exception>
    Task<WriteResult> SetValueAsync(string deviceId, string pointId, object? value, CancellationToken ct = default);

    /// <summary>带操作者身份的写入：权限按该用户角色校验，审计按该用户记账。</summary>
    /// <exception cref="KeyNotFoundException">id 不存在。</exception>
    Task<WriteResult> SetValueAsync(string deviceId, string pointId, object? value, ActingUser user, CancellationToken ct = default);
}
