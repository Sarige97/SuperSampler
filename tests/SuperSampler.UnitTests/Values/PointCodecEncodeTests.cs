using System.Linq;
using SuperSampler.Abstractions.Values;
using SuperSampler.Core.Config;
using SuperSampler.Core.Runtime;
using SuperSampler.UnitTests.Support;
using Xunit;

namespace SuperSampler.UnitTests.Values;

/// <summary>编码测试：工程值 → 寄存器；并验证「编码→解码」往返为恒等（字节序安全）。</summary>
public class PointCodecEncodeTests
{
    private static readonly System.DateTimeOffset T = new(2026, 9, 11, 10, 0, 0, System.TimeSpan.FromHours(8));

    [Fact]
    public void UInt16_encodes_default_swap()
    {
        var p = Support.RuntimePointFactory.Point(p => { p.DataType = RuntimeDataType.UInt16; });
        var regs = PointCodec.Encode(p, (ushort)0x1234);
        Assert.Equal(new ushort[] { 0x1234 }, regs);
    }

    [Theory]
    [InlineData(SwapMode.None)]
    [InlineData(SwapMode.Word)]
    [InlineData(SwapMode.Byte)]
    [InlineData(SwapMode.WordByte)]
    public void Int32_encode_decode_roundtrip(SwapMode swap)
    {
        var p = Support.RuntimePointFactory.Point(p => { p.DataType = RuntimeDataType.Int32; p.Swap = swap; });
        var regs = PointCodec.Encode(p, 0x12345678);
        var v = PointCodec.Decode(p, regs, T);
        Assert.True(v.IsGood);
        Assert.Equal(0x12345678, (int)v.Value!);
    }

    [Theory]
    [InlineData(SwapMode.None)]
    [InlineData(SwapMode.Byte)]
    [InlineData(SwapMode.Word)]
    [InlineData(SwapMode.WordByte)]
    public void Float64_encode_decode_roundtrip_preserves_fraction(SwapMode swap)
    {
        // 浮点编码不得取整：3.14159 写出去必须还是原值（findings #3 已修）
        var p = Support.RuntimePointFactory.Point(p => { p.DataType = RuntimeDataType.Float64; p.Swap = swap; });
        var regs = PointCodec.Encode(p, 3.14159265358979);
        var v = PointCodec.Decode(p, regs, T);
        Assert.True(v.IsGood);
        Assert.Equal(3.14159265358979, (double)v.Value!);
    }

    [Fact]
    public void Float32_encode_preserves_fraction()
    {
        var p = Support.RuntimePointFactory.Point(p => { p.DataType = RuntimeDataType.Float32; p.Swap = SwapMode.None; });
        var regs = PointCodec.Encode(p, 1.5f);
        var v = PointCodec.Decode(p, regs, T);
        Assert.True(v.IsGood);
        Assert.Equal(1.5f, (float)v.Value!);
    }

    [Fact]
    public void Encode_bool_coil_produces_one_zero_register()
    {
        var p = Support.RuntimePointFactory.Point(p => { p.DataType = RuntimeDataType.Bool; });
        Assert.Equal(new ushort[] { 1 }, PointCodec.Encode(p, true));
        Assert.Equal(new ushort[] { 0 }, PointCodec.Encode(p, false));
    }

    [Fact]
    public void Encode_bit_point_places_value_at_configured_bit()
    {
        // 位点编码：值写到点配置的位上（findings #4 已修）
        var p = Support.RuntimePointFactory.Point(p => { p.DataType = RuntimeDataType.Bool; p.Bit = 3; p.Length = 1; });
        Assert.Equal(new ushort[] { 0x0008 }, PointCodec.Encode(p, true));
        Assert.Equal(new ushort[] { 0x0000 }, PointCodec.Encode(p, false));
    }
}