using System;
using SuperSampler.Abstractions.Values;
using SuperSampler.Core.Config;
using SuperSampler.Core.Runtime;
using SuperSampler.UnitTests.Support;
using Xunit;

namespace SuperSampler.UnitTests.Values;

/// <summary>点位解码测试：寄存器 → 工程值，覆盖全部类型与字节序。</summary>
public class PointCodecDecodeTests
{
    private static readonly DateTimeOffset T = new(2026, 9, 11, 10, 0, 0, TimeSpan.FromHours(8));

    private static PointValue Dec(RuntimePoint p, params ushort[] words)
        => PointCodec.Decode(p, words, T);

    // ───────────── 16 位整型 ─────────────

    [Fact]
    public void UInt16_default_swap_decodes()
    {
        var p = Support.RuntimePointFactory.Point(p => p.DataType = RuntimeDataType.UInt16);
        var v = Dec(p, 0x1234);
        Assert.True(v.IsGood);
        Assert.Equal((ushort)0x1234, v.Value);
    }

    [Theory]
    [InlineData(new ushort[] { 0x1234 }, (short)0x1234)]
    [InlineData(new ushort[] { 0xFFFF }, (short)-1)]
    public void Int16_decodes_signed(ushort[] regs, short expected)
    {
        var p = Support.RuntimePointFactory.Point(p => p.DataType = RuntimeDataType.Int16);
        Assert.Equal(expected, Dec(p, regs).Value);
    }

    // ───────────── 32 位整型与浮点 ─────────────

    [Theory]
    [InlineData(SwapMode.None, "1234,5678")]
    [InlineData(SwapMode.Word, "5678,1234")]   // CDAB：相邻字成对交换
    [InlineData(SwapMode.Byte, "3412,7856")]   // BADC：逐字倒字节
    [InlineData(SwapMode.WordByte, "7856,3412")] // DCBA
    public void Int32_decodes_across_all_swaps(SwapMode swap, string hex)
    {
        var p = Support.RuntimePointFactory.Point(p => { p.DataType = RuntimeDataType.Int32; p.Swap = swap; });
        var words = ParseHex(hex);
        Assert.Equal(0x12345678, (int)Dec(p, words).Value!);
    }

    [Fact]
    public void Int32_roundtrips_all_swaps_including_wordbyte()
    {
        // 自反变换：编→解用同源 swap 不破坏数值（也覆盖实现有缺陷的 swap）
        foreach (SwapMode swap in Enum.GetValues(typeof(SwapMode)))
        {
            var p = Support.RuntimePointFactory.Point(p => { p.DataType = RuntimeDataType.Int32; p.Swap = swap; });
            var value = 0x1357BDF1;
            var back = PointCodec.Decode(p, PointCodec.Encode(p, value), T);
            Assert.True(back.IsGood);
            Assert.Equal(value, (int)back.Value!);
        }
    }

    [Theory]
    [InlineData(SwapMode.None, "3FC0,0000")]
    [InlineData(SwapMode.Word, "0000,3FC0")]
    public void Float32_decodes_across_swaps(SwapMode swap, string hex)
    {
        var p = Support.RuntimePointFactory.Point(p => { p.DataType = RuntimeDataType.Float32; p.Swap = swap; });
        var words = ParseHex(hex);
        Assert.Equal(1.5f, Dec(p, words).Value);
    }

    // ───────────── 64 位 ─────────────

    [Theory]
    [InlineData(SwapMode.None, 0x0123456789ABCDEFUL, "0123,4567,89AB,CDEF")]
    [InlineData(SwapMode.Byte, 0x0123456789ABCDEFUL, "2301,6745,AB89,EFCD")]
    [InlineData(SwapMode.Word, 0x0123456789ABCDEFUL, "4567,0123,CDEF,89AB")]  // CDAB 相邻字成对交换
    [InlineData(SwapMode.WordByte, 0x0123456789ABCDEFUL, "EFCD,AB89,6745,2301")] // DCBA 全字节反转
    public void UInt64_decodes_valid_swaps(SwapMode swap, ulong expected, string hex)
    {
        var p = Support.RuntimePointFactory.Point(p => { p.DataType = RuntimeDataType.UInt64; p.Swap = swap; });
        Assert.Equal(expected, (ulong)Dec(p, ParseHex(hex)).Value!);
    }

    // ───────────── 位与位域 ─────────────

    [Fact]
    public void Single_bit_decodes_from_register_zero()
    {
        var p = Support.RuntimePointFactory.Point(p => { p.Bit = 3; p.Length = 1; });
        Assert.Equal(true, Dec(p, 0x0008).Value);
        Assert.Equal(false, Dec(p, 0x0000).Value);
        Assert.Equal(false, Dec(p, 0x0001).Value);
    }

    [Fact]
    public void Bit_range_extracts_field()
    {
        var p = Support.RuntimePointFactory.Point(p => { p.BitRange = "2-4"; p.Length = 1; });
        // 0x001C = 11100_0，取 bit2..4 = 111b = 7
        Assert.Equal((ushort)7, Dec(p, 0x001C).Value);
    }

    // ───────────── 字符串 / BCD / 时间 ─────────────

    [Fact]
    public void String_ascii_trims_nulls()
    {
        var p = Support.RuntimePointFactory.Point(p => { p.DataType = RuntimeDataType.String; p.Length = 2; });
        // 0x4142 0x0000 → "AB\0\0" 截断 → "AB"
        Assert.Equal("AB", Dec(p, 0x4142, 0x0000).Value);
    }

    [Fact]
    public void Bcd_decodes_and_truncates_to_digits()
    {
        var full = Support.RuntimePointFactory.Point(p => { p.DataType = RuntimeDataType.Bcd; p.BcdDigits = 8; p.Length = 1; });
        Assert.Equal(1234L, Dec(full, 0x1234).Value);

        var twoDigit = Support.RuntimePointFactory.Point(p => { p.DataType = RuntimeDataType.Bcd; p.BcdDigits = 2; p.Length = 1; });
        Assert.Equal(34L, Dec(twoDigit, 0x1234).Value);
    }

    [Fact]
    public void DateTime_plc6_decodes()
    {
        var p = Support.RuntimePointFactory.Point(p => { p.DataType = RuntimeDataType.DateTime; p.DateTimeFormat = "plc6"; p.Length = 6; });
        var v = Dec(p, 2026, 9, 11, 10, 30, 0);
        Assert.Equal(new DateTime(2026, 9, 11, 10, 30, 0), (DateTime)v.Value!);
    }

    // ───────────── 缩放 ─────────────

    [Fact]
    public void Scale_factor_applies_to_numeric()
    {
        var p = Support.RuntimePointFactory.Point(p =>
        {
            p.DataType = RuntimeDataType.Float32;
            p.Swap = SwapMode.None;
            p.Scale = new ScaleConfig { Factor = 2.0 };
        });
        // 原始 1.5 → 工程 3.0
        Assert.Equal(3.0, Dec(p, 0x3FC0, 0x0000).Value);
    }

    // ───────────── 坏帧 ─────────────

    [Fact]
    public void Short_frame_returns_bad()
    {
        var p = Support.RuntimePointFactory.Point(p => { p.DataType = RuntimeDataType.Int32; p.Length = 0; });
        // Length 由 RuntimePoint 推导（Int32 → 2），只给 1 字
        var v = Dec(p, 0x1234);
        Assert.False(v.IsGood);
        Assert.Equal("ss.reason.shortFrame", v.Reason);
    }

    private static ushort[] ParseHex(string hex)
    {
        var parts = hex.Split(',');
        var outArr = new ushort[parts.Length];
        for (var i = 0; i < parts.Length; i++) outArr[i] = Convert.ToUInt16(parts[i].Trim(), 16);
        return outArr;
    }
}