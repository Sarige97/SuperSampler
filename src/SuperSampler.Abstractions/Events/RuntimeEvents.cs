using System;
using SuperSampler.Abstractions.Values;

namespace SuperSampler.Abstractions.Events;

/// <summary>报警类事件基接口：宿主一条订阅收齐激活/清除/确认。</summary>
public interface IAlarmEvent : IEvent
{
    /// <summary>报警标识。</summary>
    string AlarmId { get; }

    /// <summary>设备 id。</summary>
    string DeviceId { get; }

    /// <summary>点位 id。</summary>
    string PointId { get; }
}

/// <summary>点位值变化。只在值（或质量）相对缓存变化时发出；高频类别，宿主按需订阅或用批量订阅。</summary>
public sealed record PointValueChangedEvent : IEvent
{
    public PointValueChangedEvent(string deviceId, string pointId, PointValue value)
    {
        DeviceId = deviceId;
        PointId = pointId;
        Value = value;
    }

    public EventCategory Category => EventCategory.Value;

    public EventLevel Level => EventLevel.Info;

    public string DeviceId { get; }

    public string PointId { get; }

    /// <summary>新值三元组。</summary>
    public PointValue Value { get; }
}

/// <summary>报警激活。</summary>
public sealed record AlarmRaisedEvent : IAlarmEvent
{
    public AlarmRaisedEvent(string alarmId, string deviceId, string pointId,
        string alarmType, double? limit, object? value, string? priority, string? message)
    {
        AlarmId = alarmId;
        DeviceId = deviceId;
        PointId = pointId;
        AlarmType = alarmType;
        Limit = limit;
        Value = value;
        Priority = priority;
        Message = message;
    }

    public EventCategory Category => EventCategory.Alarm;

    public EventLevel Level => EventLevel.Warn;

    /// <summary>报警标识：pointId + 类型。</summary>
    public string AlarmId { get; }

    public string DeviceId { get; }

    public string PointId { get; }

    /// <summary>报警类型：high / highHigh / low / lowLow / digital。</summary>
    public string AlarmType { get; }

    /// <summary>触发限值（位报警为 null）。</summary>
    public double? Limit { get; }

    /// <summary>触发时的值。</summary>
    public object? Value { get; }

    /// <summary>报警等级（AlarmClasses id）。</summary>
    public string? Priority { get; }

    /// <summary>报警文本（i18n 已替换）。</summary>
    public string? Message { get; }
}

/// <summary>报警清除。</summary>
public sealed record AlarmClearedEvent : IAlarmEvent
{
    public AlarmClearedEvent(string alarmId, string deviceId, string pointId, string alarmType)
    {
        AlarmId = alarmId;
        DeviceId = deviceId;
        PointId = pointId;
        AlarmType = alarmType;
    }

    public EventCategory Category => EventCategory.Alarm;

    public EventLevel Level => EventLevel.Info;

    public string AlarmId { get; }

    public string DeviceId { get; }

    public string PointId { get; }

    public string AlarmType { get; }
}

/// <summary>报警确认（写审计用；latch 报警由此解锁）。</summary>
public sealed record AlarmAcknowledgedEvent : IAlarmEvent
{
    public AlarmAcknowledgedEvent(string alarmId, string deviceId, string pointId, string user)
    {
        AlarmId = alarmId;
        DeviceId = deviceId;
        PointId = pointId;
        User = user;
    }

    public EventCategory Category => EventCategory.Alarm;

    public EventLevel Level => EventLevel.Info;

    public string AlarmId { get; }

    public string DeviceId { get; }

    public string PointId { get; }

    /// <summary>确认人。</summary>
    public string User { get; }
}

/// <summary>退避作用域：设备级（只有该从站等）/ 链路级（整条链路一起等）。</summary>
public enum BackoffScope
{
    /// <summary>设备级：链路正常（连接在、其它从站正常），只有该从站读超时。</summary>
    Device = 0,

    /// <summary>链路级：IO 层失败（连接断开 / 写不出去 / socket 异常），或同链路全部从站都在设备级退避。</summary>
    Link = 1,
}

/// <summary>退避类事件的公共字段（进入 / 恢复）。设备级带 DeviceId，链路级 DeviceId 为 null。</summary>
public interface IBackoffEvent : IEvent
{
    /// <summary>退避作用域。</summary>
    BackoffScope Scope { get; }

    /// <summary>设备 id（链路级为 null）。</summary>
    string? DeviceId { get; }

    /// <summary>链路 id。</summary>
    string TransportId { get; }
}

/// <summary>
/// 进入退避（失败一次即进队列，docs/01 §6.3）：本次失败已判定该目标要等一段时间。
/// 每次失败（重试后仍失败也一样）发一条，携带本次档位与下次重试时刻，宿主可直接显示「下次重试时间」。
/// </summary>
public sealed record BackoffEnteredEvent : IBackoffEvent
{
    public BackoffEnteredEvent(BackoffScope scope, string? deviceId, string transportId, int? unitId,
        int attempt, int delayMs, DateTimeOffset nextRetryAt, string reasonCode)
    {
        Scope = scope;
        DeviceId = deviceId;
        TransportId = transportId;
        UnitId = unitId;
        Attempt = attempt;
        DelayMs = delayMs;
        NextRetryAt = nextRetryAt;
        ReasonCode = reasonCode;
    }

    public EventCategory Category => Scope == BackoffScope.Device ? EventCategory.Device : EventCategory.Connection;

    public EventLevel Level => EventLevel.Warn;

    /// <inheritdoc />
    public BackoffScope Scope { get; }

    /// <inheritdoc />
    public string? DeviceId { get; }

    /// <inheritdoc />
    public string TransportId { get; }

    /// <summary>从站号（链路级为 null）。</summary>
    public int? UnitId { get; }

    /// <summary>退避档位：本次是连续第几次失败（1 基）。取 delays[Attempt-1]，超过队列长度即一直用最后一个值。</summary>
    public int Attempt { get; }

    /// <summary>本档位的等待毫秒数（= delays 里该档位的值）。</summary>
    public int DelayMs { get; }

    /// <summary>下次重试时刻（UTC）。</summary>
    public DateTimeOffset NextRetryAt { get; }

    /// <summary>触发原因码：MODBUS.TIMEOUT（设备级）/ MODBUS.LINK（链路级）/ SS.BACKOFF.ALL_DEVICES（同链路全部从站退避 → 升级）。</summary>
    public string ReasonCode { get; }
}

/// <summary>退避恢复：该目标一次成功通讯，退避队列已归零（回到正常轮询节奏）。</summary>
public sealed record BackoffRecoveredEvent : IBackoffEvent
{
    public BackoffRecoveredEvent(BackoffScope scope, string? deviceId, string transportId, int? unitId,
        int attempts, long durationMs)
    {
        Scope = scope;
        DeviceId = deviceId;
        TransportId = transportId;
        UnitId = unitId;
        Attempts = attempts;
        DurationMs = durationMs;
    }

    public EventCategory Category => Scope == BackoffScope.Device ? EventCategory.Device : EventCategory.Connection;

    public EventLevel Level => EventLevel.Info;

    /// <inheritdoc />
    public BackoffScope Scope { get; }

    /// <inheritdoc />
    public string? DeviceId { get; }

    /// <inheritdoc />
    public string TransportId { get; }

    /// <summary>从站号（链路级为 null）。</summary>
    public int? UnitId { get; }

    /// <summary>本次退避「会话」里连续失败了几次（1 = 失败一次后立刻恢复）。</summary>
    public int Attempts { get; }

    /// <summary>本次退避会话的时长（毫秒，首次失败 → 恢复）。</summary>
    public long DurationMs { get; }
}

/// <summary>
/// 写入审计事件。所有写操作（无论成败）都发出一条。
/// <para>
/// findings D67：除「谁在何时写了什么、结果如何」之外，审计侧还需要闭环校验写入效果，
/// 故补齐 <see cref="ElapsedMs"/>（耗时）、<see cref="DidReadback"/>（是否发起过回读）、
/// <see cref="Readback"/>（回读值）、<see cref="VerifyMismatch"/>（设备钳位标记）四项。
/// **只加不改**：既有字段名与顺序保持不变，新增项以可选构造参数追加（老调用方照旧编译）。
/// </para>
/// </summary>
public sealed record PointWrittenEvent : IEvent
{
    public PointWrittenEvent(string deviceId, string pointId, object? value,
        string user, string outcome, string? message,
        long elapsedMs = 0, bool didReadback = false, PointValue? readback = null,
        bool verifyMismatch = false)
    {
        DeviceId = deviceId;
        PointId = pointId;
        Value = value;
        User = user;
        Outcome = outcome;
        Message = message;
        ElapsedMs = elapsedMs;
        DidReadback = didReadback;
        Readback = readback;
        VerifyMismatch = verifyMismatch;
    }

    public EventCategory Category => EventCategory.Write;

    public EventLevel Level => EventLevel.Info;

    public string DeviceId { get; }

    public string PointId { get; }

    /// <summary>请求写入的工程值。</summary>
    public object? Value { get; }

    /// <summary>操作者（Local 或宿主传入的用户名）。</summary>
    public string User { get; }

    /// <summary>四态结果：Succeeded / Failed / Indeterminate / Rejected。</summary>
    public string Outcome { get; }

    /// <summary>补充说明（拒绝原因等）。</summary>
    public string? Message { get; }

    /// <summary>
    /// 本次写操作在管道里花掉的时间（毫秒）：从进入 <c>SetValueAsync</c> 到得出四态结果，
    /// **含**编码/读-改-写第一步、通道下发（含超时等待）、verify 回读、点动归零等待。
    /// 宿主用它做「写响应时间」统计与慢写排查；拒绝（未发通讯）路径通常接近 0。
    /// </summary>
    public long ElapsedMs { get; }

    /// <summary>
    /// 本次写操作是否发起过回读：<c>verify=true</c> 的成功写、写超时后的回读定论都算
    /// （回读请求发出但没拿到值也算 true）。拒绝（<c>Rejected</c>，未发出任何通讯）恒为 false。
    /// 与 <see cref="Readback"/> 配合使用：<c>DidReadback=true &amp;&amp; Readback=null</c> = 回读失败/没拿到值。
    /// </summary>
    public bool DidReadback { get; }

    /// <summary>回读值（工程值三元组）：verify=true 的写入成功后回读所得，或写超时后的回读结果；未回读/回读失败为 null。</summary>
    public PointValue? Readback { get; }

    /// <summary>
    /// 仅当 <c>verify=true</c> 且通讯成功时可能置位：设备应答成功，但回读值与写入值不一致（如设备自行钳位）。
    /// 此时 <see cref="Outcome"/> 仍是 Succeeded（通讯确实成功），宿主应提示「实际值 X 与写入值不同」且**不要自动重试**。
    /// </summary>
    public bool VerifyMismatch { get; }
}
