using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace SuperSampler.Core.Config;

/// <summary>配置校验失败：携带全部错误行，一次性报完，避免改一条跑一次。</summary>
public sealed class ConfigValidationException : Exception
{
    public ConfigValidationException(IReadOnlyList<string> errors)
        : base("配置校验失败：" + Environment.NewLine + string.Join(Environment.NewLine, errors))
    {
        Errors = errors;
    }

    public IReadOnlyList<string> Errors { get; }
}

/// <summary>
/// XML → 运行时配置加载器（解析主体）。
/// 完成四件继承/换算：模板继承、PointSet Defaults 继承链、PLC 编址换算、i18n 替换；
/// 并执行 docs/01 第 9 节的校验规则。校验错误一次性全部报出。
/// 点位/块解析与枚举换算工具在 partial 续文件 SamplerConfigLoader.Points.cs。
/// </summary>
public static partial class SamplerConfigLoader
{
    public static SamplerConfiguration LoadFromXml(string xmlPath)
    {
        var doc = XDocument.Load(xmlPath);
        var baseDir = Path.GetDirectoryName(Path.GetFullPath(xmlPath)) ?? string.Empty;
        return Load(doc, baseDir);
    }

    /// <summary>从 XML 文档加载。baseDirectory 用于解析 i18n 等相对路径。</summary>
    public static SamplerConfiguration Load(XDocument doc, string baseDirectory)
    {
        try
        {
            return LoadCore(doc, baseDirectory);
        }
        catch (ConfigValidationException)
        {
            throw;
        }
        catch (FormatException ex)
        {
            // 非法数值/布尔（如 bit="x"）统一升级为配置错误，而不是裸 FormatException（findings W32）
            throw new ConfigValidationException(new[] { "配置存在非法数值或布尔值：" + ex.Message });
        }
    }

    private static SamplerConfiguration LoadCore(XDocument doc, string baseDirectory)
    {
        var errors = new List<string>();
        var root = doc.Root ?? throw new ConfigValidationException(new[] { "XML 无根元素" });

        var config = new SamplerConfiguration
        {
            UnsupportedPolicy = ((string?)root.Attribute("unsupportedPolicy") ?? "warn").ToLowerInvariant(),
        };

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
        LoadGlobal(root, config.Global);
        LoadI18n(root, baseDirectory, config.I18n, config.Global);
        LoadMeta(root, config.Meta, config.I18n);
        LoadScanGroups(root, config.ScanGroups, errors);
        LoadTransports(root, config.Transports, errors);

        var deviceTemplates = ParseDeviceTemplates(root, errors);
        var pointTemplates = ParsePointTemplates(root, errors);
        LoadDevices(root, config, deviceTemplates, errors);
        LoadPointSets(root, config, pointTemplates, errors);
        ValidateReferences(root, config, errors);
        DetectUnsupported(root, config, errors);

        Validate(config, errors);

        if (errors.Count > 0) throw new ConfigValidationException(errors);
        return config;
    }

    /// <summary>解析工程元信息（findings W2：此前整段未读）。</summary>
    private static void LoadMeta(XElement root, MetaOptions meta, I18nCatalog catalog)
    {
        var m = root.Element("Meta");
        if (m == null) return;

        meta.ProjectName = I18nText(catalog, (string?)m.Element("ProjectName"));
        meta.Comment = (string?)m.Element("Comment");
        meta.Author = (string?)m.Element("Author");
        meta.CreatedAt = (string?)m.Element("CreatedAt");
        meta.Revision = (int?)m.Element("Revision") ?? 0;

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

        foreach (var point in root.Elements("PointSets").Elements("PointSet").SelectMany(s => s.Descendants("Point")))
        {
            var id = (string?)point.Attribute("id") ?? "(未命名)";
            if (point.Element("History") != null) found.Add($"点位 {id} 的 History（历史归档，后续迭代）");
            if (point.Element("Script") != null) found.Add($"点位 {id} 的 Script（点位级脚本解码，后续迭代）");
            if (point.Element("Tags") != null) found.Add($"点位 {id} 的 Tags（自定义元数据，后续迭代）");

            // 解析了但尚未被编解码消费的属性，同样显式告警而非静默（D30 原则）
            var stringOptions = point.Element("String");
            if (stringOptions?.Attribute("padding") != null || stringOptions?.Attribute("byteAligned") != null)
                found.Add($"点位 {id} 的 String@padding/@byteAligned（字符串填充与字节对齐，后续迭代）");

            if (point.Element("Scale")?.Attribute("mode") != null)
                found.Add($"点位 {id} 的 Scale@mode（缩放模式，v1 仅支持线性）");
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

        foreach (var device in root.Elements("Devices").Elements("Device"))
        {
            var id = (string?)device.Attribute("id") ?? "(未命名)";
            if ((bool?)device.Attribute("generateDiagnostics") == true)
                found.Add($"设备 {id} 的 generateDiagnostics（自动诊断点，后续迭代）");
            if (device.Element("Simulate") != null) found.Add($"设备 {id} 的 Simulate（仿真模式，后续迭代）");
        }

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

    private static void LoadGlobal(XElement root, GlobalOptions global)
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
                global.DefaultRateMs = (int?)polling.Attribute("rateMs") ?? global.DefaultRateMs;
                global.RequestTimeoutMs = (int?)polling.Attribute("requestTimeoutMs") ?? global.RequestTimeoutMs;
            }

            // findings W5：重试此前整段未读，重试次数被硬编码
            var retry = g.Element("Retry");
            if (retry != null)
            {
                global.RetryCount = (int?)retry.Attribute("count") ?? global.RetryCount;
                global.RetryIntervalMs = (int?)retry.Attribute("intervalMs") ?? global.RetryIntervalMs;
                global.RetryBackoff = (string?)retry.Attribute("backoff") ?? global.RetryBackoff;
                global.EscalateAfter = (int?)retry.Attribute("escalateAfter") ?? global.EscalateAfter;
                global.BudgetMs = (int?)retry.Attribute("budgetMs") ?? global.BudgetMs;
            }

            // findings W7：调度上限此前是代码常量
            var scheduler = g.Element("Scheduler");
            if (scheduler != null)
            {
                global.MaxRegistersPerRead = (int?)scheduler.Attribute("maxRegistersPerRead") ?? global.MaxRegistersPerRead;
                global.MaxBitsPerRead = (int?)scheduler.Attribute("maxBitsPerRead") ?? global.MaxBitsPerRead;
                global.MergeGap = (int?)scheduler.Attribute("mergeGap") ?? global.MergeGap;
            }

            // findings W8：脚本默认参数
            var script = g.Element("Script");
            if (script != null)
            {
                global.ScriptTimeoutMs = (int?)script.Attribute("timeoutMs") ?? global.ScriptTimeoutMs;
                global.ScriptOnError = (string?)script.Attribute("onError") ?? global.ScriptOnError;
            }

            var quality = g.Element("Quality");
            if (quality != null)
            {
                global.OnCommError = (string?)quality.Attribute("onCommError") ?? global.OnCommError;
                global.OnCommErrorValue = (string?)quality.Attribute("onCommErrorValue") ?? global.OnCommErrorValue;
                global.StaleAfterMs = (int?)quality.Attribute("staleAfterMs") ?? global.StaleAfterMs;
            }
        }

        // Diagnostics 与 Global 平级，读取不得依赖 Global 是否存在（findings W33）
        var diagnostics = root.Element("Diagnostics");
        if (diagnostics != null)
        {
            global.AllowRawAccess = (bool?)diagnostics.Attribute("allowRawAccess") ?? global.AllowRawAccess;
        }
    }

    // ─────────────── 扫描组 / 链路 ───────────────

    private static void LoadScanGroups(XElement root, List<ScanGroupConfig> target, List<string> errors)
    {
        foreach (var e in root.Elements("ScanGroups").Elements("ScanGroup"))
        {
            var id = (string?)e.Attribute("id");
            if (string.IsNullOrEmpty(id))
            {
                errors.Add("ScanGroup 缺少 id");
                continue;
            }

            // 枚举非法值必须报错（findings W31）
            var mode = ((string?)e.Attribute("mode") ?? "poll").ToLowerInvariant();
            if (mode is not ("poll" or "ondemand" or "once"))
                errors.Add($"ScanGroup {id}：mode 非法值 \"{mode}\"");

            target.Add(new ScanGroupConfig
            {
                Id = id!,
                Mode = mode,
                RateMs = (int?)e.Attribute("rateMs") ?? 1000,
                JitterMs = (int?)e.Attribute("jitterMs") ?? 0,
            });
        }
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

            // 枚举非法值必须报错（findings W31）
            var variant = ((string?)e.Attribute("variant") ?? "tcp").ToLowerInvariant();
            if (variant is not ("tcp" or "rtuovertcp" or "udp" or "rtu" or "ascii"))
                errors.Add($"Transport {id}：variant 非法值 \"{variant}\"");

            var parity = ((string?)e.Attribute("parity") ?? "none").ToLowerInvariant();
            if (parity is not ("none" or "even" or "odd" or "mark" or "space"))
                errors.Add($"Transport {id}：parity 非法值 \"{parity}\"");

            var stopBits = ((string?)e.Attribute("stopBits") ?? "one").ToLowerInvariant();
            if (stopBits is not ("one" or "onepointfive" or "two"))
                errors.Add($"Transport {id}：stopBits 非法值 \"{stopBits}\"");

            target.Add(new TransportConfig
            {
                Id = id!,
                Variant = variant,
                Enabled = (bool?)e.Attribute("enabled") ?? true,
                Host = (string?)e.Attribute("host"),
                Port = (int?)e.Attribute("port") ?? 502,
                PortName = (string?)e.Attribute("portName"),
                BaudRate = (int?)e.Attribute("baudRate") ?? 9600,
                DataBits = (int?)e.Attribute("dataBits") ?? 8,
                Parity = parity,
                StopBits = stopBits,
                ConnectTimeoutMs = (int?)e.Attribute("connectTimeoutMs") ?? 3000,
                RequestTimeoutMs = (int?)e.Attribute("requestTimeoutMs") ?? 1000,
                GapMs = (int?)e.Attribute("gapMs") ?? 0,
                // findings W13：串口专属（此前未读）
                Handshake = ((string?)e.Attribute("handshake") ?? "none").ToLowerInvariant(),
                DtrEnable = (bool?)e.Attribute("dtr") ?? false,
                RtsEnable = (bool?)e.Attribute("rts") ?? false,
                ReadTimeoutMs = (int?)e.Attribute("readTimeoutMs") ?? 500,
                WriteTimeoutMs = (int?)e.Attribute("writeTimeoutMs") ?? 500,
                // findings W14：链路级重试覆盖
                RetryCount = (int?)e.Element("Retry")?.Attribute("count") ?? 2,
                RetryIntervalMs = (int?)e.Element("Retry")?.Attribute("intervalMs") ?? 100,
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

            var device = new DeviceConfig
            {
                Id = id!,
                Name = I18nText(config.I18n, (string?)merged.Attribute("name")),
                Enabled = (bool?)merged.Attribute("enabled") ?? true,
                Transport = (string?)merged.Attribute("transport") ?? string.Empty,
                UnitId = (int?)merged.Attribute("unitId") ?? 1,
                PointSetId = (string?)merged.Attribute("pointSet") ?? string.Empty,
                ScanGroup = (string?)merged.Attribute("scanGroup") ?? "normal",
                Swap = ParseSwap((string?)merged.Attribute("swap")) ?? config.Global.DefaultSwap,
                RequestTimeoutMs = (int?)merged.Attribute("requestTimeoutMs") ?? config.Global.RequestTimeoutMs,
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
                device.RetryCount = (int?)retry.Attribute("count") ?? device.RetryCount;
                device.RetryIntervalMs = (int?)retry.Attribute("intervalMs") ?? device.RetryIntervalMs;
            }

            var pause = merged.Element("Pause");
            if (pause != null) device.Paused = (bool?)pause.Attribute("maintenance") ?? device.Paused;

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

    // ─────────────── 校验 ───────────────

    private static void Validate(SamplerConfiguration config, List<string> errors)
    {
        CheckDuplicate(config.Transports.Select(t => t.Id), "Transport", errors);
        CheckDuplicate(config.Devices.Select(d => d.Id), "Device", errors);
        CheckDuplicate(config.ScanGroups.Select(g => g.Id), "ScanGroup", errors);
        CheckDuplicate(config.PointSets.Select(p => p.Id), "PointSet", errors);

        var groupIds = new HashSet<string>(config.ScanGroups.Select(g => g.Id), StringComparer.Ordinal);
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

        foreach (var point in config.PointSets.SelectMany(s => s.Points)
                     .Concat(config.PointSets.SelectMany(s => s.Blocks.SelectMany(b => b.Points))))
        {
            if (!groupIds.Contains(point.ScanGroup))
                errors.Add($"点位 {point.DeviceId}/{point.Id} 引用了不存在的扫描组 {point.ScanGroup}");
            if (point.Slices == null && point.Address < 0)
                errors.Add($"点位 {point.DeviceId}/{point.Id} 地址非法：{point.Address}");
        }

        foreach (var block in config.PointSets.SelectMany(s => s.Blocks))
        {
            // 位区与寄存器区的单次读上限不同：寄存器 125，位 2000（findings W34）
            var isBitArea = block.Area is RuntimeArea.Coil or RuntimeArea.DiscreteInput;
            var limit = isBitArea ? 2000 : 125;
            if (block.Count > limit)
                errors.Add($"Block {block.Id} count={block.Count} 超过 {(isBitArea ? "位区 2000" : "寄存器区 125")} 的单次读上限");
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
