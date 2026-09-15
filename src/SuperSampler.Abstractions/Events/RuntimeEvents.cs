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

/// <summary>写入审计事件。所有写操作（无论成败）都发出一条。</summary>
public sealed record PointWrittenEvent : IEvent
{
    public PointWrittenEvent(string deviceId, string pointId, object? value,
        string user, string outcome, string? message)
    {
        DeviceId = deviceId;
        PointId = pointId;
        Value = value;
        User = user;
        Outcome = outcome;
        Message = message;
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
}
