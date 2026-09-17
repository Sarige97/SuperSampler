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
        => Decode(point, registers, timestamp, out _);

    /// <summary>
    /// 解码一个点位，并同时给出**缩放前的原始数值**（<paramref name="rawValue"/>，脚本解码的输入，
    /// 见 <c>ScriptDecoder</c>）：位点 = bool、位域 = ushort、raw 点 = ushort[]、
    /// string/bcd/datetime = 该类型的解码结果、其余数值类型 = 缩放前数值。
    /// 短帧（数据不足）时原始数值为 null——调用方按 Bad 处理，也不会去跑脚本。
    /// </summary>
    public static PointValue Decode(RuntimePoint point, ushort[] registers, DateTimeOffset timestamp, out object? rawValue)
    {
        rawValue = null;
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
                rawValue = bit == 1;
                return PointValue.Good(rawValue, timestamp);
            }

            if (point.BitFrom.HasValue && point.BitTo.HasValue)
            {
                var width = point.BitTo.Value - point.BitFrom.Value + 1;
                var mask = (1 << width) - 1;
                var field = (registers[0] >> point.BitFrom.Value) & mask;
                rawValue = (ushort)field;
                return PointValue.Good(rawValue, timestamp);
            }

            var rawU64 = Assemble(Reorder(registers, point.Swap));

            // 先得到「原始数值」（缩放前），供 mapOn=raw 的枚举映射与脚本解码使用
            object? raw;
            var isNumeric = true;
            switch (point.DataType)
            {
                case RuntimeDataType.Bool:
                    raw = rawU64 != 0;
                    isNumeric = false;
                    break;
                case RuntimeDataType.Int16:
                    raw = unchecked((short)rawU64);
                    break;
                case RuntimeDataType.UInt16:
                    raw = (ushort)rawU64;
                    break;
                case RuntimeDataType.Int32:
                    raw = unchecked((int)rawU64);
                    break;
                case RuntimeDataType.UInt32:
                    raw = (uint)rawU64;
                    break;
                case RuntimeDataType.Int64:
                    raw = unchecked((long)rawU64);
                    break;
                case RuntimeDataType.UInt64:
                    raw = rawU64;
                    break;
                case RuntimeDataType.Float32:
                    raw = Int32BitsToSingle(unchecked((int)(uint)rawU64));
                    break;
                case RuntimeDataType.Float64:
                    raw = BitConverter.Int64BitsToDouble(unchecked((long)rawU64));
                    break;
                case RuntimeDataType.String:
                    raw = DecodeString(point, registers);
                    isNumeric = false;
                    break;
                case RuntimeDataType.Bcd:
                    // BCD 的「类型解释」结果是一个**数值**（long），因此与其它数值类型一样要吃 Scale
                    // （docs/01 §0.5 值处理管道：类型解释 → 原始数值 → Scale → 工程值）。
                    // 此前误按非数值处理，导致 <Scale> 在 bcd 点上解码被静默忽略、
                    // 而 Encode 却做逆缩放——读写不对称（findings D43）。
                    raw = DecodeBcd(registers, point.BcdDigits);
                    break;
                case RuntimeDataType.DateTime:
                    raw = DecodeDateTime(point, registers);
                    isNumeric = false;
                    break;
                case RuntimeDataType.Raw:
                    var copy = new ushort[registers.Length];
                    Array.Copy(registers, copy, registers.Length);
                    rawValue = copy;
                    return PointValue.Good(copy, timestamp);
                default:
                    return PointValue.Bad("ss.reason.decode", timestamp);
            }

            rawValue = raw;

            // 缩放：仅数值类型；有 Scale 才产出工程值，否则保持原始类型
            object? engineering = raw;
            if (isNumeric && point.Scale != null)
            {
                var rawDouble = Convert.ToDouble(raw, CultureInfo.InvariantCulture);
                engineering = point.Scale.Apply(rawDouble);
            }

            // ADR D32（GATE-3）：非有限浮点降级为 Uncertain——值保留供排查，绝不当 Good。
            // NaN/±Inf 出现在工程量里意味着溢出、脏寄存器或未初始化，不是可信测量。
            if (engineering is double d)
            {
                if (double.IsNaN(d)) return PointValue.Uncertain(engineering, "ss.reason.nan", timestamp);
                if (double.IsInfinity(d)) return PointValue.Uncertain(engineering, "ss.reason.infinite", timestamp);
            }
            else if (engineering is float f)
            {
                if (float.IsNaN(f)) return PointValue.Uncertain(engineering, "ss.reason.nan", timestamp);
                if (float.IsInfinity(f)) return PointValue.Uncertain(engineering, "ss.reason.infinite", timestamp);
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

        // 补齐口径（docs/01 §4.3）：按配置的补齐字节（0x00/0x20…）与补齐侧裁剪。
        // 裁剪在**字节层**做：先裁再按 encoding 解码，多字节编码（UTF-8/GBK）不会被截出半个字符。
        var start = 0;
        var end = bytes.Length;
        if (point.StringTrimNull)
        {
            var pad = (byte)point.StringPadding;
            if (point.StringPadLeft)
            {
                while (end > start && bytes[end - 1] == pad) end--;     // 靠左：裁尾部补齐
            }
            else
            {
                while (start < end && bytes[start] == pad) start++;     // 靠右：裁头部补齐
            }
        }

        return GetEncoding(point.StringEncoding).GetString(bytes, start, end - start);
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
                // 毫秒时间戳必然超出 32 位（2^32 ms ≈ 49.7 天），故 unixms 定为 4 字（64 位）；
                // 短帧降级：设备/窗口只给到 2 字时按 32 位解（会失真，但比整点置坏更接近真值）。
                var millis = registers.Length >= 4
                    ? (long)Assemble(Reorder(new ushort[] { registers[0], registers[1], registers[2], registers[3] }, point.Swap))
                    : registers.Length >= 2
                        ? (long)(uint)Assemble(Reorder(new ushort[] { registers[0], registers[1] }, point.Swap))
                        : (long)registers[0];
                return DateTimeOffset.FromUnixTimeMilliseconds(millis).LocalDateTime;
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
            // 无 <Format> 时按值类型渲染：数组/字节块与「有 Format」时同一套可读文本，
            // 其余保持 Invariant 直出（不引入小数位/千分位等格式策略）。
            // 此前一律走 Convert.ToString → raw 点的 ushort[] 显示成 "System.UInt16[]"（无意义的类型名）。
            switch (value.Value)
            {
                case ushort[] words:
                    return FormatWords(words);
                case byte[] bytes:
                    return FormatBytes(bytes);
                default:
                    return Convert.ToString(value.Value, CultureInfo.InvariantCulture) ?? nullText;
            }
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
                return FormatBytes(bytes);
            case ushort[] words:
                return FormatWords(words);
            case double d:
                return d.ToString(format.Thousands ? "N" + format.Decimals : "F" + format.Decimals, CultureInfo.InvariantCulture);
            case float f:
                return f.ToString(format.Thousands ? "N" + format.Decimals : "F" + format.Decimals, CultureInfo.InvariantCulture);
            case decimal m:
                return m.ToString(format.Thousands ? "N" + format.Decimals : "F" + format.Decimals, CultureInfo.InvariantCulture);
            case IFormattable formattable:
                // 整型工程值（ushort/int/long…）同样遵守 Format@decimals：
                // 此前只认 thousands，`decimals="2"` 在整型点上被静默忽略（findings D44）。
                if (format.Thousands)
                {
                    return formattable.ToString("N" + format.Decimals, CultureInfo.InvariantCulture);
                }

                return format.Decimals > 0
                    ? formattable.ToString("F" + format.Decimals, CultureInfo.InvariantCulture)
                    : formattable.ToString(null, CultureInfo.InvariantCulture);
            default:
                return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        }
    }

    /// <summary>字节块的可读文本（大写十六进制连写，无分隔）。</summary>
    private static string FormatBytes(byte[] bytes)
        => BitConverter.ToString(bytes).Replace("-", string.Empty);

    /// <summary>寄存器数组（raw 点的工程值）的可读文本：<c>0x1234 0x5678</c>。</summary>
    private static string FormatWords(ushort[] words)
    {
        var parts = new List<string>(words.Length);
        foreach (var word in words) parts.Add("0x" + word.ToString("X4", CultureInfo.InvariantCulture));
        return string.Join(" ", parts);
    }

    /// <summary>编码：工程值 → 线上寄存器（含逆缩放与字序）。用于写点位。</summary>
    public static ushort[] Encode(RuntimePoint point, object? value)
    {
        if (value == null) throw new ArgumentNullException(nameof(value));

        if (point.DataType == RuntimeDataType.String)
        {
            return EncodeString(point, Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);
        }

        // raw 点的工程值就是「该点位声明字长的线上寄存器」——原样写回（与 Decode 的 Raw 分支对称）
        if (point.DataType == RuntimeDataType.Raw && value is ushort[] rawWords)
        {
            return EncodeRaw(point, rawWords);
        }

        // datetime 的线上表示按 DateTime@format 拼字（与 DecodeDateTime 严格对称：plc4/plc6 不参与字序，
        // unixsec/unixms 走 Reorder 出字序）。此前没有这条分支，写可写 datetime 点会在运行期以
        // InvalidCastException（Convert.ToDouble(DateTime)）被拒 → 只能读不能写。
        if (point.DataType == RuntimeDataType.DateTime)
        {
            return EncodeDateTime(point, value);
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

        // BCD 的线上形态是**十进制字位**（每寄存器 4 位十进制），不是二进制值：
        // 此前它落进通用二进制分支，写出去的是二进制数、与 DecodeBcd 完全不对称（findings D43 家族）。
        if (point.DataType == RuntimeDataType.Bcd)
        {
            var wordCount = point.Length > 0 ? point.Length : SamplerConfigLoader.BcdWordCount(point.BcdDigits);
            var engineering = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            var rawBcd = point.Scale != null ? point.Scale.Reverse(engineering) : engineering;
            return EncodeBcd(rawBcd, wordCount, point.BcdDigits);
        }

        var raw = ToRaw(point, Convert.ToDouble(value, CultureInfo.InvariantCulture));
        var words = SplitWords(raw, point.Length > 0 ? point.Length : 1);
        return Reorder(words, point.Swap);
    }

    /// <summary>
    /// 把工程值编成 BCD 寄存器：每寄存器 4 位十进制，高位寄存器在前、字内高半字节在前。
    /// **不经 <see cref="Reorder"/>**：<see cref="DecodeBcd"/> 是按**线上寄存器顺序**读十进制位的
    /// （BCD 的字节/字序由设备决定，框架只按读到的顺序取位），编码必须走同一条口径，
    /// 否则声明 swap 的 BCD 点「写进去再读回来」会得到另一个数。plc4/plc6 的 datetime 同理。
    /// <paramref name="digits"/> 声明有效位数（与解码的取模口径一致：超出部分从高位截去）。
    /// </summary>
    private static ushort[] EncodeBcd(double engineering, int wordCount, int digits)
    {
        var text = Math.Round(Math.Abs(engineering), MidpointRounding.AwayFromZero)
            .ToString("F0", CultureInfo.InvariantCulture);

        var total = Math.Max(1, wordCount) * 4;
        if (digits > 0 && digits < 18 && text.Length > digits)
        {
            text = text.Substring(text.Length - digits);
        }

        if (text.Length > total) text = text.Substring(text.Length - total);
        text = text.PadLeft(total, '0');

        var words = new ushort[Math.Max(1, wordCount)];
        for (var w = 0; w < words.Length; w++)
        {
            ushort word = 0;
            for (var d = 0; d < 4; d++)
            {
                var digit = text[(w * 4) + d] - '0';
                word = (ushort)((word << 4) | (digit & 0xF));
            }

            words[w] = word;
        }

        return words;
    }

    /// <summary>
    /// raw 点的编码：工程值（<c>ushort[]</c>，即 <see cref="Decode(RuntimePoint, ushort[], DateTimeOffset)"/>
    /// 交付的线上寄存器）原样写回，
    /// 按点位声明字长补齐/截断（不足补 0）。raw 不参与字序换算——解码给的就是线上顺序。
    /// </summary>
    private static ushort[] EncodeRaw(RuntimePoint point, ushort[] words)
    {
        var count = Math.Max(point.Length > 0 ? point.Length : words.Length, 1);
        var result = new ushort[count];
        Array.Copy(words, result, Math.Min(words.Length, count));
        return result;
    }

    /// <summary>
    /// datetime 的编码（与 <see cref="DecodeDateTime"/> 逐格式对称）：
    /// <c>plc6</c> = 年/月/日/时/分/秒，<c>plc4</c> = 年/月/日/时（两者直接按字序写，不解 swap，同解码）；
    /// <c>unixsec</c> = 2 字 / <c>unixms</c> = 4 字（64 位，经 <see cref="Reorder"/> 出字序）。
    /// 接受 <see cref="DateTime"/> / <see cref="DateTimeOffset"/> / 可解析的字符串。
    /// </summary>
    private static ushort[] EncodeDateTime(RuntimePoint point, object? value)
    {
        if (!TryToDateTime(value, out var moment))
        {
            throw new InvalidCastException("datetime 点位写入值必须是 DateTime/DateTimeOffset 或可解析的时间字符串，实际 "
                + (value == null ? "null" : value.GetType().Name));
        }

        switch ((point.DateTimeFormat ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "unixsec":
                return Reorder(SplitWords((ulong)UnixSecondsOf(moment), 2), point.Swap);

            case "unixms":
                return Reorder(SplitWords((ulong)UnixMillisecondsOf(moment), 4), point.Swap);

            case "plc4":
                return new[] { (ushort)moment.Year, (ushort)moment.Month, (ushort)moment.Day, (ushort)moment.Hour };

            default: // plc6
                return new[]
                {
                    (ushort)moment.Year, (ushort)moment.Month, (ushort)moment.Day,
                    (ushort)moment.Hour, (ushort)moment.Minute, (ushort)moment.Second,
                };
        }
    }

    /// <summary>写入值 → 时刻：<c>DateTime</c>（Unspecified 按本机时区，与解码的 LocalDateTime 口径一致）/ <c>DateTimeOffset</c> / 字符串。</summary>
    private static bool TryToDateTime(object? value, out DateTimeOffset moment)
    {
        switch (value)
        {
            case DateTimeOffset dto:
                moment = dto;
                return true;
            case DateTime dt:
                moment = new DateTimeOffset(dt.Kind == DateTimeKind.Unspecified
                    ? DateTime.SpecifyKind(dt, DateTimeKind.Local)
                    : dt);
                return true;
            case string text when DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var parsed):
                moment = parsed;
                return true;
            default:
                moment = default;
                return false;
        }
    }

    private static long UnixSecondsOf(DateTimeOffset moment) => moment.ToUnixTimeSeconds();

    private static long UnixMillisecondsOf(DateTimeOffset moment) => moment.ToUnixTimeMilliseconds();

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

        // 与解码对称：按设备的补齐字节填充；靠右（left=false）时把文本顶到末尾
        var buffer = new byte[total];
        var pad = (byte)point.StringPadding;
        for (var i = 0; i < total; i++) buffer[i] = pad;

        var count = Math.Min(bytes.Length, total);
        var start = point.StringPadLeft ? 0 : total - count;
        Array.Copy(bytes, 0, buffer, start, count);

        var words = new ushort[total / 2];
        for (var i = 0; i < words.Length; i++)
        {
            words[i] = (ushort)((buffer[i * 2] << 8) | buffer[(i * 2) + 1]);
        }

        return words;
    }
}
