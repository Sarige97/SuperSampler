using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace SuperSampler.Core.Config;

/// <summary>
/// <see cref="SamplerConfigLoader"/> 的引用校验与全量规则部分（全部在**加载期**判定，一次报全）：
/// CGV-8（dataType/length 位宽）、CGV-9（PLC 前缀推区并校验一致）、CGV-10（read 与 Write 互斥）、
/// CGV-13（alarm 优先级存在）、CGV-14（报警限值大小关系）、CGV-15/31（计算点循环依赖，含 DependsOn 与跨点表）、
/// CGV-16（块内点位地址不连续）、CGV-17（同址同位重复声明，含位映射展开出的子点位）、
/// CGV-18（地址+长度越出区容量）、CGV-19（间隔与地址组上限非法）、CGV-21（once 不得带 intervalMs）、
/// CGV-28（Slices 片段跨度超地址组上限）、CGV-32（脚本 language/timeoutMs/onError/未知属性非法）、
/// CGV-33（计算点必须恰有 Expression 或 Script 之一），
/// 以及 i18n key 存在性、Command 步骤点位存在性。
/// CGV-20（块内点位不得写 intervalMs/mode）落在解析点：SamplerConfigLoader.Points.cs；
/// CGV-27（位映射展开）落在 SamplerConfigLoader.Bits.cs；CGV-29/30 落在 SamplerConfigLoader.Points.cs。
/// 规则清单见 Config/配置字段说明.md 第 16 节。
/// </summary>
public static partial class SamplerConfigLoader
{
    /// <summary>Modbus 单区地址容量：0..65535。</summary>
    internal const int AreaCapacity = 65536;

    private static readonly Regex I18nKeyPattern = new(@"\$\{([^}]+)\}", RegexOptions.Compiled);

    /// <summary>计算点表达式里的取点引用 <c>P('id')</c>（单双引号都收）。</summary>
    private static readonly Regex PointRefPattern = new(@"P\(\s*['""]([^'""]+)['""]\s*\)", RegexOptions.Compiled);

    /// <summary>点表内的全部点位（散点 + 块内 + 计算点）。</summary>
    private static IEnumerable<PointConfig> PointsOf(PointSetConfig set)
        => set.Points.Concat(set.Blocks.SelectMany(b => b.Points)).Concat(set.Calculated);

    /// <summary>
    /// 计算点的依赖引用全集：表达式里的 <c>P('id')</c>、**脚本正文里的 <c>P('id')</c>**
    /// （脚本型计算点与表达式型走同一个求值链，引用同样会成环；findings D46）
    /// 与 <c>&lt;DependsOn&gt;&lt;PointRef&gt;</c>（让隐式依赖也能显式声明）——三者都必须进依赖图（CGV-15/31）。
    /// </summary>
    private static IEnumerable<string> ReferencesOf(PointConfig point)
    {
        foreach (var reference in PointReferences(point.Expression))
        {
            yield return reference;
        }

        // findings D46：只扫 Expression 会让脚本型计算点的环漏到运行期——脚本正文同样要参与成环判定
        foreach (var reference in PointReferences(point.Script))
        {
            yield return reference;
        }

        foreach (var reference in point.DependsOn)
        {
            if (reference.Length > 0) yield return reference;
        }
    }

    /// <summary>一段文本（表达式或脚本正文）里的全部 <c>P('id')</c> 引用。</summary>
    private static IEnumerable<string> PointReferences(string? text)
    {
        if (string.IsNullOrEmpty(text)) yield break;

        foreach (Match match in PointRefPattern.Matches(text!))
        {
            var reference = match.Groups[1].Value.Trim();
            if (reference.Length > 0) yield return reference;
        }
    }

    /// <summary>计算点依赖图的节点键：点表 id + "/" + 点位 id（点表之间允许点位重名）。</summary>
    private static string CalcKey(string pointSetId, string pointId) => pointSetId + "/" + pointId;

    /// <summary>依赖图节点：声明它的点表 + 计算点配置（net46 无 ValueTuple，用小类代替元组）。</summary>
    private sealed class CalculatedNode
    {
        public CalculatedNode(string setId, PointConfig point)
        {
            SetId = setId;
            Point = point;
        }

        public string SetId { get; }

        public PointConfig Point { get; }
    }

    /// <summary>解析报警等级与命令（供校验），并执行跨实体引用校验。</summary>
    private static void ValidateReferences(XElement root, SamplerConfiguration config, List<string> errors)
    {
        var alarmClassIds = new HashSet<string>(
            root.Elements("AlarmClasses").Elements("AlarmClass")
                .Select(a => (string?)a.Attribute("id") ?? string.Empty)
                .Where(id => id.Length > 0),
            StringComparer.Ordinal);

        // ── 报警：优先级必须指向存在的报警等级 ──
        foreach (var set in config.PointSets)
        {
            foreach (var point in PointsOf(set))
            {
                foreach (var alarm in point.Alarms)
                {
                    var priority = alarm.Priority;
                    if (priority != null && priority.Length > 0 && !alarmClassIds.Contains(priority))
                    {
                        errors.Add($"{PointPath(set.Id, point.Id)} 的报警引用了不存在的等级 {alarm.Priority}");
                    }
                }
            }
        }

        // ── 命令：步骤引用的点位必须存在（点位 id 在点表内唯一，命令按 id 检索）──
        var pointIds = new HashSet<string>(
            config.PointSets.SelectMany(PointsOf).Select(p => p.Id),
            StringComparer.Ordinal);

        foreach (var command in root.Elements("Commands").Elements("Command"))
        {
            var commandId = (string?)command.Attribute("id") ?? "(未命名)";
            foreach (var step in command.Elements("Steps").Elements("Step"))
            {
                var target = (string?)step.Attribute("point");
                if (target == null || target.Length == 0)
                {
                    errors.Add($"Command {commandId} 有步骤缺少 point");
                    continue;
                }

                // 允许「deviceId/pointId」或裸点 id（裸 id 必须恰好命中一个点位）
                if (target.Contains('/'))
                {
                    var parts = target.Split(new[] { '/' }, 2);
                    if (!pointIds.Contains(parts[1]))
                        errors.Add($"Command {commandId} 引用了不存在的点位 {target}");
                }
                else if (pointIds.Count(id => id == target) != 1)
                {
                    errors.Add($"Command {commandId} 引用的点位 {target} 不存在或不唯一（请用 deviceId/pointId 全限定）");
                }
            }
        }

        // ── i18n：配置里出现的 ${KEY} 必须能在资源里解析（默认语言）──
        foreach (var set in config.PointSets)
        {
            foreach (var point in PointsOf(set))
            {
                var path = PointPath(set.Id, point.Id);
                CheckI18nKeys(point.Name, path + ".name", config, errors);
                if (point.Format != null)
                {
                    CheckI18nKeys(point.Format.Prefix, path + ".prefix", config, errors);
                    CheckI18nKeys(point.Format.Suffix, path + ".suffix", config, errors);
                }

                foreach (var alarm in point.Alarms)
                {
                    CheckI18nKeys(alarm.Message, path + ".alarm", config, errors);
                }
            }
        }
    }

    /// <summary>CGV-8/9/10：单点位规则（length 位宽、plc 前缀推区、read 与 Write 互斥）。</summary>
    private static void ValidatePointRules(PointConfig point, XElement element, List<string> errors)
    {
        var path = PointPath(point.PointSetId, point.Id);

        // CGV-8：显式 length 与 dataType 位宽一致（string/raw/位点除外）。
        // bcd/datetime 的期望字长按类型参数推导（ADR D34：Bcd@digits / DateTime@format），
        // 与运行时切片共用同一个推导函数，避免「校验放行、轮询只读首字」的静默错值。
        if (point.Length > 0 && point.Slices == null && !point.Bit.HasValue && point.BitRange == null
            && point.DataType is not (RuntimeDataType.String or RuntimeDataType.Raw))
        {
            var expected = EffectiveLength(new PointConfig
            {
                DataType = point.DataType,
                Length = 0,
                Bit = null,
                BitRange = null,
                BcdDigits = point.BcdDigits,
                DateTimeFormat = point.DateTimeFormat,
            });

            if (point.Length != expected)
            {
                errors.Add($"{path}：length={point.Length} 与 dataType={point.DataType} 的位宽 {expected} 不符");
            }
        }

        // CGV-9：addrFormat=plc 时，地址前缀必须与 area 一致（3xxxx 只能是 InputRegister 等）。
        // 地址本身解析不出时由 ResolveAddress 报错，这里不重复报、也不许抛（否则会中断整段收集）。
        if (string.Equals((string?)element.Attribute("addrFormat"), "plc", StringComparison.OrdinalIgnoreCase)
            && int.TryParse((string?)element.Attribute("address"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var raw))
        {
            var prefixArea = raw switch
            {
                >= 40001 and <= 49999 => RuntimeArea.HoldingRegister,
                >= 30001 and <= 39999 => RuntimeArea.InputRegister,
                >= 10001 and <= 19999 => RuntimeArea.DiscreteInput,
                >= 1 and <= 9999 => RuntimeArea.Coil,
                _ => (RuntimeArea?)null,
            };

            if (prefixArea.HasValue && point.Area != prefixArea.Value)
            {
                errors.Add($"{path}：PLC 地址 {raw} 的区段（{prefixArea.Value}）与 area={point.Area} 不一致");
            }
        }

        // CGV-10：只读点不得带 Write 元素
        if (!point.IsWritable && element.Element("Write") != null)
        {
            errors.Add($"{path}：access={point.Access} 但声明了 Write（只读点不可写，二者互斥）");
        }
    }

    // ═══════════════ 全量规则（本步新增：CGV-14 ~ CGV-18）═══════════════

    /// <summary>
    /// CGV-14 ~ CGV-21 / CGV-28 / CGV-31：点表级与调度参数的全量校验。全部在加载期判定并一次报全，
    /// 绝不留到运行期才发现（docs/11 §二）。
    /// </summary>
    private static void ValidateFullRules(SamplerConfiguration config, List<string> errors)
    {
        ValidateScheduleParameters(config, errors);   // CGV-19 / CGV-21（+ D47：请求超时 ≥ 1ms）
        ValidateCalculatedForm(config, errors);       // CGV-33（计算点：表达式/脚本恰有其一）

        foreach (var set in config.PointSets)
        {
            foreach (var point in PointsOf(set))
            {
                ValidateAddressCapacity(set, point, errors);   // CGV-18
                ValidateRequestWidth(config, set, point, errors); // CGV-28（单点宽度 ≤ 地址组上限，D48）
                ValidateAlarmLimits(set, point, errors);       // CGV-14
                ValidateSliceSpans(config, set, point, errors); // CGV-28
                ValidateAreaAndWidth(set, point, errors);      // CGV-35（D66：只读区/位区）
                ValidateAlarmRules(set, point, errors);        // CGV-36（D68~D72）
                ValidatePointNumbers(set, point, errors);      // CGV-38（bit 范围等）
            }

            ValidateDuplicateBitDeclarations(set, errors);      // CGV-17（含位映射展开的子点位）
            ValidateBlockContiguity(set, config.Global.IgnoreGap, errors);  // CGV-16
        }

        ValidateCalculatedCycles(config, errors);           // CGV-15 / CGV-31（跨点表，含 DependsOn）
        ValidateDependsOnReferences(config, errors);        // CGV-31（悬空引用）
    }

    // ═══════════════ 脚本（ADR D41）═══════════════

    /// <summary>
    /// CGV-32：<c>Global/Script</c> 的脚本默认参数——<c>timeoutMs</c> 必须 &gt; 0、
    /// <c>onError</c> 只认 <c>markBad</c>；<c>language</c> 不属于全局段（脚本语言在 <c>Point/Script</c> 上声明），
    /// 写了报错而不是静默忽略（ADR D30 声明即生效）。
    /// </summary>
    internal static void ValidateGlobalScript(XElement script, List<string> errors)
    {
        const string where = "Global/Script";

        if (script.Attribute("language") != null)
        {
            errors.Add($"{where}：不支持 language（脚本语言在 Point/Script 上声明，当前仅 js）");
        }

        ValidateScriptNumbers(script, where, errors);
        ValidateScriptAttributes(script, where, "timeoutMs/onError", errors);

        var onError = ((string?)script.Attribute("onError"))?.Trim();
        if (onError != null && !string.Equals(onError, "markBad", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"{where}：onError=\"{onError}\" 非法（当前只实现 markBad：质量置 Bad + 明确原因）");
        }

        // 全局段只带默认参数，正文写在这里没有任何作用——报错而不是静默吞掉
        if (script.Value.Trim().Length > 0)
        {
            errors.Add($"{where}：只接受 timeoutMs/onError 两个属性，不得写脚本正文（正文写在 Point/Script 里）");
        }
    }

    /// <summary>
    /// CGV-32：<c>Point/Script</c> 合法——<c>language</c> 只认 <c>js</c>（Jint ES5.1）、
    /// <c>timeoutMs</c> &gt; 0、<c>onError</c> 只认 <c>markBad</c>、正文不得为空。
    /// </summary>
    internal static void ValidatePointScript(XElement script, string where, List<string> errors)
    {
        var language = ((string?)script.Attribute("language"))?.Trim();
        if (language != null && !string.Equals(language, "js", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"{where}：language=\"{language}\" 非法（只支持 js，Jint ES5.1；无箭头函数/let/const）");
        }

        ValidateScriptNumbers(script, where, errors);
        ValidateScriptAttributes(script, where, "language/timeoutMs/onError", errors);

        var onError = ((string?)script.Attribute("onError"))?.Trim();
        if (onError != null && !string.Equals(onError, "markBad", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"{where}：onError=\"{onError}\" 非法（当前只实现 markBad：质量置 Bad + 明确原因）");
        }

        if (script.Value.Trim().Length == 0)
        {
            errors.Add($"{where}：脚本正文为空（写最后一个表达式的值作为工程值，如 raw[0] * 0.1）");
        }
    }

    /// <summary>
    /// 脚本元素上的未知/已删除属性一律报错（不做兼容解析，同 CGV-25 口径）：
    /// 最典型的是 <c>maxMemoryMb</c>——它已按 ADR D28 删除（Jint 2.x 无内存上限 API）。
    /// </summary>
    private static void ValidateScriptAttributes(XElement script, string where, string allowed, List<string> errors)
    {
        foreach (var attribute in script.Attributes())
        {
            if (attribute.IsNamespaceDeclaration) continue;

            var name = attribute.Name.LocalName;
            if (name == "timeoutMs" || name == "onError" || (allowed.StartsWith("language", StringComparison.Ordinal) && name == "language"))
            {
                continue;
            }

            errors.Add(name == "maxMemoryMb"
                ? $"{where}：maxMemoryMb 已删除（Jint 2.x 无内存上限 API；脚本内存不受限是已知边界，见 ADR D41）"
                : $"{where}：未知属性 {name}（可用：{allowed}）");
        }
    }

    /// <summary>脚本超时必须 &gt; 0（0 或负数会让引擎的 TimeoutInterval 退化成 1ms，等于把该点位打死）。</summary>
    private static void ValidateScriptNumbers(XElement script, string where, List<string> errors)
    {
        var timeout = script.Attribute("timeoutMs");
        if (timeout == null) return;

        if (!int.TryParse(timeout.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var timeoutMs))
        {
            errors.Add($"{where}：timeoutMs=\"{timeout.Value}\" 不是合法整数（应为 > 0 的毫秒数）");
            return;
        }

        if (timeoutMs <= 0)
        {
            errors.Add($"{where}：timeoutMs={timeoutMs} 非法（必须 > 0 毫秒）");
        }
    }

    /// <summary>
    /// CGV-33：计算点必须**恰有**一种求值依据——<c>&lt;Expression&gt;</c> 或 <c>&lt;Script&gt;</c>；
    /// 两者都写或都不写都报错（此前「都没有」只在运行期静默给 Bad(ss.reason.notSupported)）。
    /// </summary>
    private static void ValidateCalculatedForm(SamplerConfiguration config, List<string> errors)
    {
        foreach (var set in config.PointSets)
        {
            foreach (var point in set.Calculated)
            {
                var hasExpression = !string.IsNullOrWhiteSpace(point.Expression);
                var hasScript = point.HasScript;

                if (hasExpression && hasScript)
                {
                    errors.Add($"计算点 {set.Id}/{point.Id}：Expression 与 Script 只能写一个（二者都会产出工程值，语义冲突）");
                }
                else if (!hasExpression && !hasScript)
                {
                    errors.Add($"计算点 {set.Id}/{point.Id}：必须写 <Expression> 或 <Script> 之一（没有求值依据）");
                }
            }
        }
    }

    /// <summary>
    /// CGV-19：调度参数非法——间隔必须 ≥ <see cref="GlobalOptions.MinIntervalMs"/>（调度节拍，也是最小间隔），
    /// 全局默认间隔同样受检；地址组上限必须 &gt; 0（否则分组退化、单次请求无从限制）；
    /// **请求超时必须 ≥ 1ms**（findings D47：0/负数会让驱动对每个请求立即判超时——
    /// <c>ModbusChannel.ReadOne</c> 对 <c>timeoutMs &lt;= 0</c> 直接返回 false——全点位离线，
    /// 加载期必须把这个纯配置矛盾报出来，而不是留到运行期靠退避刷屏）。
    /// </summary>
    private static void ValidateScheduleParameters(SamplerConfiguration config, List<string> errors)
    {
        var minInterval = GlobalOptions.MinIntervalMs;
        var global = config.Global;

        if (global.DefaultIntervalMs < minInterval)
        {
            errors.Add($"Global/Polling@defaultIntervalMs={global.DefaultIntervalMs} 非法：轮询间隔最小 {minInterval}ms（调度节拍）");
        }

        if (global.GroupLimitRegisters <= 0)
        {
            errors.Add($"Global/Scheduler@groupLimitRegisters={global.GroupLimitRegisters} 非法：地址组上限必须大于 0");
        }

        if (global.GroupLimitBits <= 0)
        {
            errors.Add($"Global/Scheduler@groupLimitBits={global.GroupLimitBits} 非法：地址组上限必须大于 0");
        }

        if (global.RequestTimeoutMs <= 0)
        {
            errors.Add($"Global/Polling@requestTimeoutMs={global.RequestTimeoutMs} 非法：请求超时必须 ≥ 1 毫秒"
                + "（0 或负数会让每个请求立即判超时 → 全部点位离线）");
        }

        foreach (var transport in config.Transports)
        {
            if (transport.RequestTimeoutMs <= 0)
            {
                errors.Add($"Transport {transport.Id} 的 requestTimeoutMs={transport.RequestTimeoutMs} 非法：请求超时必须 ≥ 1 毫秒"
                    + "（0 或负数会让每个请求立即判超时）");
            }
        }

        // 设备级超时未写时继承全局：全局本身合法才可能判定「设备显式写了 0」，
        // 否则全局那条已经报过，这里不再重复刷同一条继承来的矛盾。
        if (global.RequestTimeoutMs > 0)
        {
            foreach (var device in config.Devices)
            {
                if (device.RequestTimeoutMs <= 0)
                {
                    errors.Add($"Device {device.Id} 的 requestTimeoutMs={device.RequestTimeoutMs} 非法：请求超时必须 ≥ 1 毫秒"
                        + "（0 或负数会让每个请求立即判超时）");
                }
            }
        }

        foreach (var set in config.PointSets)
        {
            foreach (var point in set.Points.Concat(set.Calculated))
            {
                CheckInterval(point.IntervalMs, point.Mode, PointPath(set.Id, point.Id), errors);

                // CGV-21：once 只读一次，间隔无意义（写了必然是误配）
                if (point.IntervalMs.HasValue && IsOnce(point.Mode))
                {
                    errors.Add($"{PointPath(set.Id, point.Id)}：mode=\"once\" 不得同时写 intervalMs（只读一次，间隔无意义）");
                }
            }

            foreach (var block in set.Blocks)
            {
                CheckInterval(block.IntervalMs, block.Mode, $"Block {set.Id}/{block.Id}", errors);

                if (block.IntervalMs.HasValue && IsOnce(block.Mode))
                {
                    errors.Add($"Block {set.Id}/{block.Id}：mode=\"once\" 不得同时写 intervalMs（只读一次，间隔无意义）");
                }
            }
        }

        void CheckInterval(int? intervalMs, string mode, string path, List<string> target)
        {
            if (!intervalMs.HasValue) return;
            if (intervalMs.Value >= minInterval) return;

            target.Add($"{path}：intervalMs={intervalMs.Value} 非法（mode={mode}）：轮询间隔最小 {minInterval}ms、且必须大于 0");
        }
    }

    /// <summary>模式是否为 once（加载期已归一为小写，这里仍按不区分大小写比较以免调用方漏归一）。</summary>
    private static bool IsOnce(string? mode)
        => string.Equals(mode, "once", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// CGV-18：地址 + 有效长度越出区容量（65536）。同样覆盖 &lt;Slices&gt; 的每个片段，
    /// 否则片段会在运行期读到区外（静默坏值）。
    /// </summary>
    private static void ValidateAddressCapacity(PointSetConfig set, PointConfig point, List<string> errors)
    {
        if (point.IsCalculated) return;

        var path = PointPath(set.Id, point.Id);
        var length = EffectiveLength(point);

        if (point.Address < 0)
        {
            errors.Add($"{path} 地址非法：{point.Address}（区地址必须从 0 起）");
        }
        else if (point.Address + (long)length > AreaCapacity)
        {
            errors.Add($"{path} 地址+长度越出 {point.Area} 区容量：{point.Address} + {length} = {point.Address + length} > {AreaCapacity}"
                + $"（合法地址 0..{AreaCapacity - 1}）");
        }

        if (point.Slices == null) return;

        foreach (var slice in point.Slices)
        {
            if (slice.Address < 0 || slice.Address + (long)slice.Length > AreaCapacity)
            {
                errors.Add($"{path} 的 Slices/Slice 地址+长度越出 {point.Area} 区容量：address={slice.Address} length={slice.Length}"
                    + $"（合法地址 0..{AreaCapacity - 1}）");
            }
        }
    }

    /// <summary>
    /// CGV-28（与 Slices 跨度同口径，findings D48）：**单个点位的一次请求宽度**不得超过对应区的地址组上限
    /// （寄存器区 <c>Global/Scheduler@groupLimitRegisters</c> 默认 125；位区 <c>groupLimitBits</c> 默认 2000）。
    /// <para>
    /// 为什么必须在加载期拦：自动分组（<c>Scheduler.GroupPlan.Of</c>）只能切分「组」，
    /// **单个点位不可切分**——单点宽度超上限时会照发一次 `count &gt; 125` 的请求，
    /// 真设备回异常码 03，该点永远坏值，而配置加载期一句提示都没有（此前只在假链路下潜伏）。
    /// </para>
    /// Slices 点位按片段跨度（最左片段起点 → 最右片段末地址）计算：一次请求要覆盖的正是这个跨度
    /// （片段之间的空洞也要读回来，docs/01 §4.2）。
    /// </summary>
    private static void ValidateRequestWidth(SamplerConfiguration config, PointSetConfig set, PointConfig point, List<string> errors)
    {
        if (point.IsCalculated || point.Address < 0) return;

        var isBitArea = point.Area is RuntimeArea.Coil or RuntimeArea.DiscreteInput;
        var limit = isBitArea ? config.Global.GroupLimitBits : config.Global.GroupLimitRegisters;
        if (limit <= 0) return;   // 上限本身非法时由 CGV-19 报，别在这里重复刷屏

        var width = RequestWidthOf(point);
        if (width <= limit) return;

        errors.Add($"{PointPath(set.Id, point.Id)} 的一次请求宽度 {width} 超过{(isBitArea ? "位区" : "寄存器区")}的地址组上限 {limit}"
            + (point.Slices is { Count: > 0 }
                ? "（<Slices> 片段跨度，含中间空洞）"
                : "（点位有效长度）")
            + "：单点不可切分，会发出超上限的一次请求（Modbus 上限 125 寄存器 / 2000 位）"
            + "→ 请拆成多个点位、缩短长度，或调大 Global/Scheduler@groupLimitRegisters（位区 @groupLimitBits）");
    }

    /// <summary>点位在一次请求里要覆盖的宽度：连续点 = 有效长度；Slices 点 = 片段跨度（含空洞）。</summary>
    private static int RequestWidthOf(PointConfig point)
    {
        if (point.Slices is not { Count: > 0 } slices) return EffectiveLength(point);

        var start = int.MaxValue;
        var end = int.MinValue;
        foreach (var slice in slices)
        {
            var length = slice.Length < 1 ? 1 : slice.Length;
            if (slice.Address < start) start = slice.Address;
            if (slice.Address + length > end) end = slice.Address + length;
        }

        return end > start ? end - start : EffectiveLength(point);
    }

    /// <summary>
    /// CGV-28：&lt;Slices&gt; 片段**跨度**（最小 address → 最大 address+length，含中间空洞）不得超过
    /// 一次请求的地址组上限——运行期要求「片段连中间空洞合成一段读回来，不单独发多次请求」（docs/01 §4.2），
    /// 跨度超上限就根本读不回来，必须加载期拒绝（文案说明「片段太分散」并给出跨度与上限）。
    /// 上限按数据区取：寄存器区 <c>groupLimitRegisters</c>（默认 125），位区 <c>groupLimitBits</c>（默认 2000）。
    /// </summary>
    private static void ValidateSliceSpans(SamplerConfiguration config, PointSetConfig set, PointConfig point, List<string> errors)
    {
        if (point.Slices == null || point.Slices.Count == 0 || point.IsCalculated) return;

        var path = PointPath(set.Id, point.Id);
        var limit = point.Area is RuntimeArea.Coil or RuntimeArea.DiscreteInput
            ? config.Global.GroupLimitBits
            : config.Global.GroupLimitRegisters;

        var start = int.MaxValue;
        var end = int.MinValue;
        var badLength = false;

        foreach (var slice in point.Slices)
        {
            if (slice.Length < 1)
            {
                errors.Add($"{path} 的 <Slices>/Slice length={slice.Length} 非法（片段长度必须大于 0）");
                badLength = true;
                continue;
            }

            if (slice.Address < start) start = slice.Address;
            if (slice.Address + slice.Length > end) end = slice.Address + slice.Length;
        }

        if (badLength || end <= start) return;

        var span = end - start;
        if (span > limit)
        {
            errors.Add($"{path} 的 <Slices> 片段太分散：跨度 {span}（地址 {start}..{end - 1}）超过地址组上限 {limit}"
                + "（寄存器区 Global/Scheduler@groupLimitRegisters，位区 groupLimitBits）"
                + "→ 无法用一次请求把全部片段取回；请把片段排近一些，或拆成多个点位");
        }
    }

    /// <summary>
    /// CGV-31：<c>&lt;DependsOn&gt;&lt;PointRef&gt;</c> 引用的点位必须存在（悬空引用直接报错；
    /// 引用普通点作为依赖是正常的，不要求被引用点是计算点）。
    /// </summary>
    private static void ValidateDependsOnReferences(SamplerConfiguration config, List<string> errors)
    {
        if (config.PointSets.Count == 0) return;

        // 设备 → 点表：跨点表引用 deviceId/pointId 靠它解析
        var deviceSets = config.Devices.ToDictionary(d => d.Id, d => d.PointSetId, StringComparer.Ordinal);
        var idsBySet = config.PointSets.ToDictionary(
            s => s.Id,
            s => new HashSet<string>(AllPointIdsOf(s), StringComparer.Ordinal),
            StringComparer.Ordinal);

        foreach (var set in config.PointSets)
        {
            foreach (var point in set.Calculated)
            {
                foreach (var reference in point.DependsOn)
                {
                    ResolveReference(reference, set.Id, deviceSets, out var targetSet, out var targetId);
                    if (targetId.Length == 0) continue;

                    if (!idsBySet.TryGetValue(targetSet, out var ids) || !ids.Contains(targetId))
                    {
                        errors.Add($"{PointPath(set.Id, point.Id)} 的 <DependsOn><PointRef> 引用了不存在的点位 {reference}"
                            + (targetSet == set.Id ? string.Empty : $"（解析到点表 {targetSet}）"));
                    }
                }
            }
        }
    }

    /// <summary>点表内的全部点位 id（散点 + 块内 + 计算点，含位映射展开出来的子点位）。</summary>
    private static IEnumerable<string> AllPointIdsOf(PointSetConfig set)
        => set.Points.Select(p => p.Id)
            .Concat(set.Blocks.SelectMany(b => b.Points).Select(p => p.Id))
            .Concat(set.Calculated.Select(p => p.Id));

    /// <summary>
    /// 引用解析：裸 id = 同点表；<c>deviceId/pointId</c> = 经设备找到它的点表（跨点表引用）。
    /// 设备名解析不到时按「点表 id/点位 id」再试一次（与运行期 ResolveForExpression 的宽松口径一致）。
    /// </summary>
    private static void ResolveReference(
        string reference, string ownSetId, IReadOnlyDictionary<string, string> deviceSets,
        out string setId, out string pointId)
    {
        var text = reference.Trim();
        var slash = text.IndexOf('/');
        if (slash < 0)
        {
            setId = ownSetId;
            pointId = text;
            return;
        }

        var deviceId = text.Substring(0, slash);
        pointId = text.Substring(slash + 1).Trim();
        setId = deviceSets.TryGetValue(deviceId, out var setOfDevice) ? setOfDevice : deviceId;
    }

    /// <summary>
    /// CGV-14：同一点位多条报警的限值大小关系必须成立——
    /// lowLow &lt; low、low &lt; high、high &lt; highHigh。不成立即矛盾（阈值互斥，永远判不出合理报警）。
    /// digital 等不带 limit 的报警不参与比较。
    /// </summary>
    private static void ValidateAlarmLimits(PointSetConfig set, PointConfig point, List<string> errors)
    {
        if (point.Alarms.Count < 2) return;

        var limits = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var alarm in point.Alarms)
        {
            if (!alarm.Limit.HasValue) continue;
            if (limits.ContainsKey(alarm.Type)) continue;   // 同类型多条时取首条参与比较
            limits[alarm.Type] = alarm.Limit.Value;
        }

        var path = PointPath(set.Id, point.Id);

        CheckPair("lowlow", "low", "lowLow < low");
        CheckPair("low", "high", "low < high");
        CheckPair("high", "highhigh", "high < highHigh");

        void CheckPair(string lowerType, string upperType, string relation)
        {
            if (!limits.TryGetValue(lowerType, out var lower)) return;
            if (!limits.TryGetValue(upperType, out var upper)) return;
            if (lower < upper) return;

            errors.Add($"{path} 报警限值大小关系不成立：{AlarmTypeName(lowerType)}={lower} 必须小于 {AlarmTypeName(upperType)}={upper}（{relation}）");
        }
    }

    /// <summary>报警类型的显示名：解析期统一小写，报错时还原成配置里的写法（lowLow / highHigh）。</summary>
    private static string AlarmTypeName(string type)
        => type switch
        {
            "lowlow" => "lowLow",
            "highhigh" => "highHigh",
            _ => type,
        };

    /// <summary>合法的报警类型全集（findings D69：此处必须穷举，未知值运行期会被 <c>AlarmEngine</c> 静默忽略）。</summary>
    private static readonly string[] ValidAlarmTypes = { "high", "highhigh", "low", "lowlow", "digital" };

    /// <summary>限值型报警：必须有 <c>limit</c> 才判得出来（digital 不需要）。</summary>
    private static bool IsThresholdAlarmType(string type)
        => type is "high" or "highhigh" or "low" or "lowlow";

    // ═══════════════ CGV-35：数据区与宽度（findings D66）═══════════════

    /// <summary>
    /// CGV-35：**数据区与点位宽度的矛盾**（findings D66），两条都在加载期拒绝：
    /// <list type="number">
    /// <item><b>只读区（input / discrete）不得声明可写</b>：Modbus 的 0x03/0x04 区没有对应的写功能码，
    /// 真驱动在发帧前就判 Protocol 失败（<c>ModbusMaster.WriteSingle/WriteMulti</c>），引擎侧也只能拒绝
    /// （findings D65 的 <c>ss.reason.notWritable</c>）——「配了个永远写不进去的点位」是纯配置矛盾。</item>
    /// <item><b>非寄存器区（coil / discrete）不得声明多字类型</b>：位区每个地址只承载 1 位，
    /// 声明 32/64 位、string、BCD、datetime（或 <c>length&gt;1</c>、<c>&lt;Slices&gt;</c> 多片段）后，
    /// 写路径按区走单写**只发首字**（其余静默丢弃）、读路径也拼不出正确值
    /// （<c>SamplerEngine.WriteCoreInner</c> 只在寄存器区走 <c>WriteMulti</c>，findings D66 实测）。</item>
    /// </list>
    /// </summary>
    private static void ValidateAreaAndWidth(PointSetConfig set, PointConfig point, List<string> errors)
    {
        var path = PointPath(set.Id, point.Id);

        // ① 只读区不得可写
        if (point.IsWritable && point.Area is RuntimeArea.InputRegister or RuntimeArea.DiscreteInput)
        {
            errors.Add($"{path}：area={point.Area} 是只读区，不得声明 access={point.Access}"
                + "（Modbus 的输入寄存器/离散输入没有写功能码，写请求在真驱动上必然失败）"
                + "→ 请改用 holding/coil 区，或把 access 改回 read");
        }

        if (point.IsCalculated) return;   // 计算点不占地址，宽度规则不适用

        // ② 非寄存器区不得多字
        if (point.Area is not (RuntimeArea.Coil or RuntimeArea.DiscreteInput)) return;

        var multiWordType = point.DataType is RuntimeDataType.Int32 or RuntimeDataType.UInt32
            or RuntimeDataType.Float32 or RuntimeDataType.Int64 or RuntimeDataType.UInt64
            or RuntimeDataType.Float64 or RuntimeDataType.String or RuntimeDataType.Bcd
            or RuntimeDataType.DateTime;
        var width = EffectiveLength(point);

        if (!multiWordType && width <= 1) return;

        errors.Add($"{path}：area={point.Area} 是位区（每个地址 1 位），不得声明多字点位"
            + $"（dataType={point.DataType}、有效宽度 {width} 个字）"
            + "→ 位区只支持单字整数/布尔点位；多字值请改用 holding/input 区（写路径在位区只发首字，其余会静默丢弃，读也拼不出正确值）");
    }

    // ═══════════════ CGV-36：报警配置（findings D68~D72）═══════════════

    /// <summary>
    /// CGV-36：报警配置的加载期矛盾（findings D68~D72）。报警是「按配置保护设备」的语义，
    /// 写错一条就等于少了一条保护，绝不允许留到运行期静默失效：
    /// <list type="bullet">
    /// <item><b>D72</b>：计算点不得挂 <c>&lt;Alarm&gt;</c>——计算点不参与采集轮次，
    /// <c>AlarmEngine.Evaluate</c> 永远收不到它的值（实测求值 Good=4、报警 0 条）。</item>
    /// <item><b>D69</b>：<c>type</c> 必须穷举合法（high/highHigh/low/lowLow/digital）——
    /// 未知类型让 <c>IsCrossed</c> 恒 <c>null</c>，该报警永不触发也不报错。</item>
    /// <item><b>D70</b>：限值型报警必须给 <c>limit</c>（缺失/非数值/NaN/±Inf 都报错）——没有限值同样永远判不出来。</item>
    /// <item><b>D71</b>：<c>deadband ≥ 0</c>、<c>delayMs ≥ 0</c>——负死区会把清除阈值推到限值另一侧
    /// （值仍在限值之上却报清除，安全相关）；负延时是非法值。</item>
    /// <item><b>D68</b>：同一点位的报警**状态键**必须唯一。运行期键 = <c>Alarm@id</c>（写了）或
    /// <c>pointId#type</c>（没写，ADR D9）。两条都缺 id 的同类型报警会共用同一个状态 →
    /// 第二条永远发不出自己的激活事件、值回落时发出「仍在限值之上」的假清除。加载期直接拒绝。</item>
    /// </list>
    /// </summary>
    private static void ValidateAlarmRules(PointSetConfig set, PointConfig point, List<string> errors)
    {
        if (point.Alarms.Count == 0) return;

        var path = PointPath(set.Id, point.Id);

        // D72：计算点不得挂报警
        if (point.IsCalculated)
        {
            errors.Add($"{path}：计算点不得挂 <Alarm>（{point.Alarms.Count} 条）：计算点不在采集轮次里，"
                + "报警不会被评估（静默失效）→ 请把报警挂到采集点位上，判断逻辑写进计算点的表达式/脚本");
        }

        var keys = point.Alarms.Count > 1 ? new Dictionary<string, AlarmConfig>(StringComparer.Ordinal) : null;

        foreach (var alarm in point.Alarms)
        {
            var alarmWhere = $"{path} 的 <Alarm{(alarm.Id.Length > 0 ? " id=" + alarm.Id : string.Empty)} type={alarm.Type}>";

            // D69：类型穷举
            if (!ValidAlarmTypes.Contains(alarm.Type, StringComparer.Ordinal))
            {
                errors.Add($"{alarmWhere}：type=\"{alarm.Type}\" 非法（只支持 "
                    + string.Join(" / ", ValidAlarmTypes.Select(AlarmTypeName))
                    + "）——未知类型在运行期永不触发，也不会报错");
            }

            // D70：限值型报警必须有有限数值的 limit
            if (alarm.Limit.HasValue)
            {
                if (double.IsNaN(alarm.Limit.Value) || double.IsInfinity(alarm.Limit.Value))
                {
                    errors.Add($"{alarmWhere}：limit={alarm.Limit.Value} 非法（必须是有限数值）");
                }
            }
            else if (IsThresholdAlarmType(alarm.Type))
            {
                errors.Add($"{alarmWhere}：缺少 limit（{AlarmTypeName(alarm.Type)} 是限值型报警，"
                    + "没有限值就永远判不出来）→ 请写 limit，或改用 type=\"digital\"");
            }

            // D71：deadband / delayMs 非负（且有限）
            if (double.IsNaN(alarm.Deadband) || double.IsInfinity(alarm.Deadband))
            {
                errors.Add($"{alarmWhere}：deadband={alarm.Deadband} 非法（必须是有限数值）");
            }
            else if (alarm.Deadband < 0)
            {
                errors.Add($"{alarmWhere}：deadband={alarm.Deadband} 非法（必须 ≥ 0）：负死区会把清除阈值"
                    + "推到限值另一侧，值仍在限值之上就会报「清除」（假清除）");
            }

            if (alarm.DelayMs < 0)
            {
                errors.Add($"{alarmWhere}：delayMs={alarm.DelayMs} 非法（必须 ≥ 0 毫秒）");
            }

            // D68：运行期状态键冲突（键 = Alarm@id 或 pointId#type，见 AlarmEngine.AlarmIdOf / ADR D9）
            if (keys == null) continue;

            var key = alarm.Id.Length > 0 ? alarm.Id : point.Id + "#" + alarm.Type;
            if (keys.TryGetValue(key, out var first))
            {
                errors.Add($"{path}：报警键冲突——两条 <Alarm> 的运行期状态键都是 \"{key}\""
                    + $"（{DescribeAlarm(first)} 与 {DescribeAlarm(alarm)}）：同键报警会共用状态、互相清除（假清除/漏报）"
                    + "→ 请给每条报警写唯一的 Alarm@id");
            }
            else
            {
                keys[key] = alarm;
            }
        }
    }

    private static string DescribeAlarm(AlarmConfig alarm)
        => (alarm.Id.Length > 0 ? "id=" + alarm.Id : "缺 id") + "/type=" + alarm.Type
           + (alarm.Limit.HasValue ? "/limit=" + alarm.Limit.Value.ToString(CultureInfo.InvariantCulture) : string.Empty);

    // ═══════════════ CGV-38：点位数值边界 ═══════════════

    /// <summary>
    /// CGV-38：点位数值属性的边界（`bit` 越界此前被静默当成「整字点位」）。
    /// <c>bit</c> 只允许 0..15（一个寄存器 16 位）；<c>bitRange</c> 的边界由 CGV-30 负责。
    /// </summary>
    private static void ValidatePointNumbers(PointSetConfig set, PointConfig point, List<string> errors)
    {
        if (!point.Bit.HasValue) return;

        var bit = point.Bit.Value;
        if (bit < 0 || bit > 15)
        {
            errors.Add($"{PointPath(set.Id, point.Id)}：bit={bit} 越界（一个寄存器只有 0..15 位）"
                + "——越界的位此前会被静默忽略、点位退化成整字点位（读出完全不同的量纲）");
        }
    }

    /// <summary>
    /// CGV-17：同一寄存器同一位被两个点位重复声明。
    /// 边界（现有设计，别误报）：**整字点位不声明任何位**，因此「Holding@10 整字 + bit0」合法；
    /// 同址不同位（bit0 / bit1）也合法；只有位掩码实际相交才是矛盾。
    /// </summary>
    private static void ValidateDuplicateBitDeclarations(PointSetConfig set, List<string> errors)
    {
        var claims = new List<BitClaim>();
        foreach (var point in set.Points.Concat(set.Blocks.SelectMany(b => b.Points)))
        {
            if (point.Address < 0) continue;

            var mask = BitMaskOf(point);
            if (mask == 0) continue;   // 整字点位：不声明位，不参与

            var length = EffectiveLength(point);
            claims.Add(new BitClaim(point, point.Address, point.Address + (length < 1 ? 1 : length), mask));
        }

        for (var i = 0; i < claims.Count; i++)
        {
            for (var j = i + 1; j < claims.Count; j++)
            {
                var a = claims[i];
                var b = claims[j];

                // 地址范围不相交 → 无冲突（不同寄存器的同位不算重复）
                if (a.Start >= b.End || b.Start >= a.End) continue;

                var overlap = a.Mask & b.Mask;
                if (overlap == 0) continue;   // 同址不同位：允许

                var address = Math.Max(a.Start, b.Start);
                errors.Add($"点表 {set.Id}：点位 {a.Point.Id}（{BitSpecOf(a.Point)}）与点位 {b.Point.Id}（{BitSpecOf(b.Point)}）"
                    + $"在地址 {address} 重复声明了位 {FormatBits(overlap)}（同址不同位允许，同一位重复声明是矛盾）");
            }
        }
    }

    /// <summary>点位的位掩码：bit 单点 + bitRange 区间；0 表示不声明位（整字点位）。</summary>
    private static int BitMaskOf(PointConfig point)
    {
        var mask = 0;
        if (point.Bit.HasValue && point.Bit.Value >= 0 && point.Bit.Value <= 15)
            mask |= 1 << point.Bit.Value;

        if (TryParseBitRange(point.BitRange, out var from, out var to))
        {
            for (var bit = from; bit <= to; bit++) mask |= 1 << bit;
        }

        return mask;
    }

    /// <summary>解析 bitRange（形如 4-7，含端点，限 0..15）；口径与 RuntimePoint 一致。</summary>
    private static bool TryParseBitRange(string? text, out int from, out int to)
    {
        from = 0;
        to = -1;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var parts = text!.Split('-');
        if (parts.Length != 2) return false;
        if (!int.TryParse(parts[0].Trim(), out from) || !int.TryParse(parts[1].Trim(), out to)) return false;
        return from >= 0 && to >= from && to <= 15;
    }

    /// <summary>
    /// 位声明的原文（用于报错定位：bit=0 / bitRange=4-7）。
    /// 位映射展开出的子点位标出来源（Bits/Bit index=0 / Bits/Field 4-7），
    /// 让「同一寄存器同一位被两条 <c>&lt;Bits&gt;</c> 条目声明」这类矛盾一眼可见。
    /// </summary>
    private static string BitSpecOf(PointConfig point)
    {
        if (point.ParentPointId != null)
        {
            return point.Bit.HasValue
                ? "Bits/Bit index=" + point.Bit.Value
                : "Bits/Field " + (point.BitRange ?? string.Empty);
        }

        if (point.Bit.HasValue) return "bit=" + point.Bit.Value;
        return "bitRange=" + (point.BitRange ?? string.Empty);
    }

    /// <summary>把位掩码渲染成 0、1 或 4-7 这样的可读文本。</summary>
    private static string FormatBits(int mask)
    {
        var ranges = new List<string>();
        var bit = 0;
        while (bit < 16)
        {
            if ((mask & (1 << bit)) == 0)
            {
                bit++;
                continue;
            }

            var start = bit;
            while (bit + 1 < 16 && (mask & (1 << (bit + 1))) != 0) bit++;
            ranges.Add(start == bit ? start.ToString() : start + "-" + bit);
            bit++;
        }

        return string.Join(",", ranges);
    }

    /// <summary>
    /// CGV-16：块内点位地址必须能用**一次请求**覆盖。按「忽略间隔数」
    /// （Global/Scheduler@ignoreGap，0 = 必须严格连续）判定：空洞超过容差即报错并指出块与断点两侧点位。
    /// 地址互相重叠的点位（如整字 raw 包住内部寄存器）不算空洞。
    /// </summary>
    private static void ValidateBlockContiguity(PointSetConfig set, int ignoreGap, List<string> errors)
    {
        var tolerance = ignoreGap < 0 ? 0 : ignoreGap;

        foreach (var block in set.Blocks)
        {
            var ordered = block.Points
                .OrderBy(p => p.Address)
                .ThenBy(p => EffectiveLength(p))
                .ToList();
            if (ordered.Count < 2) continue;

            var previous = ordered[0];
            var coveredEnd = previous.Address + EffectiveLength(previous);

            for (var i = 1; i < ordered.Count; i++)
            {
                var point = ordered[i];
                var gap = point.Address - coveredEnd;

                if (gap > tolerance)
                {
                    errors.Add($"块内点位地址不连续：Block {block.Id}（PointSet {set.Id}）的点位 {previous.Id} 覆盖到 {coveredEnd}，"
                        + $"点位 {point.Id} 从 {point.Address} 开始，中间 {gap} 个地址空洞（超过忽略间隔数 {tolerance}）"
                        + "→ 无法用一次请求覆盖；请把点位排成连续地址（或调大 Global/Scheduler@ignoreGap）或拆成多个块");
                }

                var end = point.Address + EffectiveLength(point);
                if (end > coveredEnd)
                {
                    coveredEnd = end;
                    previous = point;
                }
            }
        }
    }

    /// <summary>
    /// CGV-15 / CGV-31：计算点循环依赖（含间接环与自引用）。
    /// 依赖图同时收两类引用——表达式里的 <c>P('id')</c> 与 <c>&lt;DependsOn&gt;&lt;PointRef&gt;</c>，
    /// 并且**跨点表引用也参与成环判定**（`deviceId/pointId` 经设备找到它的点表）。
    /// 节点键是「点表id/点位id」（点表之间允许点位重名，必须带点表才能唯一）；发现环即报出环路径 A 到 B 到 A。
    /// </summary>
    private static void ValidateCalculatedCycles(SamplerConfiguration config, List<string> errors)
    {
        var calculated = new List<CalculatedNode>();
        foreach (var set in config.PointSets)
        {
            foreach (var point in set.Calculated) calculated.Add(new CalculatedNode(set.Id, point));
        }

        if (calculated.Count == 0) return;

        var keys = new HashSet<string>(calculated.Select(c => CalcKey(c.SetId, c.Point.Id)), StringComparer.Ordinal);
        var setOfKey = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var node in calculated) setOfKey[CalcKey(node.SetId, node.Point.Id)] = node.SetId;

        // 设备 → 点表：跨点表引用（deviceId/pointId）靠它解析
        var deviceSets = config.Devices.ToDictionary(d => d.Id, d => d.PointSetId, StringComparer.Ordinal);

        var edges = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var node in calculated)
        {
            var targets = new List<string>();

            foreach (var reference in ReferencesOf(node.Point))
            {
                ResolveReference(reference, node.SetId, deviceSets, out var targetSet, out var targetId);
                if (targetId.Length == 0) continue;

                var key = CalcKey(targetSet, targetId);
                if (!keys.Contains(key)) continue;      // 非计算点（或被引用点不存在）：不参与成环判定
                if (!targets.Contains(key)) targets.Add(key);
            }

            edges[CalcKey(node.SetId, node.Point.Id)] = targets;
        }

        var state = new Dictionary<string, int>(StringComparer.Ordinal);   // 0 未访问 / 1 在栈上 / 2 已完成
        var reported = new HashSet<string>(StringComparer.Ordinal);
        var path = new List<string>();

        foreach (var node in calculated)
        {
            var key = CalcKey(node.SetId, node.Point.Id);
            if (state.TryGetValue(key, out var s) && s != 0) continue;
            Visit(key);
        }

        void Visit(string id)
        {
            state[id] = 1;
            path.Add(id);

            foreach (var next in edges[id])
            {
                if (!state.TryGetValue(next, out var nextState))
                {
                    Visit(next);
                }
                else if (nextState == 1)
                {
                    // 回边 → 找到环：从 path 里 next 首次出现处截到末尾，再补 next 形成闭合路径
                    var start = path.IndexOf(next);
                    var cycle = path.GetRange(start, path.Count - start);
                    cycle.Add(next);

                    if (reported.Add(next))
                    {
                        foreach (var node in cycle) reported.Add(node);
                        var sets = cycle.Skip(1).Select(node => setOfKey[node]).Distinct().ToList();
                        var where = sets.Count == 1 ? $"点表 {sets[0]}" : "点表 " + string.Join(" 与 ", sets);
                        errors.Add($"{where} 计算点循环依赖：{string.Join(" → ", cycle)}"
                            + "（P()/DependsOn 引用成环，永远算不出值）");
                    }
                }
            }

            path.RemoveAt(path.Count - 1);
            state[id] = 2;
        }
    }

    /// <summary>点位声明的一条位占用：寄存器区间 [Start, End) 与位掩码。</summary>
    private sealed class BitClaim
    {
        public BitClaim(PointConfig point, int start, int end, int mask)
        {
            Point = point;
            Start = start;
            End = end;
            Mask = mask;
        }

        public PointConfig Point { get; }
        public int Start { get; }
        public int End { get; }
        public int Mask { get; }
    }

    // ═══════════════ CGV-37 / CGV-38：XML 层的枚举穷举与数值边界 ═══════════════

    /// <summary>
    /// CGV-37：**枚举型属性穷举校验**（`Config/配置字段说明.md` CGV-22 清单 + 本轮补齐的缺口）。
    /// 这些位置此前会静默回落到缺省值，宿主看不出「我写的值没生效」：
    /// <list type="bullet">
    /// <item><c>HostConfig@unsupportedPolicy</c>（非法值静默按 warn）</item>
    /// <item><c>Global@swap</c> / <c>Device@swap</c> / <c>Block@swap</c> / <c>PointSet/Defaults@swap</c>（拼错就静默落回 CDAB）</item>
    /// <item><c>PointSet/Defaults@area|dataType|access</c>（默认值层的枚举此前完全不校验）</item>
    /// <item><c>Block@area</c>（块区拼错静默落回 holding）</item>
    /// <item><c>Format@mapOn</c>（CGV-22 已登记但没实现：非 raw 一律当 engineering）</item>
    /// <item><c>DateTime@format</c>（未知格式静默按 plc6 解 —— 6 字读成 4 字的量纲错）</item>
    /// <item><c>Scale/Clamp@mode</c>（未知值静默不钳制）</item>
    /// <item><c>Global/Quality@onCommError|@onCommErrorValue</c>（未知值静默落回 bad / 置空）</item>
    /// <item><c>Transport@handshake</c>（未知值静默按 none，串口流控失效）</item>
    /// </list>
    /// CGV-38：同一趟顺带做数值边界（<c>bit</c>/<c>length</c>/<c>unitId</c>/<c>decimals</c>/<c>digits</c>/
    /// <c>start</c>/<c>pulseMs</c>/<c>port</c>/<c>baudRate</c>/<c>dataBits</c>/超时与重试/死区与延时，
    /// 以及 <c>Write@min ≤ @max</c>）；负数/0/超大值带来的都是「静默错值」。
    /// </summary>
    private static void ValidateXmlEnumsAndBounds(XElement root, List<string> errors)
    {
        // ── 根 ──
        var policy = ((string?)root.Attribute("unsupportedPolicy"))?.Trim();
        if (policy != null && policy is not ("warn" or "error" or "ignore"))
        {
            errors.Add($"HostConfig：unsupportedPolicy=\"{policy}\" 非法（只支持 warn / error / ignore）");
        }

        // ── Global ──
        var global = root.Element("Global");
        CheckEnumValue(global, "swap", ParseSwap, "none/abcd/badc/cdab/dcba（或 none/byte/word/word_byte）", "Global", errors);
        var quality = global?.Element("Quality");
        CheckEnumValue(quality, "onCommError", ParseCommError, "bad / offline / uncertain", "Global/Quality", errors);
        CheckEnumValue(quality, "onCommErrorValue", ParseOnCommErrorValue, "keepLast / null", "Global/Quality", errors);
        CheckAtLeast(quality, "staleAfterMs", 0, "Global/Quality", errors);
        CheckAtLeast(global?.Element("Retry"), "count", 0, "Global/Retry", errors);
        CheckAtLeast(global?.Element("Retry"), "intervalMs", 0, "Global/Retry", errors);
        CheckAtLeast(global?.Element("Scheduler"), "ignoreGap", 0, "Global/Scheduler", errors);

        // ── 链路 ──
        foreach (var transport in root.Elements("Transports").Elements("Transport"))
        {
            var where = "Transport " + ((string?)transport.Attribute("id") ?? "(未命名)");
            CheckEnumValue(transport, "handshake", ParseHandshake, "none / xonxoff / rtscts / dtrdsr", where, errors);
            CheckBetween(transport, "port", 1, 65535, where, errors);
            CheckAtLeast(transport, "baudRate", 1, where, errors);
            CheckBetween(transport, "dataBits", 5, 8, where, errors);
            CheckAtLeast(transport, "connectTimeoutMs", 1, where, errors);
            CheckAtLeast(transport, "requestTimeoutMs", 1, where, errors);
            CheckAtLeast(transport, "gapMs", 0, where, errors);
            CheckAtLeast(transport, "readTimeoutMs", 0, where, errors);
            CheckAtLeast(transport, "writeTimeoutMs", 0, where, errors);

            var transportRetry = transport.Element("Retry");
            CheckAtLeast(transportRetry, "count", 0, where + "/Retry", errors);
            CheckAtLeast(transportRetry, "intervalMs", 0, where + "/Retry", errors);
        }

        // ── 设备（实例 + 模板）──
        foreach (var device in DeclaredDeviceElements(root))
        {
            var where = "Device " + ((string?)device.Attribute("id") ?? "(未命名)");
            CheckEnumValue(device, "swap", ParseSwap, "none/abcd/badc/cdab/dcba（或 none/byte/word/word_byte）", where, errors);
            CheckBetween(device, "unitId", 0, 255, where, errors);

            var deviceRetry = device.Element("Retry");
            CheckAtLeast(deviceRetry, "count", 0, where + "/Retry", errors);
            CheckAtLeast(deviceRetry, "intervalMs", 0, where + "/Retry", errors);
        }

        // ── 点表 / 块 ──
        foreach (var set in root.Elements("PointSets").Elements("PointSet"))
        {
            var setId = (string?)set.Attribute("id") ?? "(未命名)";

            var defaults = set.Element("Defaults");
            if (defaults != null)
            {
                var where = $"PointSet {setId}/Defaults";
                CheckEnumValue(defaults, "area", ParseArea, "coil / discrete / input / holding", where, errors);
                CheckEnumValue(defaults, "dataType", ParseDataType, "bool/int16/uint16/int32/uint32/int64/uint64/float32/float64/string/bcd/datetime/raw", where, errors);
                CheckEnumValue(defaults, "swap", ParseSwap, "none/abcd/badc/cdab/dcba（或 none/byte/word/word_byte）", where, errors);
                CheckEnumValue(defaults, "access", ParseAccess, "read / write / readwrite", where, errors);
                CheckBetween(defaults, "unitId", 0, 255, where, errors);
            }

            foreach (var block in set.Elements("Blocks").Elements("Block"))
            {
                var where = $"PointSet {setId}/Block {((string?)block.Attribute("id") ?? "(未命名)")}";
                CheckEnumValue(block, "area", ParseArea, "coil / discrete / input / holding", where, errors);
                CheckEnumValue(block, "swap", ParseSwap, "none/abcd/badc/cdab/dcba（或 none/byte/word/word_byte）", where, errors);
                CheckBetween(block, "unitId", 0, 255, where, errors);
                CheckAtLeast(block, "start", 0, where, errors);
            }
        }

        // ── 点位（实例 + 模板）──
        foreach (var point in DeclaredPointElements(root))
        {
            // 定位与解析期同口径（「点位 点表/点位」）；模板里的声明标成「点位模板 名」
            var pointId = (string?)point.Attribute("id") ?? "(未命名)";
            var owningSet = point.Ancestors("PointSet").Select(a => (string?)a.Attribute("id"))
                .FirstOrDefault(id => !string.IsNullOrEmpty(id));
            var where = owningSet != null ? PointPath(owningSet!, pointId) : "点位模板 " + pointId;

            CheckAtLeast(point, "length", 1, where, errors, "（0 表示按 dataType 推导，直接不写该属性即可）");
            CheckBetween(point, "unitId", 0, 255, where, errors);
            // bit 的越界由模型级 CGV-38（ValidatePointNumbers）判定：那里的文案能说明
            // 「越界位此前被静默忽略 → 点位退化成整字」，不在这里重复报

            var format = point.Element("Format");
            CheckBetween(format, "decimals", 0, 15, where + "/Format", errors);
            CheckEnumValue(format, "mapOn", ParseMapOn, "engineering / raw", where + "/Format", errors);

            CheckEnumValue(point.Element("DateTime"), "format", ParseDateTimeFormat, "plc6 / plc4 / unixsec / unixms", where + "/DateTime", errors);
            CheckBetween(point.Element("Bcd"), "digits", 1, 64, where + "/Bcd", errors);
            CheckEnumValue(point.Element("Scale")?.Element("Clamp"), "mode", ParseClampOrNull, "none / low / high / both", where + "/Scale/Clamp", errors);

            var scale = point.Element("Scale");
            if (scale != null)
            {
                foreach (var attribute in FiniteScaleAttributes) CheckFinite(scale, attribute, where + "/Scale", errors);

                var clamp = scale.Element("Clamp");
                if (clamp != null)
                {
                    foreach (var attribute in new[] { "low", "high", "min", "max" })
                        CheckFinite(clamp, attribute, where + "/Scale/Clamp", errors);
                }
            }

            var write = point.Element("Write");
            CheckAtLeast(write, "pulseMs", 0, where + "/Write", errors);
            CheckWriteRangeOrder(write, where + "/Write", errors);
            foreach (var attribute in new[] { "min", "max", "step" }) CheckFinite(write, attribute, where + "/Write", errors);
        }
    }

    /// <summary>枚举取值是否合法（<paramref name="parse"/> 返回 null 即非法）；只对「确实写了」的属性判定。</summary>
    private static void CheckEnumValue<T>(
        XElement? element, string attribute, Func<string?, T?> parse, string allowed, string where, List<string> errors)
        where T : struct
    {
        var raw = (string?)element?.Attribute(attribute);
        if (raw == null) return;
        if (parse(raw) != null) return;

        errors.Add($"{where}：{attribute}=\"{raw}\" 非法（只支持 {allowed}）");
    }

    /// <summary>枚举取值是否合法（字符串型枚举的解析函数返回 null 即非法）。</summary>
    private static void CheckEnumValue(
        XElement? element, string attribute, Func<string?, string?> parse, string allowed, string where, List<string> errors)
    {
        var raw = (string?)element?.Attribute(attribute);
        if (raw == null) return;
        if (parse(raw) != null) return;

        errors.Add($"{where}：{attribute}=\"{raw}\" 非法（只支持 {allowed}）");
    }

    /// <summary>整数属性必须 ≥ 下限（属性不存在或本身不是整数时跳过：前者合法、后者已由 IntAttr/专用规则报过）。</summary>
    private static void CheckAtLeast(
        XElement? element, string attribute, int min, string where, List<string> errors, string hint = "")
    {
        var raw = (string?)element?.Attribute(attribute);
        if (raw == null) return;
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)) return;
        if (value >= min) return;

        errors.Add($"{where}：{attribute}={value} 非法（必须 ≥ {min}）{hint}");
    }

    /// <summary>整数属性必须落在 [min, max]（非整数文本跳过：已由 IntAttr 报过）。</summary>
    private static void CheckBetween(
        XElement? element, string attribute, int min, int max, string where, List<string> errors)
    {
        var raw = (string?)element?.Attribute(attribute);
        if (raw == null) return;
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)) return;
        if (value >= min && value <= max) return;

        errors.Add($"{where}：{attribute}={value} 非法（合法范围 {min}..{max}）");
    }

    /// <summary><c>Write@min ≤ Write@max</c>（写范围自相矛盾时，任何值都会被拒或都不被拒）。</summary>
    private static void CheckWriteRangeOrder(XElement? write, string where, List<string> errors)
    {
        var minText = (string?)write?.Attribute("min");
        var maxText = (string?)write?.Attribute("max");
        if (minText == null || maxText == null) return;
        if (!double.TryParse(minText, NumberStyles.Float, CultureInfo.InvariantCulture, out var min)) return;
        if (!double.TryParse(maxText, NumberStyles.Float, CultureInfo.InvariantCulture, out var max)) return;
        if (min <= max) return;

        errors.Add($"{where}：min={minText} > max={maxText} 非法（写入范围自相矛盾，任何写入都会被拒）");
    }

    /// <summary>Scale 上参与换算的数值属性（NaN/±Inf 会让工程值恒为 Uncertain——ADR D32 的降级面）。</summary>
    private static readonly string[] FiniteScaleAttributes =
        { "factor", "offset", "rawLow", "rawHigh", "scaledLow", "scaledHigh" };

    /// <summary>
    /// 浮点属性必须是**有限数值**：NaN/±Inf 会让值处理管道产出恒定的坏/不确定值
    /// （ADR D32 只在**解码结果**上降级，配置本身就写 NaN 属于配置矛盾），加载期报错。
    /// 非数值文本不在这里报（已由 DoubleAttr/解析点带定位报过）。
    /// </summary>
    private static void CheckFinite(XElement? element, string attribute, string where, List<string> errors)
    {
        var raw = (string?)element?.Attribute(attribute);
        if (raw == null) return;

        double value;
        try
        {
            value = XmlConvert.ToDouble(raw);   // 与 (double?)attr 同一套解析（认 NaN / INF / -INF）
        }
        catch (Exception ex) when (ex is FormatException || ex is OverflowException)
        {
            return;
        }

        if (!double.IsNaN(value) && !double.IsInfinity(value)) return;

        errors.Add($"{where}：{attribute}={raw} 非法（必须是有限数值；NaN/±Inf 会让该点位的值恒为坏/不确定）");
    }

    /// <summary><c>unsupportedPolicy</c> 的取值（供 <see cref="ValidateXmlEnumsAndBounds"/> 判定）。</summary>
    private static string? ParseAccess(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var value = text!.Trim().ToLowerInvariant();
        return value is "read" or "write" or "readwrite" ? value : null;
    }

    private static string? ParseCommError(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var value = text!.Trim().ToLowerInvariant();
        return value is "bad" or "offline" or "uncertain" ? value : null;
    }

    private static string? ParseOnCommErrorValue(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var value = text!.Trim().ToLowerInvariant();
        return value is "keeplast" or "null" ? value : null;
    }

    private static string? ParseHandshake(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var value = text!.Trim().ToLowerInvariant();
        return value is "none" or "xonxoff" or "rtscts" or "dtrdsr" ? value : null;
    }

    private static string? ParseMapOn(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var value = text!.Trim().ToLowerInvariant();
        return value is "engineering" or "raw" ? value : null;
    }

    private static string? ParseDateTimeFormat(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var value = text!.Trim().ToLowerInvariant();
        return value is "plc6" or "plc4" or "unixsec" or "unixms" ? value : null;
    }

    /// <summary>Clamp@mode 的穷举（<see cref="ParseClamp"/> 对未知值静默返回 None，故这里自己判定）。</summary>
    private static string? ParseClampOrNull(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var value = text!.Trim().ToLowerInvariant();
        return value is "none" or "low" or "high" or "both" ? value : null;
    }

    private static void CheckI18nKeys(string? text, string where, SamplerConfiguration config, List<string> errors)
    {
        if (string.IsNullOrEmpty(text)) return;

        foreach (var match in I18nKeyPattern.Matches(text).Cast<Match>())
        {
            var key = match.Groups[1].Value;
            if (config.I18n.Resolve(key) == null)
            {
                errors.Add($"{where}：i18n key 不存在：{key}");
            }
        }
    }
}
