using System;
using SuperSampler.Abstractions.Values;
using SuperSampler.Core.Runtime;
using Xunit;

namespace SuperSampler.UnitTests.Runtime;

/// <summary>实时值缓存语义测试。回归坑：#1 PointValue 是结构体，miss 必须返回 Bad 而非 default(Good)。</summary>
public class ValueCacheTests
{
    private static readonly DateTimeOffset T = new(2026, 9, 11, 10, 0, 0, TimeSpan.FromHours(8));

    [Fact]
    public void Miss_returns_bad_not_default_good()
    {
        var cache = new ValueCache();

        // default(PointValue) 的质量恰是 Good(0)，若返回 default 会伪装成可信数据
        var v = cache.Get("dev1/temp");
        Assert.False(v.IsGood);
        Assert.Equal(PointQuality.Bad, v.Quality);
        Assert.Equal("ss.reason.notInCache", v.Reason);
        Assert.Equal(DateTimeOffset.MinValue, v.Timestamp);
    }

    [Fact]
    public void Set_then_get_roundtrip()
    {
        var cache = new ValueCache();
        cache.Set("dev1/temp", PointValue.Good(21.5, T));

        var v = cache.Get("dev1/temp");
        Assert.True(v.IsGood);
        Assert.Equal(21.5, v.Value);
        Assert.Equal(T, v.Timestamp);
    }

    [Fact]
    public void Overwrite_replaces_previous()
    {
        var cache = new ValueCache();
        cache.Set("dev1/temp", PointValue.Good(20.0, T));
        cache.Set("dev1/temp", PointValue.Good(21.0, T.AddSeconds(1)));

        var v = cache.Get("dev1/temp");
        Assert.Equal(21.0, v.Value);
        Assert.Equal(T.AddSeconds(1), v.Timestamp);
    }

    [Fact]
    public void Device_point_overload_uses_same_key_space()
    {
        var cache = new ValueCache();
        cache.Set("dev1/temp", PointValue.Good(5.0, T));

        // 双参与单参 key 空间一致
        Assert.True(cache.Get("dev1", "temp").IsGood);
    }
}