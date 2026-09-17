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
/// 阶段二闸门（docs/11 §二「核查完毕后才根据 XML 实例化」）：
/// 运行时对象（注册表 / 调度器 / 引擎）只接受**已通过加载期全量校验**的配置。
/// 校验未过时配置里的链路、点表、点位可能缺失，硬跑下去只会得到半成品运行时状态。
/// </summary>
internal static class ConfigGuard
{
    /// <summary>配置未通过加载期全量校验时抛 <see cref="InvalidOperationException"/>。</summary>
    public static void RequireValidated(SamplerConfiguration config)
    {
        if (config.IsValidated) return;

        throw new InvalidOperationException(
            "配置未通过加载期全量校验（SamplerConfiguration.IsValidated=false）："
            + "运行时对象只能在 SamplerConfigLoader.Load/LoadFromXml 全量校验零错误返回后构造，"
            + "校验未过不得实例化引擎、注册表或调度器");
    }
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

    /// <summary>
    /// 该点位在一次请求里的占用起点：连续点 = <see cref="Address"/>；&lt;Slices&gt; 点 = 最小片段地址。
    /// 自动分组（Scheduler）按它排序与判断地址空洞。
    /// </summary>
    public int SpanStart { get; }

    /// <summary>
    /// 该点位在一次请求里的占用终点（开区间）：连续点 = Address + Length；&lt;Slices&gt; 点 = 最大片段末地址+长度。
    /// 与 <see cref="SpanStart"/> 一起构成「一次请求必须覆盖到的范围」（片段之间的空洞也在内）。
    /// </summary>
    public int SpanEnd { get; }

    /// <summary>位映射展开出的子点位所归属的整字点位 id；null = 本点位不是展开产物（docs/01 §4.1）。</summary>
    public string? ParentPointId { get; }

    public bool IsWritable { get; }
    public string? Unit { get; }

    /// <summary>读取模式：auto（周期读）| ondemand（只手动触发）| once（Start 后读一次）。加载期已归一为小写。</summary>
    public string Mode { get; }

    /// <summary>声明的轮询间隔（毫秒）；null = 未写，调度器取全局默认（<see cref="GlobalOptions.DefaultIntervalMs"/>）。</summary>
    public int? IntervalMs { get; }

    public int UnitId { get; }
    public string TransportId { get; }

    public ScaleConfig? Scale { get; }
    public FormatConfig? Format { get; }
    public WriteConfig? Write { get; }

    public string StringEncoding { get; }
    public bool StringTrimNull { get; }

    /// <summary>补齐字节（String@padding，默认 0x00；0x20 = 空格补齐）。</summary>
    public int StringPadding { get; }

    /// <summary>true = 字符串靠左、补齐在右（解码裁尾部）；false = 靠右、补齐在左。</summary>
    public bool StringPadLeft { get; }
    public int BcdDigits { get; }
    public string DateTimeFormat { get; }

    /// <summary>计算点：不占地址，读取时由表达式按需求值。</summary>
    public bool IsCalculated { get; }

    /// <summary>是否启用；false 不参与轮询（findings W17 接线）。</summary>
    public bool Enabled { get; }

    /// <summary>是否归属块读：块点位只随块窗口读取，不得被 MergeStandalone 当散点重复轮询（D16 配套）。</summary>
    public bool IsInBlock { get; }

    /// <summary>计算点表达式。</summary>
    public string? Expression { get; }

    /// <summary>
    /// 点位脚本正文（<c>Point/Script</c>，ADR D41）；非空时工程值由脚本产出（不再走 Scale）。
    /// 解码路径喂 <c>raw</c>/<c>rawValue</c>，计算点路径只喂 <c>P()/T()</c>。
    /// </summary>
    public string? Script { get; }

    /// <summary>脚本超时（毫秒）：点位覆盖值，未写时为 null（运行期用 <see cref="GlobalOptions.ScriptTimeoutMs"/>）。</summary>
    public int? ScriptTimeoutMs { get; }

    /// <summary>脚本失败策略：点位覆盖值，未写时为 null（运行期用 <see cref="GlobalOptions.ScriptOnError"/>）。</summary>
    public string? ScriptOnError { get; }

    /// <summary>是否带脚本（<c>Point/Script</c>）。</summary>
    public bool HasScript => !string.IsNullOrEmpty(Script);

    public PointConfig Source { get; }

    public RuntimePoint(PointConfig source, DeviceConfig device, bool isInBlock = false)
    {
        Source = source;
        DeviceId = device.Id;
        PointId = source.Id;
        Key = PointKey.Of(device.Id, source.Id);
        Name = source.Name;
        IsInBlock = isInBlock;

        Area = source.Area;
        Address = source.Address;
        DataType = source.DataType;
        Bit = source.Bit;
        Unit = source.Unit;
        Mode = source.Mode;
        IntervalMs = source.IntervalMs;
        Scale = source.Scale;
        Format = source.Format;
        Write = source.Write;
        Slices = source.Slices;

        IsWritable = source.IsWritable;
        StringEncoding = source.StringEncoding;
        StringTrimNull = source.StringTrimNull;
        StringPadding = source.StringPadding;
        StringPadLeft = source.StringPadLeft;
        BcdDigits = source.BcdDigits;
        DateTimeFormat = source.DateTimeFormat;
        IsCalculated = source.IsCalculated;
        Expression = source.Expression;
        Script = source.Script;
        ScriptTimeoutMs = source.ScriptTimeoutMs;
        ScriptOnError = source.ScriptOnError;
        Enabled = source.Enabled;
        ParentPointId = source.ParentPointId;

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

        Length = SamplerConfigLoader.EffectiveLength(source);

        // 一次请求必须覆盖的范围：连续点 = [Address, Address+Length)；Slices 点 = [最小片段地址, 最大片段末地址)
        // ——片段之间的空洞也要读回来（读回后按片段拼值），所以跨度取片段端点的并集（docs/01 §4.2）。
        if (source.Slices is { Count: > 0 } slices)
        {
            var start = int.MaxValue;
            var end = int.MinValue;
            foreach (var slice in slices)
            {
                var sliceStart = slice.Address;
                var sliceEnd = slice.Address + (slice.Length < 1 ? 1 : slice.Length);
                if (sliceStart < start) start = sliceStart;
                if (sliceEnd > end) end = sliceEnd;
            }

            SpanStart = start;
            SpanEnd = end;
        }
        else
        {
            SpanStart = Address;
            SpanEnd = Address + Length;
        }

        // swap 兜底链（ADR D38）：Point@swap > PointSet/Defaults@swap > Block@swap（块内） > Device@swap > Global@swap。
        // 只有「未显式声明」的点位才取设备级值——同一 PointSet 可被多设备共用，各自 swap 不同，
        // 所以设备级兜底只能在运行期（拿到 device 的这里）解析，绝不能在加载器里烧进共享的 PointConfig。
        Swap = source.HasSwapDeclared ? source.Swap : device.Swap;
        UnitId = source.UnitIdOverride ?? device.UnitId;
        TransportId = device.Transport;
    }

    /// <summary>
    /// 从一次请求读回的窗口寄存器里取出本点位要解码的那串寄存器：
    /// 连续点 = 窗口内 [Address, Address+Length) 的一段；&lt;Slices&gt; 点 = 按片段顺序拼接（片段之间的空洞丢弃）。
    /// 返回 false = 窗口没覆盖住本点位（短帧/窗口切分错误）——调用方置 Bad 质量，绝不抛。
    /// </summary>
    public bool TryExtract(ushort[] window, int windowStart, out ushort[] registers)
    {
        registers = Array.Empty<ushort>();
        if (window == null) return false;

        if (Slices is not { Count: > 0 } slices)
        {
            var offset = Address - windowStart;
            if (offset < 0 || offset + Length > window.Length) return false;

            registers = new ushort[Length];
            Array.Copy(window, offset, registers, 0, Length);
            return true;
        }

        var result = new ushort[Length];
        var written = 0;
        foreach (var slice in slices)
        {
            var length = slice.Length < 1 ? 1 : slice.Length;
            var offset = slice.Address - windowStart;
            if (offset < 0 || offset + length > window.Length || written + length > result.Length) return false;

            Array.Copy(window, offset, result, written, length);
            written += length;
        }

        registers = result;
        return written == result.Length;
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

    /// <summary>读取模式：auto（周期读）| ondemand（只手动触发）| once（Start 后读一次）。加载期已归一为小写。</summary>
    public string Mode { get; }

    /// <summary>声明的轮询间隔（毫秒）；null = 未写，调度器取全局默认（<see cref="GlobalOptions.DefaultIntervalMs"/>）。</summary>
    public int? IntervalMs { get; }

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
        Mode = source.Mode;
        IntervalMs = source.IntervalMs;
        UnitId = source.UnitId ?? device.UnitId;
        Swap = source.Swap;
        Enabled = source.Enabled;
        Points = source.Points.Select(p => new RuntimePoint(p, device, isInBlock: true)).ToList();
    }
}

/// <summary>运行时设备：绑定了链路、从站号与全部点位。</summary>
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

Blocks = pointSet.Blocks.Select(b => new RuntimeBlock(b, config)).ToList();

        // D16：块内点位也必须进设备点表，否则门面 GetValueDetail/GetValue 读不到（只被调度器缓存）。
        // 仅注册启用块的点位（禁用块不轮询、门面读不到是符合语义的）；与独立点位重名时后者优先。
        foreach (var block in Blocks)
        {
            if (!block.Enabled) continue;
            foreach (var blockPoint in block.Points)
            {
                if (!points.ContainsKey(blockPoint.PointId))
                {
                    points.Add(blockPoint.PointId, blockPoint);
                }
            }
        }

        Points = points;
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
        if (config == null) throw new ArgumentNullException(nameof(config));

        // 阶段二闸门：只有加载期全量校验零错误的配置才允许实例化运行时索引。
        // 半成品配置（缺链路/点表/点位）在这里会被拦下，而不是运行到一半抛 KeyNotFound。
        ConfigGuard.RequireValidated(config);

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
/// 另存「上次成功采集时刻」（ADR D40 的 <c>GetValueAge</c> 数据来源）：通讯失败不刷新它。
/// </summary>
public sealed class ValueCache
{
    private readonly ConcurrentDictionary<string, PointValue> _values = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _acquiredAt = new(StringComparer.Ordinal);

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

    /// <summary>
    /// 记一次**成功采集**（请求成功、该点完成解码落缓存；解码降级为 Uncertain/Bad 也算）。
    /// 通讯失败置坏不调用——它不是「成功采集」，<c>GetValueAge</c> 必须能区分两者。
    /// </summary>
    public void MarkAcquired(string key, DateTimeOffset at) => _acquiredAt[key] = at;

    /// <summary>距上次成功采集的时长；从未成功采集返回 null（由宿主决定陈旧阈值）。</summary>
    public TimeSpan? GetAge(string key, DateTimeOffset now)
        => _acquiredAt.TryGetValue(key, out var at) ? now - at : (TimeSpan?)null;
}
