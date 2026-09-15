using System;
using SuperSampler.Abstractions.Values;
using SuperSampler.Core.Config;
using SuperSampler.Core.Runtime;
using Xunit;

namespace SuperSampler.UnitTests.Values;

/// <summary>
/// G-C 族 Codec 补测（docs/07 测试计划 §3.2）：64 位全字序编解码往返、GBK、BCD 边界、
/// DateTime 钳制、Encode 越界回绕、字符串截断、短帧全类型。
/// </summary>
public class PointCodecGapTests
{
    private static readonly DateTimeOffset T = new(2026, 9, 11, 10, 0, 0, TimeSpan.FromHours(8));

    private static RuntimePoint Pt(Action<PointConfig>? configure = null)
    {
        var point = new PointConfig
        {
            Id = "p",
            Area = RuntimeArea.HoldingRegister,
            DataType = RuntimeDataType.UInt16,
            Swap = SwapMode.None,
            Address = 0,
        };
        configure?.Invoke(point);
        var device = new DeviceConfig { Id = "dev1", UnitId = 1, Transport = "tcp1", PointSetId = "ps1" };
        return new RuntimePoint(point, device);
    }

    // ─────────────── G-C-1：64 位全字序走完整 Decode/Encode 路径 ───────────────

    [Theory]
    [InlineData(SwapMode.None)]
    [InlineData(SwapMode.Byte)]
    [InlineData(SwapMode.Word)]
    [InlineData(SwapMode.WordByte)]
    public void Gc1_uint64_roundtrip_all_swaps_full_codec_path(SwapMode swap)
    {
        // 2^53 以内可被 double 精确表示的值（见 Gc1_uint64_precision_beyond_2pow53 的缺陷说明）
        const ulong value = 0x0011223344556677;
        var point = Pt(p =>
        {
            p.DataType = RuntimeDataType.UInt64;
            p.Length = 4;
            p.Swap = swap;
        });

        var wire = PointCodec.Encode(point, value);
        var decoded = PointCodec.Decode(point, wire, T);

        Assert.True(decoded.IsGood);
        Assert.Equal(value, Assert.IsType<ulong>(decoded.Value));

        // 编码是解码的逆：再编回去应还原 wire
        Assert.Equal(wire, PointCodec.Encode(point, decoded.Value));
    }

    [Theory]
    [InlineData(SwapMode.None)]
    [InlineData(SwapMode.Byte)]
    [InlineData(SwapMode.Word)]
    [InlineData(SwapMode.WordByte)]
    public void Gc1_int64_roundtrip_all_swaps_full_codec_path(SwapMode swap)
    {
        // 2^53 以内可被 double 精确表示的负值（见 Gc1_int64_precision_beyond_2pow53 的缺陷说明）
        const long value = unchecked((long)0xFFEEDDCCBAA99889); // = -(0x0011223344556677)
        var point = Pt(p =>
        {
            p.DataType = RuntimeDataType.Int64;
            p.Length = 4;
            p.Swap = swap;
        });

        var decoded = PointCodec.Decode(point, PointCodec.Encode(point, value), T);

        Assert.True(decoded.IsGood);
        Assert.Equal(value, Assert.IsType<long>(decoded.Value));
    }

    // ─────────────── D8（已修复）：64 位整数不经 double 中转，精度保留 ───────────────
    // 修复：整型输入且无缩放时按目标类型直通编码（D8）。断言 |value| ≥ 2^53 时往返不丢低位。

    [Fact]
    public void Gc1_uint64_precision_beyond_2pow53_is_preserved()
    {
        var point = Pt(p => { p.DataType = RuntimeDataType.UInt64; p.Length = 4; p.Swap = SwapMode.None; });
        const ulong value = 0x0123456789ABCDEF;

        var decoded = PointCodec.Decode(point, PointCodec.Encode(point, value), T);

        Assert.True(decoded.IsGood);
        Assert.Equal(value, Assert.IsType<ulong>(decoded.Value));
    }

    [Fact]
    public void Gc1_int64_precision_beyond_2pow53_is_preserved()
    {
        var point = Pt(p => { p.DataType = RuntimeDataType.Int64; p.Length = 4; p.Swap = SwapMode.None; });
        const long value = unchecked((long)0xFEDCBA9876543210);

        var decoded = PointCodec.Decode(point, PointCodec.Encode(point, value), T);

        Assert.True(decoded.IsGood);
        Assert.Equal(value, Assert.IsType<long>(decoded.Value));
    }

    // ─────────────── G-C-2：GBK 字符串往返 ───────────────

    [Fact]
    public void Gc2_gbk_chinese_string_roundtrip()
    {
        var point = Pt(p =>
        {
            p.DataType = RuntimeDataType.String;
            p.Length = 2;
            p.StringEncoding = "gbk";
        });

        var wire = PointCodec.Encode(point, "主温");
        Assert.Equal(new ushort[] { 0xD6F7, 0xCEC2 }, wire); // 主=D6F7 温=CEC2（CP936）

        var decoded = PointCodec.Decode(point, wire, T);
        Assert.True(decoded.IsGood);
        Assert.Equal("主温", decoded.Value);
    }

    // ─────────────── G-C-3：BCD 边界 ───────────────

    [Fact]
    public void Gc3_bcd_decode_and_digits_truncation()
    {
        var full = Pt(p => { p.DataType = RuntimeDataType.Bcd; p.Length = 1; p.BcdDigits = 4; });
        Assert.Equal(1234L, PointCodec.Decode(full, new ushort[] { 0x1234 }, T).Value);
        Assert.Equal(99L, PointCodec.Decode(full, new ushort[] { 0x0099 }, T).Value);

        // digits=2：有效位数 2，高位截去只留低两位
        var truncated = Pt(p => { p.DataType = RuntimeDataType.Bcd; p.Length = 1; p.BcdDigits = 2; });
        Assert.Equal(34L, PointCodec.Decode(truncated, new ushort[] { 0x1234 }, T).Value);
    }

    // ─────────────── G-C-4：DateTime plc6 合法值与越界钳制 ───────────────

    [Fact]
    public void Gc4_datetime_plc6_valid_fields_parse()
    {
        var point = Pt(p => { p.DataType = RuntimeDataType.DateTime; p.Length = 6; p.DateTimeFormat = "plc6"; });
        var decoded = PointCodec.Decode(point, new ushort[] { 2026, 9, 12, 10, 30, 0 }, T);
        Assert.True(decoded.IsGood);
        Assert.Equal(new DateTime(2026, 9, 12, 10, 30, 0), decoded.Value);
    }

    [Fact]
    public void Gc4_datetime_garbage_registers_yield_bad_never_throw()
    {
        // 秒寄存器 200 被 ClampByte 到 99，但 DateTime second 合法域是 0..59 → 解码抛出
        // → 被 Decode 顶层 catch 吞掉转 Bad。锁定：垃圾值绝不崩进程。
        var point = Pt(p => { p.DataType = RuntimeDataType.DateTime; p.Length = 6; p.DateTimeFormat = "plc6"; });
        var decoded = PointCodec.Decode(point, new ushort[] { 2026, 9, 12, 10, 30, 200 }, T);
        Assert.False(decoded.IsGood);
        Assert.Equal(PointQuality.Bad, decoded.Quality);
        Assert.Equal("ss.reason.decode", decoded.Reason);
    }

    // ─────────────── G-C-5：Encode 越界回绕（锁定原始编解码层行为） ───────────────

    [Fact]
    public void Gc5_encode_uint16_overflow_wraps_low_16_bits()
    {
        // 编解码层不做范围校验（70000 & 0xFFFF = 0x1170 = 4464）；
        // 范围保护属于写管道 Min/Max（SEC-4），此处锁定底层口径。
        var point = Pt(p => { p.DataType = RuntimeDataType.UInt16; p.Length = 1; });
        Assert.Equal(new ushort[] { 0x1170 }, PointCodec.Encode(point, 70000d));
    }

    [Fact]
    public void Gc5_encode_int16_negative_maps_to_two_complement()
    {
        var point = Pt(p => { p.DataType = RuntimeDataType.Int16; p.Length = 1; });
        Assert.Equal(new ushort[] { 0xFFFF }, PointCodec.Encode(point, -1d));
    }

    // ─────────────── G-C-6：字符串编码超长截断 ───────────────

    [Fact]
    public void Gc6_string_encode_truncates_to_declared_length()
    {
        var point = Pt(p => { p.DataType = RuntimeDataType.String; p.Length = 1; }); // 2 字节
        var wire = PointCodec.Encode(point, "ABCD");
        Assert.Equal(new ushort[] { 0x4142 }, wire); // 只装得下前 2 字节

        var decoded = PointCodec.Decode(point, wire, T);
        Assert.Equal("AB", decoded.Value);
    }

    // ─────────────── G-C-7：短帧全类型一律 Bad ───────────────

    [Theory]
    [InlineData(RuntimeDataType.Int32)]
    [InlineData(RuntimeDataType.UInt32)]
    [InlineData(RuntimeDataType.Float32)]
    [InlineData(RuntimeDataType.Int64)]
    [InlineData(RuntimeDataType.UInt64)]
    [InlineData(RuntimeDataType.Float64)]
    [InlineData(RuntimeDataType.String)]
    [InlineData(RuntimeDataType.Bcd)]
    [InlineData(RuntimeDataType.DateTime)]
    [InlineData(RuntimeDataType.Bool)]
    [InlineData(RuntimeDataType.UInt16)]
    public void Gc7_short_frame_yields_bad_for_every_data_type(RuntimeDataType dataType)
    {
        var point = Pt(p => { p.DataType = dataType; p.Length = 2; });
        var decoded = PointCodec.Decode(point, new ushort[] { 0x1234 }, T); // 要 2 个字只给 1 个

        Assert.False(decoded.IsGood);
        Assert.Equal(PointQuality.Bad, decoded.Quality);
        Assert.Equal("ss.reason.shortFrame", decoded.Reason);
    }
}
