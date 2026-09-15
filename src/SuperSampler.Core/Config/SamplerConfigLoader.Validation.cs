using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace SuperSampler.Core.Config;

/// <summary>
/// <see cref="SamplerConfigLoader"/> 的引用校验部分：
/// CGV-8（dataType/length 位宽）、CGV-9（PLC 前缀推区并校验一致）、CGV-10（read 与 Write 互斥）、
/// CGV-13（alarm 优先级存在）、i18n key 存在性、Command 步骤点位存在性。
/// </summary>
public static partial class SamplerConfigLoader
{
    private static readonly Regex I18nKeyPattern = new(@"\$\{([^}]+)\}", RegexOptions.Compiled);

    /// <summary>解析报警等级与命令（供校验），并执行跨实体引用校验。</summary>
    private static void ValidateReferences(XElement root, SamplerConfiguration config, List<string> errors)
    {
        var alarmClassIds = new HashSet<string>(
            root.Elements("AlarmClasses").Elements("AlarmClass")
                .Select(a => (string?)a.Attribute("id") ?? string.Empty)
                .Where(id => id.Length > 0),
            StringComparer.Ordinal);

        // ── 报警：优先级必须指向存在的报警等级 ──
        foreach (var point in AllPoints(config))
        {
            foreach (var alarm in point.Alarms)
            {
                var priority = alarm.Priority;
                if (priority != null && priority.Length > 0 && !alarmClassIds.Contains(priority))
                {
                    errors.Add($"点位 {point.DeviceId}/{point.Id} 的报警引用了不存在的等级 {alarm.Priority}");
                }
            }
        }

        // ── 命令：步骤引用的点位必须存在（点位 id 在点表内唯一，命令按 id 检索）──
        var pointIds = new HashSet<string>(
            AllPoints(config).Select(p => p.Id),
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
        foreach (var point in AllPoints(config))
        {
            CheckI18nKeys(point.Name, $"点位 {point.DeviceId}/{point.Id}.name", config, errors);
            if (point.Format != null)
            {
                CheckI18nKeys(point.Format.Prefix, $"点位 {point.DeviceId}/{point.Id}.prefix", config, errors);
                CheckI18nKeys(point.Format.Suffix, $"点位 {point.DeviceId}/{point.Id}.suffix", config, errors);
            }

            foreach (var alarm in point.Alarms)
            {
                CheckI18nKeys(alarm.Message, $"点位 {point.DeviceId}/{point.Id}.alarm", config, errors);
            }
        }
    }

    /// <summary>CGV-8/9/10：单点位规则（length 位宽、plc 前缀推区、read 与 Write 互斥）。</summary>
    private static void ValidatePointRules(PointConfig point, XElement element, List<string> errors)
    {
        var path = point.DeviceId.Length > 0 ? point.DeviceId + "/" + point.Id : point.Id;

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
                errors.Add($"点位 {path}：length={point.Length} 与 dataType={point.DataType} 的位宽 {expected} 不符");
            }
        }

        // CGV-9：addrFormat=plc 时，地址前缀必须与 area 一致（3xxxx 只能是 InputRegister 等）
        if (string.Equals((string?)element.Attribute("addrFormat"), "plc", StringComparison.OrdinalIgnoreCase))
        {
            var raw = (int?)element.Attribute("address") ?? 0;
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
                errors.Add($"点位 {path}：PLC 地址 {raw} 的区段（{prefixArea.Value}）与 area={point.Area} 不一致");
            }
        }

        // CGV-10：只读点不得带 Write 元素
        if (!point.IsWritable && element.Element("Write") != null)
        {
            errors.Add($"点位 {path}：access={point.Access} 但声明了 Write（只读点不可写，二者互斥）");
        }
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

    private static IEnumerable<PointConfig> AllPoints(SamplerConfiguration config)
    {
        return config.PointSets.SelectMany(s => s.Points.Concat(s.Calculated))
            .Concat(config.PointSets.SelectMany(s => s.Blocks).SelectMany(b => b.Points));
    }
}
