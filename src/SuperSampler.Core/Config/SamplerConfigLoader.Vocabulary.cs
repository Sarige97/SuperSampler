using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

namespace SuperSampler.Core.Config;

/// <summary>
/// <see cref="SamplerConfigLoader"/> 的**词汇校验**（findings D53，CGV-34）：
/// 「拼错的元素名 / 属性名被静默忽略」——例如 <c>&lt;Ponits&gt;</c>（Points 拼错）会让点表变成空点表，
/// 加载期零错误零告警，宿主拿到一个没有任何点位的设备；<c>intervallMs="500"</c>（intervalMs 拼错）
/// 同样是「写了不生效」却毫无提示。ADR D30「声明即生效」要求这种情况必须被报出来。
///
/// <para>
/// 处置口径（未知**元素**是结构性错误，未知**属性**走既有 <c>unsupportedPolicy</c> 通道）：
/// <list type="bullet">
/// <item>未知元素、以及出现在错误父节点下的已知元素 → **加载期错误**（该元素一定不会被读取：
/// 与「未实现段」不同，后者是设计上先留位，前者是拼写/结构错误）。发现未知元素后不深入其子树，
/// 避免一个拼错的名字带出几十条级联错误。</item>
/// <item>未知属性 → 按 <c>HostConfig@unsupportedPolicy</c> 处置：<c>warn</c>（默认，记 <c>Warnings</c>）/
/// <c>error</c>（拒绝加载）/ <c>ignore</c>（静默），与「未实现段/未消费属性」共用一条通道（CGV-24）。</item>
/// <item>未实现的整段（<c>Storage</c>/<c>Ui</c>/<c>Users</c>/<c>Commands</c>/<c>Drivers</c>/<c>Parsers</c>、
/// <c>Diagnostics/Trace|Events|Recovery</c>、<c>Transport/Reconnect</c>、<c>Point/History</c>、
/// <c>Point/Tags</c>、<c>Device/Simulate</c>）**不深入校验**：整段按 C 类机制告警，
/// 其内部结构属于后续迭代/宿主自定，深入校验只会制造噪声与误报。</item>
/// <item><c>&lt;Comment&gt;</c> 是全文档通用的注释元素（加载器不读），任何位置都允许。</item>
/// </list>
/// </para>
/// 已删除/已改名的旧写法（<c>scanGroup</c>/<c>rateMs</c>/<c>mergeGap</c>/<c>perByte</c> 等）
/// 由各自的专用规则给出更精确的错误文案，这里跳过不重复报（见 <see cref="HandledByDedicatedRules"/>）。
/// </summary>
public static partial class SamplerConfigLoader
{
    /// <summary>根元素允许的两个名字：文档口径 <c>HostConfig</c>，历史/测试里通用的 <c>SamplerConfig</c>。</summary>
    private static readonly HashSet<string> RootElementNames =
        new(new[] { "HostConfig", "SamplerConfig" }, StringComparer.Ordinal);

    /// <summary>
    /// 由专用规则（CGV-25/26/29/32）给出更精确错误文案的属性名——词汇校验跳过，
    /// 避免同一条问题报两遍（一条「已删除/未实现」+ 一条「未知属性」）。
    /// </summary>
    private static readonly HashSet<string> HandledByDedicatedRules = new(StringComparer.Ordinal)
    {
        // CGV-25：已删除/已改名
        "scanGroup", "rateMs", "mergeGap", "maxRegistersPerRead", "maxBitsPerRead",
        // CGV-26：Reconnect 早期属性名
        "initialDelayMs", "maxDelayMs", "keepAliveMs", "keepAliveMode", "flushRx", "resetTxn", "failInFlight", "resetRetry",
        // CGV-32：脚本已删除属性
        "maxMemoryMb",
        // CGV-29：字符串未实现属性
        "perByte", "byteAligned",
    };

    /// <summary>
    /// 已知元素与属性清单。键有两种形式：
    /// <list type="bullet">
    /// <item><c>Name</c>：任意父节点下的默认规格；</item>
    /// <item><c>Parent/Name</c>：同名元素在不同父节点下的专属规格（如 <c>Retry</c> / <c>Script</c> / <c>Tags</c>）。</item>
    /// </list>
    /// 元素按 <c>LocalName</c> 比对（文档不使用 XML 命名空间）。
    /// </summary>
    private static readonly Dictionary<string, ElementSpec> XmlVocabulary = BuildVocabulary();

    /// <summary>
    /// CGV-34：扫全文，未知元素 → 错误；未知属性 → 按 <c>unsupportedPolicy</c> 处置。
    /// </summary>
    internal static void ValidateVocabulary(XElement root, SamplerConfiguration config, List<string> errors)
    {
        if (!RootElementNames.Contains(root.Name.LocalName))
        {
            errors.Add($"配置根元素是 <{root.Name.LocalName}>：应为 <HostConfig>（schemaVersion=\"3.0\"）；"
                + "该根元素名不会被读取，请检查拼写");
        }

        CheckAttributes(root, config, errors, isRoot: true);

        var spec = Lookup(root.Name.LocalName, null);
        Walk(root, spec, config, errors);
    }

    private static void Walk(XElement element, ElementSpec? spec, SamplerConfiguration config, List<string> errors)
    {
        foreach (var child in element.Elements())
        {
            var name = child.Name.LocalName;

            // <Comment> 是全文档通用的注释元素（加载器不读、宿主自行消费），任何位置都允许
            if (name == "Comment") continue;

            var childSpec = Lookup(name, element.Name.LocalName);
            if (childSpec == null)
            {
                errors.Add($"{XmlPathOf(child)}：未知元素 <{name}>（拼写错误或该段不属于 schemaVersion 3.x；"
                    + "它不会被读取——段落清单见 Config/配置字段说明.md）");
                continue;
            }

            if (spec?.Children != null && !spec.Children.Contains(name))
            {
                errors.Add($"{XmlPathOf(child)}：元素 <{name}> 不能出现在 <{element.Name.LocalName}> 内（不会被读取，请检查层级）");
                continue;
            }

            CheckAttributes(child, config, errors, isRoot: false);

            // Children == null 表示「不深入校验的整段」（未实现段 / 宿主自定结构）
            if (childSpec.Children == null) continue;
            Walk(child, childSpec, config, errors);
        }
    }

    private static void CheckAttributes(
        XElement element, SamplerConfiguration config, List<string> errors, bool isRoot)
    {
        var spec = Lookup(element.Name.LocalName, isRoot ? null : element.Parent?.Name.LocalName);
        if (spec?.Attributes == null) return;

        foreach (var attribute in element.Attributes())
        {
            if (attribute.IsNamespaceDeclaration) continue;

            var name = attribute.Name.LocalName;
            if (spec.Attributes.Contains(name)) continue;
            if (HandledByDedicatedRules.Contains(name)) continue;

            var where = isRoot ? "<" + element.Name.LocalName + ">" : XmlPathOf(element);
            var message = $"{where}：未知属性 {name}=\"{AttributePreview(attribute)}\"（不会被读取，请检查拼写）";

            switch (config.UnsupportedPolicy)
            {
                case "ignore":
                    continue;
                case "error":
                    errors.Add(message + "（unsupportedPolicy=error）");
                    continue;
                default:
                    config.Warnings.Add(message + "（unsupportedPolicy=warn：已忽略）");
                    continue;
            }
        }
    }

    /// <summary>按「父/子」优先、裸名兜底查找规格；找不到返回 null（= 未知元素）。</summary>
    private static ElementSpec? Lookup(string name, string? parentName)
    {
        if (parentName != null && XmlVocabulary.TryGetValue(parentName + "/" + name, out var scoped)) return scoped;
        return XmlVocabulary.TryGetValue(name, out var global) ? global : null;
    }

    private static string AttributePreview(XAttribute attribute)
    {
        var value = attribute.Value ?? string.Empty;
        return value.Length <= 32 ? value : value.Substring(0, 32) + "…";
    }

    /// <summary>
    /// 元素定位串：<c>PointSets/PointSet@ps1/Points/Point@p</c> 形式，最多回溯 4 层
    /// （与 <see cref="ElementPath"/> 同口径，但**始终**带元素名——未知元素没有 id 可依赖）。
    /// </summary>
    private static string XmlPathOf(XElement element)
    {
        var parts = new List<string>();
        for (var e = element; e != null && parts.Count < 4; e = e.Parent)
        {
            var id = (string?)e.Attribute("id");
            parts.Insert(0, id == null ? e.Name.LocalName : e.Name.LocalName + "@" + id);
        }

        return string.Join("/", parts);
    }

    // ═══════════════ 词汇表 ═══════════════

    /// <summary>一个元素的允许属性集与允许子元素集；两者为 null 表示「不深入校验」。</summary>
    private sealed class ElementSpec
    {
        private ElementSpec(HashSet<string>? attributes, HashSet<string>? children)
        {
            Attributes = attributes;
            Children = children;
        }

        public HashSet<string>? Attributes { get; }

        public HashSet<string>? Children { get; }

        /// <summary>允许这些属性，只允许这些子元素（<paramref name="children"/> 为空串 = 叶子元素）。</summary>
        public static ElementSpec Of(string attributes, string children)
            => new(Split(attributes), Split(children));

        /// <summary>整段不深入校验（未实现段 / 宿主自定结构）：属性与子树都跳过。</summary>
        public static ElementSpec Skip() => new(null, null);

        private static HashSet<string> Split(string text)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in text.Split(','))
            {
                var name = item.Trim();
                if (name.Length > 0) set.Add(name);
            }

            return set;
        }
    }

    private static Dictionary<string, ElementSpec> BuildVocabulary()
    {
        var map = new Dictionary<string, ElementSpec>(StringComparer.Ordinal);

        void Of(string element, string attributes, string children) => map[element] = ElementSpec.Of(attributes, children);
        void Leaf(string element, string attributes) => map[element] = ElementSpec.Of(attributes, string.Empty);
        void Skip(string element) => map[element] = ElementSpec.Skip();

        // ── 根与元信息 ──
        var hostConfig = ElementSpec.Of(
            "schemaVersion,unsupportedPolicy",
            "Meta,Global,I18n,Transports,Devices,DeviceTemplates,PointTemplates,PointSets,AlarmClasses,"
            + "Commands,Diagnostics,Drivers,Storage,Ui,Users,Parsers");
        map["HostConfig"] = hostConfig;
        map["SamplerConfig"] = hostConfig;

        Of("Meta", string.Empty, "ProjectName,Comment,Author,CreatedAt,Revision,Tags");
        Leaf("ProjectName", string.Empty);
        Leaf("Author", string.Empty);
        Leaf("CreatedAt", string.Empty);
        Leaf("Revision", string.Empty);
        Of("Meta/Tags", string.Empty, "Tag");
        Of("Tags", string.Empty, "Tag");
        Leaf("Tag", string.Empty);

        // ── 全局 ──
        Of("Global", "language,fallbackLanguage,timeZone,nullText,swap", "Polling,Retry,Scheduler,Script,Reconnect,Quality");
        Leaf("Global/Polling", "defaultIntervalMs,requestTimeoutMs");
        Leaf("Polling", "defaultIntervalMs,requestTimeoutMs");
        Leaf("Global/Retry", "count,intervalMs,backoff,escalateAfter,budgetMs,maxBackoffMs,onLinkError,onException");
        Leaf("Retry", "count,intervalMs,backoff,escalateAfter,budgetMs,maxBackoffMs,onLinkError,onException");
        Leaf("Global/Scheduler", "groupLimitRegisters,groupLimitBits,ignoreGap");
        Leaf("Scheduler", "groupLimitRegisters,groupLimitBits,ignoreGap");
        Leaf("Global/Script", "language,timeoutMs,onError");
        Leaf("Script", "language,timeoutMs,onError");
        Leaf("Global/Reconnect", "enabled,delays,manualRetry,offlineQuality");
        Leaf("Reconnect", "enabled,delays,manualRetry,offlineQuality");
        Leaf("Global/Quality", "onCommError,onCommErrorValue,staleAfterMs");
        Leaf("Quality", "onCommError,onCommErrorValue,staleAfterMs");
        Of("Diagnostics", "allowRawAccess", "Trace,Events,Recovery,Statistics");
        Skip("Diagnostics/Trace");
        Skip("Diagnostics/Events");
        Skip("Diagnostics/Recovery");
        Skip("Diagnostics/Statistics");

        // ── i18n ──
        Of("I18n", string.Empty, "Files");
        Of("Files", string.Empty, "File");
        Leaf("File", "path,lang");

        // ── 链路 ──
        Of("Transports", string.Empty, "Transport");
        Of("Transport",
            "id,variant,enabled,host,port,portName,baudRate,dataBits,parity,stopBits,connectTimeoutMs,requestTimeoutMs,"
            + "gapMs,handshake,dtr,rts,readTimeoutMs,writeTimeoutMs,driver,maxConcurrent",
            "Retry,Reconnect");
        Leaf("Transport/Retry", "count,intervalMs");
        Skip("Transport/Reconnect");

        // ── 设备 ──
        Of("Devices", string.Empty, "Device");
        var device = ElementSpec.Of(
            "id,template,name,desc,enabled,transport,unitId,pointSet,swap,requestTimeoutMs,generateDiagnostics",
            "Retry,Pause,Simulate");
        map["Device"] = device;
        map["Devices/Device"] = device;
        map["DeviceTemplates/Device"] = device;
        Of("DeviceTemplates", string.Empty, "Device");
        Leaf("Device/Retry", "count,intervalMs");
        Leaf("Device/Pause", "maintenance");
        Skip("Device/Simulate");

        // ── 点表 / 块 / 点位 ──
        Of("PointSets", string.Empty, "PointSet");
        Of("PointSet", "id", "Defaults,Blocks,Points,Calculated,CalculatedPoints");
        Leaf("PointSet/Defaults", "area,dataType,swap,unitId,access");
        Leaf("Defaults", "area,dataType,swap,unitId,access");
        Of("Blocks", string.Empty, "Block");
        Of("Block", "id,area,start,count,intervalMs,mode,unitId,swap,enabled", "Point");
        Of("Points", string.Empty, "Point");
        Of("Calculated", string.Empty, "Point");
        Of("CalculatedPoints", string.Empty, "Point");
        Of("PointTemplates", string.Empty, "Point");

        var point = ElementSpec.Of(
            "id,template,name,area,dataType,swap,intervalMs,mode,access,unit,unitId,length,bit,bitRange,enabled,"
            + "desc,readonlyBy,range,address,addrFormat",
            "Scale,Format,Write,String,Bcd,DateTime,Script,Alarm,Slices,Bits,History,Tags,Expression,DependsOn");
        map["Point"] = point;
        map["Points/Point"] = point;
        map["Block/Point"] = point;
        map["Calculated/Point"] = point;
        map["CalculatedPoints/Point"] = point;
        map["PointTemplates/Point"] = point;

        Of("Scale", "factor,offset,rawLow,rawHigh,scaledLow,scaledHigh,mode", "Clamp");
        Leaf("Clamp", "mode,low,high,min,max");
        Leaf("Scale/Clamp", "mode,low,high,min,max");
        Of("Format", "decimals,prefix,suffix,thousands,mapOn,pattern", "Map");
        Of("Map", string.Empty, "Item");
        Leaf("Item", "key");
        Leaf("Write", "min,max,step,permission,verify,confirm,pulseMs");
        Leaf("String", "encoding,trimNull,padding,left");
        Leaf("Bcd", "digits");
        Leaf("DateTime", "format");
        Leaf("Point/Script", "language,timeoutMs,onError");
        Leaf("Alarm", "id,type,limit,delayMs,deadband,priority,message,latch,ackRequired");
        Of("Slices", string.Empty, "Slice");
        Leaf("Slice", "address,length");
        Of("Bits", string.Empty, "Bit,Field");
        Of("Bit", "index,name,text", "Map");
        Of("Field", "from,to,name,text", "Map");
        Of("DependsOn", string.Empty, "PointRef");
        Leaf("PointRef", string.Empty);
        Leaf("Expression", string.Empty);
        Skip("Point/History");
        Skip("Point/Tags");

        // ── 报警等级 ──
        Of("AlarmClasses", string.Empty, "AlarmClass");
        Leaf("AlarmClass", "id,name,color,sound,escalateAfterMs");

        // ── 未实现整段（整段按 unsupportedPolicy 告警，不深入校验内部结构）──
        Skip("Commands");
        Skip("Drivers");
        Skip("Storage");
        Skip("Ui");
        Skip("Users");
        Skip("Parsers");

        return map;
    }
}
