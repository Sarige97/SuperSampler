using System;
using SuperSampler.Abstractions.Values;
using SuperSampler.Core.Config;
using SuperSampler.Core.Runtime;
using SuperSampler.UnitTests.Support;
using Xunit;

namespace SuperSampler.UnitTests.Values;

/// <summary>格式化测试：无格式、工程映射、原始映射、缩放后工程值、坏值占位。</summary>
public class PointCodecFormatTests
{
    private static readonly DateTimeOffset T = new(2026, 9, 11, 10, 0, 0, TimeSpan.FromHours(8));

    private static string Fmt(RuntimePoint p, object? value, PointQuality q = PointQuality.Good, object? raw = null, string nullText = "--")
        => PointCodec.Format(p, new PointValue(value, q, T), nullText, raw);

    [Fact]
    public void No_format_renders_invariant()
    {
        var p = Support.RuntimePointFactory.Point(p => { p.DataType = RuntimeDataType.Float32; });
        Assert.Equal("1.5", Fmt(p, 1.5f));
    }

    [Fact]
    public void Engineering_map_applies_to_engineering_value()
    {
        var p = Support.RuntimePointFactory.Point(p =>
        {
            p.DataType = RuntimeDataType.UInt16;
            p.Format = new FormatConfig { MapOn = "engineering", Map = { ["1"] = "RUN", ["0"] = "STOP" } };
        });

        Assert.Equal("RUN", Fmt(p, (ushort)1));
        Assert.Equal("STOP", Fmt(p, (ushort)0));
    }

    [Fact]
    public void Raw_map_uses_raw_when_passed()
    {
        var p = Support.RuntimePointFactory.Point(p =>
        {
            p.DataType = RuntimeDataType.UInt16;
            p.Format = new FormatConfig { MapOn = "raw", Map = { ["10"] = "ALARM" } };
        });

        // 工程值 5，但原始值 10 → 应映射 ALARM
        Assert.Equal("ALARM", Fmt(p, (ushort)5, raw: (ushort)10));
    }

    [Fact]
    public void Bad_value_returns_null_text()
    {
        var p = Support.RuntimePointFactory.Point(p => { p.DataType = RuntimeDataType.UInt16; });
        Assert.Equal("--", Fmt(p, null, PointQuality.Bad));
    }

    [Fact]
    public void Scaled_engineering_value_format_uses_decimals()
    {
        var p = Support.RuntimePointFactory.Point(p =>
        {
            p.DataType = RuntimeDataType.Float32;
            p.Scale = new ScaleConfig { Factor = 2.0 };
            p.Format = new FormatConfig { Decimals = 2 };
        });
        // 工程值 3.0 → "3.00"（Format 传入的是已缩放工程值）
        var v = PointCodec.Decode(p, new ushort[] { 0x3FC0, 0x0000 }, T);
        Assert.Equal("3.00", PointCodec.Format(p, v, "--"));
    }
}