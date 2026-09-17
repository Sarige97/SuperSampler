using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SuperSampler.Core.Config;
using SuperSampler.Core.Runtime;

namespace InjectionLineMonitor;

/// <summary>
/// 宿主自己的点位索引。为什么需要：门面只提供 (deviceId, pointId) 取值的窄接口，
/// 「枚举全部点位、按名字反查、看点位定义（类型/区/地址/扫描组/报警）」这些宿主日常动作
/// 没有门面可用；这里直接用 Core 的 PointRegistry 拼一个。
/// 注意：不要用它替换门面读值，运行时值一律走 IDeviceManager.GetValueDetail。
/// </summary>
internal sealed class PointCatalog
{
    private readonly PointRegistry _registry;
    private readonly List<string> _deviceIds;

    public PointCatalog(SamplerConfiguration config)
    {
        _registry = new PointRegistry(config);
        _deviceIds = config.Devices.Select(d => d.Id).ToList();
    }

    public IReadOnlyList<string> DeviceIds => _deviceIds;

    public IEnumerable<RuntimePoint> PointsOf(string deviceId) => _registry.GetDevice(deviceId).Points.Values;

    public IEnumerable<RuntimePoint> AllPoints() => _deviceIds.SelectMany(PointsOf);

    /// <summary>块的 id 清单（用于打印解析摘要）。</summary>
    public IEnumerable<string> BlockIdsOf(string deviceId)
        => _registry.GetDevice(deviceId).Blocks.Select(b => b.Id + "(" + b.Area + "@" + b.Start + "+" + b.Count + ")");

    /// <summary>
    /// 点位引用解析：支持「设备/点位」全名，也支持点位 id 全局唯一时的简写（脚本里更好写）。
    /// </summary>
    public RuntimePoint Resolve(string reference)
    {
        var slash = reference.IndexOf('/');
        if (slash > 0 && slash < reference.Length - 1)
        {
            var deviceId = reference.Substring(0, slash);
            var pointId = reference.Substring(slash + 1);
            try
            {
                return _registry.GetPoint(deviceId, pointId);
            }
            catch (KeyNotFoundException ex)
            {
                throw new OptionException("点位不存在：" + reference + "（" + ex.Message + "）");
            }
        }

        var matches = _deviceIds
            .SelectMany(deviceId => PointsOf(deviceId))
            .Where(p => string.Equals(p.PointId, reference, StringComparison.Ordinal))
            .ToList();

        if (matches.Count == 1) return matches[0];
        if (matches.Count == 0) throw new OptionException("点位不存在：" + reference);

        throw new OptionException("点位 id 不唯一，请写「设备/点位」：" + reference
            + " → " + string.Join(", ", matches.Select(m => m.DeviceId + "/" + m.PointId)));
    }

    /// <summary>点位报警 id 清单，规则与框架 AlarmIdOf 一致：显式 @id 优先，缺省为「点位id#类型」。</summary>
    public static List<string> AlarmIdsOf(RuntimePoint point)
        => point.Source.Alarms
            .Select(a => a.Id.Length > 0 ? a.Id : point.PointId + "#" + a.Type)
            .ToList();

    /// <summary>点位定义的一行摘要（类型/区/地址/字序/扫描组/单位/可写性）。</summary>
    public static string Describe(RuntimePoint p)
    {
        var area = p.Area switch
        {
            RuntimeArea.Coil => "coil",
            RuntimeArea.DiscreteInput => "discrete",
            RuntimeArea.InputRegister => "input",
            RuntimeArea.HoldingRegister => "holding",
            _ => "?",
        };

        var address = p.IsCalculated
            ? "calc"
            : area + "@" + p.Address.ToString(CultureInfo.InvariantCulture)
              + (p.Length > 1 ? "+" + p.Length.ToString(CultureInfo.InvariantCulture) : string.Empty);

        var bits = p.Bit.HasValue
            ? " bit" + p.Bit.Value.ToString(CultureInfo.InvariantCulture)
            : p.BitFrom.HasValue
                ? " bits" + p.BitFrom.Value.ToString(CultureInfo.InvariantCulture) + "-" + p.BitTo!.Value.ToString(CultureInfo.InvariantCulture)
                : string.Empty;

        return address + " " + p.DataType.ToString().ToLowerInvariant() + bits
               + " unit=" + p.UnitId.ToString(CultureInfo.InvariantCulture)
               + " interval=" + (p.IntervalMs.HasValue
                   ? p.IntervalMs.Value.ToString(CultureInfo.InvariantCulture) + "ms"
                   : "auto")
               + " mode=" + p.Mode
               + (p.IsWritable ? " RW" : " R")
               + (p.Enabled ? string.Empty : " [disabled]");
    }
}
