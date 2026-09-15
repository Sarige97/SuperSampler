using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SuperSampler.Abstractions.Values;
using SuperSampler.Core.Runtime;
using Xunit;

namespace SuperSampler.UnitTests.Runtime;

/// <summary>
/// PointRegistry / ValueCache 补测（docs/07 测试计划 §3.2 G-R 族）：
/// PointKey 口径、ValueCache 并发无撕裂、default(Good) 防御。
/// </summary>
public class RegistryCacheGapTests
{
    // ─────────────── G-R-1：PointKey 口径 ───────────────

    [Fact]
    public void Gr1_point_key_is_ordinal_device_slash_point()
    {
        // PointKey.Of 产出 "deviceId/pointId" 字符串，缓存用 Ordinal 比较
        Assert.Equal("dev1/p1", PointKey.Of("dev1", "p1"));
        Assert.Equal(PointKey.Of("dev1", "p1"), PointKey.Of("dev1", "p1"));
        Assert.NotEqual(PointKey.Of("dev1", "p1"), PointKey.Of("dev1", "p2"));
        Assert.NotEqual(PointKey.Of("dev1", "p1"), PointKey.Of("dev2", "p1"));

        // Ordinal 口径：大小写不同即不同槽位（不折叠）
        var cache = new ValueCache();
        cache.Set(PointKey.Of("Dev1", "x"), PointValue.Good(1, DateTimeOffset.UtcNow));
        var missing = cache.Get("dev1", "x");
        Assert.False(missing.IsGood);
        Assert.Equal(PointQuality.Bad, missing.Quality);
    }

    // ─────────────── G-R-2：ValueCache 并发无撕裂、无 default(Good) 泄漏 ───────────────

    [Fact]
    public async Task Gr2_value_cache_concurrent_access_never_yields_torn_or_fake_good()
    {
        var cache = new ValueCache();
        cache.Set(PointKey.Of("dev1", "p"), PointValue.Good(1, DateTimeOffset.UtcNow)); // 预置种子，读循环从第一拍就只见完整值
        using var cts = new CancellationTokenSource(2_000);

        // 写线程：反复把同一点位的值在 1/2 之间切换
        var writers = Enumerable.Range(0, 2).Select(id => Task.Run(() =>
        {
            while (!cts.IsCancellationRequested)
            {
                cache.Set(PointKey.Of("dev1", "p"), PointValue.Good(id + 1, DateTimeOffset.UtcNow));
            }
        })).ToArray();

        try
        {
            // 读线程：任何一次 Get 都必须是完整的 Good(1) 或 Good(2)，绝不出现假 Good/撕裂值
            for (var i = 0; i < 20_000; i++)
            {
                var value = cache.Get("dev1", "p");
                Assert.True(value.IsGood, $"iteration {i}: {value}");
                var number = Assert.IsType<int>(value.Value);
                Assert.True(number == 1 || number == 2, $"iteration {i}: torn value {number}");
            }
        }
        finally
        {
            cts.Cancel();
            await Task.WhenAll(writers);
        }

        // 并发写入 distinct key 后全部可读
        Parallel.For(0, 100, i => cache.Set(PointKey.Of("d" + i, "p"), PointValue.Good(i, DateTimeOffset.UtcNow)));
        for (var i = 0; i < 100; i++)
        {
            Assert.Equal(i, Assert.IsType<int>(cache.Get("d" + i, "p").Value));
        }
    }
}
