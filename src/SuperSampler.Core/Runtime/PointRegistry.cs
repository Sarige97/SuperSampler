using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using SuperSampler.Abstractions.Values;
using SuperSampler.Core.Config;

namespace SuperSampler.Core.Runtime;

/// <summary>寻址主键：deviceId + 点名（点表内唯一）。见 docs/04 第 1 节三层身份。</summary>
public static class PointKey
{
    public static string Of(string deviceId, string pointId) => deviceId + "/" + pointId;
}

/// <summary>
/// 解析完成的运行时点位：配置已继承、地址已换算、从站号已落实。
/// 不可变；调度器、写管道、门面都只读它。
/// </summary>
public sealed class RuntimePoint
{
    public string DeviceId { get; }
    public string PointId { get; }
    public string Key { get; }
    public string? Name { get; }

    public RuntimeArea Area { get; }
    public int Address { get; }
    public int Length { get; }
    public RuntimeDataType DataType { get; }
    public SwapMode Swap { get; }

    public int? Bit { get; }
    public int? BitFrom { get; }   // bitRange 起位（含）
    public int? BitTo { get; }     // bitRange 止位（含）

    public IReadOnlyList<SliceConfig>? Slices { get; }

    public bool IsWritable { get; }
    public string? Unit { get; }
    public string ScanGroup { get; }
    public int UnitId { get; }
    public string TransportId { get; }

    public ScaleConfig? Scale { get; }
    public FormatConfig? Format { get; }
    public WriteConfig? Write { get; }

    public string StringEncoding { get; }
    public bool StringTrimNull { get; }
    public int BcdDigits { get; }
    public string DateTimeFormat { get; }

    /// <summary>计算点：不占地址，读取时由表达式按需求值。</summary>
    public bool IsCalculated { get; }

    /// <summary>是否启用；false 不参与轮询（findings W17 接线）。</summary>
    public bool Enabled { get; }

    /// <summary>计算点表达式。</summary>
    public string? Expression { get; }

    public PointConfig Source { get; }

    public RuntimePoint(PointConfig source, DeviceConfig device)
    {
        Source = source;
        DeviceId = device.Id;
        PointId = source.Id;
        Key = PointKey.Of(device.Id, source.Id);
        Name = source.Name;

        Area = source.Area;
        Address = source.Address;
        DataType = source.DataType;
        Bit = source.Bit;
        Unit = source.Unit;
        ScanGroup = source.ScanGroup;
        Scale = source.Scale;
        Format = source.Format;
        Write = source.Write;
        Slices = source.Slices;

        IsWritable = source.IsWritable;
        StringEncoding = source.StringEncoding;
        StringTrimNull = source.StringTrimNull;
        BcdDigits = source.BcdDigits;
        DateTimeFormat = source.DateTimeFormat;
        IsCalculated = source.IsCalculated;
        Expression = source.Expression;
        Enabled = source.Enabled;

        // bitRange 解析为起止位（含端点）；非法区间留空并按普通 16 位处理
        if (source.BitRange != null)
        {
            var parts = source.BitRange.Split('-');
            if (parts.Length == 2
                && int.TryParse(parts[0], out var from)
                && int.TryParse(parts[1], out var to)
                && from >= 0 && to >= from && to <= 15)
            {
                BitFrom = from;
                BitTo = to;
            }
        }

        Length = source.Length > 0
            ? source.Length
            : Bit.HasValue || BitFrom.HasValue
                ? 1
                : DataType switch
                {
                    RuntimeDataType.Int32 => 2,
                    RuntimeDataType.UInt32 => 2,
                    RuntimeDataType.Float32 => 2,
                    RuntimeDataType.Int64 => 4,
                    RuntimeDataType.UInt64 => 4,
                    RuntimeDataType.Float64 => 4,
                    _ => 1,
                };

        // 设备级 swap 已在配置解析阶段作为点位缺省写入 source.Swap，这里直接采用
        Swap = source.Swap;
        UnitId = source.UnitIdOverride ?? device.UnitId;
        TransportId = device.Transport;
    }
}

/// <summary>运行时块：一次请求读一段连续地址，块内点位共享结果。</summary>
public sealed class RuntimeBlock
{
    public string Id { get; }
    public string DeviceId { get; }
    public RuntimeArea Area { get; }
    public int Start { get; }
    public int Count { get; }
    public string ScanGroup { get; }
    public int UnitId { get; }
    public SwapMode Swap { get; }
    public IReadOnlyList<RuntimePoint> Points { get; }

    /// <summary>是否启用；false 的块不参与轮询（findings W16 接线）。</summary>
    public bool Enabled { get; }

    public RuntimeBlock(BlockConfig source, DeviceConfig device)
    {
        Id = source.Id;
        DeviceId = device.Id;
        Area = source.Area;
        Start = source.Start;
        Count = source.Count;
        ScanGroup = source.ScanGroup;
        UnitId = source.UnitId ?? device.UnitId;
        Swap = source.Swap;
        Enabled = source.Enabled;
        Points = source.Points.Select(p => new RuntimePoint(p, device)).ToList();
    }
}

/// <summary>运行时设备：绑定了链路、从站号、扫描组与全部点位。</summary>
public sealed class RuntimeDevice
{
    public DeviceConfig Config { get; }
    public TransportConfig Transport { get; }
    public IReadOnlyDictionary<string, RuntimePoint> Points { get; }
    public IReadOnlyList<RuntimeBlock> Blocks { get; }

    public string Id => Config.Id;
    public int UnitId => Config.UnitId;

    public RuntimeDevice(DeviceConfig config, TransportConfig transport, PointSetConfig pointSet)
    {
        Config = config;
        Transport = transport;

        var points = new Dictionary<string, RuntimePoint>(StringComparer.Ordinal);
        foreach (var point in pointSet.Points)
        {
            points.Add(point.Id, new RuntimePoint(point, config));
        }

        // 计算点进注册表供门面读取，但不参与轮询（调度器按 IsCalculated 过滤）
        foreach (var calculated in pointSet.Calculated)
        {
            points.Add(calculated.Id, new RuntimePoint(calculated, config));
        }

        Points = points;
        Blocks = pointSet.Blocks.Select(b => new RuntimeBlock(b, config)).ToList();
    }
}

/// <summary>
/// 点位注册表：整份配置的运行时索引。
/// 三条查询路径：按 (deviceId, pointId)、按设备（浏览全部点）、按块 id。
/// </summary>
public sealed class PointRegistry
{
    private readonly Dictionary<string, RuntimeDevice> _devices;
    private readonly Dictionary<string, RuntimePoint> _points;
    private readonly Dictionary<string, RuntimeBlock> _blocks;

    public PointRegistry(SamplerConfiguration config)
    {
        var deviceMap = config.Devices.ToDictionary(d => d.Id, StringComparer.Ordinal);
        var pointSetMap = config.PointSets.ToDictionary(p => p.Id, StringComparer.Ordinal);
        var transportMap = config.Transports.ToDictionary(t => t.Id, StringComparer.Ordinal);

        _devices = new Dictionary<string, RuntimeDevice>(StringComparer.Ordinal);
        _points = new Dictionary<string, RuntimePoint>(StringComparer.Ordinal);
        _blocks = new Dictionary<string, RuntimeBlock>(StringComparer.Ordinal);

        foreach (var device in config.Devices)
        {
            var runtime = new RuntimeDevice(device, transportMap[device.Transport], pointSetMap[device.PointSetId]);
            _devices[device.Id] = runtime;

            foreach (var point in runtime.Points.Values) _points[point.Key] = point;
            foreach (var block in runtime.Blocks) _blocks[PointKey.Of(device.Id, block.Id)] = block;
        }
    }

    public RuntimeDevice GetDevice(string deviceId)
        => _devices.TryGetValue(deviceId, out var device)
            ? device
            : throw new KeyNotFoundException($"设备不存在：{deviceId}");

    public bool TryGetDevice(string deviceId, out RuntimeDevice? device)
    {
        device = _devices.TryGetValue(deviceId, out var d) ? d : null;
        return device != null;
    }

    public IEnumerable<RuntimeDevice> Devices => _devices.Values;

    public RuntimePoint GetPoint(string deviceId, string pointId)
        => _points.TryGetValue(PointKey.Of(deviceId, pointId), out var point)
            ? point
            : throw new KeyNotFoundException($"点位不存在：{deviceId}/{pointId}");

    public bool TryGetPoint(string deviceId, string pointId, out RuntimePoint? point)
    {
        point = _points.TryGetValue(PointKey.Of(deviceId, pointId), out var p) ? p : null;
        return point != null;
    }

    public RuntimeBlock GetBlock(string deviceId, string blockId)
        => _blocks.TryGetValue(PointKey.Of(deviceId, blockId), out var block)
            ? block
            : throw new KeyNotFoundException($"块不存在：{deviceId}/{blockId}");
}

/// <summary>
/// 实时值缓存：(deviceId, pointId) → {值, 质量, 时间戳}。线程安全。
/// 门面 GetValue/GetValueDetail 的唯一数据来源；通信失败时按全局策略保留旧值或置空。
/// </summary>
public sealed class ValueCache
{
    private readonly ConcurrentDictionary<string, PointValue> _values = new(StringComparer.Ordinal);

    /// <summary>
    /// 取实时值。key 不存在时返回 Bad（不是 default）——
    /// PointValue 是结构体，default 的质量恰好是 Good(0)，会伪装成可信数据，必须拦住。
    /// </summary>
    public PointValue Get(string key)
        => _values.TryGetValue(key, out var value)
            ? value
            : PointValue.Bad("ss.reason.notInCache", DateTimeOffset.MinValue);

    public PointValue Get(string deviceId, string pointId) => Get(PointKey.Of(deviceId, pointId));

    public void Set(string key, PointValue value) => _values[key] = value;
}
