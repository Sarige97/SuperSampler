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
                    if (pointElement.Attribute("swap") == null) point.Swap = block.Swap;

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

        var point = new PointConfig
        {
            Id = id!,
            Name = I18nText(config.I18n, (string?)merged.Attribute("name")),
            Area = ParseArea((string?)merged.Attribute("area")) ?? defaults?.Area ?? RuntimeArea.HoldingRegister,
            DataType = ParseDataType((string?)merged.Attribute("dataType")) ?? defaults?.DataType ?? RuntimeDataType.UInt16,
            Swap = ParseSwap((string?)merged.Attribute("swap")) ?? defaults?.Swap ?? config.Global.DefaultSwap,
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
            _ => 1,
        };
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
