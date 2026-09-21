using System;
using System.Collections.Generic;
using System.Globalization;
using SuperSampler.Abstractions.Values;
using SuperSampler.Core.Config;
using SuperSampler.Core.Runtime;
using Xunit;

namespace SuperSampler.UnitTests.Values;

/// <summary>
/// 编解码全矩阵（模块重测「编解码」覆盖 1–8）：13 种 <c>dataType</c> 的「解码值正确 + 编码回原样」双向断言、
/// 4 种字序 × 1/2/4 字宽（含 0/最大/负数/非规格化/±0）、位与位域、Scale（linear/双点/钳制/Bcd 组合）、
/// BCD 对称、datetime 四格式与 Format 组合、string 的 padding/left 字节级语义、Format 各配置项与降级口径。
/// 纯单测（不依赖端口、不起线程）。既有覆盖见 <c>PointCodecDecodeTests</c>/<c>EncodeTests</c>/<c>ReorderTests</c>/
/// <c>FormatTests</c>/<c>GapTests</c>，本文件只补矩阵缺口。
/// </summary>
public class CodecMatrixTests
{
    private static readonly DateTimeOffset T = new(2026, 9, 16, 10, 0, 0, TimeSpan.FromHours(8));

    private static RuntimePoint Pt(Action<PointConfig>? configure = null)
    {
        var point = new PointConfig
        {
            Id = "p",
            Area = RuntimeArea.HoldingRegister,
            DataType = RuntimeDataType.UInt16,
            Swap = SwapMode.None,
            HasSwapDeclared = true,   // 测试用具在点位级显式给 swap，不走设备级兜底（ADR D38）
            Address = 0,
        };
        configure?.Invoke(point);
        var device = new DeviceConfig { Id = "dev1", UnitId = 1, Transport = "tcp1", PointSetId = "ps1" };
        return new RuntimePoint(point, device);
    }

    // ═══════════════ 1. 13 种 dataType 双向矩阵 ═══════════════

    public static IEnumerable<object[]> TypeCases()
    {
        // dataType, length, 线上寄存器（大端/默认字序）, 期望工程值
        yield return new object[] { RuntimeDataType.UInt16, 1, new ushort[] { 0 }, (ushort)0 };
        yield return new object[] { RuntimeDataType.UInt16, 1, new ushort[] { 0xFFFF }, (ushort)65535 };
        yield return new object[] { RuntimeDataType.Int16, 1, new ushort[] { 0x8000 }, (short)short.MinValue };
        yield return new object[] { RuntimeDataType.Int16, 1, new ushort[] { 0xFFFF }, (short)-1 };
        yield return new object[] { RuntimeDataType.UInt32, 2, new ushort[] { 0x0000, 0x0000 }, (uint)0 };
        yield return new object[] { RuntimeDataType.UInt32, 2, new ushort[] { 0xFFFF, 0xFFFF }, uint.MaxValue };
        yield return new object[] { RuntimeDataType.Int32, 2, new ushort[] { 0x8000, 0x0000 }, int.MinValue };
        yield return new object[] { RuntimeDataType.Int32, 2, new ushort[] { 0x7FFF, 0xFFFF }, int.MaxValue };
        yield return new object[] { RuntimeDataType.UInt64, 4, new ushort[] { 0xFFFF, 0xFFFF, 0xFFFF, 0xFFFF }, ulong.MaxValue };
        yield return new object[] { RuntimeDataType.Int64, 4, new ushort[] { 0x8000, 0, 0, 0 }, long.MinValue };
        yield return new object[] { RuntimeDataType.Int64, 4, new ushort[] { 0x7FFF, 0xFFFF, 0xFFFF, 0xFFFF }, long.MaxValue };
        yield return new object[] { RuntimeDataType.Float32, 2, new ushort[] { 0x3FC0, 0x0000 }, 1.5f };
        yield return new object[] { RuntimeDataType.Float64, 4, new ushort[] { 0x4009, 0x21FB, 0x5444, 0x2D18 }, 3.141592653589793 };
        yield return new object[] { RuntimeDataType.String, 2, new ushort[] { 0x4142, 0x0000 }, "AB" };
        yield return new object[] { RuntimeDataType.Bcd, 1, new ushort[] { 0x1234 }, 1234L };
        yield return new object[] { RuntimeDataType.Bool, 1, new ushort[] { 0x0000 }, false };
        yield return new object[] { RuntimeDataType.Bool, 1, new ushort[] { 0x0001 }, true };
    }

    [Theory]
    [MemberData(nameof(TypeCases))]
    public void Each_data_type_decodes_expected_value_and_encodes_back_to_the_same_wire(
        RuntimeDataType dataType, int length, ushort[] wire, object expected)
    {
        var point = Pt(p => { p.DataType = dataType; p.Length = length; });

        var decoded = PointCodec.Decode(point, wire, T);
        Assert.True(decoded.IsGood, $"{dataType} 解码应为 Good，实际 {decoded}");
        Assert.Equal(expected, decoded.Value);

        // 编码回原样：解出的工程值必须编回同一串线上寄存器
        Assert.Equal(wire, PointCodec.Encode(point, decoded.Value));
    }

    [Fact]
    public void Float32_negative_zero_and_denormal_round_trip_bit_exactly()
    {
        var point = Pt(p => { p.DataType = RuntimeDataType.Float32; p.Length = 2; });

        foreach (var bits in new[] { 0x80000000u, 0x00000001u, 0x7F7FFFFFu, 0x00800000u })
        {
            var wire = new ushort[] { (ushort)(bits >> 16), (ushort)(bits & 0xFFFF) };
            var decoded = PointCodec.Decode(point, wire, T);

            Assert.True(decoded.IsGood);
            var value = Assert.IsType<float>(decoded.Value);
            Assert.Equal(bits, BitConverter.ToUInt32(BitConverter.GetBytes(value), 0));   // 位级一致（含 -0 符号位、非规格化）
            Assert.Equal(wire, PointCodec.Encode(point, value));
        }
    }

    [Fact]
    public void Float64_negative_zero_and_denormal_round_trip_bit_exactly()
    {
        var point = Pt(p => { p.DataType = RuntimeDataType.Float64; p.Length = 4; });

        foreach (var bits in new[] { 0x8000000000000000UL, 0x0000000000000001UL, 0x7FEFFFFFFFFFFFFFUL })
        {
            var wire = new ushort[]
            {
                (ushort)(bits >> 48), (ushort)((bits >> 32) & 0xFFFF),
                (ushort)((bits >> 16) & 0xFFFF), (ushort)(bits & 0xFFFF),
            };
            var decoded = PointCodec.Decode(point, wire, T);

            Assert.True(decoded.IsGood);
            var value = Assert.IsType<double>(decoded.Value);
            Assert.Equal((long)bits, BitConverter.DoubleToInt64Bits(value));
            Assert.Equal(wire, PointCodec.Encode(point, value));
        }
    }

    [Fact]
    public void Int64_min_and_max_round_trip_without_double_rounding()
    {
        var point = Pt(p => { p.DataType = RuntimeDataType.Int64; p.Length = 4; });

        foreach (var value in new[] { long.MinValue, long.MaxValue, -1L, 0L })
        {
            var decoded = PointCodec.Decode(point, PointCodec.Encode(point, value), T);
            Assert.Equal(value, Assert.IsType<long>(decoded.Value));
        }

        var ulongPoint = Pt(p => { p.DataType = RuntimeDataType.UInt64; p.Length = 4; });
        foreach (var value in new[] { ulong.MaxValue, 1UL << 60, 0UL })
        {
            var decoded = PointCodec.Decode(ulongPoint, PointCodec.Encode(ulongPoint, value), T);
            Assert.Equal(value, Assert.IsType<ulong>(decoded.Value));
        }
    }

    // ═══════════════ 2. 字序 × 字宽（1/2/4 字）边界 ═══════════════

    [Theory]
    [InlineData(SwapMode.None, 0x11223344u, new ushort[] { 0x1122, 0x3344 })]
    [InlineData(SwapMode.Byte, 0x11223344u, new ushort[] { 0x2211, 0x4433 })]
    [InlineData(SwapMode.Word, 0x11223344u, new ushort[] { 0x3344, 0x1122 })]
    [InlineData(SwapMode.WordByte, 0x11223344u, new ushort[] { 0x4433, 0x2211 })]
    public void UInt32_encode_produces_the_expected_wire_for_every_swap(SwapMode swap, uint value, ushort[] wire)
    {
        var point = Pt(p => { p.DataType = RuntimeDataType.UInt32; p.Length = 2; p.Swap = swap; });

        Assert.Equal(wire, PointCodec.Encode(point, value));

        var decoded = PointCodec.Decode(point, wire, T);
        Assert.Equal(value, Assert.IsType<uint>(decoded.Value));
    }

    [Theory]
    [InlineData(SwapMode.None, new ushort[] { 0x1122, 0x3344, 0x5566, 0x7788 })]
    [InlineData(SwapMode.Byte, new ushort[] { 0x2211, 0x4433, 0x6655, 0x8877 })]
    [InlineData(SwapMode.Word, new ushort[] { 0x3344, 0x1122, 0x7788, 0x5566 })]
    [InlineData(SwapMode.WordByte, new ushort[] { 0x8877, 0x6655, 0x4433, 0x2211 })]
    public void UInt64_encode_follows_the_HA_word_pair_model_for_every_swap(SwapMode swap, ushort[] wire)
    {
        const ulong value = 0x1122334455667788UL;
        var point = Pt(p => { p.DataType = RuntimeDataType.UInt64; p.Length = 4; p.Swap = swap; });

        Assert.Equal(wire, PointCodec.Encode(point, value));
        Assert.Equal(value, Assert.IsType<ulong>(PointCodec.Decode(point, wire, T).Value));
    }

    [Theory]
    [InlineData(SwapMode.None, new ushort[] { 0x1122 })]
    [InlineData(SwapMode.Byte, new ushort[] { 0x2211 })]
    [InlineData(SwapMode.Word, new ushort[] { 0x1122 })]       // 单字没有字对可换
    [InlineData(SwapMode.WordByte, new ushort[] { 0x2211 })]   // DCBA 单字退化为字节交换
    public void Single_word_points_decode_identically_regardless_of_the_word_swap(SwapMode swap, ushort[] wire)
    {
        var point = Pt(p => { p.DataType = RuntimeDataType.UInt16; p.Length = 1; p.Swap = swap; });

        Assert.Equal((ushort)0x1122, Assert.IsType<ushort>(PointCodec.Decode(point, wire, T).Value));
        Assert.Equal(wire, PointCodec.Encode(point, (ushort)0x1122));
    }

    [Fact]
    public void Scale_applies_after_the_word_reorder()
    {
        // 管道顺序（docs/01 §4）：字序重排 → 类型解释 → Scale。CDAB 下 r0/r1 交换后再乘 gain。
        var point = Pt(p =>
        {
            p.DataType = RuntimeDataType.UInt32;
            p.Length = 2;
            p.Swap = SwapMode.Word;
            p.Scale = new ScaleConfig { Factor = 0.5, Offset = 1 };
        });

        // 线上 [0x000A, 0x0004] → CDAB 重排为 [0x0004, 0x000A] → 原始 0x0004000A = 262154 → 262154*0.5+1
        var decoded = PointCodec.Decode(point, new ushort[] { 0x000A, 0x0004 }, T);
        Assert.Equal((0x0004000AL * 0.5) + 1, Assert.IsType<double>(decoded.Value));

        // 编码是解码的逆：逆换算后按同一字序出线
        Assert.Equal(new ushort[] { 0x000A, 0x0004 }, PointCodec.Encode(point, decoded.Value));
    }

    // ═══════════════ 3. bit / bitRange ═══════════════

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(15)]
    public void Every_bit_position_reads_only_its_own_bit(int bit)
    {
        var point = Pt(p => { p.DataType = RuntimeDataType.Bool; p.Bit = bit; p.Length = 1; });

        Assert.True(Assert.IsType<bool>(PointCodec.Decode(point, new ushort[] { (ushort)(1 << bit) }, T).Value));
        Assert.False(Assert.IsType<bool>(PointCodec.Decode(point, new ushort[] { 0 }, T).Value));
        Assert.False(Assert.IsType<bool>(PointCodec.Decode(point, new ushort[] { (ushort)~(1 << bit) }, T).Value));

        // 编码把值放到该位（其余位为 0，完整读-改-写由引擎的写管道负责，A4）
        Assert.Equal(new ushort[] { (ushort)(1 << bit) }, PointCodec.Encode(point, true));
        Assert.Equal(new ushort[] { (ushort)0 }, PointCodec.Encode(point, false));
    }

    [Theory]
    [InlineData("0-0", 0x0001, 1)]
    [InlineData("4-7", 0x00F0, 15)]
    [InlineData("12-15", 0xF000, 15)]
    [InlineData("8-11", 0x0B00, 11)]
    public void Bit_range_extracts_the_field_and_rawValue_is_the_field_value(string range, int register, int expected)
    {
        var point = Pt(p => { p.DataType = RuntimeDataType.UInt16; p.BitRange = range; p.Length = 1; });

        var decoded = PointCodec.Decode(point, new ushort[] { (ushort)register }, T);
        Assert.True(decoded.IsGood);
        Assert.Equal((ushort)expected, Assert.IsType<ushort>(decoded.Value));
    }

    [Fact]
    public void Bit_and_whole_register_points_on_the_same_address_do_not_interfere()
    {
        // CGV-17 允许「整字点位 + 位点位」共存：整字点看全 16 位，位点只看自己那一位
        var whole = Pt(p => { p.DataType = RuntimeDataType.UInt16; p.Address = 10; p.Length = 1; });
        var bit = Pt(p => { p.DataType = RuntimeDataType.Bool; p.Address = 10; p.Bit = 3; p.Length = 1; });
        var range = Pt(p => { p.DataType = RuntimeDataType.UInt16; p.Address = 10; p.BitRange = "4-7"; p.Length = 1; });

        var wire = new ushort[] { 0x00A8 };   // bit3=1, bit5=1, 0xA8 = 1010 1000
        Assert.Equal((ushort)0x00A8, Assert.IsType<ushort>(PointCodec.Decode(whole, wire, T).Value));
        Assert.True(Assert.IsType<bool>(PointCodec.Decode(bit, wire, T).Value));
        Assert.Equal((ushort)0x0A, Assert.IsType<ushort>(PointCodec.Decode(range, wire, T).Value));   // bits 4-7 = 1010
    }

    // ═══════════════ 4. Scale：linear / 双点 / clamp / 非 linear / bcd 组合 ═══════════════

    [Fact]
    public void Linear_scale_with_offset_and_its_reverse_are_inverse()
    {
        var scale = new ScaleConfig { Factor = 0.1, Offset = -40 };
        Assert.Equal(-40 + 25 * 0.1, scale.Apply(25), 10);
        Assert.Equal(25, scale.Reverse(scale.Apply(25)), 10);
    }

    [Fact]
    public void Decode_exposes_both_engineering_value_and_pre_scale_origin_value()
    {
        // 用户场景：int16 + <Scale factor="0.1"/>，寄存器原始 2200
        var point = Pt(p =>
        {
            p.DataType = RuntimeDataType.Int16;
            p.Length = 1;
            p.Scale = new ScaleConfig { Factor = 0.1 };
        });

        var decoded = PointCodec.Decode(point, new ushort[] { 0x0898 }, T);   // 0x0898 = 2200

        Assert.True(decoded.IsGood, "应解码为 Good：" + decoded);
        Assert.Equal(220.0, Assert.IsType<double>(decoded.Value), 10);        // 工程值（已缩放）
        Assert.Equal((short)2200, Assert.IsType<short>(decoded.OriginValue)); // 协议侧缩放前原始值
    }

    [Fact]
    public void Dual_point_scale_maps_both_ends_and_reverse_is_inverse()
    {
        var scale = new ScaleConfig
        {
            RawLow = 4, RawHigh = 20, ScaledLow = 0, ScaledHigh = 100,
            Factor = 999, Offset = 999,   // 四个点齐备时优先于 factor/offset
        };

        Assert.Equal(0, scale.Apply(4), 10);
        Assert.Equal(100, scale.Apply(20), 10);
        Assert.Equal(50, scale.Apply(12), 10);
        Assert.Equal(12, scale.Reverse(50), 10);
    }

    [Fact]
    public void Dual_point_scale_with_equal_raw_ends_does_not_divide_by_zero()
    {
        // 两点 X 相等 = 原始量程为 0：不能抛，取 scaledLow（写入侧取 rawLow）
        var scale = new ScaleConfig { RawLow = 7, RawHigh = 7, ScaledLow = 3, ScaledHigh = 9 };
        Assert.Equal(3, scale.Apply(7), 10);
        Assert.Equal(7, scale.Reverse(5), 10);
    }

    [Fact]
    public void Dual_point_scale_with_equal_scaled_ends_reverses_to_raw_low()
    {
        var scale = new ScaleConfig { RawLow = 0, RawHigh = 100, ScaledLow = 5, ScaledHigh = 5 };
        Assert.Equal(5, scale.Apply(37), 10);
        Assert.Equal(0, scale.Reverse(5), 10);
    }

    [Theory]
    [InlineData(ClampMode.Low, 10, 0, 5, 10)]        // 下限钳制：低于下限抬到下限
    [InlineData(ClampMode.High, 0, 20, 30, 20)]      // 上限钳制：高于上限压到上限
    [InlineData(ClampMode.Both, 10, 20, 5, 10)]
    [InlineData(ClampMode.Both, 10, 20, 25, 20)]
    [InlineData(ClampMode.None, 10, 20, 5, 5)]       // 不钳制：原样
    public void Clamp_modes_bound_the_engineering_value(ClampMode mode, double low, double high, double raw, double expected)
    {
        var scale = new ScaleConfig { Clamp = mode, ClampLow = low, ClampHigh = high };
        Assert.Equal(expected, scale.Apply(raw), 10);
    }

    [Fact]
    public void Clamp_applies_to_bcd_points_after_scale()
    {
        // findings D42 回归面：bcd 的类型解释结果是数值 → 同样走 Scale/Clamp
        var point = Pt(p =>
        {
            p.DataType = RuntimeDataType.Bcd;
            p.BcdDigits = 4;
            p.Length = 1;
            p.Scale = new ScaleConfig { Factor = 2, Clamp = ClampMode.High, ClampHigh = 1000 };
        });

        var decoded = PointCodec.Decode(point, new ushort[] { 0x0600 }, T);   // BCD 600
        Assert.True(decoded.IsGood);
        Assert.Equal(1000.0, Assert.IsType<double>(decoded.Value));           // 600*2=1200 → 钳到 1000

        // 逆换算：1000/2 = 500 → BCD 字位 0x0500
        Assert.Equal(new ushort[] { 0x0500 }, PointCodec.Encode(point, 1000.0));
    }

    // ═══════════════ 5. BCD ═══════════════

    [Theory]
    [InlineData(new ushort[] { 0x0000 }, 0L)]
    [InlineData(new ushort[] { 0x0001 }, 1L)]
    [InlineData(new ushort[] { 0x9999 }, 9999L)]
    [InlineData(new ushort[] { 0x1234, 0x5678 }, 12345678L)]
    [InlineData(new ushort[] { 0x0001, 0x0002, 0x0003, 0x0004 }, 1000200030004L)]   // 4 字：每寄存器 4 位十进制
    public void Bcd_decodes_every_word_count_and_round_trips(ushort[] wire, long expected)
    {
        var point = Pt(p => { p.DataType = RuntimeDataType.Bcd; p.BcdDigits = wire.Length * 4; p.Length = wire.Length; });

        var decoded = PointCodec.Decode(point, wire, T);
        Assert.Equal(expected, Assert.IsType<long>(decoded.Value));
        Assert.Equal(wire, PointCodec.Encode(point, decoded.Value));
    }

    [Fact]
    public void Bcd_digits_truncates_from_the_high_side_on_decode_and_encode()
    {
        var point = Pt(p => { p.DataType = RuntimeDataType.Bcd; p.BcdDigits = 2; p.Length = 1; });   // 只看低 2 位十进制

        Assert.Equal(34L, Assert.IsType<long>(PointCodec.Decode(point, new ushort[] { 0x1234 }, T).Value));

        // 编码同样从高位截去：写 1234 → 线上只有 34
        Assert.Equal(new ushort[] { 0x0034 }, PointCodec.Encode(point, 1234));
    }

    [Fact]
    public void Bcd_round_trips_through_every_swap()
    {
        // BCD 的编解码都按**线上寄存器顺序**读/写十进制位（不经 Reorder，与 plc4/plc6 datetime 同口径）：
        // 因此声明任何字序都满足 decode(encode(x)) == x。此前 Encode 走了 Reorder 而 Decode 不走，
        // 非 None 字序下「写进去再读回来」会变成另一个数（本轮修复，见报告缺陷清单）。
        foreach (var swap in new[] { SwapMode.None, SwapMode.Byte, SwapMode.Word, SwapMode.WordByte })
        {
            var point = Pt(p => { p.DataType = RuntimeDataType.Bcd; p.BcdDigits = 8; p.Length = 2; p.Swap = swap; });

            var wire = PointCodec.Encode(point, 12345678);
            Assert.Equal(new ushort[] { 0x1234, 0x5678 }, wire);   // 十进制字位按线上顺序排列，与字序无关
            Assert.Equal(12345678L, Assert.IsType<long>(PointCodec.Decode(point, wire, T).Value));
            Assert.Equal(wire, PointCodec.Encode(point, 12345678));
        }
    }

    [Fact]
    public void Bcd_non_decimal_nibbles_are_read_as_digits_todays_behaviour_locked()
    {
        // 口径留档（用户口径①「框架正常返回的数据不算异常」）：BCD 半字节 A-F 没有特殊判定，
        // 解码按「高半字节×10 + 低半字节」逐字节拼数：0x1A2B → (0x1A: 1×10+10=20)*100 + (0x2B: 2×10+11=31) = 2031。
        // 框架不做字节合法性判断；但这条行为必须被钉住——将来若改成「非法 BCD 降级」，这里会红。
        var point = Pt(p => { p.DataType = RuntimeDataType.Bcd; p.BcdDigits = 4; p.Length = 1; });

        var decoded = PointCodec.Decode(point, new ushort[] { 0x1A2B }, T);
        Assert.True(decoded.IsGood);
        Assert.Equal(2031L, Assert.IsType<long>(decoded.Value));
    }

    // ═══════════════ 6. datetime ═══════════════

    [Fact]
    public void DateTime_plc6_and_plc4_round_trip_and_honour_format_pattern()
    {
        var plc6 = Pt(p => { p.DataType = RuntimeDataType.DateTime; p.DateTimeFormat = "plc6"; });
        var wire = new ushort[] { 2026, 9, 16, 10, 20, 30 };
        var decoded = PointCodec.Decode(plc6, wire, T);
        Assert.Equal(new DateTime(2026, 9, 16, 10, 20, 30), Assert.IsType<DateTime>(decoded.Value));
        Assert.Equal(wire, PointCodec.Encode(plc6, decoded.Value));

        var plc4 = Pt(p => { p.DataType = RuntimeDataType.DateTime; p.DateTimeFormat = "plc4"; });
        var wire4 = new ushort[] { 2026, 9, 16, 10 };
        var decoded4 = PointCodec.Decode(plc4, wire4, T);
        Assert.Equal(new DateTime(2026, 9, 16, 10, 0, 0), Assert.IsType<DateTime>(decoded4.Value));
        Assert.Equal(wire4, PointCodec.Encode(plc4, decoded4.Value));

        // Format@pattern：datetime 显示按 pattern，不走 decimals
        var formatted = Pt(p =>
        {
            p.DataType = RuntimeDataType.DateTime;
            p.DateTimeFormat = "plc6";
            p.Format = new FormatConfig { Pattern = "yyyy/MM/dd" };
        });
        Assert.Equal("2026/09/16", PointCodec.Format(formatted, PointCodec.Decode(formatted, wire, T), "--"));
    }

    [Fact]
    public void DateTime_unixsec_and_unixms_round_trip_including_64bit_milliseconds()
    {
        var unixSec = Pt(p => { p.DataType = RuntimeDataType.DateTime; p.DateTimeFormat = "unixsec"; });
        var seconds = (ushort)(1700000000 >> 16);
        _ = seconds;
        var secWire = new ushort[] { 0x6553, 0xF100 };   // 1700000000 = 0x6553F100
        var decodedSec = PointCodec.Decode(unixSec, secWire, T);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000).LocalDateTime, Assert.IsType<DateTime>(decodedSec.Value));
        Assert.Equal(secWire, PointCodec.Encode(unixSec, decodedSec.Value));

        // unixms 是 4 字（64 位）：findings D25——毫秒时间戳必然超 32 位
        var unixMs = Pt(p => { p.DataType = RuntimeDataType.DateTime; p.DateTimeFormat = "unixms"; });
        var expected = DateTimeOffset.FromUnixTimeMilliseconds(1700000000123L).LocalDateTime;
        var msWire = PointCodec.Encode(unixMs, expected);
        Assert.Equal(4, msWire.Length);
        Assert.Equal(expected, Assert.IsType<DateTime>(PointCodec.Decode(unixMs, msWire, T).Value));

        // 显式钉住高位字：1700000000123 = 0x18B_CFE5_687B（4 字 = 64 位，2 字装不下）
        Assert.Equal(new ushort[] { 0x0000, 0x018B, 0xCFE5, 0x687B }, msWire);
    }

    [Fact]
    public void DateTime_unixms_short_frame_degrades_to_32bit_instead_of_bad()
    {
        // findings W49：`unixms` 声明字长是 4 字（64 位），但**解码输入**只给到 2 字时主动降级按 32 位解
        // （PointCodec.cs 注释：「会失真，但比整点置坏更接近真值」）。
        // 解码输入长度 = RuntimePoint.Length = SamplerConfigLoader.EffectiveLength：
        //   · 连续声明：unixms 恒 4 字（CGV-8 拒绝 length≠4），短于 4 字会被 Decode 的短帧门先判 Bad(shortFrame)；
        //   · <Slices> 声明：长度 = 各片段之和（CGV-8 对 Slices 点位跳过），
        //     故「unixms + 两个 1 字片段」是**合法配置**走到 2 字降级分支的真实路径——下面第一条就是它。
        var sliced = Pt(p =>
        {
            p.DataType = RuntimeDataType.DateTime;
            p.DateTimeFormat = "unixms";
            p.Slices = new[]
            {
                new SliceConfig { Address = 0, Length = 1 },
                new SliceConfig { Address = 4, Length = 1 },
            };
        });
        Assert.Equal(2, sliced.Length);   // 有效字长 = 片段之和（不是 DateTimeWordCount 的 4）

        var twoWords = new ushort[] { 0x0001, 0x86A0 };   // 0x000186A0 = 100000ms
        var degraded2 = PointCodec.Decode(sliced, twoWords, T);
        Assert.True(degraded2.IsGood, "2 字输入必须走降级路径而不是判坏：" + degraded2);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(100000).LocalDateTime,
            Assert.IsType<DateTime>(degraded2.Value));

        // 3 字同样落在 2 字分支（多出来的第 3 字被忽略，不参与拼装）
        var threeWords = Pt(p =>
        {
            p.DataType = RuntimeDataType.DateTime;
            p.DateTimeFormat = "unixms";
            p.Length = 3;   // 连续声明走不到这里（CGV-8），此处直接钉编解码分支
        });
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(100000).LocalDateTime,
            Assert.IsType<DateTime>(PointCodec.Decode(threeWords, new ushort[] { 0x0001, 0x86A0, 0xFFFF }, T).Value));

        // 1 字：再降一级，直接当毫秒数（0x86A0 = 34464ms ≈ 34.5s）——同样不判坏
        var oneWord = Pt(p =>
        {
            p.DataType = RuntimeDataType.DateTime;
            p.DateTimeFormat = "unixms";
            p.Length = 1;
        });
        var degraded1 = PointCodec.Decode(oneWord, new ushort[] { 0x86A0 }, T);
        Assert.True(degraded1.IsGood, "1 字输入同样走降级路径（不判坏）");
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(34464).LocalDateTime,
            Assert.IsType<DateTime>(degraded1.Value));

        // 降级是**失真**的：同一串 4 字线上数据的真值在 32 位之外，2 字解只能拿到低 32 位——
        // 「值 ≠ 真值但质量仍 Good」正是这条口径的代价（故意保留：比整点置坏更接近真值）
        var full = Pt(p => { p.DataType = RuntimeDataType.DateTime; p.DateTimeFormat = "unixms"; });
        var trueValue = Assert.IsType<DateTime>(
            PointCodec.Decode(full, new ushort[] { 0x0000, 0x018B, 0xCFE5, 0x687B }, T).Value);   // 1700000000123
        var lowTwoOnly = Assert.IsType<DateTime>(
            PointCodec.Decode(sliced, new ushort[] { 0xCFE5, 0x687B }, T).Value);
        Assert.NotEqual(trueValue, lowTwoOnly);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(0xCFE5687BL).LocalDateTime, lowTwoOnly);
    }

    [Fact]
    public void DateTime_out_of_range_fields_yield_bad_never_throw()
    {
        var plc6 = Pt(p => { p.DataType = RuntimeDataType.DateTime; p.DateTimeFormat = "plc6"; });

        // 月 0（ClampByte 只压上限）→ DateTime 构造抛 → 兜底判 Bad(ss.reason.decode)
        var decoded = PointCodec.Decode(plc6, new ushort[] { 2026, 0, 1, 0, 0, 0 }, T);
        Assert.Equal(PointQuality.Bad, decoded.Quality);
        Assert.Equal("ss.reason.decode", decoded.Reason);

        // unixsec 溢出到 DateTime 上限之外（0xFFFFFFFF 秒 ≈ 2106 年，仍在范围内；用超大毫秒验证上界）
        var unixMs = Pt(p => { p.DataType = RuntimeDataType.DateTime; p.DateTimeFormat = "unixms"; });
        var overflow = PointCodec.Decode(unixMs, new ushort[] { 0x7FFF, 0xFFFF, 0xFFFF, 0xFFFF }, T);
        Assert.False(overflow.IsGood);   // 超 DateTimeOffset 上限 → Bad，不抛
        Assert.Equal("ss.reason.decode", overflow.Reason);
    }

    // ═══════════════ 7. string：encoding / padding / left / trimNull ═══════════════

    [Theory]
    [InlineData("ascii")]
    [InlineData("utf8")]
    [InlineData("utf-8")]
    [InlineData("gbk")]
    [InlineData("gb2312")]
    public void String_encodings_round_trip_ascii_text(string encoding)
    {
        var point = Pt(p => { p.DataType = RuntimeDataType.String; p.Length = 4; p.StringEncoding = encoding; });

        var wire = PointCodec.Encode(point, "AB");
        Assert.Equal(new ushort[] { 0x4142, 0x0000, 0x0000, 0x0000 }, wire);
        Assert.Equal("AB", Assert.IsType<string>(PointCodec.Decode(point, wire, T).Value));
    }

    [Fact]
    public void Utf8_chinese_round_trips_across_register_boundaries()
    {
        var point = Pt(p => { p.DataType = RuntimeDataType.String; p.Length = 4; p.StringEncoding = "utf-8"; });

        const string text = "温度";                     // 6 字节 utf-8，跨 3 个寄存器
        var wire = PointCodec.Encode(point, text);
        Assert.Equal(new ushort[] { 0xE6B8, 0xA9E5, 0xBAA6, 0x0000 }, wire);   // 字节序：高字节先上线
        Assert.Equal(text, Assert.IsType<string>(PointCodec.Decode(point, wire, T).Value));
        Assert.Equal(wire, PointCodec.Encode(point, text));
    }

    [Fact]
    public void Utf8_padding_is_trimmed_at_the_byte_level_so_the_decoded_text_is_exact()
    {
        // 4 寄存器 = 8 字节；"温度" 6 字节 + 2 字节 0x00 补齐 → 先按字节裁掉尾部补齐，再解码
        var point = Pt(p => { p.DataType = RuntimeDataType.String; p.Length = 4; p.StringEncoding = "utf-8"; });

        var wire = new ushort[] { 0xE6B8, 0xA9E5, 0xBAA6, 0x0000 };
        Assert.Equal("温度", Assert.IsType<string>(PointCodec.Decode(point, wire, T).Value));
    }

    [Fact]
    public void Incomplete_utf8_tail_decodes_to_the_replacement_char_without_throwing()
    {
        // 口径留档：字节层裁剪只保证「补齐字节不会把字符切一半」；设备给的数据本身就是半个字符时，
        // 按 .NET 的口径解成 U+FFFD（不抛、不静默丢），坏数据由宿主按显示层处理。
        var point = Pt(p => { p.DataType = RuntimeDataType.String; p.Length = 4; p.StringEncoding = "utf-8"; });

        var wire = new ushort[] { 0xE6B8, 0xA9E5, 0xBAA6, 0x00E6 };   // 完整 6 字节 + 0xE6（半个汉字）
        var decoded = PointCodec.Decode(point, wire, T);

        Assert.True(decoded.IsGood);
        var text = Assert.IsType<string>(decoded.Value);
        Assert.StartsWith("温度", text, StringComparison.Ordinal);
        Assert.Contains('\uFFFD', text);
    }

    [Theory]
    [InlineData(0x00, true, new ushort[] { 0x4142, 0x0000 }, "AB")]      // 0x00 补齐、靠左 → 裁尾部
    [InlineData(0x20, true, new ushort[] { 0x4142, 0x2020 }, "AB")]      // 0x20 补齐、靠左
    [InlineData(0x20, false, new ushort[] { 0x2020, 0x4142 }, "AB")]     // 0x20 补齐、靠右 → 裁头部
    [InlineData(0x00, false, new ushort[] { 0x0000, 0x4142 }, "AB")]
    public void String_padding_and_left_trim_on_the_correct_side(int padding, bool left, ushort[] wire, string expected)
    {
        var point = Pt(p =>
        {
            p.DataType = RuntimeDataType.String;
            p.Length = 2;
            p.StringPadding = padding;
            p.StringPadLeft = left;
        });

        Assert.Equal(expected, Assert.IsType<string>(PointCodec.Decode(point, wire, T).Value));

        // 编码：按同一补齐字节与对齐侧回填，与解码严格对称
        Assert.Equal(wire, PointCodec.Encode(point, expected));
    }

    [Fact]
    public void String_trim_null_false_keeps_padding_and_empty_or_full_length_strings_are_stable()
    {
        var keep = Pt(p =>
        {
            p.DataType = RuntimeDataType.String;
            p.Length = 2;
            p.StringTrimNull = false;
        });

        Assert.Equal("AB\0\0", Assert.IsType<string>(PointCodec.Decode(keep, new ushort[] { 0x4142, 0x0000 }, T).Value));

        var point = Pt(p => { p.DataType = RuntimeDataType.String; p.Length = 2; });
        var empty = PointCodec.Encode(point, string.Empty);
        Assert.Equal(new ushort[] { 0, 0 }, empty);
        Assert.Equal(string.Empty, Assert.IsType<string>(PointCodec.Decode(point, empty, T).Value));

        // 满长串（4 字符 = 2 寄存器，无终止符）原样解出
        Assert.Equal("ABCD", Assert.IsType<string>(PointCodec.Decode(point, new ushort[] { 0x4142, 0x4344 }, T).Value));
    }

    [Fact]
    public void Unknown_string_encoding_falls_back_to_ascii_todays_behaviour_locked()
    {
        // 口径留档：GetEncoding 只认 utf8/utf-8/gbk/gb18030/gb2312，其余一律 ASCII（不报错、不抛）。
        // 加载期不对 String@encoding 做白名单校验——写了未知编码不会失败，只会按 ASCII 解释。
        var point = Pt(p => { p.DataType = RuntimeDataType.String; p.Length = 2; p.StringEncoding = "utf-16"; });

        Assert.Equal("AB", Assert.IsType<string>(PointCodec.Decode(point, new ushort[] { 0x4142, 0x0000 }, T).Value));
    }

    // ═══════════════ 8. Format ═══════════════

    [Fact]
    public void Format_decimals_and_thousands_apply_to_every_numeric_kind()
    {
        var doublePoint = Pt(p =>
        {
            p.DataType = RuntimeDataType.Float64;
            p.Length = 4;
            p.Format = new FormatConfig { Decimals = 2 };
        });
        Assert.Equal("3.14", PointCodec.Format(doublePoint, PointValue.Good(3.14159, T), "--"));

        var floatPoint = Pt(p =>
        {
            p.DataType = RuntimeDataType.Float32;
            p.Length = 2;
            p.Format = new FormatConfig { Decimals = 1, Thousands = true };
        });
        Assert.Equal("1,234.5", PointCodec.Format(floatPoint, PointValue.Good(1234.5f, T), "--"));

        var intPoint = Pt(p =>
        {
            p.DataType = RuntimeDataType.UInt16;
            p.Length = 1;
            p.Format = new FormatConfig { Decimals = 2, Thousands = true };
        });
        Assert.Equal("1,234.00", PointCodec.Format(intPoint, PointValue.Good((ushort)1234, T), "--"));
    }

    [Fact]
    public void Format_raw_array_and_prefix_suffix_and_null_text()
    {
        var raw = Pt(p => { p.DataType = RuntimeDataType.Raw; p.Length = 2; });
        var rawDecoded = PointCodec.Decode(raw, new ushort[] { 0x1234, 0x5678 }, T);
        Assert.Equal("0x1234 0x5678", PointCodec.Format(raw, rawDecoded, "--"));

        var prefixed = Pt(p =>
        {
            p.DataType = RuntimeDataType.Float64;
            p.Length = 4;
            p.Format = new FormatConfig { Decimals = 0, Prefix = "T=", Suffix = "°C" };
        });
        Assert.Equal("T=25°C", PointCodec.Format(prefixed, PointValue.Good(25.4, T), "--"));

        // 坏值：直接给 nullText，绝不拼接前后缀、也不显示残值
        var bad = PointValue.Bad("ss.reason.shortFrame", T);
        Assert.Equal("--", PointCodec.Format(prefixed, bad, "--"));
        Assert.Equal("(无)", PointCodec.Format(prefixed, bad, "(无)"));
    }

    [Fact]
    public void Format_map_matches_on_engineering_or_raw_and_is_ignored_without_items()
    {
        var engineeringMap = Pt(p =>
        {
            p.DataType = RuntimeDataType.UInt16;
            p.Length = 1;
            p.Scale = new ScaleConfig { Factor = 10 };
            p.Format = new FormatConfig { MapOn = "engineering" };
        });
        engineeringMap.Format!.Map["100"] = "满";
        Assert.Equal("满", PointCodec.Format(engineeringMap, PointValue.Good(100.0, T), "--", rawValue: 10));

        var rawMap = Pt(p =>
        {
            p.DataType = RuntimeDataType.UInt16;
            p.Length = 1;
            p.Scale = new ScaleConfig { Factor = 10 };
            p.Format = new FormatConfig { MapOn = "raw" };
        });
        rawMap.Format!.Map["10"] = "原始命中";
        Assert.Equal("原始命中", PointCodec.Format(rawMap, PointValue.Good(100.0, T), "--", rawValue: 10));

        // mapOn=raw 但没有 <Map> 条目：不抛、按普通数值格式输出
        var noMap = Pt(p =>
        {
            p.DataType = RuntimeDataType.UInt16;
            p.Length = 1;
            p.Format = new FormatConfig { MapOn = "raw" };
        });
        Assert.Equal("7", PointCodec.Format(noMap, PointValue.Good((ushort)7, T), "--", rawValue: 7));
    }

    // ═══════════════ 9. 降级口径（原因码全覆盖） ═══════════════

    [Theory]
    [InlineData(RuntimeDataType.UInt16, 1)]
    [InlineData(RuntimeDataType.Int16, 1)]
    [InlineData(RuntimeDataType.UInt32, 2)]
    [InlineData(RuntimeDataType.Int32, 2)]
    [InlineData(RuntimeDataType.UInt64, 4)]
    [InlineData(RuntimeDataType.Int64, 4)]
    [InlineData(RuntimeDataType.Float32, 2)]
    [InlineData(RuntimeDataType.Float64, 4)]
    [InlineData(RuntimeDataType.String, 2)]
    [InlineData(RuntimeDataType.Bcd, 1)]
    [InlineData(RuntimeDataType.DateTime, 6)]
    [InlineData(RuntimeDataType.Bool, 1)]
    [InlineData(RuntimeDataType.Raw, 1)]
    public void Short_frame_yields_bad_shortFrame_for_every_type_and_never_throws(RuntimeDataType dataType, int length)
    {
        var point = Pt(p => { p.DataType = dataType; p.Length = length; });

        var decoded = PointCodec.Decode(point, Array.Empty<ushort>(), T);
        Assert.Equal(PointQuality.Bad, decoded.Quality);
        Assert.Equal("ss.reason.shortFrame", decoded.Reason);
        Assert.Null(decoded.Value);
    }

    [Fact]
    public void Non_finite_engineering_values_are_uncertain_with_the_reason_code_and_keep_the_value()
    {
        var point = Pt(p => { p.DataType = RuntimeDataType.Float64; p.Length = 4; });

        var nan = PointCodec.Decode(point, new ushort[] { 0x7FF8, 0, 0, 0 }, T);
        Assert.Equal(PointQuality.Uncertain, nan.Quality);
        Assert.Equal("ss.reason.nan", nan.Reason);
        Assert.True(double.IsNaN((double)nan.Value!));

        var infinity = PointCodec.Decode(point, new ushort[] { 0x7FF0, 0, 0, 0 }, T);
        Assert.Equal(PointQuality.Uncertain, infinity.Quality);
        Assert.Equal("ss.reason.infinite", infinity.Reason);
        Assert.True(double.IsPositiveInfinity((double)infinity.Value!));
    }

    [Fact]
    public void Raw_point_returns_a_copy_of_the_wire_registers_and_encodes_it_back()
    {
        var point = Pt(p => { p.DataType = RuntimeDataType.Raw; p.Length = 3; });

        var wire = new ushort[] { 0x1111, 0x2222, 0x3333 };
        var decoded = PointCodec.Decode(point, wire, T);

        var words = Assert.IsType<ushort[]>(decoded.Value);
        Assert.Equal(wire, words);
        Assert.NotSame(wire, words);                       // 是副本，改动不回灌线上缓冲
        Assert.Equal(wire, PointCodec.Encode(point, words));

        // 短一截的输入按声明字长补齐（不足补 0），不抛
        Assert.Equal(new ushort[] { 0x1111, 0x0000, 0x0000 }, PointCodec.Encode(point, new ushort[] { 0x1111 }));
    }

    [Fact]
    public void Encode_null_throws_argument_null_but_other_bad_inputs_are_explicit()
    {
        var point = Pt(p => { p.DataType = RuntimeDataType.DateTime; p.DateTimeFormat = "plc6"; });

        Assert.Throws<ArgumentNullException>(() => PointCodec.Encode(point, null));
        // 写 datetime 点给非法类型 → InvalidCastException 带明确文案（引擎写管道会转成 Reject(ss.reason.encode)）
        var ex = Assert.Throws<InvalidCastException>(() => PointCodec.Encode(point, "不是时间"));
        Assert.Contains("datetime", ex.Message);
    }
}
