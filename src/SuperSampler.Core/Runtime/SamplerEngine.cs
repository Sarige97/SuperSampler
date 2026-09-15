using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SuperSampler.Abstractions.Errors;
using SuperSampler.Abstractions.Events;
using SuperSampler.Abstractions.Facade;
using SuperSampler.Abstractions.Values;
using SuperSampler.Core.Config;
using SuperSampler.Core.Events;
using SuperSampler.Core.Runtime;
using SuperSampler.Drivers.Modbus;
using SuperSampler.Drivers.Modbus.Wire;

namespace SuperSampler.Core.Runtime;

/// <summary>
/// 引擎：装配配置加载、注册表、缓存、事件总线、调度器，并实现两个宿主门面。
/// 门面遵守 docs/04 第 4 节纪律：薄壳；业务在内部服务里。
/// </summary>
public sealed class SamplerEngine : IDeviceManager, IModbusDebugTool, IDisposable
{
    private readonly SamplerConfiguration _config;
    private readonly PointRegistry _registry;
    private readonly ValueCache _cache = new();
    private readonly InProcessEventBus _bus = new();
    private readonly Scheduler _scheduler;
    private int _disposed;

    /// <summary>事件总线：宿主订阅错误事件（值/写事件 v1 尚未发出，见开放问题）。</summary>
    public InProcessEventBus Bus => _bus;

    /// <summary>从 XML 配置路径构造引擎（不启动）。</summary>
    public SamplerEngine(string configXmlPath)
        : this(SamplerConfigLoader.LoadFromXml(configXmlPath))
    {
    }

    /// <summary>从已加载配置构造引擎。</summary>
    public SamplerEngine(SamplerConfiguration config)
        : this(config, null)
    {
    }

    /// <summary>构造引擎（可注入假链路做确定性测试，findings B1）。</summary>
    public SamplerEngine(SamplerConfiguration config, IReadOnlyDictionary<string, IModbusLink>? masters)
    {
        _config = config;
        _registry = new PointRegistry(config);
        _scheduler = new Scheduler(_bus, _registry, _cache, config, masters);
    }

    /// <summary>启动全部轮询。</summary>
    public void Start() => _scheduler.Start();

    /// <summary>停止全部轮询。</summary>
    public void Stop() => _scheduler.Dispose();

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _scheduler.Dispose();
        _bus.Dispose();
    }

    // ═══════════════ IDeviceManager ═══════════════

    /// <inheritdoc />
    public string GetValue(string deviceId, string pointId)
    {
        var point = _registry.GetPoint(deviceId, pointId);
        var value = point.IsCalculated ? EvaluateCalculated(point) : _cache.Get(point.Key);
        return PointCodec.Format(point, value, _config.Global.NullText, value.Value);
    }

    /// <inheritdoc />
    public PointValue GetValueDetail(string deviceId, string pointId)
    {
        var point = _registry.GetPoint(deviceId, pointId);
        return point.IsCalculated ? EvaluateCalculated(point) : _cache.Get(point.Key);
    }

    /// <inheritdoc />
    public Task<WriteResult> SetValueAsync(string deviceId, string pointId, object? value, CancellationToken ct = default)
        => SetValueAsync(deviceId, pointId, value, new ActingUser("Local", "Local"), ct);

    /// <inheritdoc />
    public Task<WriteResult> SetValueAsync(string deviceId, string pointId, object? value, ActingUser user, CancellationToken ct = default)
    {
        if (user == null) throw new ArgumentNullException(nameof(user));
        return Task.Run(() =>
        {
            var result = WriteCoreInner(deviceId, pointId, value, user);

            // 写审计：无论成败都发一条（docs/04 第 3 节；宿主落审计库）
            _bus.Emit(new PointWrittenEvent(deviceId, pointId, value, user.Name,
                result.Outcome.ToString(), result.Error?.MessageKey));

            return result;
        }, ct);
    }

    /// <summary>实际写管道。</summary>
    private WriteResult WriteCoreInner(string deviceId, string pointId, object? value, ActingUser user)
    {
        var point = _registry.GetPoint(deviceId, pointId);

        // ① 可写性：门面契约「能不能写看 access」
        if (!point.IsWritable)
        {
            return Reject(point, "ss.reason.notWritable", "点位不可写（access）");
        }

        // ② 范围校验（数值型；字符串/布尔点不做范围）
        if (point.Write != null && TryNumber(value, out var number))
        {
            if (point.Write.Min.HasValue && number < point.Write.Min.Value)
            {
                return Reject(point, "ss.reason.belowMin", "低于下限 " + point.Write.Min.Value);
            }

            if (point.Write.Max.HasValue && number > point.Write.Max.Value)
            {
                return Reject(point, "ss.reason.aboveMax", "超过上限 " + point.Write.Max.Value);
            }
        }

        // ③ 联锁：v1 未实现评估（配置已解析），按通过处理——见 docs/04 开放问题

        // ④ 编码（位点走读-改-写：直接写会把同寄存器其他 15 位清零）
        var unitId = (byte)point.UnitId;
        ushort[] registers;
        if (point.Bit.HasValue)
        {
            var current = TryReadRawRegisters(point, unitId);
            if (current == null || current.Length == 0)
            {
                return new WriteResult(WriteOutcome.Failed,
                    new ErrorInfo("MODBUS.LINK", EventCategory.Write, EventLevel.Error, ErrorSource.Device,
                        "ss.error.writeFailed",
                        new ErrorContext(DeviceId: point.DeviceId, PointId: point.PointId,
                            Address: point.Address, UnitId: unitId)),
                    null);
            }

            if (!MergeBit(point, current[0], value, out var merged))
            {
                return Reject(point, "ss.reason.encode", "位写入值非法（应为布尔或 0/1）");
            }

            registers = new ushort[] { merged };
        }
        else
        {
            try
            {
                registers = PointCodec.Encode(point, value);
            }
            catch (Exception ex)
            {
                return Reject(point, "ss.reason.encode", "编码失败：" + ex.Message);
            }
        }

        // ⑤ 通道与下发
        if (!_scheduler.TryGetMaster(deviceId, out var master) || master == null)
        {
            return Reject(point, "ss.reason.noChannel", "设备无可用链路");
        }

        var timeout = _config.Global.RequestTimeoutMs;
        ModbusReply reply;

        if (point.Area == RuntimeArea.HoldingRegister && point.Length > 1)
        {
            reply = master.WriteMulti(ToDriverArea(RuntimeArea.HoldingRegister), point.Address, registers, unitId, timeout, RetryCount(point), RetryInterval(point));
        }
        else
        {
            reply = master.WriteSingle(ToDriverArea(point.Area), point.Address, registers[0], unitId, timeout, RetryCount(point), RetryInterval(point));
        }

        // ⑥ 三态判定：Indeterminate 必须回读确认，绝不自动重写（docs/02 第 6 节）
        if (reply.Success)
        {
            // 点动脉冲：写 true 后延时自动写回 false（findings W22 接线）
            if (point.Write is { PulseMs: > 0 } pulse && IsTrueValue(value))
            {
                System.Threading.Thread.Sleep(pulse.PulseMs);
                if (point.Area == RuntimeArea.HoldingRegister && point.Length > 1)
                {
                    master.WriteMulti(ToDriverArea(point.Area), point.Address, new ushort[point.Length], unitId, timeout, RetryCount(point), RetryInterval(point));
                }
                else
                {
                    master.WriteSingle(ToDriverArea(point.Area), point.Address, 0, unitId, timeout, RetryCount(point), RetryInterval(point));
                }
            }

            // 写后回读校验：通讯成功但值不符 → 打标记而非报失败（D10 决策③：
            // 设备可能故意钳位，标失败会误触发宿主的自动重试）
            var verifyMismatch = false;
            PointValue? readback = null;
            if (point.Write is { Verify: true })
            {
                var rawBack = TryReadRawRegisters(point, unitId);
                if (rawBack != null)
                {
                    readback = PointCodec.Decode(point, rawBack, DateTimeOffset.UtcNow);
                    verifyMismatch = !rawBack.SequenceEqual(registers);
                }
            }

            return new WriteResult(WriteOutcome.Succeeded, null, readback, verifyMismatch);
        }

        if (reply.Kind == ModbusFailureKind.Timeout)
        {
            // 写超时：可能已生效。回读原始寄存器比对定论（findings D7：回读的是工程值，
            // 不能拿它跟原始寄存器按类型比对）；回读也失败则保持不确定
            var rawBack = TryReadRawRegisters(point, unitId);
            if (rawBack != null && rawBack.SequenceEqual(registers))
            {
                var readback = PointCodec.Decode(point, rawBack, DateTimeOffset.UtcNow);
                return new WriteResult(WriteOutcome.Succeeded, null, readback);
            }

            if (rawBack != null)
            {
                // 设备上的值与写入值不同 → 确认未生效 → 明确失败，可安全重试
                var notApplied = PointCodec.Decode(point, rawBack, DateTimeOffset.UtcNow);
                return new WriteResult(WriteOutcome.Failed, BuildError(point, reply), notApplied);
            }

            return new WriteResult(WriteOutcome.Indeterminate, BuildError(point, reply), null);
        }

        // 链路失败 / 永久协议错误：明确失败，可安全重试
        return new WriteResult(WriteOutcome.Failed, BuildError(point, reply), null);
    }

    /// <summary>计算点按需求值：读取时实时计算并回写缓存，供后续读取与订阅方使用。</summary>
    private PointValue EvaluateCalculated(RuntimePoint point)
    {
        var now = DateTimeOffset.UtcNow;

        if (point.Expression == null)
        {
            // 脚本型计算点（parser 引用 Parsers 段）v1 不评估
            return PointValue.Bad("ss.reason.notSupported", now);
        }

        var result = ExpressionEvaluator.Evaluate(
            point.Expression,
            id => ResolveForExpression(point, id),
            key => _config.I18n.Resolve(key));

        if (double.IsNaN(result))
        {
            return PointValue.Bad("ss.reason.calculate", now);
        }

        var value = PointValue.Good(result, now);
        _cache.Set(point.Key, value);
        return value;
    }

    /// <summary>表达式里的点位引用：同点表用短 id，跨点表用「deviceId/pointId」限定名。</summary>
    private PointValue? ResolveForExpression(RuntimePoint point, string id)
    {
        string key;
        var slash = id.IndexOf('/');
        if (slash >= 0)
        {
            key = PointKey.Of(id.Substring(0, slash), id.Substring(slash + 1));
        }
        else
        {
            key = PointKey.Of(point.DeviceId, id);
        }

        var value = _cache.Get(key);
        return value.IsGood ? value : null;
    }

    /// <summary>读点位当前原始寄存器（位写入的读-改-写第一步）。失败返回 null。</summary>
    private ushort[]? TryReadRawRegisters(RuntimePoint point, byte unitId)
    {
        if (!_scheduler.TryGetMaster(point.DeviceId, out var master) || master == null) return null;

        var reply = master.Read(ToDriverArea(point.Area), point.Address, point.Length, unitId,
            _config.Global.RequestTimeoutMs, RetryCount(point), RetryInterval(point));

        return reply.Success ? reply.Registers : null;
    }

    /// <summary>把布尔值合并进当前寄存器的指定位。</summary>
    private static bool MergeBit(RuntimePoint point, ushort current, object? value, out ushort merged)
    {
        merged = current;
        if (!point.Bit.HasValue) return false;

        var on = value is bool b ? b : TryNumber(value, out var n) && n != 0;
        var mask = (ushort)(1 << point.Bit.Value);
        merged = on ? (ushort)(current | mask) : (ushort)(current & ~mask);
        return true;
    }

    private static WriteResult Reject(RuntimePoint point, string reasonKey, string message)
    {
        var info = new ErrorInfo(
            "SS.WRITE.REJECTED",
            EventCategory.Write,
            EventLevel.Warn,
            ErrorSource.Policy,
            reasonKey,
            new ErrorContext(DeviceId: point.DeviceId, PointId: point.PointId, Address: point.Address, UnitId: point.UnitId));
        _ = message; // 明细已进事件总线日志；结果对象只带 Error
        return new WriteResult(WriteOutcome.Rejected, info, null);
    }

    private static ErrorInfo BuildError(RuntimePoint point, ModbusReply reply)
    {
        var code = reply.Kind == ModbusFailureKind.Protocol
            ? "MODBUS.EXCEPTION." + reply.ExceptionCode.ToString("X2")
            : reply.Kind == ModbusFailureKind.Timeout
                ? "MODBUS.TIMEOUT"
                : "MODBUS.LINK";

        return new ErrorInfo(
            code,
            EventCategory.Write,
            EventLevel.Error,
            ErrorSource.Device,
            "ss.error.writeFailed",
            new ErrorContext(
                DeviceId: point.DeviceId,
                PointId: point.PointId,
                UnitId: point.UnitId,
                Area: point.Area.ToString(),
                Address: point.Address,
                ElapsedMs: reply.ElapsedMs));
    }

    /// <summary>重试次数：设备配置优先，回退全局（findings W5 接线，此前硬编码 2）。</summary>
    private int RetryCount(RuntimePoint point)
        => _registry.TryGetDevice(point.DeviceId, out var device) && device != null
            ? device.Config.RetryCount
            : _config.Global.RetryCount;

    /// <summary>重试间隔：设备配置优先，回退全局（findings W5 接线，此前硬编码 100）。</summary>
    private int RetryInterval(RuntimePoint point)
        => _registry.TryGetDevice(point.DeviceId, out var device) && device != null
            ? device.Config.RetryIntervalMs
            : _config.Global.RetryIntervalMs;

    private static bool TryNumber(object? value, out double number)
        => double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture),
            NumberStyles.Any, CultureInfo.InvariantCulture, out number);

    /// <summary>写入值是否为「真」（布尔或非零数值）。</summary>
    private static bool IsTrueValue(object? value)
        => value is bool flag ? flag : TryNumber(value, out var number) && number != 0;

    // ═══════════════ IModbusDebugTool ═══════════════

    /// <inheritdoc />
    public Task<RawReadResult> RawReadAsync(string deviceId, ModbusArea area, ushort address, ushort count, CancellationToken ct = default)
    {
        RequireRawAccess();
        return Task.Run(() =>
        {
            if (!_scheduler.TryGetMaster(deviceId, out var master) || master == null)
            {
                return new RawReadResult(false, Array.Empty<ushort>(), Array.Empty<byte>(), null, 0);
            }

            var reply = master.Read(ToDriverArea(area), address, count, UnitIdOf(deviceId),
                _config.Global.RequestTimeoutMs, 0, 0);

            var bytes = new byte[reply.Registers.Length * 2];
            for (var i = 0; i < reply.Registers.Length; i++)
            {
                bytes[i * 2] = (byte)(reply.Registers[i] >> 8);
                bytes[(i * 2) + 1] = (byte)(reply.Registers[i] & 0xFF);
            }

            return new RawReadResult(reply.Success, reply.Registers, bytes,
                reply.Success ? null : MakeDebugError(deviceId, reply), reply.ElapsedMs);
        }, ct);
    }

    /// <inheritdoc />
    public Task<RawWriteResult> RawWriteAsync(string deviceId, ModbusArea area, ushort address, IReadOnlyList<ushort> values, CancellationToken ct = default)
    {
        RequireRawAccess();
        return Task.Run(() =>
        {
            if (!_scheduler.TryGetMaster(deviceId, out var master) || master == null)
            {
                return new RawWriteResult(false, null, 0);
            }

            var reply = values.Count == 1
                ? master.WriteSingle(ToDriverArea(area), address, values[0], UnitIdOf(deviceId), _config.Global.RequestTimeoutMs, 0, 0)
                : master.WriteMulti(ToDriverArea(area), address, values.ToArray(), UnitIdOf(deviceId), _config.Global.RequestTimeoutMs, 0, 0);

            return new RawWriteResult(reply.Success, reply.Success ? null : MakeDebugError(deviceId, reply), reply.ElapsedMs);
        }, ct);
    }

    /// <inheritdoc />
    public Task<PointValue> TriggerReadAsync(string deviceId, string pointId, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            var point = _registry.GetPoint(deviceId, pointId);
            var registers = ReadPointRegisters(point);
            var value = PointCodec.Decode(point, registers, DateTimeOffset.UtcNow);
            _cache.Set(point.Key, value);
            return value;
        }, ct);
    }

    /// <inheritdoc />
    public Task<int> TriggerBlockReadAsync(string blockId, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            // blockId 允许带设备前缀（deviceId/blockId），也可全局唯一匹配
            var slash = blockId.IndexOf('/');
            var block = slash >= 0
                ? _registry.GetBlock(blockId.Substring(0, slash), blockId.Substring(slash + 1))
                : _registry.GetBlock(FindBlockOwner(blockId), blockId);

            var master = _scheduler.TryGetMaster(block.DeviceId, out var m) && m != null
                ? m
                : throw new InvalidOperationException("设备无可用链路");

            var reply = master.Read(ToDriverArea(block.Area), block.Start, block.Count, (byte)block.UnitId,
                _config.Global.RequestTimeoutMs, 0, 0);

            if (!reply.Success) return 0;

            foreach (var point in block.Points)
            {
                var offset = point.Address - block.Start;
                if (offset < 0 || offset + point.Length > reply.Registers.Length) continue;

                var slice = new ushort[point.Length];
                Array.Copy(reply.Registers, offset, slice, 0, point.Length);
                _cache.Set(point.Key, PointCodec.Decode(point, slice, DateTimeOffset.UtcNow));
            }

            return block.Count;
        }, ct);
    }

    private ushort[] ReadPointRegisters(RuntimePoint point)
    {
        if (!_scheduler.TryGetMaster(point.DeviceId, out var master) || master == null)
        {
            throw new InvalidOperationException("设备无可用链路：" + point.DeviceId);
        }

        // 非连续片段：逐段读取后拼接
        if (point.Slices != null)
        {
            var words = new List<ushort>();
            foreach (var slice in point.Slices)
            {
                var reply = master.Read(ToDriverArea(point.Area), slice.Address, slice.Length, (byte)point.UnitId,
                    _config.Global.RequestTimeoutMs, 0, 0);
                if (!reply.Success) throw new InvalidOperationException("读取片段失败：地址 " + slice.Address);
                words.AddRange(reply.Registers);
            }

            return words.ToArray();
        }

        var single = master.Read(ToDriverArea(point.Area), point.Address, point.Length, (byte)point.UnitId,
            _config.Global.RequestTimeoutMs, 0, 0);
        if (!single.Success) throw new InvalidOperationException("读取失败：" + single.Message);

        return single.Registers;
    }

    private string FindBlockOwner(string blockId)
    {
        foreach (var device in _registry.Devices)
        {
            if (device.Blocks.Any(b => b.Id == blockId)) return device.Id;
        }

        throw new KeyNotFoundException("块不存在：" + blockId);
    }

    private void RequireRawAccess()
    {
        if (!_config.Global.AllowRawAccess)
        {
            throw new InvalidOperationException("原始读写已被禁用：配置 Diagnostics@allowRawAccess=false");
        }
    }

    private byte UnitIdOf(string deviceId) => (byte)_registry.GetDevice(deviceId).UnitId;

    private static RuntimeArea ToRuntimeArea(ModbusArea area)
    {
        return area switch
        {
            ModbusArea.Coil => RuntimeArea.Coil,
            ModbusArea.DiscreteInput => RuntimeArea.DiscreteInput,
            ModbusArea.InputRegister => RuntimeArea.InputRegister,
            _ => RuntimeArea.HoldingRegister,
        };
    }

    /// <summary>门面数据区 → 驱动数据区。</summary>
    private static DataArea ToDriverArea(ModbusArea area)
        => ToDriverArea(ToRuntimeArea(area));

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

    private ErrorInfo? MakeDebugError(string deviceId, ModbusReply reply)
    {
        return new ErrorInfo(
            reply.Kind == ModbusFailureKind.Protocol
                ? "MODBUS.EXCEPTION." + reply.ExceptionCode.ToString("X2")
                : reply.Kind == ModbusFailureKind.Timeout ? "MODBUS.TIMEOUT" : "MODBUS.LINK",
            EventCategory.Device,
            EventLevel.Error,
            ErrorSource.Device,
            "ss.error.comm",
            new ErrorContext(DeviceId: deviceId, ElapsedMs: reply.ElapsedMs));
    }
}
