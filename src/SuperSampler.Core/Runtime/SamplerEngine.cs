using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    private readonly ScriptDecoder _scripts;
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
        if (config == null) throw new ArgumentNullException(nameof(config));

        // 阶段二闸门（docs/11 §二）：引擎只接受**已通过加载期全量校验**的配置，
        // 校验未过不得实例化注册表/调度器等运行时对象。
        ConfigGuard.RequireValidated(config);

        _config = config;
        _registry = new PointRegistry(config);

        // 门面与调度共用**同一个** ScriptDecoder（findings W72）：调度器构造时创建，门面在这里复用。
        // 此前两条路径各 new 一个 → ScriptDecoder 的「同点位同类失败去重」状态分裂，同一次脚本失败最多发两条
        // 错误事件；去重状态只有一个所有者才成立。P('id') 取值口径（D60）也因此只有一份实现。
        _scheduler = new Scheduler(_bus, _registry, _cache, config, masters);
        _scripts = _scheduler.Scripts;
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
    public TimeSpan? GetValueAge(string deviceId, string pointId)
    {
        var point = _registry.GetPoint(deviceId, pointId);
        return _cache.GetAge(point.Key, DateTimeOffset.UtcNow);
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
            var started = Stopwatch.GetTimestamp();
            var result = WriteCoreInner(deviceId, pointId, value, user);
            var elapsedMs = (long)((Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency);

            // 写审计：无论成败都发一条（docs/04 第 3 节；宿主落审计库）
            // findings D67：补齐耗时 / 是否发起过回读 / 回读值 / VerifyMismatch，宿主据此闭环校验写入效果。
            // 「发起过回读」口径：Rejected 恒 false（未发出任何通讯）；Indeterminate 恒 true
            //（写超时必回读定论，回读本身失败也是发起过）；其余看结果是否带回读值/不一致标记，
            // 或该点位配置了 verify（回读请求已发出但没拿到值时 Readback 为 null）。
            var didReadback = result.Outcome != WriteOutcome.Rejected
                && (result.Outcome == WriteOutcome.Indeterminate
                    || result.Readback != null
                    || result.VerifyMismatch
                    || (_registry.TryGetPoint(deviceId, pointId, out var written) && written!.Write is { Verify: true }));

            _bus.Emit(new PointWrittenEvent(deviceId, pointId, value, user.Name,
                result.Outcome.ToString(), result.Error?.MessageKey, elapsedMs, didReadback,
                result.Readback, result.VerifyMismatch));

            return result;
        }, ct);
    }

    // ═══════════════ 报警确认（IDeviceManager，ADR D36） ═══════════════

    /// <inheritdoc />
    public Task<AlarmAckResult> AcknowledgeAlarmAsync(string deviceId, string pointId, string alarmId, CancellationToken ct = default)
        => AcknowledgeAlarmAsync(deviceId, pointId, alarmId, new ActingUser("Local", "Local"), ct);

    /// <inheritdoc />
    public Task<AlarmAckResult> AcknowledgeAlarmAsync(string deviceId, string pointId, string alarmId, ActingUser user, CancellationToken ct = default)
    {
        if (user == null) throw new ArgumentNullException(nameof(user));
        if (alarmId == null) throw new ArgumentNullException(nameof(alarmId));

        // 未知 device/point 抛 KeyNotFoundException（配置错误尽早暴露，docs/04 第 3 节）
        _ = _registry.GetPoint(deviceId, pointId);

        return Task.Run(() =>
        {
            // 语义与 AlarmEngine.Acknowledge 完全一致：清 AckPending → 可重触发 → 发确认事件（含 user）
            return _scheduler.Alarms.TryAcknowledge(deviceId, pointId, alarmId, user.Name)
                ? new AlarmAckResult(AlarmAckOutcome.Acknowledged)
                : new AlarmAckResult(AlarmAckOutcome.NotPending);
        }, ct);
    }

    /// <summary>
    /// 写请求的重试预算恒为 <c>0</c>（findings D61；docs/02 §6、docs/07 GATE-1/IT-15）：
    /// 写超时的语义是 <c>Indeterminate</c>（「可能已生效」），若把配置的重试预算交给驱动，
    /// 驱动会按瞬时可重试语义**重发写请求**——「启动」这类命令会被执行两次，正是 docs/02 §6 点名的
    /// 设备/人身安全风险。写路径一次失败即回，由「回读原始寄存器」与四态定论。
    /// 读路径（读-改-写第一步、回读、轮询）照旧吃重试预算：读可安全重试。
    /// </summary>
    private const int WriteRetries = 0;

    /// <summary>实际写管道。</summary>
    private WriteResult WriteCoreInner(string deviceId, string pointId, object? value, ActingUser user)
    {
        var point = _registry.GetPoint(deviceId, pointId);

        // ① 可写性：门面契约「能不能写看 access」
        if (!point.IsWritable)
        {
            return Reject(point, "ss.reason.notWritable", "点位不可写（access）");
        }

        // ①b 区可写性（findings D65）：Modbus 只有线圈（05/0F）与保持寄存器（06/10）可写，
        // 离散输入/输入寄存器是只读区——在只读区下发写在真驱动上必然以协议失败告终
        // （ModbusMaster.WriteSingle/WriteMulti 对其它区直接判 Protocol 失败），
        // 必须在下发前拒绝：Rejected 的契约是「未发出任何通讯」。
        if (point.Area is RuntimeArea.DiscreteInput or RuntimeArea.InputRegister)
        {
            return Reject(point, "ss.reason.notWritable", "数据区不可写（" + point.Area + "）");
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

        // ②b 类型容量校验（findings D63）：超出目标整型可表示范围的值必须在编码前拒绝。
        // 此前 uint16 点写 70000 会被编码静默取模成 4464、int16 写 -40000 变成 25536、
        // uint16 写 -1 变成 65535、NaN/±Inf 变成 0，而结果报 Succeeded——
        // 设备上落下的是没人要的值（伪正常写入，与 ADR D30 / 门级 GATE-3 的口径冲突）。
        if (!IsWithinTypeCapacity(point, value, out var capacityMessage))
        {
            return Reject(point, "ss.reason.encode", capacityMessage);
        }

        // ②c raw 点的工程值就是「该点位声明字长的线上寄存器序列」（findings D56 / EncodeRaw）：
        // 给别的类型（数字/文本/对象）必须拒绝——否则会被当整型拆字写出，读写语义完全对不上。
        if (point.DataType == RuntimeDataType.Raw && value is not ushort[])
        {
            return Reject(point, "ss.reason.encode", "raw 点位的写入值必须是 ushort[]（线上寄存器序列）");
        }

        // ③ 联锁：v1 未实现评估（配置已解析），按通过处理——见 docs/04 开放问题

        // ④ 编码（位点/位域点走读-改-写：直接写会把同寄存器其它位清零）
        var unitId = (byte)point.UnitId;
        ushort[] registers;
        if (point.Bit.HasValue || point.BitFrom.HasValue)
        {
            // findings D62：值非法必须在**读之前**拒绝。此前无法解析的值（如 "abc"）在位点分支里
            // 被当成 false 清位——读-改-写照做、写 0 成功、结果 Succeeded（伪正常写入）；
            // 而且 Rejected 的契约是「未发出任何通讯」，先读再拒会留下一次多余通讯。
            if (!CanEncodeBitWrite(point, value))
            {
                return Reject(point, "ss.reason.encode",
                    point.Bit.HasValue ? "位写入值非法（应为布尔或数值）" : "位域写入值非法（应为数值）");
            }

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
                return Reject(point, "ss.reason.encode",
                    point.Bit.HasValue ? "位写入值非法（应为布尔或数值）" : "位域写入值非法（应为数值）");
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

        // ⑤ 通道与下发（写请求一律不带重试预算，见 WriteRetries）
        if (!_scheduler.TryGetMaster(deviceId, out var master) || master == null)
        {
            return Reject(point, "ss.reason.noChannel", "设备无可用链路");
        }

        var timeout = _config.Global.RequestTimeoutMs;
        ModbusReply reply;

        if (point.Area == RuntimeArea.HoldingRegister && point.Length > 1)
        {
            reply = master.WriteMulti(ToDriverArea(RuntimeArea.HoldingRegister), point.Address, registers, unitId, timeout, WriteRetries, RetryInterval(point));
        }
        else
        {
            reply = master.WriteSingle(ToDriverArea(point.Area), point.Address, registers[0], unitId, timeout, WriteRetries, RetryInterval(point));
        }

        // ⑥ 三态判定：Indeterminate 必须回读确认，绝不自动重写（docs/02 第 6 节）
        if (reply.Success)
        {
            // 写成功也是一种「成功通讯」：该设备与所属链路的退避队列归零（ADR D40）
            _scheduler.NotifyCommSuccess(deviceId);

            // 写后回读校验：通讯成功但值不符 → 打标记而非报失败（D10 决策③：
            // 设备可能故意钳位，标失败会误触发宿主的自动重试）
            // findings D64：回读必须在**点动自动归零之前**——归零是框架自己的动作，
            // 若回读排在归零之后，正常工作的设备也会被报 VerifyMismatch=true（假警报 + Readback 是 0）。
            var verifyMismatch = false;
            PointValue? readback = null;
            if (point.Write is { Verify: true })
            {
                var rawBack = TryReadRawRegisters(point, unitId);
                if (rawBack != null)
                {
                    readback = DecodePoint(point, rawBack, DateTimeOffset.UtcNow);
                    verifyMismatch = !rawBack.SequenceEqual(registers);
                }
            }

            // 点动脉冲：写 true 后延时自动写回 false（findings W22 接线）
            if (point.Write is { PulseMs: > 0 } pulse && IsTrueValue(value))
            {
                System.Threading.Thread.Sleep(pulse.PulseMs);
                if (point.Area == RuntimeArea.HoldingRegister && point.Length > 1)
                {
                    master.WriteMulti(ToDriverArea(point.Area), point.Address, new ushort[point.Length], unitId, timeout, WriteRetries, RetryInterval(point));
                }
                else
                {
                    master.WriteSingle(ToDriverArea(point.Area), point.Address, 0, unitId, timeout, WriteRetries, RetryInterval(point));
                }
            }

            return new WriteResult(WriteOutcome.Succeeded, null, readback, verifyMismatch);
        }

        if (reply.Kind == ModbusFailureKind.Timeout)
        {            // 写超时：可能已生效。回读原始寄存器比对定论（findings D7：回读的是工程值，
            // 不能拿它跟原始寄存器按类型比对）；回读也失败则保持不确定
            var rawBack = TryReadRawRegisters(point, unitId);
            if (rawBack != null && rawBack.SequenceEqual(registers))
            {
                var readback = DecodePoint(point, rawBack, DateTimeOffset.UtcNow);
                return new WriteResult(WriteOutcome.Succeeded, null, readback);
            }

            if (rawBack != null)
            {
                // 设备上的值与写入值不同 → 确认未生效 → 明确失败，可安全重试
                var notApplied = DecodePoint(point, rawBack, DateTimeOffset.UtcNow);
                return new WriteResult(WriteOutcome.Failed, BuildError(point, reply), notApplied);
            }

            // 写超时 + 回读也失败 → 不确定；退避按「读超时 = 设备级」处理
            _scheduler.NotifyCommFailure(deviceId, reply.Kind);
            return new WriteResult(WriteOutcome.Indeterminate, BuildError(point, reply), null);
        }

        // 链路失败 / 永久协议错误：明确失败，可安全重试
        _scheduler.NotifyCommFailure(deviceId, reply.Kind);   // 超时→设备级退避；IO 失败→链路级；协议异常不计入
        return new WriteResult(WriteOutcome.Failed, BuildError(point, reply), null);
    }

    /// <summary>
    /// 计算点按需求值：读取时实时计算并回写缓存，供后续读取与订阅方使用。
    /// 求值口径（表达式/脚本、依赖链递归、防环、深度上限、坏值传染）由
    /// <see cref="CalculatedPointResolver"/> 统一实现——轮询路径（<see cref="Scheduler"/>）
    /// 与门面路径共用同一份（含同一个 <see cref="ScriptDecoder"/> 实例，findings D45/D60/W72）。
    /// </summary>
    private PointValue EvaluateCalculated(RuntimePoint point)
        => CalculatedPointResolver.Evaluate(point, _registry, _cache, _scripts, _config.I18n.Resolve);

    /// <summary>
    /// 单点位解码（按需读、块读、写回读共用）：标准编解码 + 点位脚本（ADR D41），
    /// 与调度器轮询路径共用同一个 <see cref="ScriptDecoder"/> 实例（findings W72）。
    /// </summary>
    private PointValue DecodePoint(RuntimePoint point, ushort[] registers, DateTimeOffset timestamp)
    {
        var value = PointCodec.Decode(point, registers, timestamp, out var rawValue);
        return _scripts.ApplyDecode(point, registers, value, rawValue, timestamp);
    }

    /// <summary>读点位当前原始寄存器（位写入的读-改-写第一步）。失败返回 null。</summary>
    private ushort[]? TryReadRawRegisters(RuntimePoint point, byte unitId)
    {
        if (!_scheduler.TryGetMaster(point.DeviceId, out var master) || master == null) return null;

        var reply = master.Read(ToDriverArea(point.Area), point.Address, point.Length, unitId,
            _config.Global.RequestTimeoutMs, RetryCount(point), RetryInterval(point));

        return reply.Success ? reply.Registers : null;
    }

    /// <summary>
    /// 把写入值合并进当前寄存器的指定位（bit）或位域（bitRange，含端点）：只翻目标位，其余位原样保留。
    /// 位点接受布尔/0-1（与历史语义一致）；位域点接受整数，按位宽截断后放进 [from, to]。
    /// </summary>
    private static bool MergeBit(RuntimePoint point, ushort current, object? value, out ushort merged)
    {
        merged = current;

        if (point.Bit.HasValue)
        {
            if (point.Bit.Value < 0 || point.Bit.Value > 15) return false;

            var on = value is bool b ? b : TryNumber(value, out var n) && n != 0;
            var bitMask = (ushort)(1 << point.Bit.Value);
            merged = on ? (ushort)(current | bitMask) : (ushort)(current & ~bitMask);
            return true;
        }

        if (!point.BitFrom.HasValue || !point.BitTo.HasValue) return false;

        var from = point.BitFrom.Value;
        var to = point.BitTo.Value;
        if (from < 0 || to > 15 || from > to) return false;
        if (!TryNumber(value, out var number)) return false;

        var width = to - from + 1;
        var mask = (ushort)(((1 << width) - 1) << from);

        // 位域是无符号小整数：负数与超位宽的值按目标位宽截断（与整型写入的「落位取整」口径一致）
        var rounded = Math.Round(number, MidpointRounding.AwayFromZero);
        var clamped = rounded <= 0 ? 0.0 : Math.Min(rounded, 65535.0);
        var field = (ushort)(((ushort)clamped << from) & mask);
        merged = (ushort)((current & ~mask) | field);
        return true;
    }

    /// <summary>
    /// 位点/位域点的写入值是否可编码（findings D62）。判定口径与 <see cref="MergeBit"/> 一致：
    /// 位点接受布尔或可解析数值；位域点只接受可解析数值。
    /// 本检查必须发生在读-改-写的**读之前**——非法的值绝不能变成一次「清位/写 0」。
    /// </summary>
    private static bool CanEncodeBitWrite(RuntimePoint point, object? value)
        => point.Bit.HasValue
            ? value is bool || TryNumber(value, out _)
            : TryNumber(value, out _);

    /// <summary>
    /// 写入值是否落在目标**整型**可表示范围内（findings D63）。
    /// 只约束整型点位：编码层对整型是「落位取整后按目标位宽取模」，超范围会静默回绕
    /// （70000 → 4464）。浮点走 IEEE 语义（溢出的 ±Inf 在解码侧按 ADR D32 降级为 Uncertain）、
    /// 字符串/BCD/datetime/raw 各有自己的口径，都不在这里判定。
    /// <para>
    /// 非数值（对象、纯文本）返回 true：交给 <see cref="PointCodec.Encode"/> 按原有口径拒绝，
    /// 避免两处判定打架（那里的异常会被包装成 <c>Rejected(ss.reason.encode)</c>）。
    /// </para>
    /// </summary>
    private static bool IsWithinTypeCapacity(RuntimePoint point, object? value, out string message)
    {
        message = string.Empty;

        double min;
        double max;
        switch (point.DataType)
        {
            case RuntimeDataType.Int16:
                min = short.MinValue;
                max = short.MaxValue;
                break;
            case RuntimeDataType.UInt16:
                min = ushort.MinValue;
                max = ushort.MaxValue;
                break;
            case RuntimeDataType.Int32:
                min = int.MinValue;
                max = int.MaxValue;
                break;
            case RuntimeDataType.UInt32:
                min = uint.MinValue;
                max = uint.MaxValue;
                break;
            case RuntimeDataType.Int64:
                min = long.MinValue;
                max = long.MaxValue;
                break;
            case RuntimeDataType.UInt64:
                min = ulong.MinValue;
                max = ulong.MaxValue;
                break;
            default:
                return true;   // 非整型点位：不在本检查范围内
        }

        double number;
        if (value is bool flag)
        {
            number = flag ? 1 : 0;   // 与 Encode 的布尔口径一致（Convert.ToDouble(bool)）
        }
        else if (!TryNumber(value, out number))
        {
            return true;            // 非数值：交给编码层按原口径拒绝
        }

        if (double.IsNaN(number) || double.IsInfinity(number))
        {
            message = "写入值不是有限数值（" + number + "）";
            return false;
        }

        // 有缩放时工程值先逆换算成原始值（与 Encode 的 ToRaw 同一步），再按类型容量判定
        var raw = point.Scale != null ? point.Scale.Reverse(number) : number;
        var rounded = Math.Round(raw, MidpointRounding.AwayFromZero);
        if (rounded < min || rounded > max)
        {
            message = "写入值 " + number + "（原始值 " + rounded + "）超出 " + point.DataType + " 可表示范围 ["
                + min + ", " + max + "]";
            return false;
        }

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
            var timestamp = DateTimeOffset.UtcNow;
            var value = DecodePoint(point, registers, timestamp);
            _cache.Set(point.Key, value);
            _cache.MarkAcquired(point.Key, timestamp);   // 成功采集（GetValueAge）
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

            var timestamp = DateTimeOffset.UtcNow;
            foreach (var point in block.Points)
            {
                // 与轮询同一条取值口径：Slices 点位按片段从块窗口里拼值（不再有"只在按需路径生效"的差异）
                if (!point.TryExtract(reply.Registers, block.Start, out var slice)) continue;

                _cache.Set(point.Key, DecodePoint(point, slice, timestamp));
                _cache.MarkAcquired(point.Key, timestamp);   // 成功采集（GetValueAge）
            }

            return block.Count;
        }, ct);
    }

    /// <inheritdoc />
    public Task<int> TriggerOnDemandReadAsync(string deviceId, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            // 未知设备抛 KeyNotFoundException（与 TriggerReadAsync 一致：配置错误尽早暴露）
            var device = _registry.GetDevice(deviceId);
            return _scheduler.TriggerOnDemand(device);
        }, ct);
    }

    /// <inheritdoc />
    public Task<ManualRetryResult> RetryDeviceAsync(string deviceId, CancellationToken ct = default)
    {
        // 未知设备抛 KeyNotFoundException（配置错误尽早暴露）；不受 allowRawAccess 门禁（运维动作）
        var device = _registry.GetDevice(deviceId);
        return Task.Run(() => _scheduler.RetryDevice(device), ct);
    }

    /// <inheritdoc />
    public Task<ManualRetryResult> RetryLinkAsync(string transportId, CancellationToken ct = default)
    {
        if (transportId == null) throw new ArgumentNullException(nameof(transportId));
        return Task.Run(() => _scheduler.RetryLink(transportId), ct);
    }

    /// <summary>
    /// 读一个点位当前的寄存器（返回按片段拼好、可直接解码的那串）。
    /// 连续点读 [Address, Address+Length)；&lt;Slices&gt; 点把片段连中间空洞合成**一段**读回再按片段拼值
    /// ——绝不为了一个点发多次请求（docs/01 §4.2、docs/11 §一.5）。
    /// </summary>
    private ushort[] ReadPointRegisters(RuntimePoint point)
    {
        if (!_scheduler.TryGetMaster(point.DeviceId, out var master) || master == null)
        {
            throw new InvalidOperationException("设备无可用链路：" + point.DeviceId);
        }

        var start = point.SpanStart;
        var count = Math.Max(1, point.SpanEnd - start);
        var reply = master.Read(ToDriverArea(point.Area), start, count, (byte)point.UnitId,
            _config.Global.RequestTimeoutMs, 0, 0);
        if (!reply.Success) throw new InvalidOperationException("读取失败：" + reply.Message);

        if (!point.TryExtract(reply.Registers, start, out var registers))
        {
            throw new InvalidOperationException("读取窗口未覆盖点位 " + point.PointId
                + "（请求 [" + start + "," + (start + count) + ")，应答 " + reply.Registers.Length + " 字）");
        }

        return registers;
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

/// <summary>
/// 「按 id 取点位值」与「计算点求值」的**统一口径**（findings D45 / D60）：
/// 门面路径（<see cref="SamplerEngine"/>）与轮询/脚本解码路径（<see cref="Scheduler"/>）
/// 传同一个 <see cref="PointRegistry"/> 与 <see cref="ValueCache"/>、各自的 <see cref="ScriptDecoder"/>，
/// 口径只有这一份实现——「宿主有没有先读过那个计算点」不再影响同一份配置的结果。
/// <list type="bullet">
/// <item><b>取值</b>：只认好值；缓存没有好值且目标是计算点（表达式型或脚本型）时**递归求值**，
/// 依赖不是计算点时维持「没采到就是 null」——不替它编值。依赖算出仍是坏值 → 引用方拿到 null →
/// 判 Bad(<c>ss.reason.calculate</c>)：<b>坏值沿链传染</b>，绝不给出伪正常数。</item>
/// <item><b>防环/深度</b>：线程内「正在求值」集合 + 深度上限 <see cref="MaxEvaluationDepth"/>，
/// 命中环或超深立即判坏值返回，<b>不递归、不抛、不死循环、不栈溢出</b>
/// （静态环由加载期 CGV-15 报死，这里是运行期兜底：脚本可以动态拼引用）。</item>
/// <item><b>纯内存</b>：只读注册表/缓存、只跑表达式或脚本，<b>绝不发起任何通讯</b>
/// ——轮询线程在为点位脚本解析依赖时递归求值，不会插进任何 Modbus 请求。</item>
/// </list>
/// 用 <see cref="ThreadStaticAttribute"/> 是因为求值发生在**调用方线程**（门面同步读 / 轮询线程解码），
/// 不同线程各自持链，无需加锁。
/// </summary>
internal static class CalculatedPointResolver
{
    /// <summary>
    /// 计算点求值的最大依赖深度（运行期兜底防线）。加载期 CGV-15 已把环报死，
    /// 这里只防「配置被绕过/未来漏报」时递归爆栈——超深按坏值返回，绝不抛。
    /// </summary>
    private const int MaxEvaluationDepth = 32;

    /// <summary>线程内「正在求值中」的计算点点位键（最外层求值创建、结束置空）。</summary>
    [ThreadStatic]
    private static HashSet<string>? _evaluatingKeys;

    /// <summary>当前线程的求值深度（与 <see cref="_evaluatingKeys"/> 同生命周期）。</summary>
    [ThreadStatic]
    private static int _evaluationDepth;

    /// <summary>
    /// 计算点按需求值：表达式型走 <see cref="ExpressionEvaluator"/>，脚本型走
    /// <see cref="ScriptDecoder.Evaluate"/>（ADR D41，超时保护、失败置 Bad 并记错误事件）。
    /// 成功即回写缓存并按「上次求值成功」计 <c>GetValueAge</c>（既有口径，W43 留档）。
    /// </summary>
    public static PointValue Evaluate(RuntimePoint point, PointRegistry registry, ValueCache cache,
        ScriptDecoder scripts, Func<string, string?> resolveI18n)
    {
        var now = DateTimeOffset.UtcNow;

        var outermost = _evaluatingKeys == null;
        var active = _evaluatingKeys ??= new HashSet<string>(StringComparer.Ordinal);

        // 运行期防环兜底：本点已在当前求值链上（A→B→A 这类环）或链太深 → 坏值，绝不再递归
        if (!active.Add(point.Key) || _evaluationDepth >= MaxEvaluationDepth)
        {
            if (outermost) _evaluatingKeys = null;
            return PointValue.Bad("ss.reason.calculate", now);
        }

        try
        {
            _evaluationDepth++;
            try
            {
                return EvaluateCore(point, now, registry, cache, scripts, resolveI18n);
            }
            finally
            {
                _evaluationDepth--;
            }
        }
        finally
        {
            active.Remove(point.Key);
            if (outermost) _evaluatingKeys = null;
        }
    }

    /// <summary>
    /// 按 id 取点位当前值（短 id = 本设备内，含 '/' = "deviceId/pointId" 跨设备）。
    /// 表达式与脚本的 <c>P('id')</c> 共用同一口径：只认好值，坏值/缺失返回 null。
    /// <para>
    /// **依赖链递归求值**（findings D45 / D60）：依赖点是计算点且缓存里没有好值
    /// （从未算过 / 上次算成坏值）时，按同一个 <see cref="Evaluate"/> 口径把它求出来，
    /// 而不是直接给引用方一个空值。依赖点不是计算点时仍维持「没采到就是 null」——不替它编值。
    /// 依赖点算出来仍是坏值 → 这里返回 null → 引用方判 Bad：**坏值沿链传染**。
    /// </para>
    /// </summary>
    public static PointValue? Resolve(string deviceId, string id, PointRegistry registry, ValueCache cache,
        ScriptDecoder scripts, Func<string, string?> resolveI18n)
    {
        var slash = id.IndexOf('/');
        var refDevice = slash >= 0 ? id.Substring(0, slash) : deviceId;
        var refPointId = slash >= 0 ? id.Substring(slash + 1) : id;

        var value = cache.Get(PointKey.Of(refDevice, refPointId));

        if (!value.IsGood
            && registry.TryGetPoint(refDevice, refPointId, out var dependency)
            && dependency is { IsCalculated: true })
        {
            value = Evaluate(dependency, registry, cache, scripts, resolveI18n);
        }

        return value.IsGood ? value : null;
    }

    /// <summary>计算点的实际求值体（防环/深度由 <see cref="Evaluate"/> 统一管）。</summary>
    private static PointValue EvaluateCore(RuntimePoint point, DateTimeOffset now, PointRegistry registry,
        ValueCache cache, ScriptDecoder scripts, Func<string, string?> resolveI18n)
    {
        PointValue value;
        if (point.HasScript)
        {
            value = scripts.Evaluate(point, now);
        }
        else if (point.Expression != null)
        {
            var result = ExpressionEvaluator.Evaluate(
                point.Expression,
                id => Resolve(point.DeviceId, id, registry, cache, scripts, resolveI18n),
                resolveI18n);

            value = double.IsNaN(result)
                ? PointValue.Bad("ss.reason.calculate", now)
                : PointValue.Good(result, now);
        }
        else
        {
            // 计算点既无表达式也无脚本：加载期已报错（CGV-32），运行时兜底坏值，不静默给数
            return PointValue.Bad("ss.reason.notSupported", now);
        }

        if (value.IsGood)
        {
            cache.Set(point.Key, value);
            cache.MarkAcquired(point.Key, now);   // 计算点按「上次求值成功」计（GetValueAge）
        }

        return value;
    }
}
