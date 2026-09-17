using System;
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

/// <summary>报警确认结果。</summary>
public enum AlarmAckOutcome
{
    /// <summary>确认已记录：AckPending 清除（报警回到可重触发状态），并已发出 AlarmAcknowledgedEvent。</summary>
    Acknowledged = 0,

    /// <summary>
    /// 该报警当前没有可确认的状态：**从未触发过**（状态条目在、但 <c>Raised</c> 一直为 false），
    /// 或该 (deviceId, pointId, alarmId) 从未被评估过 / alarmId 不在该点位的报警配置里（无状态条目）。
    /// 未发出任何事件；这不是失败。
    /// <para>
    /// findings W60 的口径订正（按实现 <c>AlarmEngine.TryAcknowledge</c>）：只要该报警**触发过**，
    /// 就返回 <see cref="AlarmAckOutcome.Acknowledged"/> 并再发一条 AlarmAcknowledgedEvent——
    /// 因此「重复确认」与「确认已恢复正常的报警」都算成功（确认是幂等运维动作，
    /// 宿主不必自己判「是不是已经确认过了」）。
    /// </para>
    /// </summary>
    NotPending = 1,
}

/// <summary>报警确认的结果对象（业务失败不抛异常，见 docs/02 D25）。</summary>
/// <param name="Outcome">确认结果。</param>
public sealed record AlarmAckResult(AlarmAckOutcome Outcome);

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

    /// <summary>
    /// 确认报警（ADR D36）：清除待确认标志（latch/ackRequired 报警由此回到可重触发状态），
    /// 并发出 <c>AlarmAcknowledgedEvent</c>（含确认人）。
    /// alarmId 与 AlarmRaisedEvent 的 AlarmId 一致（点位未写 Alarm@id 时为 "pointId#type"）。
    /// 无 user 重载以框架内置身份 Local 记审计。
    /// </summary>
    /// <exception cref="KeyNotFoundException">deviceId 或 pointId 不存在。</exception>
    Task<AlarmAckResult> AcknowledgeAlarmAsync(string deviceId, string pointId, string alarmId, CancellationToken ct = default);

    /// <summary>带操作者身份的报警确认：确认事件与审计按该用户记账。</summary>
    /// <exception cref="KeyNotFoundException">deviceId 或 pointId 不存在。</exception>
    Task<AlarmAckResult> AcknowledgeAlarmAsync(string deviceId, string pointId, string alarmId, ActingUser user, CancellationToken ct = default);

    /// <summary>
    /// 「多久没刷新」：距该点位**上次成功采集**的时长（ADR D40）。
    /// <c>null</c> = 从未成功采集过（如引擎刚启动、该点一直读不到）。
    /// <para>
    /// 「成功采集」= 该点所在读窗口通讯成功且该点完成解码落缓存；解码降级为 <c>Uncertain</c>/<c>Bad</c>
    /// （NaN、短帧等）也算采集成功——那说明通讯是通的。通讯失败置坏**不算**成功采集。
    /// 计算点按「上次求值成功」计。
    /// </para>
    /// <para>
    /// 框架**不做**陈旧判定：一个点算不算「太久没刷新」取决于工艺（温度 1s、配方 1h），
    /// 由宿主用这个值配合自己的阈值决定置灰/提示，不在这里硬编码。
    /// </para>
    /// </summary>
    /// <exception cref="KeyNotFoundException">deviceId 或 pointId 不存在。</exception>
    TimeSpan? GetValueAge(string deviceId, string pointId);
}
