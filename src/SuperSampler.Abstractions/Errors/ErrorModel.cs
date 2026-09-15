namespace SuperSampler.Abstractions.Errors;

using System.Collections.Generic;
using SuperSampler.Abstractions.Events;

/// <summary>错误发生在哪一层。宿主据此判断该找谁排查。</summary>
public enum ErrorSource
{
    /// <summary>链路层：连接、串口、IO。</summary>
    Transport = 0,

    /// <summary>协议层：CRC、帧长、事务号。</summary>
    Protocol = 1,

    /// <summary>恢复层：设备语义、异常码、拒绝写入。</summary>
    Device = 2,

    /// <summary>解码层：类型、字序、字符串、BCD。</summary>
    Codec = 3,

    /// <summary>框架策略层：预算耗尽、队列溢出、重连次数用尽。</summary>
    Policy = 4,

    /// <summary>配置层：悬空引用、地址越界、类型矛盾。</summary>
    Config = 5,
}

/// <summary>
/// 错误的可恢复性分类，直接决定默认策略（docs/02 第 5 节）。
/// 这个值由错误事件的类型（标记接口）唯一确定，见 <see cref="ErrorClasses.Classify"/>；
/// 不允许在 ErrorInfo 里再写一份，否则必然出现互相矛盾的两份分类。
/// </summary>
public enum ErrorClass
{
    /// <summary>瞬时错误：可重试（超时、偶发校验错）。</summary>
    Transient = 0,

    /// <summary>链路故障：需要重连。</summary>
    LinkDown = 1,

    /// <summary>永久错误：重试无意义（非法地址、配置错误、硬件故障）。</summary>
    Permanent = 2,

    /// <summary>不确定：写入可能已生效，绝不自动重试，需回读校验。</summary>
    Indeterminate = 3,

    /// <summary>策略性放弃：预算耗尽、队列溢出、重连次数用尽。</summary>
    Policy = 4,
}

/// <summary>把错误事件映射到它的分类。分类由类型上的标记接口唯一确定。</summary>
public static class ErrorClasses
{
    public static ErrorClass Classify(IErrorEvent error) => error switch
    {
        ITransientError => ErrorClass.Transient,
        ILinkError => ErrorClass.LinkDown,
        IPermanentError => ErrorClass.Permanent,
        IIndeterminateError => ErrorClass.Indeterminate,
        IPolicyError => ErrorClass.Policy,
        _ => ErrorClass.Transient, // 未标记的错误事件按最保守（可重试）处理
    };
}

/// <summary>
/// 错误的结构化上下文：回答「哪里出错、第几次、花了多久、和哪次操作相关」。
/// 框架用它把深处发生的错误完整带给宿主；字段全部可选，按实际可用性填充。
/// </summary>
public sealed record ErrorContext(
    string? DeviceId = null,
    string? TransportId = null,
    int? UnitId = null,
    string? PointId = null,
    string? Area = null,
    int? Address = null,
    byte? FunctionCode = null,
    int Attempt = 0,
    int MaxAttempts = 0,
    long ElapsedMs = 0,
    int ConsecutiveFailures = 0,
    long CorrelationId = 0,
    IReadOnlyList<byte>? TxFrame = null,
    IReadOnlyList<byte>? RxFrame = null)
{
    /// <summary>单行诊断摘要，仅供日志与调试；用户可见文本一律走 MessageKey 本地化。</summary>
    public override string ToString()
    {
        var parts = new List<string>(10);
        if (DeviceId != null) parts.Add("device=" + DeviceId);
        if (TransportId != null) parts.Add("transport=" + TransportId);
        if (UnitId.HasValue) parts.Add("unit=" + UnitId.Value);
        if (PointId != null) parts.Add("point=" + PointId);
        if (Address.HasValue) parts.Add((Area ?? "addr") + "@" + Address.Value);
        if (FunctionCode.HasValue) parts.Add("fc=0x" + FunctionCode.Value.ToString("X2"));
        if (MaxAttempts > 0) parts.Add("attempt=" + Attempt + "/" + MaxAttempts);
        if (ElapsedMs > 0) parts.Add("elapsed=" + ElapsedMs + "ms");
        if (ConsecutiveFailures > 0) parts.Add("consecutive=" + ConsecutiveFailures);
        if (CorrelationId != 0) parts.Add("corr=" + CorrelationId);
        return string.Join(", ", parts);
    }
}

/// <summary>
/// 错误的稳定标识。<see cref="Code"/> 与 <see cref="MessageKey"/> 必须跨版本稳定，
/// 外部系统、脚本与报警规则会引用它们。
/// 分类（Transient / LinkDown / …）不在这里：它由错误事件的标记接口唯一确定，见 <see cref="ErrorClasses"/>。
/// </summary>
/// <param name="Code">稳定错误码，如 MODBUS.EXCEPTION.02。</param>
/// <param name="Category">事件类别，进入事件总线的路由依据。</param>
/// <param name="Level">事件级别。</param>
/// <param name="Source">错误发生在哪一层。</param>
/// <param name="MessageKey">i18n key，由宿主本地化；框架不产出具体语言的句子。</param>
/// <param name="Context">结构化上下文。</param>
public sealed record ErrorInfo(
    string Code,
    EventCategory Category,
    EventLevel Level,
    ErrorSource Source,
    string MessageKey,
    ErrorContext Context);
