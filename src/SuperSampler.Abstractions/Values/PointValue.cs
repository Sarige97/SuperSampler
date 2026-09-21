using System;
using System.Globalization;

namespace SuperSampler.Abstractions.Values;

/// <summary>点位数据质量。语义对齐 OPC UA：值永远伴随质量与时间戳，三者不可分割。</summary>
public enum PointQuality
{
    /// <summary>数据可信。</summary>
    Good = 0,

    /// <summary>数据可疑（如越界、换算异常），值可能仍可用但需谨慎对待。</summary>
    Uncertain = 1,

    /// <summary>数据不可用（解析失败、被策略丢弃等）。</summary>
    Bad = 2,

    /// <summary>链路或设备离线，值是旧的或不存在。</summary>
    Offline = 3,
}

/// <summary>
/// 点位值四元组：{值, 原始值, 质量, 时间戳}。框架内一切消费方（界面、历史、报警、报表、导出）
/// 统一使用这个结构，不直接接触原始寄存器。不可变。
/// <see cref="OriginValue"/> 是协议侧**缩放前**的原始值（类型解码后、Scale 之前），
/// 计算点与通讯失败/离线点为 null。
/// </summary>
public readonly struct PointValue : IEquatable<PointValue>
{
    /// <summary>工程值：bool / int / long / double / string / DateTime / byte[]，由点位 dataType 决定。</summary>
    public object? Value { get; }

    /// <summary>
    /// 协议侧**缩放前**原始值（= 脚本输入里的 rawValue）：
    /// 数值类型 = 类型解码后未缩放的值（如 uint16 的 2200）；位点 = bool；位域 = ushort；
    /// string/bcd/datetime = 各自解码结果；raw = ushort[]。计算点、通讯失败/离线点为 null。
    /// </summary>
    public object? OriginValue { get; }

    public PointQuality Quality { get; }

    public DateTimeOffset Timestamp { get; }

    /// <summary>质量非 Good 时的坏值原因（i18n key）；Good 时为 null。</summary>
    public string? Reason { get; }

    /// <summary>质量是否可信。界面、报警、历史都应先看这个。</summary>
    public bool IsGood => Quality == PointQuality.Good;

    public PointValue(object? value, PointQuality quality, DateTimeOffset timestamp, string? reason = null, object? originValue = null)
    {
        Value = value;
        OriginValue = originValue;
        Quality = quality;
        Timestamp = timestamp;
        Reason = reason;
    }

    public static PointValue Good(object? value, DateTimeOffset timestamp, object? originValue = null)
        => new(value, PointQuality.Good, timestamp, originValue: originValue);

    public static PointValue Uncertain(object? value, string reason, DateTimeOffset timestamp, object? originValue = null)
        => new(value, PointQuality.Uncertain, timestamp, reason, originValue);

    /// <summary>坏值：不带旧值。</summary>
    public static PointValue Bad(string reason, DateTimeOffset timestamp)
        => new(null, PointQuality.Bad, timestamp, reason);

    /// <summary>坏值：保留最后一次可信值。界面可置灰显示，但必须能看到质量标注。</summary>
    public static PointValue Bad(string reason, DateTimeOffset timestamp, object? lastValue)
        => new(lastValue, PointQuality.Bad, timestamp, reason);

    public static PointValue Offline(DateTimeOffset timestamp)
        => new(null, PointQuality.Offline, timestamp, "ss.quality.offline");

    /// <summary>按期望类型取值；类型不符或值为空时返回 false，不抛异常。</summary>
    public bool TryGetValue<T>(out T value)
    {
        if (Value is T typed)
        {
            value = typed;
            return true;
        }

        value = default!;
        return false;
    }

    public bool Equals(PointValue other)
        => Quality == other.Quality
           && Timestamp == other.Timestamp
           && string.Equals(Reason, other.Reason, StringComparison.Ordinal)
           && Equals(Value, other.Value)
           && Equals(OriginValue, other.OriginValue);

    public override bool Equals(object? obj) => obj is PointValue other && Equals(other);

    public override int GetHashCode()
        => unchecked((Value?.GetHashCode() ?? 0)
                     ^ ((int)Quality * 397)
                     ^ Timestamp.GetHashCode()
                     ^ (OriginValue?.GetHashCode() ?? 0));

    public override string ToString()
    {
        var text = Value switch
        {
            null => "null",
            double d => d.ToString("R", CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => Value.ToString(),
        };
        return IsGood ? text : $"{text} [{Quality}{(Reason is null ? string.Empty : ": " + Reason)}]";
    }
}
