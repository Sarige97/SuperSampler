using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using SuperSampler.Abstractions.Values;
using SuperSampler.Core.Config;

namespace SuperSampler.Core.Runtime;

/// <summary>
/// 点位编解码：原始寄存器 ↔ 工程值 ↔ 显示文本。
/// 全部为纯函数（输入寄存器与点位定义，输出结果），便于穷举测试。
///
/// 字序模型：先把线上的寄存器序列「重排」为逻辑大端字序，再按大端拼位。
/// 四种排布都是对合变换（做两次等于没做），所以编码与解码用同一个 <see cref="Reorder"/>。
/// </summary>
public static class PointCodec
{
    /// <summary>
    /// 把线上寄存器序列重排为逻辑大端字序。
    /// 四种排布（按 Home Assistant 模型，64 位与 32 位语义一致）：
    /// - None  (ABCD)：不变
    /// - Byte  (BADC)：每个字内字节交换
    /// - Word  (CDAB)：相邻字成对交换（4 字时 [w1,w0,w3,w2]，不是整体反转！）
    /// - WordByte(DCBA)：字内字节交换 + 整体反字（等价于全字节反转）
    /// 三种变换都是对合（做两次等于没做），所以编码与解码用同一个函数。
    /// </summary>
    public static ushort[] Reorder(IReadOnlyList<ushort> source, SwapMode swap)
    {
        var result = new ushort[source.Count];
        for (var i = 0; i < source.Count; i++)
        {
            var word = source[i];
            result[i] = swap is SwapMode.Byte or SwapMode.WordByte
                ? (ushort)(((word & 0xFF) << 8) | ((word >> 8) & 0xFF))
                : word;
        }

        switch (swap)
        {
            case SwapMode.Word:
                // 字交换：按 32 位半字成对交换，4 字时绝不能写成整体反转
                for (var i = 0; i + 1 < result.Length; i += 2)
                {
                    var tmp = result[i];
                    result[i] = result[i + 1];
                    result[i + 1] = tmp;
                }

                break;

            case SwapMode.WordByte:
                Array.Reverse(result);
                break;
        }

        return result;
    }

    /// <summary>把逻辑大端字序的寄存器拼成无符号整数。</summary>
    private static ulong Assemble(IReadOnlyList<ushort> logicalWords)
    {
        ulong value = 0;
        foreach (var word in logicalWords)
        {
            value = (value << 16) | word;
        }

        return value;
    }

    /// <summary>
    /// 解码一个点位。registers 必须恰好是该点占用的寄存器（块内解码时由调用方按偏移切好）。
    /// 永不抛异常：数据不足或非法编码返回 Bad 质量。
    /// </summary>
    public static PointValue Decode(RuntimePoint point, ushort[] registers, DateTimeOffset timestamp)
    {
        try
        {
            if (registers.Length < point.Length)
            {
                return PointValue.Bad("ss.reason.shortFrame", timestamp);
            }

            // 位与位域只在第一个寄存器内取，与字序无关
            if (point.Bit.HasValue)
            {
                var bit = (registers[0] >> point.Bit.Value) & 1;
                return PointValue.Good(bit == 1, timestamp);
            }

            if (point.BitFrom.HasValue && point.BitTo.HasValue)
            {
                var width = point.BitTo.Value - point.BitFrom.Value + 1;
                var mask = (1 << width) - 1;
                var field = (registers[0] >> point.BitFrom.Value) & mask;
                return PointValue.Good((ushort)field, timestamp);
            }

            var rawU64 = Assemble(Reorder(registers, point.Swap));

            // 先得到「原始数值」（缩放前），供 mapOn=raw 的枚举映射使用
            object? rawValue;
            var isNumeric = true;
            switch (point.DataType)
            {
                case RuntimeDataType.Bool:
                    rawValue = rawU64 != 0;
                    isNumeric = false;
                    break;
                case RuntimeDataType.Int16:
                    rawValue = unchecked((short)rawU64);
                    break;
                case RuntimeDataType.UInt16:
                    rawValue = (ushort)rawU64;
                    break;
                case RuntimeDataType.Int32:
                    rawValue = unchecked((int)rawU64);
                    break;
                case RuntimeDataType.UInt32:
                    rawValue = (uint)rawU64;
                    break;
                case RuntimeDataType.Int64:
                    rawValue = unchecked((long)rawU64);
                    break;
                case RuntimeDataType.UInt64:
                    rawValue = rawU64;
                    break;
                case RuntimeDataType.Float32:
                    rawValue = Int32BitsToSingle(unchecked((int)(uint)rawU64));
                    break;
                case RuntimeDataType.Float64:
                    rawValue = BitConverter.Int64BitsToDouble(unchecked((long)rawU64));
                    break;
                case RuntimeDataType.String:
                    rawValue = DecodeString(point, registers);
                    isNumeric = false;
                    break;
                case RuntimeDataType.Bcd:
                    rawValue = DecodeBcd(registers, point.BcdDigits);
                    isNumeric = false;
                    break;
                case RuntimeDataType.DateTime:
                    rawValue = DecodeDateTime(point, registers);
                    isNumeric = false;
                    break;
                case RuntimeDataType.Raw:
                    var copy = new ushort[registers.Length];
                    Array.Copy(registers, copy, registers.Length);
                    return PointValue.Good(copy, timestamp);
                default:
                    return PointValue.Bad("ss.reason.decode", timestamp);
            }

            // 缩放：仅数值类型；有 Scale 才产出工程值，否则保持原始类型
            object? engineering = rawValue;
            if (isNumeric && point.Scale != null)
            {
                var rawDouble = Convert.ToDouble(rawValue, CultureInfo.InvariantCulture);
                engineering = point.Scale.Apply(rawDouble);
            }

            return PointValue.Good(engineering, timestamp);
        }
        catch (Exception ex)
        {
            // 编解码属于 Permanent 类错误：规则错了重试也没用（docs/02 第 2.2 节）
            return PointValue.Bad("ss.reason.decode", timestamp, ex.Message);
        }
    }

    private static object DecodeString(RuntimePoint point, ushort[] registers)
    {
        var bytes = new byte[registers.Length * 2];
        for (var i = 0; i < registers.Length; i++)
        {
            bytes[i * 2] = (byte)(registers[i] >> 8);
            bytes[(i * 2) + 1] = (byte)(registers[i] & 0xFF);
        }

        var encoding = GetEncoding(point.StringEncoding);
        var text = encoding.GetString(bytes);

        if (point.StringTrimNull)
        {
            var end = text.IndexOf('\0');
            if (end >= 0) text = text.Substring(0, end);
            text = text.TrimEnd(' ');
        }

        return text;
    }

    internal static Encoding GetEncoding(string name)
    {
        return name.ToLowerInvariant() switch
        {
            "utf8" or "utf-8" => new UTF8Encoding(false),
            "gbk" or "gb18030" or "gb2312" => Encoding.GetEncoding(936),
            _ => Encoding.ASCII,
        };
    }

    private static object DecodeBcd(ushort[] registers, int digits)
    {
        // 每个字节存两位十进制，从高位寄存器的高字节开始拼接
        long value = 0;
        foreach (var register in registers)
        {
            value = (value * 100) + ((register >> 12) & 0xF) * 10 + ((register >> 8) & 0xF);
            value = (value * 100) + ((register >> 4) & 0xF) * 10 + (register & 0xF);
        }

        // digits 声明有效位数，超出部分从高位截去
        if (digits > 0 && digits < 18)
        {
            var modulus = 1L;
            for (var i = 0; i < digits; i++) modulus *= 10;
            value %= modulus;
        }

        return value;
    }

    private static object DecodeDateTime(RuntimePoint point, ushort[] registers)
    {
        switch (point.DateTimeFormat)
        {
            case "unixsec":
            {
                var seconds = registers.Length >= 2
                    ? Assemble(Reorder(new ushort[] { registers[0], registers[1] }, point.Swap))
                    : registers[0];
                return DateTimeOffset.FromUnixTimeSeconds((long)(uint)seconds).LocalDateTime;
            }

            case "unixms":
            {
                var millis = registers.Length >= 2
                    ? Assemble(Reorder(new ushort[] { registers[0], registers[1] }, point.Swap))
                    : registers[0];
                return DateTimeOffset.FromUnixTimeMilliseconds((long)(uint)millis).LocalDateTime;
            }

            case "plc4":
            {
                return new DateTime(
                    registers[0],
                    ClampByte(registers.Length > 1 ? registers[1] : (ushort)1),
                    ClampByte(registers.Length > 2 ? registers[2] : (ushort)1),
                    ClampByte(registers.Length > 3 ? registers[3] : (ushort)0),
                    0,
                    0);
            }

            default: // plc6：年 月 日 时 分 秒 各 1 寄存器
            {
                return new DateTime(
                    registers[0],
                    ClampByte(registers.Length > 1 ? registers[1] : (ushort)1),
                    ClampByte(registers.Length > 2 ? registers[2] : (ushort)1),
                    ClampByte(registers.Length > 3 ? registers[3] : (ushort)0),
                    ClampByte(registers.Length > 4 ? registers[4] : (ushort)0),
                    ClampByte(registers.Length > 5 ? registers[5] : (ushort)0));
            }
        }
    }

    private static int ClampByte(ushort value) => Math.Min((int)value, 99);

    // net46 没有 BitConverter.Int32BitsToSingle / SingleToInt32Bits，用字节序列换
    private static float Int32BitsToSingle(int bits)
        => BitConverter.ToSingle(BitConverter.GetBytes(bits), 0);

    private static int SingleToInt32Bits(float value)
        => BitConverter.ToInt32(BitConverter.GetBytes(value), 0);

    /// <summary>
    /// 格式化为显示文本。rawValue 供 mapOn=raw 的枚举映射使用（缩放前的原始值）；
    /// 不掌握 raw 时传与工程值相同的引用即可。
    /// </summary>
    public static string Format(RuntimePoint point, PointValue value, string nullText, object? rawValue = null)
    {
        if (!value.IsGood) return nullText;

        var format = point.Format;
        if (format == null)
        {
            return Convert.ToString(value.Value, CultureInfo.InvariantCulture) ?? nullText;
        }

        // 枚举映射优先于数值格式
        var mapped = TryMap(format, value.Value, rawValue);
        if (mapped != null)
        {
            return (format.Prefix ?? string.Empty) + mapped + (format.Suffix ?? string.Empty);
        }

        var text = FormatValue(value.Value, format);
        return (format.Prefix ?? string.Empty) + text + (format.Suffix ?? string.Empty);
    }

    private static string? TryMap(FormatConfig format, object? engineering, object? rawValue)
    {
        if (format.Map.Count == 0) return null;

        var basis = format.MapOn == "raw" ? rawValue : engineering;
        if (basis is bool b)
        {
            return format.Map.TryGetValue(b ? "true" : "false", out var boolText) ? boolText : null;
        }

        if (basis is byte or sbyte or short or ushort or int or long or ulong)
        {
            return format.Map.TryGetValue(Convert.ToString(basis, CultureInfo.InvariantCulture)!, out var intText) ? intText : null;
        }

        if (basis is double or float or decimal)
        {
            var rounded = Math.Round(Convert.ToDouble(basis), MidpointRounding.AwayFromZero);
            return format.Map.TryGetValue(((long)rounded).ToString(CultureInfo.InvariantCulture), out var decText) ? decText : null;
        }

        return null;
    }

    private static string FormatValue(object? value, FormatConfig format)
    {
        switch (value)
        {
            case null:
                return string.Empty;
            case bool b:
                return b ? "true" : "false";
            case DateTime dt:
                return dt.ToString(format.Pattern ?? "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            case DateTimeOffset dto:
                return dto.ToString(format.Pattern ?? "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            case byte[] bytes:
                return BitConverter.ToString(bytes).Replace("-", string.Empty);
            case ushort[] words:
            {
                var parts = new List<string>(words.Length);
                foreach (var word in words) parts.Add("0x" + word.ToString("X4", CultureInfo.InvariantCulture));
                return string.Join(" ", parts);
            }
            case double d:
                return d.ToString(format.Thousands ? "N" + format.Decimals : "F" + format.Decimals, CultureInfo.InvariantCulture);
            case float f:
                return f.ToString(format.Thousands ? "N" + format.Decimals : "F" + format.Decimals, CultureInfo.InvariantCulture);
            case decimal m:
                return m.ToString(format.Thousands ? "N" + format.Decimals : "F" + format.Decimals, CultureInfo.InvariantCulture);
            case IFormattable formattable:
                return format.Thousands
                    ? formattable.ToString("N0", CultureInfo.InvariantCulture)
                    : formattable.ToString(null, CultureInfo.InvariantCulture);
            default:
                return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        }
    }

    /// <summary>编码：工程值 → 线上寄存器（含逆缩放与字序）。用于写点位。</summary>
    public static ushort[] Encode(RuntimePoint point, object? value)
    {
        if (value == null) throw new ArgumentNullException(nameof(value));

        if (point.DataType == RuntimeDataType.String)
        {
            return EncodeString(point, Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);
        }

        if (point.DataType == RuntimeDataType.Bool)
        {
            var on = value is bool flag && flag || Convert.ToDouble(value, CultureInfo.InvariantCulture) != 0;

            // 位点：把值放到配置指定的位上（其余位为 0）；
            // 完整语义（读-改-写，避免清掉同寄存器其他位）由引擎写管道处理
            if (point.Bit.HasValue)
            {
                return new ushort[] { on ? (ushort)(1 << point.Bit.Value) : (ushort)0 };
            }

            return new ushort[] { on ? (ushort)1 : (ushort)0 };
        }

        // 整数直通路径：无缩放时不经 double 中转，避免 ≥2^53 丢精度（findings D8）
        if (point.Scale == null && IsIntegral(point.DataType) && TryIntegralRaw(value, point.DataType, out var direct))
        {
            return Reorder(SplitWords(direct, point.Length > 0 ? point.Length : 1), point.Swap);
        }

        var raw = ToRaw(point, Convert.ToDouble(value, CultureInfo.InvariantCulture));
        var words = SplitWords(raw, point.Length > 0 ? point.Length : 1);
        return Reorder(words, point.Swap);
    }

    private static bool IsIntegral(RuntimeDataType dataType)
        => dataType is RuntimeDataType.Int16 or RuntimeDataType.UInt16
            or RuntimeDataType.Int32 or RuntimeDataType.UInt32
            or RuntimeDataType.Int64 or RuntimeDataType.UInt64;

    /// <summary>整型输入按目标位宽取模（与 double 路径的「取整→截断」口径一致），不经 double。</summary>
    private static bool TryIntegralRaw(object? value, RuntimeDataType dataType, out ulong raw)
    {
        ulong bits;
        switch (value)
        {
            case ulong u: bits = u; break;
            case long l: bits = unchecked((ulong)l); break;
            case uint ui: bits = ui; break;
            case int i: bits = unchecked((uint)i); break;
            case ushort us: bits = us; break;
            case short s: bits = unchecked((ushort)s); break;
            case byte b: bits = b; break;
            case sbyte sb: bits = unchecked((byte)sb); break;
            default:
                raw = 0;
                return false;
        }

        var mask = dataType switch
        {
            RuntimeDataType.Int16 or RuntimeDataType.UInt16 => 0xFFFFUL,
            RuntimeDataType.Int32 or RuntimeDataType.UInt32 => 0xFFFFFFFFUL,
            _ => ulong.MaxValue,
        };

        raw = bits & mask;
        return true;
    }

    private static ulong ToRaw(RuntimePoint point, double engineering)
    {
        var raw = point.Scale != null ? point.Scale.Reverse(engineering) : engineering;

        // 浮点绝不能取整：写 3.14159 出去必须还是 3.14159（findings #3）
        if (point.DataType == RuntimeDataType.Float32)
        {
            return (ulong)(uint)SingleToInt32Bits((float)raw);
        }

        if (point.DataType == RuntimeDataType.Float64)
        {
            return (ulong)BitConverter.DoubleToInt64Bits(raw);
        }

        // 整数类型才做落位取整
        var rounded = Math.Round(raw, MidpointRounding.AwayFromZero);

        return point.DataType switch
        {
            RuntimeDataType.Int16 => unchecked((ulong)(short)rounded) & 0xFFFF,
            RuntimeDataType.UInt16 => (ulong)rounded & 0xFFFF,
            RuntimeDataType.Int32 => unchecked((ulong)(int)rounded) & 0xFFFFFFFF,
            RuntimeDataType.UInt32 => (ulong)rounded & 0xFFFFFFFF,
            RuntimeDataType.Int64 => unchecked((ulong)(long)rounded),
            RuntimeDataType.UInt64 => (ulong)rounded,
            _ => (ulong)rounded,
        };
    }

    private static ushort[] SplitWords(ulong value, int wordCount)
    {
        var words = new ushort[wordCount];
        for (var i = 0; i < wordCount; i++)
        {
            var shift = 16 * (wordCount - 1 - i);
            words[i] = (ushort)((value >> shift) & 0xFFFF);
        }

        return words;
    }

    private static ushort[] EncodeString(RuntimePoint point, string text)
    {
        var encoding = GetEncoding(point.StringEncoding);
        var bytes = encoding.GetBytes(text);
        var total = Math.Max(point.Length, 1) * 2;
        var buffer = new byte[total];
        Array.Copy(bytes, buffer, Math.Min(bytes.Length, total));

        var words = new ushort[total / 2];
        for (var i = 0; i < words.Length; i++)
        {
            words[i] = (ushort)((buffer[i * 2] << 8) | buffer[(i * 2) + 1]);
        }

        return words;
    }
}
