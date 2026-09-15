using SuperSampler.Abstractions.Events;

namespace SuperSampler.Abstractions.Errors;

/// <summary>
/// 所有错误事件的基接口。订阅它即可收到全部错误事件，包括将来新增的类型。
/// 这是防「新增错误类型静默逃脱审计」的唯一手段（docs/02 第 10.2 节）。
/// </summary>
public interface IErrorEvent : IEvent
{
    /// <summary>错误标识与上下文。</summary>
    ErrorInfo Info { get; }
}

/// <summary>瞬时错误：可重试。</summary>
public interface ITransientError : IErrorEvent;

/// <summary>链路级错误：需要重连，重试没有意义。</summary>
public interface ILinkError : IErrorEvent;

/// <summary>永久错误：重试无意义，通常是配置或设备语义错误。</summary>
public interface IPermanentError : IErrorEvent;

/// <summary>不确定：写入可能已生效，绝不自动重试，需要回读校验。</summary>
public interface IIndeterminateError : IErrorEvent;

/// <summary>策略性放弃：预算耗尽、队列溢出、重连次数用尽等。</summary>
public interface IPolicyError : IErrorEvent;

/// <summary>
/// 错误事件公共基类：统一携带 <see cref="Info"/>，并把 Category/Level 委托给它。
/// 错误族划分对应 docs/02 第 10.3 节，粒度判据是「订阅者会不会区别对待」；
/// 比文档多出的 <see cref="WriteIndeterminateError"/> 来自三态写入设计（docs/02 第 6 节）。
/// </summary>
public abstract record ErrorEvent : IErrorEvent
{
    protected ErrorEvent(ErrorInfo info)
    {
        Info = info;
    }

    /// <summary>错误标识与上下文。</summary>
    public ErrorInfo Info { get; }

    /// <summary>事件类别，来自 <see cref="Info"/>。</summary>
    public EventCategory Category => Info.Category;

    /// <summary>事件级别，来自 <see cref="Info"/>。</summary>
    public EventLevel Level => Info.Level;
}

/// <summary>请求/应答超时。耗时、第几次见 Context。</summary>
public sealed record TimeoutError : ErrorEvent, ITransientError
{
    public TimeoutError(ErrorInfo info) : base(info) { }
}

/// <summary>链路故障：连接断开、被拒、IO 失败。重试无意义，需要重连。</summary>
public sealed record LinkError : ErrorEvent, ILinkError
{
    public LinkError(ErrorInfo info) : base(info) { }
}

/// <summary>协议帧错误：CRC、帧长、事务号不匹配。原始帧见 Context。</summary>
public sealed record ProtocolError : ErrorEvent, ITransientError
{
    public ProtocolError(ErrorInfo info) : base(info) { }
}

/// <summary>设备返回异常码。具体码在 <see cref="ExceptionCode"/>（如 02=非法数据地址，重试无意义）。</summary>
public sealed record DeviceExceptionError : ErrorEvent, IPermanentError
{
    public DeviceExceptionError(ErrorInfo info, byte exceptionCode) : base(info)
    {
        ExceptionCode = exceptionCode;
    }

    /// <summary>Modbus 异常码（01~0B）。</summary>
    public byte ExceptionCode { get; }
}

/// <summary>解码失败：长度不足、编码非法、BCD 非法。解码规则错了重试也没用。</summary>
public sealed record DecodeError : ErrorEvent, IPermanentError
{
    public DecodeError(ErrorInfo info) : base(info) { }
}

/// <summary>策略性放弃：预算耗尽、队列溢出、重连次数用尽。策略名见 <see cref="PolicyName"/>。</summary>
public sealed record PolicyError : ErrorEvent, IPolicyError
{
    public PolicyError(ErrorInfo info, string policyName) : base(info)
    {
        PolicyName = policyName;
    }

    /// <summary>触发放弃的策略名，如 Budget、QueueOverflow。</summary>
    public string PolicyName { get; }
}

/// <summary>配置非法：悬空引用、地址越界、类型矛盾。配置路径见 <see cref="ConfigPath"/>。</summary>
public sealed record ConfigError : ErrorEvent, IPermanentError
{
    public ConfigError(ErrorInfo info, string configPath) : base(info)
    {
        ConfigPath = configPath;
    }

    /// <summary>出问题的配置路径，如 points[mold.setTemp].address。</summary>
    public string ConfigPath { get; }
}

/// <summary>
/// 写入结果不确定：超时或应答异常，写入可能已生效。
/// 绝不自动重试（重复触发命令可能是安全事故），必须回读校验。
/// </summary>
public sealed record WriteIndeterminateError : ErrorEvent, IIndeterminateError
{
    public WriteIndeterminateError(ErrorInfo info) : base(info) { }
}
