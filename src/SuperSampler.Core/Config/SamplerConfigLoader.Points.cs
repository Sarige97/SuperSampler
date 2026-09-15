using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;

namespace SuperSampler.Core.Config;

/// <summary>
/// <see cref="SamplerConfigLoader"/> 的点位解析部分：点表、块、点位属性、子元素、地址换算、枚举解析。
/// </summary>
public static partial class SamplerConfigLoader
{
    private static void LoadPointSets(
        XElement root,
        SamplerConfiguration config,
        Dictionary<string, XElement> pointTemplates,
        List<string> errors)
    {
        foreach (var setElement in root.Elements("PointSets").Elements("PointSet"))
        {
            var setId = (string?)setElement.Attribute("id");
            if (string.IsNullOrEmpty(setId))
            {
                errors.Add("PointSet 缺少 id");
                continue;
            }

            var set = new PointSetConfig { Id = setId! };

            var defaultsElement = setElement.Element("Defaults");
            if (defaultsElement != null)
            {
                set.Defaults = new PointDefaults
                {
                    Area = ParseArea((string?)defaultsElement.Attribute("area")),
                    DataType = ParseDataType((string?)defaultsElement.Attribute("dataType")),
                    Swap = ParseSwap((string?)defaultsElement.Attribute("swap")),
                    ScanGroup = (string?)defaultsElement.Attribute("scanGroup"),
                    UnitId = (int?)defaultsElement.Attribute("unitId"),
                    Access = (string?)defaultsElement.Attribute("access"),
                };
            }

            // 块读：area/unitId/scanGroup/swap 由块统一决定；块内点位不得覆盖（docs/01 规则二）
            foreach (var blockElement in setElement.Elements("Blocks").Elements("Block"))
            {
                var blockId = (string?)blockElement.Attribute("id");
                var start = (int?)blockElement.Attribute("start");
                var count = (int?)blockElement.Attribute("count");
                if (string.IsNullOrEmpty(blockId) || start == null || count == null)
                {
                    errors.Add($"PointSet {setId} 的 Block 缺少 id/start/count");
                    continue;
                }

                var block = new BlockConfig
                {
                    Id = blockId!,
                    Area = ParseArea((string?)blockElement.Attribute("area")) ?? RuntimeArea.HoldingRegister,
                    Start = start.Value,
                    Count = count.Value,
                    ScanGroup = (string?)blockElement.Attribute("scanGroup") ?? "normal",
                    UnitId = (int?)blockElement.Attribute("unitId"),
                    Swap = ParseSwap((string?)blockElement.Attribute("swap")) ?? SwapMode.Word,
                    Enabled = (bool?)blockElement.Attribute("enabled") ?? true,
                };

                foreach (var pointElement in blockElement.Elements("Point"))
                {
                    var point = ParsePoint(pointElement, setId!, config, pointTemplates, set.Defaults, errors);
                    if (point == null) continue;

                    if (pointElement.Attribute("area") != null)
                        errors.Add($"Block {blockId} 内点位 {point.Id} 不得覆盖 area");
                    if (pointElement.Attribute("unitId") != null)
                        errors.Add($"Block {blockId} 内点位 {point.Id} 不得覆盖 unitId");
                    if (pointElement.Attribute("scanGroup") != null)
                        errors.Add($"Block {blockId} 内点位 {point.Id} 不得覆盖 scanGroup");

                    point.Area = block.Area;
                    point.ScanGroup = block.ScanGroup;
                    if (block.UnitId.HasValue) point.UnitIdOverride = block.UnitId;
                    if (pointElement.Attribute("swap") == null)
                    {
                        // 块内点位未写 swap：由块决定（Block@swap，缺省 word）。
                        // 视为块作用域内的显式声明，不再向 Defaults/Device 兜底（docs/01 第 6 节）。
                        point.Swap = block.Swap;
                        point.HasSwapDeclared = true;
                    }

                    point.Address = ResolveAddress(pointElement, errors, $"{setId}/{point.Id}");
                    ValidatePointRules(point, pointElement, errors);
                    var end = point.Address + EffectiveLength(point);
                    if (point.Address < block.Start || end > block.Start + block.Count)
                    {
                        errors.Add($"Block {blockId} 窗口 [{block.Start},{block.Start + block.Count}) 不含点位 {point.Id} 的 [{point.Address},{end})");
                    }

                    block.Points.Add(point);
                }

                set.Blocks.Add(block);
            }

            foreach (var pointElement in setElement.Elements("Points").Elements("Point"))
            {
                var point = ParsePoint(pointElement, setId!, config, pointTemplates, set.Defaults, errors);
                if (point == null) continue;
                point.Address = ResolveAddress(pointElement, errors, $"{setId}/{point.Id}");
                ValidatePointRules(point, pointElement, errors);
                set.Points.Add(point);
            }

            // 计算点：解析并标记；评估在引擎按需进行（元素名兼容 Calculated / CalculatedPoints）
            foreach (var pointElement in setElement.Elements("Calculated").Elements("Point")
                         .Concat(setElement.Elements("CalculatedPoints").Elements("Point")))
            {
                var point = ParsePoint(pointElement, setId!, config, pointTemplates, set.Defaults, errors);
                if (point == null) continue;
                point.IsCalculated = true;
                point.Expression = pointElement.Element("Expression")?.Value.Trim();
                set.Calculated.Add(point);
            }

            config.PointSets.Add(set);
        }
    }

    private static PointConfig? ParsePoint(
        XElement e,
        string pointSetId,
        SamplerConfiguration config,
        Dictionary<string, XElement> pointTemplates,
        PointDefaults? defaults,
        List<string> errors)
    {
        var id = (string?)e.Attribute("id");
        if (string.IsNullOrEmpty(id))
        {
            errors.Add($"PointSet {pointSetId} 内有 Point 缺少 id");
            return null;
        }

        // 数值/布尔属性写错（如 limit="true"、bit="x"）时，要把「哪个点位、哪个属性」报出来：
        // 裸 FormatException 的消息只有类型转换文本，宿主拿不到定位（findings：v3 样例因此难查）。
        try
        {
            return ParsePointCore(e, id!, pointSetId, config, pointTemplates, defaults, errors);
        }
        catch (FormatException ex)
        {
            var attr = FindOffendingAttribute(e);
            errors.Add($"点位 {id}（PointSet {pointSetId}）属性 '{attr}' 不是合法数值或布尔值：{ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 粗定位：在元素**及其子元素**上按已知的数值/布尔属性顺序尝试解析，第一个失败的即为元凶。
    /// （必须含子元素：`limit` 这类属性挂在 &lt;Alarm&gt;/&lt;Write&gt;/&lt;Scale&gt; 等子元素上。）
    /// </summary>
    private static string FindOffendingAttribute(XElement e)
    {
        foreach (var element in e.DescendantsAndSelf())
        {
            var found = FindOffendingAttributeOn(element);
            if (found != null) return element.Name.LocalName + "@" + found;
        }

        return "(未知属性)";
    }

    private static string? FindOffendingAttributeOn(XElement e)
    {
        var numeric = new[] { "limit", "delayMs", "deadband", "min", "max", "step", "factor", "offset",
            "rawLow", "rawHigh", "scaledLow", "scaledHigh", "low", "high", "decimals", "digits",
            "address", "length", "bit", "unitId", "pulseMs" };
        foreach (var name in numeric)
        {
            var a = e.Attribute(name);
            if (a == null) continue;
            var text = a.Value;
            if (string.Equals(name, "unitId") || string.Equals(name, "address") || string.Equals(name, "length")
                || string.Equals(name, "bit") || string.Equals(name, "decimals") || string.Equals(name, "digits")
                || string.Equals(name, "pulseMs") || string.Equals(name, "delayMs"))
            {
                if (!int.TryParse(text, System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out _))
                {
                    return name;
                }
            }
            else if (!double.TryParse(text, System.Globalization.NumberStyles.Float,
                         System.Globalization.CultureInfo.InvariantCulture, out _))
            {
                return name;
            }
        }

        return null;
    }

    private static PointConfig? ParsePointCore(
        XElement e,
        string id,
        string pointSetId,
        SamplerConfiguration config,
        Dictionary<string, XElement> pointTemplates,
        PointDefaults? defaults,
        List<string> errors)
    {
        // 模板先行，实例覆盖
        XElement merged = e;
        var templateRef = (string?)e.Attribute("template");
        if (templateRef != null)
        {
            if (!pointTemplates.TryGetValue(templateRef, out var template))
            {
                errors.Add($"点位 {id} 引用了不存在的模板 {templateRef}");
            }
            else
            {
                merged = MergeAttributes(template, e, exclude: "id");
            }
        }

        // 枚举非法值必须报错，不能静默回落（findings W30/W31）
        ValidateEnum(merged, "area", ParseArea, errors, $"点位 {id}");
        ValidateEnum(merged, "dataType", ParseDataType, errors, $"点位 {id}");
        ValidateEnum(merged, "swap", ParseSwap, errors, $"点位 {id}");

        var accessText = ((string?)merged.Attribute("access"))?.Trim().ToLowerInvariant();
        if (accessText != null && accessText is not ("read" or "write" or "readwrite"))
        {
            errors.Add($"点位 {id}：access 非法值 \"{accessText}\"（应为 read/write/readwrite）");
        }

        // swap 兜底链（ADR D38）：Point@swap > PointSet/Defaults@swap > Device@swap > Global@swap。
        // 设备级与全局级兜底在运行期按「点位所属设备」解析（PointSet 可被多设备共用），
        // 这里只记「是否显式声明」；未显式声明时 Swap 暂存全局缺省，仅用于宿主展示。
        var declaredSwap = ParseSwap((string?)merged.Attribute("swap")) ?? defaults?.Swap;

        var point = new PointConfig
        {
            Id = id!,
            Name = I18nText(config.I18n, (string?)merged.Attribute("name")),
            Area = ParseArea((string?)merged.Attribute("area")) ?? defaults?.Area ?? RuntimeArea.HoldingRegister,
            DataType = ParseDataType((string?)merged.Attribute("dataType")) ?? defaults?.DataType ?? RuntimeDataType.UInt16,
            Swap = declaredSwap ?? config.Global.DefaultSwap,
            HasSwapDeclared = declaredSwap.HasValue,
            ScanGroup = (string?)merged.Attribute("scanGroup") ?? defaults?.ScanGroup ?? "normal",
            Access = ((string?)merged.Attribute("access") ?? defaults?.Access ?? "read").ToLowerInvariant(),
            Unit = I18nText(config.I18n, (string?)merged.Attribute("unit")),
            UnitIdOverride = (int?)merged.Attribute("unitId") ?? defaults?.UnitId,
            Length = (int?)merged.Attribute("length") ?? 0,
            Bit = (int?)merged.Attribute("bit"),
            BitRange = (string?)merged.Attribute("bitRange"),
            // findings W17：以下四项此前未读
            Enabled = (bool?)merged.Attribute("enabled") ?? true,
            Desc = (string?)merged.Attribute("desc"),
            ReadonlyBy = (string?)merged.Attribute("readonlyBy"),
        };

        // range 形如 "0..300"（findings W17）
        var rangeText = (string?)merged.Attribute("range");
        if (!string.IsNullOrWhiteSpace(rangeText))
        {
            var parts = rangeText!.Split(new[] { ".." }, StringSplitOptions.None);
            if (parts.Length == 2
                && double.TryParse(parts[0].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var rangeLow)
                && double.TryParse(parts[1].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var rangeHigh))
            {
                point.RangeLow = rangeLow;
                point.RangeHigh = rangeHigh;
            }
            else
            {
                errors.Add($"点位 {id}：range 非法值 \"{rangeText}\"（应为 0..300）");
            }
        }

        var scale = merged.Element("Scale");
        if (scale != null)
        {
            var clamp = scale.Element("Clamp");
            point.Scale = new ScaleConfig
            {
                Factor = (double?)scale.Attribute("factor") ?? 1.0,
                Offset = (double?)scale.Attribute("offset") ?? 0.0,
                RawLow = (double?)scale.Attribute("rawLow"),
                RawHigh = (double?)scale.Attribute("rawHigh"),
                ScaledLow = (double?)scale.Attribute("scaledLow"),
                ScaledHigh = (double?)scale.Attribute("scaledHigh"),
                Clamp = ParseClamp(clamp == null ? null : (string?)clamp.Attribute("mode")),
                ClampLow = (double?)clamp?.Attribute("low") ?? (double?)clamp?.Attribute("min") ?? 0,
                ClampHigh = (double?)clamp?.Attribute("high") ?? (double?)clamp?.Attribute("max") ?? 0,
            };
        }

        var format = merged.Element("Format");
        if (format != null)
        {
            var fmt = new FormatConfig
            {
                Decimals = (int?)format.Attribute("decimals") ?? 0,
                Prefix = I18nText(config.I18n, (string?)format.Attribute("prefix")),
                Suffix = I18nText(config.I18n, (string?)format.Attribute("suffix")),
                Thousands = (bool?)format.Attribute("thousands") ?? false,
                MapOn = ((string?)format.Attribute("mapOn") ?? "engineering").ToLowerInvariant(),
                Pattern = (string?)format.Attribute("pattern"),
            };

            foreach (var item in format.Elements("Map").Elements("Item"))
            {
                var key = (string?)item.Attribute("key");
                if (key != null) fmt.Map[key] = item.Value;
            }

            point.Format = fmt;
        }

        var write = merged.Element("Write");
        if (write != null)
        {
            point.Write = new WriteConfig
            {
                Min = (double?)write.Attribute("min"),
                Max = (double?)write.Attribute("max"),
                Step = (double?)write.Attribute("step"),
                Permission = (string?)write.Attribute("permission"),
                Verify = (bool?)write.Attribute("verify") ?? false,
                Confirm = (bool?)write.Attribute("confirm") ?? false,
                PulseMs = (int?)write.Attribute("pulseMs") ?? 0,
            };
        }

        var stringOptions = merged.Element("String");
        if (stringOptions != null)
        {
            point.StringEncoding = ((string?)stringOptions.Attribute("encoding") ?? "ascii").ToLowerInvariant();
            point.StringTrimNull = (bool?)stringOptions.Attribute("trimNull") ?? true;
        }

        var bcd = merged.Element("Bcd");
        if (bcd != null) point.BcdDigits = (int?)bcd.Attribute("digits") ?? 4;

        var dateTime = merged.Element("DateTime");
        if (dateTime != null) point.DateTimeFormat = ((string?)dateTime.Attribute("format") ?? "plc6").ToLowerInvariant();

        foreach (var alarm in merged.Elements("Alarm"))
        {
            point.Alarms.Add(new AlarmConfig
            {
                Id = (string?)alarm.Attribute("id") ?? string.Empty,
                Type = ((string?)alarm.Attribute("type") ?? "high").ToLowerInvariant(),
                Limit = (double?)alarm.Attribute("limit"),
                DelayMs = (int?)alarm.Attribute("delayMs") ?? 0,
                Deadband = (double?)alarm.Attribute("deadband") ?? 0,
                Priority = (string?)alarm.Attribute("priority"),
                Message = I18nText(config.I18n, (string?)alarm.Attribute("message")),
                Latch = (bool?)alarm.Attribute("latch") ?? false,
                AckRequired = (bool?)alarm.Attribute("ackRequired") ?? false,
            });
        }

        var slices = merged.Element("Slices");
        if (slices != null)
        {
            var list = new List<SliceConfig>();
            foreach (var slice in slices.Elements("Slice"))
            {
                list.Add(new SliceConfig
                {
                    Address = (int?)slice.Attribute("address") ?? 0,
                    Length = (int?)slice.Attribute("length") ?? 1,
                });
            }

            if (list.Count > 0) point.Slices = list;
        }

        return point;
    }

    /// <summary>地址换算：addrFormat=plc 时按 3xxxx/4xxxx/1xxxx/0xxxx 转数据区 + 0 基偏移，只换算这一次。</summary>
    private static int ResolveAddress(XElement pointElement, List<string> errors, string pointPath)
    {
        var raw = (int?)pointElement.Attribute("address");
        if (raw == null)
        {
            errors.Add($"点位 {pointPath} 缺少 address");
            return 0;
        }

        if (!string.Equals((string?)pointElement.Attribute("addrFormat"), "plc", StringComparison.OrdinalIgnoreCase))
        {
            return raw.Value;
        }

        var value = raw.Value;
        if (value >= 40001 && value <= 49999) return value - 40001;
        if (value >= 30001 && value <= 39999) return value - 30001;
        if (value >= 10001 && value <= 19999) return value - 10001;
        if (value >= 1 && value <= 9999) return value - 1;

        errors.Add($"点位 {pointPath} 的 PLC 地址 {value} 无法识别区段（应形如 40001/30001/10001/1）");
        return 0;
    }

    /// <summary>属性存在但解析不出合法枚举值时报错（findings W30/W31），避免静默回落。</summary>
    private static void ValidateEnum<T>(
        XElement element, string attribute, Func<string?, T?> parse, List<string> errors, string where)
        where T : struct
    {
        var raw = (string?)element.Attribute(attribute);
        if (raw != null && parse(raw) == null)
        {
            errors.Add($"{where}：{attribute} 非法值 \"{raw}\"");
        }
    }

    /// <summary>
    /// 点位的有效字长（寄存器/位数）：显式 length 优先，其余按 dataType 推导。
    /// BCD 与 datetime 的字长按类型参数推导（ADR D34），必须与
    /// <c>PointCodec.DecodeBcd</c> / <c>PointCodec.DecodeDateTime</c> 的实际读取需求一致；
    /// 校验（CGV-8）、块窗口校验与运行时 <c>RuntimePoint.Length</c> 共用这一个函数，避免两处各写一份。
    /// </summary>
    internal static int EffectiveLength(PointConfig point)
    {
        if (point.Length > 0) return point.Length;
        if (point.Bit.HasValue || point.BitRange != null) return 1;

        return point.DataType switch
        {
            RuntimeDataType.Int32 => 2,
            RuntimeDataType.UInt32 => 2,
            RuntimeDataType.Float32 => 2,
            RuntimeDataType.Int64 => 4,
            RuntimeDataType.UInt64 => 4,
            RuntimeDataType.Float64 => 4,
            RuntimeDataType.Bcd => BcdWordCount(point.BcdDigits),
            RuntimeDataType.DateTime => DateTimeWordCount(point.DateTimeFormat),
            _ => 1,
        };
    }

    /// <summary>BCD 每寄存器 4 位十进制；digits 非法（≤0）按 1 字兜底（与解码截位口径一致）。</summary>
    internal static int BcdWordCount(int digits)
        => digits <= 0 ? 1 : (digits + 3) / 4;

    /// <summary>
    /// datetime 各格式的字数：plc6 年/月/日/时/分/秒 = 6；plc4 年/月/日/时 = 4；
    /// unixsec = 2 字（32 位秒）、unixms = 4 字（64 位毫秒，毫秒必超 32 位）；
    /// 未知格式按 plc6 兜底（与 DecodeDateTime 的 default 分支一致）。
    /// </summary>
    internal static int DateTimeWordCount(string format)
    {
        switch ((format ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "plc4": return 4;
            case "unixsec": return 2;
            case "unixms": return 4;
            default: return 6;
        }
    }

    internal static SwapMode? ParseSwap(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        return text!.Trim().ToLowerInvariant() switch
        {
            "none" or "abcd" => SwapMode.None,
            "byte" or "badc" => SwapMode.Byte,
            "word" or "cdab" => SwapMode.Word,
            "word_byte" or "wordbyte" or "dcba" => SwapMode.WordByte,
            _ => null,
        };
    }

    internal static RuntimeArea? ParseArea(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        return text!.Trim().ToLowerInvariant() switch
        {
            "coil" => RuntimeArea.Coil,
            "discrete" or "discreteinput" => RuntimeArea.DiscreteInput,
            "input" or "inputregister" => RuntimeArea.InputRegister,
            "holding" or "holdingregister" => RuntimeArea.HoldingRegister,
            _ => null,
        };
    }

    internal static RuntimeDataType? ParseDataType(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        return text!.Trim().ToLowerInvariant() switch
        {
            "bool" => RuntimeDataType.Bool,
            "int16" => RuntimeDataType.Int16,
            "uint16" => RuntimeDataType.UInt16,
            "int32" => RuntimeDataType.Int32,
            "uint32" => RuntimeDataType.UInt32,
            "int64" => RuntimeDataType.Int64,
            "uint64" => RuntimeDataType.UInt64,
            "float32" or "float" => RuntimeDataType.Float32,
            "float64" or "double" => RuntimeDataType.Float64,
            "string" => RuntimeDataType.String,
            "bcd" => RuntimeDataType.Bcd,
            "datetime" => RuntimeDataType.DateTime,
            "raw" or "rawbytes" => RuntimeDataType.Raw,
            _ => null,
        };
    }

    internal static ClampMode ParseClamp(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return ClampMode.None;

        return text!.Trim().ToLowerInvariant() switch
        {
            "low" => ClampMode.Low,
            "high" => ClampMode.High,
            "both" => ClampMode.Both,
            _ => ClampMode.None,
        };
    }
}
