using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;

namespace SuperSampler.Core.Config;

/// <summary>
/// <see cref="SamplerConfigLoader"/> 的点位解析部分：点表、块、点位属性、子元素、地址换算、枚举解析。
/// 属性取值统一走 <c>IntAttr/BoolAttr/DoubleAttr</c>：写错时报出「实体 + 属性 + 原因」并继续收集，
/// 绝不提前抛（阶段一必须把全部问题一次报完）。
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
                var defaultsWhere = $"PointSet {setId} 的 Defaults";
                set.Defaults = new PointDefaults
                {
                    Area = ParseArea((string?)defaultsElement.Attribute("area")),
                    DataType = ParseDataType((string?)defaultsElement.Attribute("dataType")),
                    Swap = ParseSwap((string?)defaultsElement.Attribute("swap")),
                    UnitId = IntAttr(defaultsElement, "unitId", errors, defaultsWhere),
                    Access = (string?)defaultsElement.Attribute("access"),
                };
            }

            // 块读：area/unitId/swap 由块统一决定；间隔与模式也由块统一决定（点位的 intervalMs/mode 报错，见下）
            foreach (var blockElement in setElement.Elements("Blocks").Elements("Block"))
            {
                var blockId = (string?)blockElement.Attribute("id");
                var blockWhere = $"PointSet {setId} 的 Block {blockId ?? "(未命名)"}";
                var start = IntAttr(blockElement, "start", errors, blockWhere);
                var count = IntAttr(blockElement, "count", errors, blockWhere);
                if (string.IsNullOrEmpty(blockId) || start == null || count == null)
                {
                    errors.Add($"PointSet {setId} 的 Block 缺少 id/start/count");
                    continue;
                }

                // CGV-22：mode 非法值报错（不静默回落）
                ValidateMode(blockElement, errors, blockWhere);

                var block = new BlockConfig
                {
                    Id = blockId!,
                    Area = ParseArea((string?)blockElement.Attribute("area")) ?? RuntimeArea.HoldingRegister,
                    Start = start.Value,
                    Count = count.Value,
                    IntervalMs = IntAttr(blockElement, "intervalMs", errors, blockWhere),
                    Mode = ParseMode((string?)blockElement.Attribute("mode")) ?? "auto",
                    UnitId = IntAttr(blockElement, "unitId", errors, blockWhere),
                    Swap = ParseSwap((string?)blockElement.Attribute("swap")) ?? SwapMode.Word,
                    Enabled = BoolAttr(blockElement, "enabled", errors, blockWhere) ?? true,
                };

                foreach (var pointElement in blockElement.Elements("Point"))
                {
                    var point = ParsePoint(pointElement, setId!, config, pointTemplates, set.Defaults, errors);
                    if (point == null) continue;

                    // CGV-7：块内点位不得覆盖「怎么读」的四项
                    if (pointElement.Attribute("area") != null)
                        errors.Add($"Block {blockId} 内点位 {point.Id} 不得覆盖 area");
                    if (pointElement.Attribute("unitId") != null)
                        errors.Add($"Block {blockId} 内点位 {point.Id} 不得覆盖 unitId");

                    // CGV-20：块统一决定间隔与模式，块内点位不得自带（点位属性与模板带入的都算）
                    if (point.IntervalMs.HasValue)
                        errors.Add($"Block {blockId} 内点位 {point.Id} 不得写 intervalMs（块统一决定间隔，请写到 Block@intervalMs）");
                    if (!string.Equals(point.Mode, "auto", StringComparison.Ordinal))
                        errors.Add($"Block {blockId} 内点位 {point.Id} 不得写 mode（块统一决定模式，请写到 Block@mode）");

                    point.Area = block.Area;
                    point.IntervalMs = null;
                    point.Mode = "auto";
                    if (block.UnitId.HasValue) point.UnitIdOverride = block.UnitId;
                    if (pointElement.Attribute("swap") == null && blockElement.Attribute("swap") != null)
                    {
                        // 只有块**显式声明** swap 时才管住块内点位（窄作用域优先，ADR D38）。
                        // 块未声明时绝不强塞缺省值：曾因此把块内所有多字点位按 word/CDAB 解错
                        // （累计电能、float64 读出天文数字，findings D27），须让 Defaults → Device → Global 正常兜底。
                        point.Swap = block.Swap;
                        point.HasSwapDeclared = true;
                    }

                    point.Address = ResolveAddress(pointElement, errors, PointPath(setId!, point.Id));
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
                point.Address = ResolveAddress(pointElement, errors, PointPath(setId!, point.Id));
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
                foreach (var pointRef in pointElement.Elements("DependsOn").Elements("PointRef"))
                {
                    var reference = pointRef.Value.Trim();
                    if (reference.Length > 0) point.DependsOn.Add(reference);
                }

                set.Calculated.Add(point);
            }

            // 位映射展开（B2）：<Bits> 的记录在解析期已进模型，这里统一展开成子点位
            // （必须在本表全部点位解析完之后：子点位 id 冲突要拿全表比对）。
            ExpandBitMaps(set, errors);

            config.PointSets.Add(set);
        }
    }

    /// <summary>点位定位串：点表 id + 点位 id（加载期还没有 deviceId，点表才是声明所在）。</summary>
    internal static string PointPath(string pointSetId, string pointId) => $"点位 {pointSetId}/{pointId}";

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
        // 常规情况已由 IntAttr/BoolAttr/DoubleAttr 逐属性转成带定位的错误；这里是兜底。
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

        var where = $"点位 {id}（PointSet {pointSetId}）";

        // 枚举非法值必须报错，不能静默回落（findings W30/W31）
        ValidateEnum(merged, "area", ParseArea, errors, where);
        ValidateEnum(merged, "dataType", ParseDataType, errors, where);
        ValidateEnum(merged, "swap", ParseSwap, errors, where);
        ValidateMode(merged, errors, where);

        var accessText = ((string?)merged.Attribute("access"))?.Trim().ToLowerInvariant();
        if (accessText != null && accessText is not ("read" or "write" or "readwrite"))
        {
            errors.Add($"{where}：access 非法值 \"{accessText}\"（应为 read/write/readwrite）");
        }

        // swap 兜底链（ADR D38）：Point@swap > PointSet/Defaults@swap > Device@swap > Global@swap。
        // 设备级与全局级兜底在运行期按「点位所属设备」解析（PointSet 可被多设备共用），
        // 这里只记「是否显式声明」；未显式声明时 Swap 暂存全局缺省，仅用于宿主展示。
        var declaredSwap = ParseSwap((string?)merged.Attribute("swap")) ?? defaults?.Swap;

        var point = new PointConfig
        {
            Id = id!,
            PointSetId = pointSetId,
            Name = I18nText(config.I18n, (string?)merged.Attribute("name")),
            Area = ParseArea((string?)merged.Attribute("area")) ?? defaults?.Area ?? RuntimeArea.HoldingRegister,
            DataType = ParseDataType((string?)merged.Attribute("dataType")) ?? defaults?.DataType ?? RuntimeDataType.UInt16,
            Swap = declaredSwap ?? config.Global.DefaultSwap,
            HasSwapDeclared = declaredSwap.HasValue,
            // 间隔/模式只在点位与块上声明（PointSet/Defaults 不承载，避免「写了不生效」的中间层）
            IntervalMs = IntAttr(merged, "intervalMs", errors, where),
            Mode = ParseMode((string?)merged.Attribute("mode")) ?? "auto",
            Access = ((string?)merged.Attribute("access") ?? defaults?.Access ?? "read").ToLowerInvariant(),
            Unit = I18nText(config.I18n, (string?)merged.Attribute("unit")),
            UnitIdOverride = IntAttr(merged, "unitId", errors, where) ?? defaults?.UnitId,
            Length = IntAttr(merged, "length", errors, where) ?? 0,
            Bit = IntAttr(merged, "bit", errors, where),
            BitRange = (string?)merged.Attribute("bitRange"),
            // findings W17：以下四项此前未读
            Enabled = BoolAttr(merged, "enabled", errors, where) ?? true,
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
                // CGV-38：端值必须是有限数（"0..NaN" 会静默进模型，量程/百分比死区随后全是 NaN）
                if (double.IsNaN(rangeLow) || double.IsInfinity(rangeLow)
                    || double.IsNaN(rangeHigh) || double.IsInfinity(rangeHigh))
                {
                    errors.Add($"{where}：range=\"{rangeText}\" 非法（端值必须是有限数值）");
                }
                else
                {
                    point.RangeLow = rangeLow;
                    point.RangeHigh = rangeHigh;
                }
            }
            else
            {
                errors.Add($"{where}：range 非法值 \"{rangeText}\"（应为 0..300）");
            }
        }

        var scale = merged.Element("Scale");
        if (scale != null)
        {
            var clamp = scale.Element("Clamp");
            point.Scale = new ScaleConfig
            {
                Factor = DoubleAttr(scale, "factor", errors, where + "/Scale") ?? 1.0,
                Offset = DoubleAttr(scale, "offset", errors, where + "/Scale") ?? 0.0,
                RawLow = DoubleAttr(scale, "rawLow", errors, where + "/Scale"),
                RawHigh = DoubleAttr(scale, "rawHigh", errors, where + "/Scale"),
                ScaledLow = DoubleAttr(scale, "scaledLow", errors, where + "/Scale"),
                ScaledHigh = DoubleAttr(scale, "scaledHigh", errors, where + "/Scale"),
                Clamp = ParseClamp(clamp == null ? null : (string?)clamp.Attribute("mode")),
                ClampLow = clamp == null
                    ? 0
                    : DoubleAttr(clamp, "low", errors, where + "/Scale/Clamp")
                      ?? DoubleAttr(clamp, "min", errors, where + "/Scale/Clamp") ?? 0,
                ClampHigh = clamp == null
                    ? 0
                    : DoubleAttr(clamp, "high", errors, where + "/Scale/Clamp")
                      ?? DoubleAttr(clamp, "max", errors, where + "/Scale/Clamp") ?? 0,
            };
        }

        var format = merged.Element("Format");
        if (format != null)
        {
            var fmt = new FormatConfig
            {
                Decimals = IntAttr(format, "decimals", errors, where + "/Format") ?? 0,
                Prefix = I18nText(config.I18n, (string?)format.Attribute("prefix")),
                Suffix = I18nText(config.I18n, (string?)format.Attribute("suffix")),
                Thousands = BoolAttr(format, "thousands", errors, where + "/Format") ?? false,
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
                Min = DoubleAttr(write, "min", errors, where + "/Write"),
                Max = DoubleAttr(write, "max", errors, where + "/Write"),
                Step = DoubleAttr(write, "step", errors, where + "/Write"),
                Permission = (string?)write.Attribute("permission"),
                Verify = BoolAttr(write, "verify", errors, where + "/Write") ?? false,
                Confirm = BoolAttr(write, "confirm", errors, where + "/Write") ?? false,
                PulseMs = IntAttr(write, "pulseMs", errors, where + "/Write") ?? 0,
            };
        }

        var stringOptions = merged.Element("String");
        if (stringOptions != null)
        {
            point.StringEncoding = ((string?)stringOptions.Attribute("encoding") ?? "ascii").ToLowerInvariant();
            point.StringTrimNull = BoolAttr(stringOptions, "trimNull", errors, where + "/String") ?? true;

            // 补齐口径（docs/01 §4.3）：padding = 设备用的补齐字节，left = 靠左（补齐在右）还是靠右。
            point.StringPadding = ParsePadding(stringOptions, errors, where + "/String") ?? 0;
            point.StringPadLeft = BoolAttr(stringOptions, "left", errors, where + "/String") ?? true;

            // CGV-29：perByte（字节紧凑排列）与早期草案的 byteAligned 都不实现——写了直接报错，
            // 绝不静默忽略（用户 2026-09-16 决定：不做 perByte）。
            foreach (var removed in new[] { "perByte", "byteAligned" })
            {
                if (stringOptions.Attribute(removed) != null)
                {
                    errors.Add($"{where}/String：String@{removed} 未实现，请勿使用（字符串按寄存器对齐，字节紧凑排列不在框架范围）");
                }
            }
        }

        var bcd = merged.Element("Bcd");
        if (bcd != null) point.BcdDigits = IntAttr(bcd, "digits", errors, where + "/Bcd") ?? 4;

        var dateTime = merged.Element("DateTime");
        if (dateTime != null) point.DateTimeFormat = ((string?)dateTime.Attribute("format") ?? "plc6").ToLowerInvariant();

        // 点位脚本（ADR D41）：解码路径把原始寄存器/原始数值交给 JS，脚本返回值即工程值。
        // 计算点同样用它（脚本型计算点），所以这里不区分点位形态。
        var script = merged.Element("Script");
        if (script != null)
        {
            var scriptWhere = where + "/Script";
            point.Script = script.Value.Trim();
            point.ScriptLanguage = ((string?)script.Attribute("language") ?? "js").Trim().ToLowerInvariant();
            point.ScriptTimeoutMs = IntAttr(script, "timeoutMs", errors, scriptWhere);
            point.ScriptOnError = (string?)script.Attribute("onError");
            ValidatePointScript(script, scriptWhere, errors);
        }

        foreach (var alarm in merged.Elements("Alarm"))
        {
            point.Alarms.Add(new AlarmConfig
            {
                Id = (string?)alarm.Attribute("id") ?? string.Empty,
                Type = ((string?)alarm.Attribute("type") ?? "high").ToLowerInvariant(),
                Limit = DoubleAttr(alarm, "limit", errors, where + "/Alarm"),
                DelayMs = IntAttr(alarm, "delayMs", errors, where + "/Alarm") ?? 0,
                Deadband = DoubleAttr(alarm, "deadband", errors, where + "/Alarm") ?? 0,
                Priority = (string?)alarm.Attribute("priority"),
                Message = I18nText(config.I18n, (string?)alarm.Attribute("message")),
                Latch = BoolAttr(alarm, "latch", errors, where + "/Alarm") ?? false,
                AckRequired = BoolAttr(alarm, "ackRequired", errors, where + "/Alarm") ?? false,
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
                    Address = IntAttr(slice, "address", errors, where + "/Slices/Slice") ?? 0,
                    Length = IntAttr(slice, "length", errors, where + "/Slices/Slice") ?? 1,
                });
            }

            if (list.Count > 0) point.Slices = list;
        }

        var bits = merged.Element("Bits");
        if (bits != null)
        {
            var entries = new List<BitMapEntry>();
            foreach (var entry in bits.Elements())
            {
                if (entry.Name == "Bit") entries.Add(ParseBitEntry(entry, config, errors, where));
                else if (entry.Name == "Field") entries.Add(ParseFieldEntry(entry, config, errors, where));
                else errors.Add($"{where}/Bits：未知条目 <{entry.Name.LocalName}>（只支持 <Bit> 与 <Field>）");
            }

            if (entries.Count > 0) point.Bits = entries;
        }

        // CGV-30：bitRange 必须能解析成 0..15 且 from ≤ to（此前非法值被静默当成"不声明位"）
        ValidateBitRange(merged, id!, pointSetId, errors);

        return point;
    }

    /// <summary>&lt;Bits&gt;/&lt;Bit&gt; 条目：单一位（index 必填、name 必填、text 可选）。</summary>
    private static BitMapEntry ParseBitEntry(
        XElement entry, SamplerConfiguration config, List<string> errors, string where)
    {
        var entryWhere = where + "/Bits/Bit";
        var result = new BitMapEntry
        {
            Index = IntAttr(entry, "index", errors, entryWhere),
            Name = ParseBitName(entry, errors, entryWhere),
            Text = I18nText(config.I18n, (string?)entry.Attribute("text")),
        };

        if (result.Index == null && entry.Attribute("index") == null)
        {
            errors.Add($"{entryWhere}：缺少 index（位号 0..15）");
        }

        if (result.Text == null) result.Text = result.Name;
        return result;
    }

    /// <summary>&lt;Bits&gt;/&lt;Field&gt; 条目：连续位域 from..to（含端点）+ 可选 &lt;Map&gt; 枚举显示。</summary>
    private static BitMapEntry ParseFieldEntry(
        XElement entry, SamplerConfiguration config, List<string> errors, string where)
    {
        var entryWhere = where + "/Bits/Field";
        var result = new BitMapEntry
        {
            From = IntAttr(entry, "from", errors, entryWhere),
            To = IntAttr(entry, "to", errors, entryWhere),
            Name = ParseBitName(entry, errors, entryWhere),
            Text = I18nText(config.I18n, (string?)entry.Attribute("text")),
        };

        if (result.From == null && entry.Attribute("from") == null)
        {
            errors.Add($"{entryWhere}：缺少 from（起位 0..15）");
        }

        if (result.To == null && entry.Attribute("to") == null)
        {
            errors.Add($"{entryWhere}：缺少 to（止位 0..15，含端点）");
        }

        foreach (var item in entry.Elements("Map").Elements("Item"))
        {
            var key = (string?)item.Attribute("key");
            if (key != null) result.Map[key] = item.Value;
        }

        if (result.Text == null) result.Text = result.Name;
        return result;
    }

    /// <summary>位名（子点位 id 后缀）：必填且不得含 . / 空白（否则拼出的 id 无法与"点表/点位"定位区分）。</summary>
    private static string ParseBitName(XElement entry, List<string> errors, string entryWhere)
    {
        var name = ((string?)entry.Attribute("name") ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            errors.Add($"{entryWhere}：缺少 name（位名，子点位 id = 整字点位id.位名）");
            return string.Empty;
        }

        if (name.IndexOf('.') >= 0 || name.IndexOf('/') >= 0 || name.Any(char.IsWhiteSpace))
        {
            errors.Add($"{entryWhere}：name=\"{name}\" 非法（位名不得含 . / 空白，它要拼进子点位 id）");
        }

        return name;
    }

    /// <summary>
    /// CGV-30：`bitRange` 必须是 `from-to` 且 0 ≤ from ≤ to ≤ 15。
    /// 此前非法写法被静默当成「不声明位」（整字点位），会悄悄读出错误量纲的值。
    /// </summary>
    private static void ValidateBitRange(XElement merged, string id, string pointSetId, List<string> errors)
    {
        var raw = (string?)merged.Attribute("bitRange");
        if (raw == null) return;

        var where = $"点位 {id}（PointSet {pointSetId}）";
        var parts = raw.Split('-');
        if (parts.Length == 2
            && int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var from)
            && int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var to))
        {
            if (from < 0 || to > 15 || from > to)
            {
                errors.Add($"{where}：bitRange=\"{raw}\" 非法（应为 from-to 且 0 ≤ from ≤ to ≤ 15，含端点）");
            }

            return;
        }

        errors.Add($"{where}：bitRange=\"{raw}\" 非法（应为 from-to，如 4-7；含端点、限 0..15）");
    }

    /// <summary>CGV-29：String@padding 解析（支持 0x20 / 32 两种写法），非法值报错并返回 null。</summary>
    private static int? ParsePadding(XElement element, List<string> errors, string where)
    {
        var attr = element.Attribute("padding");
        if (attr == null) return null;

        var text = attr.Value.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(text.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex)
            && hex >= 0 && hex <= 0xFF)
        {
            return hex;
        }

        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dec) && dec >= 0 && dec <= 0xFF)
        {
            return dec;
        }

        errors.Add($"{where}：padding 非法值 \"{text}\"（应为补齐字节 0x00..0xFF，可写 0x20 或 32）");
        return null;
    }

    /// <summary>地址换算：addrFormat=plc 时按 3xxxx/4xxxx/1xxxx/0xxxx 转数据区 + 0 基偏移，只换算这一次。</summary>
    private static int ResolveAddress(XElement pointElement, List<string> errors, string pointPath)
    {
        var raw = IntAttr(pointElement, "address", errors, pointPath);
        if (raw == null)
        {
            if (pointElement.Attribute("address") == null)
                errors.Add($"{pointPath} 缺少 address");
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

        errors.Add($"{pointPath} 的 PLC 地址 {value} 无法识别区段（应形如 40001/30001/10001/1）");
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

    /// <summary>CGV-22：mode 属性非法值报错（auto/onDemand/once），不静默回落成 auto。</summary>
    private static void ValidateMode(XElement element, List<string> errors, string where)
    {
        var raw = (string?)element.Attribute("mode");
        if (raw != null && ParseMode(raw) == null)
        {
            errors.Add($"{where}：mode 非法值 \"{raw}\"（应为 auto/onDemand/once）");
        }
    }

    /// <summary>
    /// 点位的有效字长（寄存器/位数）：显式 length 优先，其余按 dataType 推导。
    /// **&lt;Slices&gt; 片段点位以片段长度之和为准**（解码输入就是按片段拼起来的那串寄存器，
    /// docs/01 §4.2）：与运行期切片、块窗口校验共用这一个口径。
    /// BCD 与 datetime 的字长按类型参数推导（ADR D34），必须与
    /// <c>PointCodec.DecodeBcd</c> / <c>DecodeDateTime</c> 的实际读取需求一致；
    /// 校验（CGV-8）、块窗口校验与运行时 <c>RuntimePoint.Length</c> 共用这一个函数，避免两处各写一份。
    /// </summary>
    internal static int EffectiveLength(PointConfig point)
    {
        if (point.Slices is { Count: > 0 })
        {
            var total = 0;
            foreach (var slice in point.Slices) total += slice.Length < 1 ? 1 : slice.Length;
            return total;
        }

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

    /// <summary>
    /// 读取模式（Point@mode / Block@mode）：auto（默认，周期读）| onDemand（只手动触发）| once（Start 后读一次）。
    /// 解析不出返回 null（由 <c>ValidateEnum</c> 报非法值），比较一律用归一后的小写形式。
    /// </summary>
    internal static string? ParseMode(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var mode = text!.Trim().ToLowerInvariant();
        return mode is "auto" or "ondemand" or "once" ? mode : null;
    }
}
