using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using SuperSampler.Abstractions.Errors;
using SuperSampler.Abstractions.Events;
using SuperSampler.Abstractions.Values;
using SuperSampler.Core.Config;
using SuperSampler.Core.Events;
using SuperSampler.Drivers.Modbus;
using SuperSampler.Drivers.Modbus.Wire;

namespace SuperSampler.Core.Runtime;

/// <summary>
/// 轮询调度器：每个启用设备一条轮询线程，按扫描组到期执行。
/// 批处理单位：块（配置显式声明）与散点合并段（同区相邻地址、上限 125）。
/// 错误按「一次窗口失败 = 一条错误事件」聚合上报，避免事件风暴（docs/02 第 7 节）。
/// </summary>
public sealed class Scheduler : IDisposable
{
    private readonly InProcessEventBus _bus;
    private readonly PointRegistry _registry;
    private readonly ValueCache _cache;
    private readonly AlarmEngine _alarms;
    private readonly GlobalOptions _global;
    private readonly Dictionary<string, ScanGroupConfig> _groups;
    private readonly Dictionary<string, IModbusLink> _masters;
    private readonly List<Thread> _workers = new();
    private readonly CancellationTokenSource _cts = new();

    /// <summary>构造调度器。master 由调度器创建与释放（一条链路一个）；cache 由引擎注入共享。</summary>
    public Scheduler(InProcessEventBus bus, PointRegistry registry, ValueCache cache, SamplerConfiguration config)
        : this(bus, registry, cache, config, null)
    {
    }

    /// <summary>构造调度器（可注入链路：测试用假链路替换真实 Modbus 通道，findings B1）。</summary>
    public Scheduler(InProcessEventBus bus, PointRegistry registry, ValueCache cache, SamplerConfiguration config,
        IReadOnlyDictionary<string, IModbusLink>? masters)
    {
        _bus = bus;
        _registry = registry;
        _cache = cache;
        _alarms = new AlarmEngine(bus);
        _global = config.Global;
        _groups = config.ScanGroups.ToDictionary(g => g.Id, StringComparer.Ordinal);
        _masters = masters != null
            ? masters.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal)
            : config.Transports.ToDictionary(
                t => t.Id,
                t => (IModbusLink)new ModbusMaster(ParseVariant(t.Variant), ToTransportLike(t)),
                StringComparer.Ordinal);
    }

    /// <summary>启动全部启用设备的轮询线程。</summary>
    public void Start()
    {
        foreach (var device in _registry.Devices)
        {
            if (!device.Config.Enabled || device.Config.Paused) continue;

            var worker = new Thread(() => DeviceLoop(device))
            {
                IsBackground = true,
                Name = "SuperSampler.Poll[" + device.Id + "]",
            };
            _workers.Add(worker);
            worker.Start();
        }
    }

    /// <summary>停止轮询并释放全部主站。</summary>
    public void Dispose()
    {
        _cts.Cancel();
        foreach (var worker in _workers) worker.Join(2000);
        foreach (var master in _masters.Values) master.Dispose();
    }

    /// <summary>按设备所属链路取主站（调试工具复用，保证与轮询同通道排队、不插队）。</summary>
    internal bool TryGetMaster(string deviceId, out IModbusLink? master)
    {
        master = _registry.TryGetDevice(deviceId, out var device)
                 && device != null
                 && _masters.TryGetValue(device.Config.Transport, out var m)
            ? m
            : null;
        return master != null;
    }

    // ─────────────── 设备轮询线程 ───────────────

    private void DeviceLoop(RuntimeDevice device)
    {
        var token = _cts.Token;

        var groups = device.Blocks.Where(b => b.Enabled).Select(b => b.ScanGroup)
            .Concat(device.Points.Values.Where(p => !p.IsCalculated && p.Enabled).Select(p => p.ScanGroup))
            .Distinct()
            // onDemand 只由手动触发；大小写不敏感（findings D12：加载器存小写，曾致其被当周期轮询）
            .Where(g => !string.Equals(GetGroupMode(g), "onDemand", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var dueTimes = groups.ToDictionary(g => g, _ => DateTime.UtcNow);

        // once 组执行一次后不再排期（findings D11）
        var onceGroups = new HashSet<string>(
            groups.Where(g => string.Equals(GetGroupMode(g), "once", StringComparison.OrdinalIgnoreCase)),
            StringComparer.Ordinal);

        while (!token.IsCancellationRequested)
        {
            var now = DateTime.UtcNow;
            var nextWake = DateTime.MaxValue;

            foreach (var group in groups)
            {
                if (now >= dueTimes[group])
                {
                    try
                    {
                        ExecuteGroup(device, group);
                    }
                    catch
                    {
                        // 单组失败不终止轮询线程；错误已经由事件与缓存质量上报
                    }

                    dueTimes[group] = onceGroups.Contains(group)
                        ? DateTime.MaxValue
                        : now + TimeSpan.FromMilliseconds(ResolveRate(group));
                }

                if (dueTimes[group] < nextWake) nextWake = dueTimes[group];
            }

            var sleepMs = (int)Math.Min((nextWake - DateTime.UtcNow).TotalMilliseconds, 100);
            if (sleepMs > 0)
            {
                try
                {
                    token.WaitHandle.WaitOne(sleepMs);
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
            }
        }
    }

    private string GetGroupMode(string groupId)
        => _groups.TryGetValue(groupId, out var group) ? group.Mode : "poll";

    private int ResolveRate(string groupId)
    {
        var rate = _groups.TryGetValue(groupId, out var group) ? group.RateMs : _global.DefaultRateMs;
        return Math.Max(10, rate);
    }

    // ─────────────── 组执行 ───────────────

    private void ExecuteGroup(RuntimeDevice device, string group)
    {
        var master = _masters[device.Config.Transport];

        foreach (var block in device.Blocks.Where(b => b.Enabled && b.ScanGroup == group))
        {
            ReadWindow(device, master, block.Area, block.Start, block.Count, block.UnitId, block.Points, block.Start);
        }

        foreach (var batch in MergeStandalone(device, group))
        {
            ReadWindow(device, master, batch.Area, batch.Address, batch.Count, device.UnitId, batch.Points, batch.Address);
        }
    }

    private IEnumerable<ReadBatch> MergeStandalone(RuntimeDevice device, string group)
    {
        var batches = new List<ReadBatch>();
        foreach (var areaGroup in device.Points.Values
                     .Where(p => p.Enabled && !p.IsCalculated && p.ScanGroup == group)
                     .GroupBy(p => p.Area))
        {
            var pending = new List<RuntimePoint>();
            foreach (var point in areaGroup.OrderBy(p => p.Address))
            {
                if (pending.Count == 0)
                {
                    pending.Add(point);
                    continue;
                }

                var last = pending[pending.Count - 1];
                var pendingLength = pending.Sum(p => p.Length);

                // 单次读上限按数据区区分：寄存器 125、位 2000（findings W7 接线，值来自配置）
                var mergeLimit = areaGroup.Key is RuntimeArea.Coil or RuntimeArea.DiscreteInput
                    ? _global.MaxBitsPerRead
                    : _global.MaxRegistersPerRead;

                // 容差内合并（mergeGap=0 即严格相邻）；findings W7：上限与容差改为配置驱动
                var gap = point.Address - (last.Address + last.Length);
                if (gap >= 0 && gap <= _global.MergeGap && pendingLength + point.Length <= mergeLimit)
                {
                    pending.Add(point);
                }
                else
                {
                    batches.Add(ReadBatch.From(pending));
                    pending.Clear();
                    pending.Add(point);
                }
            }

            if (pending.Count > 0) batches.Add(ReadBatch.From(pending));
        }

        return batches;
    }

    // ─────────────── 单窗口读取与解码 ───────────────

    private void ReadWindow(
        RuntimeDevice device,
        IModbusLink master,
        RuntimeArea area,
        int address,
        int count,
        int unitId,
        IReadOnlyList<RuntimePoint> points,
        int windowStart)
    {
        var reply = master.Read(ToDriverArea(area), address, Math.Max(1, count), (byte)unitId,
            device.Config.RequestTimeoutMs, device.Config.RetryCount, device.Config.RetryIntervalMs);

        var timestamp = DateTimeOffset.UtcNow;

        if (!reply.Success)
        {
            EmitAggregateError(device, area, address, unitId, reply, points.Count);
            MarkWindowFailed(points, timestamp);
            return;
        }

        foreach (var point in points)
        {
            if (!point.Enabled) continue; // 停用的点位不写缓存、不报警
            if (point.Slices != null) continue; // 非连续片段点位 v1 仅在 TriggerRead 路径支持

            var offset = point.Address - windowStart;
            if (offset < 0 || offset + point.Length > reply.Registers.Length)
            {
                _cache.Set(point.Key, PointValue.Bad("ss.reason.shortFrame", timestamp));
                continue;
            }

            var slice = new ushort[point.Length];
            Array.Copy(reply.Registers, offset, slice, 0, point.Length);
            var value = PointCodec.Decode(point, slice, timestamp);
            PublishIfChanged(point, value);
            _alarms.Evaluate(point, value);
        }
    }

    /// <summary>
    /// 值或质量变化才写缓存并发事件。比较只看「值 + 质量 + 原因」，
    /// 不含时间戳——每轮时间戳都不同，若用全等比较事件会永远触发（findings D3）。
    /// 双 NaN 视为相等（NaN != NaN 的语言默认会破坏变化检测）。
    /// </summary>
    private static bool SameObservation(PointValue a, PointValue b)
    {
        if (a.Quality != b.Quality) return false;
        if (!string.Equals(a.Reason, b.Reason, StringComparison.Ordinal)) return false;

        if (a.Value is double ad && b.Value is double bd)
        {
            if (double.IsNaN(ad) && double.IsNaN(bd)) return true;
        }

        return Equals(a.Value, b.Value);
    }

    /// <summary>值或质量变化才写缓存并发事件；未变化静默，避免高频刷事件。</summary>
    private void PublishIfChanged(RuntimePoint point, PointValue value)
    {
        var previous = _cache.Get(point.Key);
        _cache.Set(point.Key, value);
        if (!SameObservation(previous, value))
        {
            _bus.Emit(new PointValueChangedEvent(point.DeviceId, point.PointId, value));
        }
    }

    private void MarkWindowFailed(IReadOnlyList<RuntimePoint> points, DateTimeOffset timestamp)
    {
        // keepLast：保留旧值不动（界面结合 staleAfterMs 置灰）
        if (string.Equals(_global.OnCommErrorValue, "keepLast", StringComparison.OrdinalIgnoreCase)) return;

        // 质量等级由 Global/Quality@onCommError 决定（bad|offline|uncertain），
        // 值策略由 onCommErrorValue 决定（已在上方处理）；findings W9 接线
        var failed = _global.OnCommError switch
        {
            "offline" => PointValue.Offline(timestamp),
            "uncertain" => PointValue.Uncertain(null, "ss.reason.comm", timestamp),
            _ => PointValue.Bad("ss.reason.comm", timestamp),
        };

        foreach (var point in points)
        {
            // 走带变化检测的写入路径：质量变化必须发事件（findings D13——订阅方与下游需感知离线）
            PublishIfChanged(point, failed);
        }
    }

    /// <summary>一个读取窗口失败只发一条聚合错误事件，ConsecutiveFailures 记录受影响点位数。</summary>
    private void EmitAggregateError(
        RuntimeDevice device,
        RuntimeArea area,
        int address,
        int unitId,
        ModbusReply reply,
        int affectedPoints)
    {
        var context = new ErrorContext(
            DeviceId: device.Id,
            TransportId: device.Transport.Id,
            UnitId: unitId,
            Area: area.ToString(),
            Address: address,
            ConsecutiveFailures: affectedPoints,
            ElapsedMs: reply.ElapsedMs);

        var code = reply.Kind == ModbusFailureKind.Protocol
            ? "MODBUS.EXCEPTION." + reply.ExceptionCode.ToString("X2")
            : reply.Kind == ModbusFailureKind.Timeout
                ? "MODBUS.TIMEOUT"
                : "MODBUS.LINK";

        var info = new ErrorInfo(
            code,
            EventCategory.Device,
            EventLevel.Error,
            ErrorSource.Device,
            reply.Kind == ModbusFailureKind.Protocol ? "ss.error.modbusException" : "ss.error.comm",
            context);

        IErrorEvent error = reply.Kind switch
        {
            ModbusFailureKind.Timeout => new TimeoutError(info),
            ModbusFailureKind.LinkDown => new LinkError(info),
            _ => new DeviceExceptionError(info, reply.ExceptionCode),
        };

        _bus.Emit(error);
    }

    // ─────────────── 内部类型与转换 ───────────────

    private sealed class ReadBatch
    {
        public RuntimeArea Area { get; private set; }
        public int Address { get; private set; }
        public int Count { get; private set; }
        public IReadOnlyList<RuntimePoint> Points { get; private set; } = Array.Empty<RuntimePoint>();

        public static ReadBatch From(IReadOnlyList<RuntimePoint> points)
        {
            var first = points[0];
            var last = points[points.Count - 1];
            return new ReadBatch
            {
                Area = first.Area,
                Address = first.Address,
                Count = (last.Address + last.Length) - first.Address,
                Points = points,
            };
        }
    }

    /// <summary>Core 数据区 → 驱动层数据区（驱动不依赖 Core，见分层）。</summary>
    private static DataArea ToDriverArea(RuntimeArea area)
    {
        return area switch
        {
            RuntimeArea.Coil => DataArea.Coil,
            RuntimeArea.DiscreteInput => DataArea.DiscreteInput,
            RuntimeArea.InputRegister => DataArea.InputRegister,
            _ => DataArea.HoldingRegister,
        };
    }

    private static ChannelVariant ParseVariant(string variant)
    {
        return variant switch
        {
            "rtu" => ChannelVariant.Serial,
            "rtuovertcp" => ChannelVariant.RtuOverTcp,
            "udp" => ChannelVariant.Tcp, // v1：UDP 按 TCP 通道处理（占位）
            _ => ChannelVariant.Tcp,
        };
    }

    private static TransportLike ToTransportLike(TransportConfig t)
    {
        return new TransportLike
        {
            Host = t.Host,
            Port = t.Port,
            PortName = t.PortName,
            BaudRate = t.BaudRate,
            DataBits = t.DataBits,
            Parity = t.Parity,
            StopBits = t.StopBits,
            ConnectTimeoutMs = t.ConnectTimeoutMs,
            RequestTimeoutMs = t.RequestTimeoutMs,
            GapMs = t.GapMs,
            // findings W13：串口流控与读写超时接线
            Handshake = t.Handshake,
            DtrEnable = t.DtrEnable,
            RtsEnable = t.RtsEnable,
            ReadTimeoutMs = t.ReadTimeoutMs,
            WriteTimeoutMs = t.WriteTimeoutMs,
        };
    }
}
