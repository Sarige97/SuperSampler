using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace SuperSampler.Core.Config;

/// <summary>
/// 配置校验失败：携带全部错误行，一次性报完，避免改一条跑一次。
/// <para>
/// findings W70：同时携带**本次加载已经收集到的告警**（<see cref="SamplerConfiguration.Warnings"/> 的同一次快照：
/// 未实现段 / 未消费属性 / 未实现的链路变体等）。此前「这次加载有错」的宿主只看得到 <see cref="Errors"/>，
/// 告警随半成品配置一起丢弃——于是「改错 → 又冒出告警」要跑两轮。现在一次拿全：
/// 先按 Errors 改到零错误，Warnings 里剩下的就是刻意保留的未实现项。
/// </para>
/// </summary>
public sealed class ConfigValidationException : Exception
{
    public ConfigValidationException(IReadOnlyList<string> errors, IReadOnlyList<string> warnings)
        : base("配置校验失败：" + Environment.NewLine + string.Join(Environment.NewLine, errors))
    {
        Errors = errors ?? throw new ArgumentNullException(nameof(errors));
        Warnings = warnings ?? throw new ArgumentNullException(nameof(warnings));
    }

    /// <summary>全部错误行（每条带定位；「一次报全」的载体）。</summary>
    public IReadOnlyList<string> Errors { get; }

    /// <summary>
    /// 同一次加载收集到的告警（单条聚合文本，受 <c>HostConfig@unsupportedPolicy</c> 控制）；
    /// 加载因错误失败时同样可读——告警不再随半成品配置一起丢（findings W70）。
    /// </summary>
    public IReadOnlyList<string> Warnings { get; }
}

/// <summary>
/// XML 到运行时配置的加载器（解析主体）。**两阶段加载**（docs/11 §二）：
/// <list type="number">
/// <item><b>阶段一 解析 + 全量校验</b>：把 XML 解析成配置模型，同时把**全部**矛盾收集进错误列表，
/// 绝不「遇到第一个就停」——非法数值/布尔、引用悬空、块窗口不符，以及全量规则
/// （块内点位不连续、同址同位重复声明、地址越出区容量、计算点成环、报警限值矛盾）都在这一阶段汇总。</item>
/// <item><b>阶段二 交付</b>：<see cref="Load"/> 只在零错误时给配置置上
/// <see cref="SamplerConfiguration.IsValidated"/> 并返回；有错误则抛
/// <see cref="ConfigValidationException"/>（携带全部错误行）。
/// 运行时对象（<c>PointRegistry</c>/<c>Scheduler</c>/<c>SamplerEngine</c>）构造时强制检查该校验标记，
/// 所以「全部校验通过之前」不可能出现半成品运行时状态。</item>
/// </list>
/// 完成四件继承/换算：模板继承、PointSet Defaults 继承链、PLC 编址换算、i18n 替换。
/// 点位/块解析与枚举换算工具在 partial 续文件 SamplerConfigLoader.Points.cs；
/// 跨实体引用校验与全量规则在 SamplerConfigLoader.Validation.cs。
/// </summary>
public static partial class SamplerConfigLoader
{
    /// <summary>
    /// 从 XML 文件加载。文件读不动或不是合法 XML 时同样按配置错误报出，而不是裸 XmlException
    /// （路径不存在/无权限也一并包装成带定位的配置错误，宿主拿到的永远是同一种异常）。
    /// </summary>
    public static SamplerConfiguration LoadFromXml(string xmlPath)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Load(xmlPath);
        }
        catch (XmlException ex)
        {
            throw new ConfigValidationException(
                new[] { $"配置文件不是合法 XML：{xmlPath}（{ex.Message}）" }, Array.Empty<string>());
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            throw new ConfigValidationException(
                new[] { $"配置文件读不到：{xmlPath}（{ex.Message}）" }, Array.Empty<string>());
        }

        var baseDir = Path.GetDirectoryName(Path.GetFullPath(xmlPath)) ?? string.Empty;
        return Load(doc, baseDir);
    }

    /// <summary>
    /// 两阶段加载：阶段一解析并全量校验（收集全部错误），阶段二零错误才交付可用配置。
    /// </summary>
    public static SamplerConfiguration Load(XDocument doc, string baseDirectory)
    {
        var errors = new List<string>();

        // ── 阶段一：解析 + 全量校验（收集全部错误，任何解析异常都不许中断收集）──
        var config = ParseAndValidate(doc, baseDirectory, errors);

        // ── 阶段二：零错误才交付（运行时对象由调用方/引擎构造，构造时再校验一次 IsValidated）──
        // findings W70：有错误时把「本次已收集到的告警」一并交给异常——告警不再随半成品配置丢弃。
        if (errors.Count > 0) throw new ConfigValidationException(errors, config.Warnings);

        config.IsValidated = true;
        return config;
    }

    /// <summary>
    /// 阶段一：解析 XML 并跑完全部加载期校验，错误逐条追加进 <paramref name="errors"/>。
    /// 返回值在 errors 非空时是**半成品**（缺点位/缺引用），只可用于诊断，
    /// 不得用于构造运行时对象——它没有被置上 <see cref="SamplerConfiguration.IsValidated"/>。
    /// </summary>
    private static SamplerConfiguration ParseAndValidate(XDocument doc, string baseDirectory, List<string> errors)
    {
        var config = new SamplerConfiguration();

        var root = doc.Root;
        if (root == null)
        {
            // 没有根元素就没有可校验的内容，唯一一条错误即全部
            errors.Add("XML 无根元素：无法解析配置");
            return config;
        }

        config.UnsupportedPolicy = ((string?)root.Attribute("unsupportedPolicy") ?? "warn").ToLowerInvariant();

        // findings W1：schemaVersion 必填且必须受支持，不能静默跑
        var schemaVersion = (string?)root.Attribute("schemaVersion");
        if (string.IsNullOrEmpty(schemaVersion))
        {
            errors.Add("HostConfig 缺少 schemaVersion（必填）");
        }
        else if (!schemaVersion!.StartsWith("3.", StringComparison.Ordinal))
        {
            errors.Add($"schemaVersion \"{schemaVersion}\" 不受支持（当前支持 3.x）");
        }

        // ADR D39：i18n 要按 Global@language 选文件，故 Global 必须先于 I18n 加载
        Collect(errors, "Global", () => LoadGlobal(root, config.Global, errors));
        Collect(errors, "I18n", () => LoadI18n(root, baseDirectory, config.I18n, config.Global));
        Collect(errors, "Meta", () => LoadMeta(root, config.Meta, config.I18n, errors));
        Collect(errors, "Transports", () => LoadTransports(root, config.Transports, errors));

        var deviceTemplates = new Dictionary<string, XElement>(StringComparer.Ordinal);
        var pointTemplates = new Dictionary<string, XElement>(StringComparer.Ordinal);
        Collect(errors, "DeviceTemplates", () => deviceTemplates = ParseDeviceTemplates(root, errors));
        Collect(errors, "PointTemplates", () => pointTemplates = ParsePointTemplates(root, errors));
        Collect(errors, "Devices", () => LoadDevices(root, config, deviceTemplates, errors));
        Collect(errors, "PointSets", () => LoadPointSets(root, config, pointTemplates, errors));

        Collect(errors, "引用校验", () => ValidateReferences(root, config, errors));
        Collect(errors, "结构校验", () => Validate(config, errors));
        Collect(errors, "全量规则", () => ValidateFullRules(config, errors));
        Collect(errors, "词汇校验", () => ValidateVocabulary(root, config, errors));
        Collect(errors, "枚举与边界校验", () => ValidateXmlEnumsAndBounds(root, errors));
        Collect(errors, "已删除字段检测", () => ValidateRemovedFields(root, errors));
        Collect(errors, "未实现段检测", () => DetectUnsupported(root, config, errors));

        return config;
    }

    /// <summary>
    /// 阶段一的「继续收集」闸门：某一段解析意外抛异常时，把它转成一条错误后继续跑后面的段，
    /// 而不是让第一个异常吃掉后面全部校验结果（用户要求：一次性报出全部问题）。
    /// 常规非法值在各自解析点就已转成带定位的错误，这里是兜底。
    /// </summary>
    private static void Collect(List<string> errors, string stage, Action body)
    {
        try
        {
            body();
        }
        catch (ConfigValidationException ex)
        {
            errors.AddRange(ex.Errors);
        }
        catch (FormatException ex)
        {
            errors.Add($"{stage}：存在非法数值或布尔值（{ex.Message}）");
        }
        catch (OverflowException ex)
        {
            errors.Add($"{stage}：数值超出允许范围（{ex.Message}）");
        }
        catch (ArgumentException ex)
        {
            errors.Add($"{stage}：{ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            errors.Add($"{stage}：{ex.Message}");
        }
    }

    // ─────────────── 属性取值（非法值只记错误、不提前抛）───────────────
    //
    // 每个属性取值都带「哪个实体、哪个属性、为什么」的定位：宿主改配置时不用猜，
    // 且一条错误不会妨碍同一份配置里其它错误继续被收集（findings W32 的推广）。

    /// <summary>取整数属性；写错时报出实体与属性名并返回 null（让解析继续）。</summary>
    private static int? IntAttr(XElement element, string name, List<string> errors, string where)
    {
        var attr = element.Attribute(name);
        if (attr == null) return null;

        try
        {
            return (int?)attr;
        }
        catch (Exception ex) when (ex is FormatException || ex is OverflowException)
        {
            errors.Add($"{where}：属性 {name}=\"{attr.Value}\" 不是合法数值或布尔值（应为整数）");
            return null;
        }
    }

    /// <summary>取布尔属性；写错时报出实体与属性名并返回 null（让解析继续）。</summary>
    private static bool? BoolAttr(XElement element, string name, List<string> errors, string where)
    {
        var attr = element.Attribute(name);
        if (attr == null) return null;

        try
        {
            return (bool?)attr;
        }
        catch (Exception ex) when (ex is FormatException || ex is OverflowException)
        {
            errors.Add($"{where}：属性 {name}=\"{attr.Value}\" 不是合法数值或布尔值（应为 true/false）");
            return null;
        }
    }

    /// <summary>取浮点属性；写错时报出实体与属性名并返回 null（让解析继续）。</summary>
    private static double? DoubleAttr(XElement element, string name, List<string> errors, string where)
    {
        var attr = element.Attribute(name);
        if (attr == null) return null;

        try
        {
            return (double?)attr;
        }
        catch (Exception ex) when (ex is FormatException || ex is OverflowException)
        {
            errors.Add($"{where}：属性 {name}=\"{attr.Value}\" 不是合法数值或布尔值（应为数值）");
            return null;
        }
    }

    /// <summary>取「子元素文本」形式的整数（如 Meta/Revision）；写错时报出实体与元素名。</summary>
    private static int? IntElement(XElement parent, string name, List<string> errors, string where)
    {
        var child = parent.Element(name);
        if (child == null) return null;

        try
        {
            return (int?)child;
        }
        catch (Exception ex) when (ex is FormatException || ex is OverflowException)
        {
            errors.Add($"{where}：元素 {name} 的值 \"{child.Value}\" 不是合法数值或布尔值（应为整数）");
            return null;
        }
    }

    /// <summary>
    /// 宽松读取布尔属性：只有 true/1 当 true，非法值一律 false 且**不报错**
    /// （正式校验在 <c>BoolAttr</c> 处已报过，这里只做「有没有写」的检测，不许重复报也不许抛）。
    /// </summary>
    private static bool BoolOrFalse(XElement element, string name)
    {
        var attr = element.Attribute(name);
        if (attr == null) return false;

        var text = attr.Value.Trim();
        return string.Equals(text, "true", StringComparison.OrdinalIgnoreCase)
               || string.Equals(text, "1", StringComparison.Ordinal);
    }

    /// <summary>解析工程元信息（findings W2：此前整段未读）。</summary>
    private static void LoadMeta(XElement root, MetaOptions meta, I18nCatalog catalog, List<string> errors)
    {
        var m = root.Element("Meta");
        if (m == null) return;

        meta.ProjectName = I18nText(catalog, (string?)m.Element("ProjectName"));
        meta.Comment = (string?)m.Element("Comment");
        meta.Author = (string?)m.Element("Author");
        meta.CreatedAt = (string?)m.Element("CreatedAt");
        meta.Revision = IntElement(m, "Revision", errors, "Meta") ?? 0;

        foreach (var tag in m.Elements("Tags").Elements("Tag"))
        {
            var text = tag.Value.Trim();
            if (text.Length > 0) meta.Tags.Add(text);
        }
    }

    /// <summary>
    /// 未实现功能的显式化（findings C 类机制）：检测已知未实现的段/字段，
    /// 按 HostConfig@unsupportedPolicy 处理：warn（默认，记入 Warnings）| error（加载失败）| ignore。
    /// 目的：绝不让「写了不生效」静默存在。
    /// </summary>
    private static void DetectUnsupported(XElement root, SamplerConfiguration config, List<string> errors)
    {
        if (config.UnsupportedPolicy == "ignore") return;

        var found = new List<string>();

        void CheckSection(XElement? element, string what)
        {
            if (element != null) found.Add(what);
        }

        CheckSection(root.Element("Drivers"), "Drivers（驱动声明：驱动由 variant 决定，无需声明）");
        CheckSection(root.Element("Storage"), "Storage（历史/事件存储，后续迭代）");
        CheckSection(root.Element("Ui"), "Ui（界面组态，后续迭代）");
        CheckSection(root.Element("Users"), "Users（用户权限，后续迭代）");
        CheckSection(root.Element("Commands"), "Commands（命令执行引擎，后续迭代）");
        CheckSection(root.Element("Diagnostics")?.Element("Trace"), "Diagnostics/Trace（框架内部追踪，后续迭代）");
        CheckSection(root.Element("Diagnostics")?.Element("Events"), "Diagnostics/Events（事件总线策略，后续迭代）");
        CheckSection(root.Element("Diagnostics")?.Element("Recovery"), "Diagnostics/Recovery（离线恢复，后续迭代）");
        // Global/Script 与 Point/Script 已于 2026-09-16 正式接线（ADR D41）——不再是未实现段，见 LoadGlobal/ParsePointCore
        CheckSection(root.Element("Parsers"), "Parsers（脚本/自定义解码器，归宿主）");

        foreach (var point in DeclaredPointElements(root))
        {
            var id = (string?)point.Attribute("id") ?? "(未命名)";
            if (point.Element("History") != null) found.Add($"点位 {id} 的 History（历史归档，后续迭代）");
            if (point.Element("Tags") != null) found.Add($"点位 {id} 的 Tags（自定义元数据，后续迭代）");

            // 解析了但尚未被编解码消费的属性，同样显式告警而非静默（D30 原则）。
            // 注意：String@padding/@left 已于 2026-09-16 第七步接线（不再告警）；
            // String@perByte/@byteAligned 未实现，直接在解析点报错（CGV-29），不走这条告警。

            // D28：只有**非 linear** 的 mode 才算未实现；显式写 mode="linear"（受支持值）不该报警
            var scaleMode = (string?)point.Element("Scale")?.Attribute("mode");
            if (scaleMode != null && !string.Equals(scaleMode.Trim(), "linear", StringComparison.OrdinalIgnoreCase))
                found.Add($"点位 {id} 的 Scale@mode={scaleMode.Trim()}（缩放模式，v1 仅支持 linear）");
        }

        // 报警等级的展示属性（宿主负责渲染；框架只校验 @id 引用）
        foreach (var alarmClass in root.Elements("AlarmClasses").Elements("AlarmClass"))
        {
            if (alarmClass.Attribute("name") != null || alarmClass.Attribute("color") != null
                || alarmClass.Attribute("sound") != null || alarmClass.Attribute("escalateAfterMs") != null)
            {
                found.Add("AlarmClass@name/@color/@sound/@escalateAfterMs（等级展示属性由宿主消费，框架未使用）");
                break;
            }
        }

        foreach (var device in DeclaredDeviceElements(root))
        {
            var id = (string?)device.Attribute("id") ?? "(未命名)";
            if (BoolOrFalse(device, "generateDiagnostics"))
                found.Add($"设备 {id} 的 generateDiagnostics（自动诊断点，后续迭代）");
            if (device.Element("Simulate") != null) found.Add($"设备 {id} 的 Simulate（仿真模式，后续迭代）");
        }

        DetectUnconsumed(root, found);

        if (found.Count == 0) return;

        var message = "配置包含尚未实现的功能：" + string.Join("；", found);
        if (config.UnsupportedPolicy == "error")
        {
            errors.Add(message + "（unsupportedPolicy=error）");
        }
        else
        {
            config.Warnings.Add(message + "（unsupportedPolicy=warn：已忽略）");
        }
    }

    /// <summary>
    /// 声明了「点位属性」的全部 XML 来源：点表内的点位（散点 + 块内 + 计算点）与**点位模板**
    /// （模板属性会经 MergeAttributes 落到引用它的点位上，所以模板里的声明也要算「写了」）。
    /// </summary>
    private static IEnumerable<XElement> DeclaredPointElements(XElement root)
        => root.Elements("PointSets").Elements("PointSet").SelectMany(set => set.Descendants("Point"))
            .Concat(root.Elements("PointTemplates").Elements("Point"));

    /// <summary>声明了「设备属性」的全部 XML 来源：设备实例与设备模板（同 <see cref="DeclaredPointElements"/> 的口径）。</summary>
    private static IEnumerable<XElement> DeclaredDeviceElements(XElement root)
        => root.Elements("Devices").Elements("Device")
            .Concat(root.Elements("DeviceTemplates").Elements("Device"));

    /// <summary>
    /// ADR D30「声明即生效」：**解析进模型（或压根没解析）但没有任何运行期消费者**的属性，
    /// 写了必须显式告警，绝不静默忽略。与 <see cref="DetectUnsupported"/> 共用同一条告警通道
    /// （记入 <see cref="SamplerConfiguration.Warnings"/>，受 <c>HostConfig@unsupportedPolicy</c> 控制）。
    ///
    /// 口径：只列「确实写了」的属性——不写不告警（缺省值不产生噪声）。
    /// 已接线或明确归宿主消费（<c>Meta/*</c>、<c>Point@desc/@readonlyBy/@unit</c>、
    /// <c>Device@name/@desc</c>、<c>Global/Quality@staleAfterMs</c>）的字段**不在此列**。
    /// </summary>
    private static void DetectUnconsumed(XElement root, List<string> found)
    {
        void AddIf(XElement? element, string attribute, string message)
        {
            if (element?.Attribute(attribute) != null) found.Add(message);
        }

        // ── Global：解析进模型但无消费者 / 根本没解析 ──
        var global = root.Element("Global");
        AddIf(global, "timeZone",
            "Global@timeZone（时区换算未实现：框架无显示层、日期时间按机器本地时区解，时区显示归宿主）");

        var retry = global?.Element("Retry");
        AddIf(retry, "backoff",
            "Global/Retry@backoff（重试间隔策略未实现：重试间隔恒为 intervalMs；退避见 Global/Reconnect@delays）");
        AddIf(retry, "escalateAfter",
            "Global/Retry@escalateAfter（超时升级策略未实现：设备级→链路级升级由 Global/Reconnect 的两层退避承担）");
        AddIf(retry, "budgetMs",
            "Global/Retry@budgetMs（重试总预算未实现：驱动只按 Retry@count 计次，不做时间预算）");
        AddIf(retry, "maxBackoffMs",
            "Global/Retry@maxBackoffMs（未解析：退避上限由 Global/Reconnect@delays 队列本身表述）");
        AddIf(retry, "onLinkError",
            "Global/Retry@onLinkError（未解析：链路错不重试，直接交两层退避）");
        AddIf(retry, "onException",
            "Global/Retry@onException（未解析：异常码 05/06/0A 瞬时重试，其余永不重试）");

        // ── Transports：已删除的声明段 / 属性写了仍然被忽略 ──
        foreach (var transport in root.Elements("Transports").Elements("Transport"))
        {
            var transportId = (string?)transport.Attribute("id") ?? "(未命名)";
            if (transport.Attribute("driver") != null || transport.Attribute("maxConcurrent") != null)
            {
                found.Add($"链路 {transportId} 的 driver/maxConcurrent（已删除：驱动由 variant 表达，同链路恒为串行）");
            }

            // findings W79：udp/ascii 是「枚举合法但未实现」的变体——Scheduler.ParseVariant 把两者都映射到
            // TCP/MBAP 通道，现场按字面写 ascii 会以 MBAP 帧收发且**加载期一声不响**（静默行为缺口）。
            // 这里按 unsupportedPolicy 显式告警（warn 默认 / error 加载失败 / ignore 静默）。
            // 判据与 Scheduler.ParseVariant 的映射表对齐：只有 tcp / rtuovertcp / rtu 是真实现。
            var variant = ((string?)transport.Attribute("variant") ?? string.Empty).Trim().ToLowerInvariant();
            if (variant is "udp" or "ascii")
            {
                found.Add($"链路 {transportId} 的 variant=\"{variant}\"（未实现：当前按 TCP/MBAP 通道收发，"
                    + "帧格式与端口语义都不对；请改用 variant=\"tcp\"/\"rtuovertcp\"/\"rtu\"，或等该变体实现后再用）");
            }

            if (transport.Element("Reconnect") != null)
            {
                found.Add($"链路 {transportId} 的 Reconnect 段（未解析：退避参数只走 Global/Reconnect）");
            }
        }

        // ── 点位级：解析进模型但无运行期消费者的属性（逐种聚合，避免逐点刷屏）──
        // net46 无 ValueTuple：用「名称数组 + 同下标的消息数组」表达（同一处维护，不会错位）。
        var pointAttributeNames = new[] { "range" };
        var pointAttributeMessages = new[]
        {
            "Point@range（界面刻度/百分比死区未实现：量程只进模型，没有消费者）",
        };

        var writeAttributeNames = new[] { "confirm", "step", "permission" };
        var writeAttributeMessages = new[]
        {
            "Point/Write@confirm（二次确认是宿主界面动作，写管道不消费）",
            "Point/Write@step（步进是界面语义，写管道只做 min/max 范围校验，不做步进网格校验）",
            "Point/Write@permission（角色体系未实现：无角色可校验，写管道按放行处理）",
        };

        var hits = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var pointSet in root.Elements("PointSets").Elements("PointSet"))
        {
            var setId = (string?)pointSet.Attribute("id") ?? "(未命名)";
            foreach (var point in pointSet.Descendants("Point"))
            {
                TrackPoint(point, setId);
            }
        }

        // 模板里的声明同样是「声明」：模板属性会落到引用它的点位上，
        // 只扫 PointSets 会让「只写在模板里」的未消费属性静默（本轮补，见报告 §2）。
        foreach (var template in root.Elements("PointTemplates").Elements("Point"))
        {
            TrackPoint(template, "(模板)");
        }

        void TrackPoint(XElement point, string setId)
        {
            var key = setId + "/" + ((string?)point.Attribute("id") ?? "(未命名)");
            for (var i = 0; i < pointAttributeNames.Length; i++)
            {
                if (point.Attribute(pointAttributeNames[i]) == null) continue;
                Track(hits, pointAttributeNames[i], key);
            }

            var write = point.Element("Write");
            if (write == null) return;

            for (var i = 0; i < writeAttributeNames.Length; i++)
            {
                if (write.Attribute(writeAttributeNames[i]) == null) continue;
                Track(hits, "Write@" + writeAttributeNames[i], key);
            }
        }

        for (var i = 0; i < pointAttributeNames.Length; i++)
        {
            Emit(pointAttributeNames[i], pointAttributeMessages[i]);
        }

        for (var i = 0; i < writeAttributeNames.Length; i++)
        {
            Emit("Write@" + writeAttributeNames[i], writeAttributeMessages[i]);
        }

        void Track(Dictionary<string, List<string>> target, string name, string pointKey)
        {
            if (!target.TryGetValue(name, out var list)) target[name] = list = new List<string>();
            list.Add(pointKey);
        }

        void Emit(string name, string message)
        {
            if (!hits.TryGetValue(name, out var list) || list.Count == 0) return;

            var shown = string.Join("、", list.Take(3));
            found.Add(message + "（" + list.Count.ToString(CultureInfo.InvariantCulture) + " 处："
                + shown + (list.Count > 3 ? " 等" : string.Empty) + "）");
        }
    }

    // ─────────────── i18n ───────────────

    private static void LoadI18n(XElement root, string baseDirectory, I18nCatalog catalog, GlobalOptions global)
    {
        var i18n = root.Element("I18n");
        if (i18n == null) return;

        var files = i18n.Elements("Files").Elements("File").ToList();
        if (files.Count == 0) return;

        // ADR D39：只加载与 Global@language 匹配且**磁盘上确实存在**的语言文件；
        // 该语言一个都没有时，用 Global@fallbackLanguage 兜底；仍然没有才退回"全部加载"
        // （兼容只写一个文件、或 @lang 与语言码不一致的老配置）。文件缺失本身仍静默跳过。
        var primary = (global.Language ?? string.Empty).Trim();
        var fallback = (global.FallbackLanguage ?? string.Empty).Trim();

        var available = files
            .Select(f => new { Element = f, Lang = LangOf(f), Path = FullPathOf(f, baseDirectory) })
            .Where(x => x.Path != null && File.Exists(x.Path))
            .ToList();

        var selected = available
            .Where(x => string.Equals(x.Lang, primary, StringComparison.OrdinalIgnoreCase))
            .Select(x => x.Element)
            .ToList();
        if (selected.Count == 0)
        {
            selected = available
                .Where(x => string.Equals(x.Lang, fallback, StringComparison.OrdinalIgnoreCase))
                .Select(x => x.Element)
                .ToList();
        }

        if (selected.Count == 0) selected = files;

        foreach (var file in selected)
        {
            var path = (string?)file.Attribute("path");
            if (string.IsNullOrEmpty(path)) continue;

            var full = Path.IsPathRooted(path) ? path! : Path.Combine(baseDirectory, path);
            if (!File.Exists(full)) continue; // 语言文件缺失不算致命：${} 原样保留

            foreach (var line in File.ReadAllLines(full))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith("#", StringComparison.Ordinal)) continue;

                var eq = trimmed.IndexOf('=');
                if (eq <= 0) continue;

                var key = trimmed.Substring(0, eq).Trim();
                var value = trimmed.Substring(eq + 1).Trim();
                if (value.Length >= 2 && value.StartsWith("\"", StringComparison.Ordinal) && value.EndsWith("\"", StringComparison.Ordinal))
                {
                    value = value.Substring(1, value.Length - 2);
                }

                if (key.Length > 0) catalog.Add(key, value);
            }
        }
    }

    private static string? I18nText(I18nCatalog catalog, string? text)
        => text == null ? null : catalog.Substitute(text);

    // ─────────────── 全局 ───────────────

    private static string? FullPathOf(XElement file, string baseDirectory)
    {
        var path = (string?)file.Attribute("path");
        if (string.IsNullOrEmpty(path)) return null;
        return Path.IsPathRooted(path) ? path : Path.Combine(baseDirectory, path);
    }

    private static string LangOf(XElement file)
        => ((string?)file.Attribute("lang") ?? string.Empty).Trim();

    private static void LoadGlobal(XElement root, GlobalOptions global, List<string> errors)
    {
        var g = root.Element("Global");
        if (g != null)
        {
            global.Language = (string?)g.Attribute("language") ?? global.Language;
            global.FallbackLanguage = (string?)g.Attribute("fallbackLanguage") ?? global.FallbackLanguage;
            global.TimeZone = (string?)g.Attribute("timeZone") ?? global.TimeZone;

            // findings W37 / ADR D38：nullText 与 swap 此前声明但从未读取
            global.NullText = (string?)g.Attribute("nullText") ?? global.NullText;
            global.DefaultSwap = ParseSwap((string?)g.Attribute("swap")) ?? global.DefaultSwap;

            var polling = g.Element("Polling");
            if (polling != null)
            {
                global.DefaultIntervalMs = IntAttr(polling, "defaultIntervalMs", errors, "Global/Polling") ?? global.DefaultIntervalMs;
                global.RequestTimeoutMs = IntAttr(polling, "requestTimeoutMs", errors, "Global/Polling") ?? global.RequestTimeoutMs;
            }

            // findings W5：重试此前整段未读，重试次数被硬编码
            var retry = g.Element("Retry");
            if (retry != null)
            {
                global.RetryCount = IntAttr(retry, "count", errors, "Global/Retry") ?? global.RetryCount;
                global.RetryIntervalMs = IntAttr(retry, "intervalMs", errors, "Global/Retry") ?? global.RetryIntervalMs;
                global.RetryBackoff = (string?)retry.Attribute("backoff") ?? global.RetryBackoff;
                global.EscalateAfter = IntAttr(retry, "escalateAfter", errors, "Global/Retry") ?? global.EscalateAfter;
                global.BudgetMs = IntAttr(retry, "budgetMs", errors, "Global/Retry") ?? global.BudgetMs;
            }

            // findings W7：调度上限此前是代码常量；第 2/3 步改名为地址组上限与忽略间隔数（docs/11 §一.3）
            var scheduler = g.Element("Scheduler");
            if (scheduler != null)
            {
                global.GroupLimitRegisters = IntAttr(scheduler, "groupLimitRegisters", errors, "Global/Scheduler") ?? global.GroupLimitRegisters;
                global.GroupLimitBits = IntAttr(scheduler, "groupLimitBits", errors, "Global/Scheduler") ?? global.GroupLimitBits;
                global.IgnoreGap = IntAttr(scheduler, "ignoreGap", errors, "Global/Scheduler") ?? global.IgnoreGap;
            }

            // findings W8（2026-09-16 恢复）：脚本默认参数 —— 点位未写 Script@timeoutMs/@onError 时的取值。
            // 语义与校验见 ADR D41 与 CGV-32：language 只认 js、timeoutMs 必须 > 0、onError 只认 markBad。
            var script = g.Element("Script");
            if (script != null)
            {
                global.ScriptTimeoutMs = IntAttr(script, "timeoutMs", errors, "Global/Script") ?? global.ScriptTimeoutMs;
                global.ScriptOnError = (string?)script.Attribute("onError") ?? global.ScriptOnError;
                ValidateGlobalScript(script, errors);
            }

            // findings W6（第四步）：两层退避配置正式接线
            LoadReconnect(g, global.Reconnect, errors);

            var quality = g.Element("Quality");
            if (quality != null)
            {
                global.OnCommError = (string?)quality.Attribute("onCommError") ?? global.OnCommError;
                global.OnCommErrorValue = (string?)quality.Attribute("onCommErrorValue") ?? global.OnCommErrorValue;
                global.StaleAfterMs = IntAttr(quality, "staleAfterMs", errors, "Global/Quality") ?? global.StaleAfterMs;
            }
        }

        // Diagnostics 与 Global 平级，读取不得依赖 Global 是否存在（findings W33）
        var diagnostics = root.Element("Diagnostics");
        if (diagnostics != null)
        {
            global.AllowRawAccess = BoolAttr(diagnostics, "allowRawAccess", errors, "Diagnostics") ?? global.AllowRawAccess;
        }
    }

    /// <summary>
    /// <c>Global/Reconnect</c>（两层退避，findings W6，2026-09-16 第四步）：
    /// <c>enabled</c> / <c>delays</c> / <c>manualRetry</c> / <c>offlineQuality</c> 四项，全部**已接线**。
    /// 校验（CGV-26）：队列不得为空、不得含 0 或负数、项数 ≤ <see cref="ReconnectOptions.MaxDelayCount"/>、
    /// 每项必须是合法整数；<c>offlineQuality</c> 只接受 offline | bad。
    /// 早期的 initialDelayMs/maxDelayMs/backoff/keepAliveMs/keepAliveMode/flushRx/resetTxn/failInFlight/resetRetry
    /// 一律报错（不做兼容解析，同 CGV-25 口径）。
    /// </summary>
    private static void LoadReconnect(XElement global, ReconnectOptions options, List<string> errors)
    {
        var e = global.Element("Reconnect");
        if (e == null) return;

        const string where = "Global/Reconnect";

        options.Enabled = BoolAttr(e, "enabled", errors, where) ?? options.Enabled;
        options.ManualRetry = BoolAttr(e, "manualRetry", errors, where) ?? options.ManualRetry;

        var quality = (string?)e.Attribute("offlineQuality");
        if (quality != null)
        {
            var normalized = quality.Trim().ToLowerInvariant();
            if (normalized is not ("offline" or "bad"))
            {
                errors.Add($"{where}：offlineQuality 非法值 \"{quality}\"（只接受 offline | bad）");
            }
            else
            {
                options.OfflineQuality = normalized;
            }
        }

        var delaysAttr = e.Attribute("delays");
        if (delaysAttr != null)
        {
            if (TryParseDelays(delaysAttr.Value, out var delays, out var reason))
            {
                options.Delays = delays!;
            }
            else
            {
                errors.Add($"{where}：delays=\"{delaysAttr.Value}\" 非法（{reason}）");
            }
        }

        // 已删除属性名：写了直接报错（ADR D30「声明即生效」，不静默）
        foreach (var removed in RemovedReconnectAttributes)
        {
            if (e.Attribute(removed) == null) continue;

            errors.Add($"{where}@{removed} 已删除：退避队列改用 delays（毫秒，逗号分隔，逐项等待、"
                + "用完后用最后一个值循环）；keepalive 参数固定为 空闲 10s / 间隔 3s / 3 次，不再可配（CGV-26）");
        }
    }

    /// <summary>Reconnect 上已删除的属性名（写了报错，CGV-26）。</summary>
    private static readonly string[] RemovedReconnectAttributes =
    {
        "initialDelayMs", "maxDelayMs", "backoff", "keepAliveMs", "keepAliveMode",
        "flushRx", "resetTxn", "failInFlight", "resetRetry",
    };

    /// <summary>
    /// 解析退避队列（逗号分隔的毫秒数）：逐项必须是 &gt; 0 的整数，项数 1..<see cref="ReconnectOptions.MaxDelayCount"/>。
    /// 上下文 unrelatedReason 给出中文化的失败原因（进错误行，宿主不必猜）。
    /// </summary>
    private static bool TryParseDelays(string text, out IReadOnlyList<int>? delays, out string reason)
    {
        delays = null;
        reason = string.Empty;

        var parts = text.Split(',');
        var parsed = new List<int>(parts.Length);

        foreach (var part in parts)
        {
            var item = part.Trim();
            if (item.Length == 0)
            {
                reason = "存在空项（逗号之间没有数值）";
                return false;
            }

            if (!int.TryParse(item, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms))
            {
                reason = $"项 \"{item}\" 不是合法整数";
                return false;
            }

            if (ms <= 0)
            {
                reason = $"项 {ms} 非法：退避毫秒数必须大于 0";
                return false;
            }

            parsed.Add(ms);
        }

        if (parsed.Count == 0)
        {
            reason = "队列为空";
            return false;
        }

        if (parsed.Count > ReconnectOptions.MaxDelayCount)
        {
            reason = $"项数 {parsed.Count} 超过上限 {ReconnectOptions.MaxDelayCount}";
            return false;
        }

        delays = parsed;
        return true;
    }

    // ─────────────── 已删除字段 / 链路 ───────────────
    /// <summary>
    /// CGV-25：已删除的旧写法一律**报错**，不做兼容解析（docs/11 首段「当前阶段不做任何兼容」）。
    /// <list type="bullet">
    /// <item>&lt;ScanGroups&gt; 整段：命名的扫描节奏已被「点位/块自带的 <c>intervalMs</c> + <c>mode</c>」取代。</item>
    /// <item>任何元素上的 <c>scanGroup</c> 属性（Device/Block/Point/Defaults/模板…）：改为 <c>intervalMs</c>（毫秒）。</item>
    /// </list>
    /// 单个方法统一处理，避免漏掉某个承载元素；错误信息带元素路径，宿主不必猜是哪一个。
    /// </summary>
    private static void ValidateRemovedFields(XElement root, List<string> errors)
    {
        if (root.Element("ScanGroups") != null)
        {
            errors.Add("ScanGroups 段已删除：命名的扫描节奏不再支持，请改用 Point@intervalMs / Block@intervalMs（毫秒）"
                + "与 Point@mode / Block@mode（auto/onDemand/once）");
        }

        foreach (var element in root.DescendantsAndSelf())
        {
            foreach (var attr in element.Attributes())
            {
                if (!string.Equals(attr.Name.LocalName, "scanGroup", StringComparison.Ordinal)) continue;

                errors.Add($"{ElementPath(element)} 的属性 scanGroup=\"{attr.Value}\" 已删除：请改用 intervalMs（毫秒）"
                    + "，读取模式改用 mode（auto/onDemand/once）");
            }
        }

        // 已改名的字段同样必须报错：旧名不再被读取，闷着不报就是「写了不生效」（ADR D30）
        var polling = root.Element("Global")?.Element("Polling");
        if (polling?.Attribute("rateMs") != null)
        {
            errors.Add("Global/Polling@rateMs 已改名为 defaultIntervalMs：请改用 defaultIntervalMs（毫秒，未写 intervalMs 的点位/块取它）");
        }

        var scheduler = root.Element("Global")?.Element("Scheduler");
        if (scheduler != null)
        {
            CheckRenamed(scheduler, "mergeGap", "ignoreGap");
            CheckRenamed(scheduler, "maxRegistersPerRead", "groupLimitRegisters");
            CheckRenamed(scheduler, "maxBitsPerRead", "groupLimitBits");
        }

        // PointSet/Defaults 不承载间隔与模式：写了必须报错，否则就是「写了不生效」的继承中间层（ADR D30）
        foreach (var defaults in root.Elements("PointSets").Elements("PointSet").Select(s => s.Element("Defaults")))
        {
            if (defaults == null) continue;

            if (defaults.Attribute("intervalMs") != null)
            {
                errors.Add($"{ElementPath(defaults)} 的属性 intervalMs 不生效：Defaults 只承载 area/dataType/swap/unitId/access，"
                    + "间隔请写到 Point@intervalMs / Block@intervalMs（未写取 Global/Polling@defaultIntervalMs）");
            }

            if (defaults.Attribute("mode") != null)
            {
                errors.Add($"{ElementPath(defaults)} 的属性 mode 不生效：Defaults 不承载读取模式，请写到 Point@mode / Block@mode");
            }
        }

        void CheckRenamed(XElement element, string oldName, string newName)
        {
            if (element.Attribute(oldName) == null) return;

            errors.Add($"Global/Scheduler@{oldName} 已改名为 {newName}：请改用 {newName}（旧名不再生效）");
        }
    }

    /// <summary>元素路径（如 <c>Devices/Device@d1</c>），供错误定位；识别不出 id 时退回元素名。</summary>
    private static string ElementPath(XElement element)
    {
        var parts = new List<string>();
        for (var e = element; e != null && parts.Count < 3; e = e.Parent)
        {
            var id = (string?)e.Attribute("id");
            parts.Insert(0, id == null ? e.Name.LocalName : e.Name.LocalName + "@" + id);
        }

        return string.Join("/", parts);
    }

    private static void LoadTransports(XElement root, List<TransportConfig> target, List<string> errors)
    {
        foreach (var e in root.Elements("Transports").Elements("Transport"))
        {
            var id = (string?)e.Attribute("id");
            if (string.IsNullOrEmpty(id))
            {
                errors.Add("Transport 缺少 id");
                continue;
            }

            var where = "Transport " + id;

            // 枚举非法值必须报错（findings W31）
            var variant = ((string?)e.Attribute("variant") ?? "tcp").ToLowerInvariant();
            if (variant is not ("tcp" or "rtuovertcp" or "udp" or "rtu" or "ascii"))
                errors.Add($"{where}：variant 非法值 \"{variant}\"");

            var parity = ((string?)e.Attribute("parity") ?? "none").ToLowerInvariant();
            if (parity is not ("none" or "even" or "odd" or "mark" or "space"))
                errors.Add($"{where}：parity 非法值 \"{parity}\"");

            var stopBits = ((string?)e.Attribute("stopBits") ?? "one").ToLowerInvariant();
            if (stopBits is not ("one" or "onepointfive" or "two"))
                errors.Add($"{where}：stopBits 非法值 \"{stopBits}\"");

            target.Add(new TransportConfig
            {
                Id = id!,
                Variant = variant,
                Enabled = BoolAttr(e, "enabled", errors, where) ?? true,
                Host = (string?)e.Attribute("host"),
                Port = IntAttr(e, "port", errors, where) ?? 502,
                PortName = (string?)e.Attribute("portName"),
                BaudRate = IntAttr(e, "baudRate", errors, where) ?? 9600,
                DataBits = IntAttr(e, "dataBits", errors, where) ?? 8,
                Parity = parity,
                StopBits = stopBits,
                ConnectTimeoutMs = IntAttr(e, "connectTimeoutMs", errors, where) ?? 3000,
                RequestTimeoutMs = IntAttr(e, "requestTimeoutMs", errors, where) ?? 1000,
                GapMs = IntAttr(e, "gapMs", errors, where) ?? 0,
                // findings W13：串口专属（此前未读）
                Handshake = ((string?)e.Attribute("handshake") ?? "none").ToLowerInvariant(),
                DtrEnable = BoolAttr(e, "dtr", errors, where) ?? false,
                RtsEnable = BoolAttr(e, "rts", errors, where) ?? false,
                ReadTimeoutMs = IntAttr(e, "readTimeoutMs", errors, where) ?? 500,
                WriteTimeoutMs = IntAttr(e, "writeTimeoutMs", errors, where) ?? 500,
                // findings W14：链路级重试覆盖
                RetryCount = e.Element("Retry") == null ? 2 : IntAttr(e.Element("Retry")!, "count", errors, where + "/Retry") ?? 2,
                RetryIntervalMs = e.Element("Retry") == null ? 100 : IntAttr(e.Element("Retry")!, "intervalMs", errors, where + "/Retry") ?? 100,
                HasRetryOverride = e.Element("Retry") != null,
            });
        }
    }

    // ─────────────── 模板 ───────────────

    private static Dictionary<string, XElement> ParseDeviceTemplates(XElement root, List<string> errors)
    {
        var templates = new Dictionary<string, XElement>(StringComparer.Ordinal);
        foreach (var e in root.Elements("DeviceTemplates").Elements("Device"))
        {
            var id = (string?)e.Attribute("id");
            if (string.IsNullOrEmpty(id))
            {
                errors.Add("DeviceTemplate 缺少 id");
                continue;
            }

            templates[id!] = e;
        }

        return templates;
    }

    private static Dictionary<string, XElement> ParsePointTemplates(XElement root, List<string> errors)
    {
        var templates = new Dictionary<string, XElement>(StringComparer.Ordinal);
        foreach (var e in root.Elements("PointTemplates").Elements("Point"))
        {
            var id = (string?)e.Attribute("id");
            if (string.IsNullOrEmpty(id))
            {
                errors.Add("PointTemplate 缺少 id");
                continue;
            }

            templates[id!] = e;
        }

        return templates;
    }

    // ─────────────── 设备 ───────────────

    private static void LoadDevices(
        XElement root,
        SamplerConfiguration config,
        Dictionary<string, XElement> deviceTemplates,
        List<string> errors)
    {
        foreach (var e in root.Elements("Devices").Elements("Device"))
        {
            var id = (string?)e.Attribute("id");
            if (string.IsNullOrEmpty(id))
            {
                errors.Add("Device 缺少 id");
                continue;
            }

            // 模板属性先行，实例上显式写出的属性覆盖；模板独有子元素（如 Retry）也垫入
            XElement merged = e;
            var templateRef = (string?)e.Attribute("template");
            if (templateRef != null)
            {
                if (!deviceTemplates.TryGetValue(templateRef, out var template))
                {
                    // 与 Point@template 对称：引用不存在的模板必须报错（findings W36）
                    errors.Add($"Device {id} 引用了不存在的设备模板 {templateRef}");
                }
                else
                {
                    merged = MergeAttributes(template, e, exclude: "id");
                }
            }

            var where = "Device " + id;

            var device = new DeviceConfig
            {
                Id = id!,
                Name = I18nText(config.I18n, (string?)merged.Attribute("name")),
                // findings W17 遗留补齐：Device@desc 此前完全没解析（写了被静默忽略，字典 §9 标为「说明」）
                Desc = (string?)merged.Attribute("desc"),
                Enabled = BoolAttr(merged, "enabled", errors, where) ?? true,
                Transport = (string?)merged.Attribute("transport") ?? string.Empty,
                UnitId = IntAttr(merged, "unitId", errors, where) ?? 1,
                PointSetId = (string?)merged.Attribute("pointSet") ?? string.Empty,
                Swap = ParseSwap((string?)merged.Attribute("swap")) ?? config.Global.DefaultSwap,
                RequestTimeoutMs = IntAttr(merged, "requestTimeoutMs", errors, where) ?? config.Global.RequestTimeoutMs,
            };

            // 重试：设备级 <Retry> 优先，其次链路级覆盖，最后全局（findings W5/W14）
            var transportConfig = config.Transports.FirstOrDefault(t => t.Id == device.Transport);
            if (transportConfig != null && transportConfig.HasRetryOverride)
            {
                device.RetryCount = transportConfig.RetryCount;
                device.RetryIntervalMs = transportConfig.RetryIntervalMs;
            }
            else
            {
                device.RetryCount = config.Global.RetryCount;
                device.RetryIntervalMs = config.Global.RetryIntervalMs;
            }

            var retry = merged.Element("Retry");
            if (retry != null)
            {
                device.RetryCount = IntAttr(retry, "count", errors, where + "/Retry") ?? device.RetryCount;
                device.RetryIntervalMs = IntAttr(retry, "intervalMs", errors, where + "/Retry") ?? device.RetryIntervalMs;
            }

            var pause = merged.Element("Pause");
            if (pause != null) device.Paused = BoolAttr(pause, "maintenance", errors, where + "/Pause") ?? device.Paused;

            // generateDiagnostics 的“值”本身要校验（写了非法布尔就是配置错误，不能静默当 false）；
            // 该功能未实现，是否要告警由 DetectUnsupported 决定（那里读同一个属性，但不重复报错）。
            _ = BoolAttr(merged, "generateDiagnostics", errors, where);

            config.Devices.Add(device);
        }
    }

    /// <summary>把模板属性垫在实例下面：实例显式写的属性优先；模板独有子元素也垫入。</summary>
    private static XElement MergeAttributes(XElement template, XElement instance, string exclude)
    {
        var merged = new XElement(instance);
        foreach (var attr in template.Attributes())
        {
            if (attr.Name.LocalName == exclude) continue;
            if (instance.Attribute(attr.Name) == null) merged.SetAttributeValue(attr.Name, attr.Value);
        }

        foreach (var child in template.Elements())
        {
            if (instance.Element(child.Name) == null) merged.Add(new XElement(child));
        }

        return merged;
    }

    // ─────────────── 结构校验 ───────────────

    private static void Validate(SamplerConfiguration config, List<string> errors)
    {
        CheckDuplicate(config.Transports.Select(t => t.Id), "Transport", errors);
        CheckDuplicate(config.Devices.Select(d => d.Id), "Device", errors);
        CheckDuplicate(config.PointSets.Select(p => p.Id), "PointSet", errors);

        var transportIds = new HashSet<string>(config.Transports.Select(t => t.Id), StringComparer.Ordinal);
        var pointSetIds = new HashSet<string>(config.PointSets.Select(p => p.Id), StringComparer.Ordinal);

        // 同一 (transport, unitId) 只允许一个启用的设备声明，否则重复轮询同一从站
        var slaveOwners = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var device in config.Devices)
        {
            if (!transportIds.Contains(device.Transport))
                errors.Add($"Device {device.Id} 引用了不存在的链路 {device.Transport}");
            if (!pointSetIds.Contains(device.PointSetId))
                errors.Add($"Device {device.Id} 引用了不存在的点表 {device.PointSetId}");

            if (!device.Enabled) continue;

            var slaveKey = device.Transport + "#" + device.UnitId;
            if (slaveOwners.TryGetValue(slaveKey, out var ownerId))
            {
                errors.Add($"Device {device.Id} 与 {ownerId} 声明了相同的 (transport={device.Transport}, unitId={device.UnitId})，会重复轮询同一从站");
            }
            else
            {
                slaveOwners[slaveKey] = device.Id;
            }
        }

        foreach (var set in config.PointSets)
        {
            var pointIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var point in set.Points.Concat(set.Blocks.SelectMany(b => b.Points)).Concat(set.Calculated))
            {
                if (!pointIds.Add(point.Id))
                    errors.Add($"PointSet {set.Id} 内点位 id 重复：{point.Id}（点表内必须唯一）");
            }
        }

        foreach (var set in config.PointSets)
        {
            foreach (var block in set.Blocks)
            {
                // findings D49：块是「显式声明的一次请求」，count < 1 读不出任何东西——
                // 空块（块内无点位）此前无条件放行，运行期一个请求都不发、也不报错（静默无效）。
                if (block.Count < 1)
                {
                    errors.Add($"Block {set.Id}/{block.Id} count={block.Count} 非法（必须 ≥ 1）：块是显式声明的一次请求，"
                        + "空块读不出任何东西；不想要这段窗口就删掉该块");
                }

                // CGV-11：块是「显式声明的一次请求」，count 不得超地址组上限（位区与寄存器区上限不同）
                var isBitArea = block.Area is RuntimeArea.Coil or RuntimeArea.DiscreteInput;
                var limit = isBitArea ? config.Global.GroupLimitBits : config.Global.GroupLimitRegisters;
                if (block.Count > limit)
                {
                    errors.Add($"Block {block.Id} count={block.Count} 超过 {(isBitArea ? "位区" : "寄存器区")}的地址组上限 {limit}"
                        + "（Global/Scheduler@groupLimitBits / @groupLimitRegisters）");
                }
            }
        }
    }

    private static void CheckDuplicate(IEnumerable<string> ids, string what, List<string> errors)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            if (!seen.Add(id)) errors.Add($"{what} id 重复：{id}");
        }
    }
}
