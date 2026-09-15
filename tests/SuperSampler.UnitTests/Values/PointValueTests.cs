using System;
using SuperSampler.Abstractions.Values;
using Xunit;

namespace SuperSampler.UnitTests.Values;

/// <summary>值模型语义测试：质量、工厂方法、类型安全取值、相等性、调试输出。</summary>
public class PointValueTests
{
    private static readonly DateTimeOffset T = new(2026, 9, 11, 10, 0, 0, TimeSpan.FromHours(8));

    [Fact]
    public void Good_carries_value_quality_timestamp()
    {
        var v = PointValue.Good(12.3, T);

        Assert.True(v.IsGood);
        Assert.Equal(PointQuality.Good, v.Quality);
        Assert.Equal(T, v.Timestamp);
        Assert.Null(v.Reason);
    }

    [Fact]
    public void TryGetValue_extracts_matching_type_only()
    {
        Assert.True(PointValue.Good(12.3, T).TryGetValue(out double d));
        Assert.Equal(12.3, d);

        // 类型不符 → false，不抛异常
        Assert.False(PointValue.Good(12.3, T).TryGetValue(out string? _));

        // 坏值没有工程值 → false
        Assert.False(PointValue.Bad("x", T).TryGetValue(out double _));
    }

    [Fact]
    public void Bad_keeps_reason_and_optional_last_value()
    {
        var v = PointValue.Bad("ss.reason.timeout", T, 20.0);

        Assert.Equal(PointQuality.Bad, v.Quality);
        Assert.Equal("ss.reason.timeout", v.Reason);
        Assert.Equal(20.0, v.Value);   // 保留最后一次可信值，但质量必须一起看
        Assert.False(v.IsGood);
    }

    [Fact]
    public void Equality_compares_all_fields()
    {
        Assert.Equal(PointValue.Good(1, T), PointValue.Good(1, T));
        Assert.NotEqual(PointValue.Good(1, T), PointValue.Good(2, T));
        Assert.NotEqual(PointValue.Good(1, T), PointValue.Bad("x", T));
        Assert.NotEqual(PointValue.Good(1, T), PointValue.Good(1, T.AddSeconds(1)));
    }

    [Fact]
    public void ToString_marks_bad_quality_with_reason()
    {
        var s = PointValue.Bad("ss.reason.timeout", T).ToString();

        Assert.Contains("Bad", s);
        Assert.Contains("ss.reason.timeout", s);
    }

    [Fact]
    public void Offline_carries_reason_key()
    {
        var v = PointValue.Offline(T);

        Assert.Equal(PointQuality.Offline, v.Quality);
        Assert.Equal("ss.quality.offline", v.Reason);
        Assert.Null(v.Value);
    }
}
