using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using SuperSampler.Abstractions.Errors;
using SuperSampler.Abstractions.Events;
using SuperSampler.Abstractions.Facade;
using SuperSampler.Abstractions.Values;
using SuperSampler.Core.Config;
using SuperSampler.Core.Events;
using SuperSampler.Drivers.Modbus;
using SuperSampler.Drivers.Modbus.Wire;

namespace SuperSampler.Core.Runtime;

/// <summary>
/// 轮询调度器：每个启用设备一条轮询线程。
/// <list type="bullet">
/// <item><b>分桶</b>：同一设备内按 <c>intervalMs</c> 分桶——相同间隔的块/点位组走同一节拍；
/// 最小间隔与节拍同为 <see cref="GlobalOptions.MinIntervalMs"/>（50ms），实际周期误差 ≤ 一个节拍。</item>
/// <item><b>自动分组</b>：同设备、同从站、同数据区、地址空洞 ≤ <c>Global/Scheduler@ignoreGap</c> 的散点自动归为一组，
/// 一次请求读回；超过地址组上限（<c>groupLimitRegisters</c>/<c>groupLimitBits</c>）自动切成多次请求，
/// 但**同一组的所有请求在同一节拍内读完**，绝不跨节拍。</item>
/// <item><b>模式</b>：<c>onDemand</c> 不参与轮询（只由门面手动触发）；<c>once</c> 在 Start 后读一次，
/// 直到成功一次为止——失败的重试节奏就是两层退避（退避期间不发请求）。</item>
/// <item><b>两层退避</b>（<c>Global/Reconnect</c>，docs/01 §6.3）：读超时 → 只把该从站排进退避（其它从站照常）；
/// IO 层失败 → 整条链路退避（关连接 → 等待 → 重连）；协议异常不计入；同链路全部从站都在设备级退避 → 升级链路级。</item>
/// </list>
/// 错误按「一次窗口失败 = 一条错误事件」聚合上报，避免事件风暴（docs/02 第 7 节）。
/// </summary>
public sealed class Scheduler : IDisposable
{
    /// <summary>调度节拍：轮询线程醒来对齐到期时刻的粒度（= 最小间隔）。</summary>
    private const int TickMs = GlobalOptions.MinIntervalMs;

    private readonly InProcessEventBus _bus;
    private readonly PointRegistry _registry;
    private readonly ValueCache _cache;
    private readonly AlarmEngine _alarms;
    private readonly GlobalOptions _global;
    private readonly ScriptDecoder _scripts;
    private readonly Func<string, string?> _resolveI18n;
    private readonly Dictionary<string, IModbusLink> _masters;
    private readonly List<Thread> _workers = new();
    private readonly CancellationTokenSource _cts = new();

    /// <summary>
    /// 运行时时钟（PROD-8 / findings D107）：<b>排程到期判定只看单调侧</b>（<see cref="IRuntimeClock.NowTicks"/>），
    /// 墙钟侧（<see cref="IRuntimeClock.UtcNow"/>）只用来给事件/值打时间戳。
    /// 默认系统时钟；测试可经 <see cref="SamplerConfiguration.Clock"/> 注入假时钟模拟系统时间跳变。
    /// </summary>
    private readonly IRuntimeClock _clock;

    /// <summary><see cref="Start"/> 的幂等闸门（0 = 未启动，1 = 已启动）。</summary>
    private int _started;

    /// <summary>两层退避状态机（设备级 / 链路级各自的队列进度）。</summary>
    private readonly BackoffGovernor _backoff;

    /// <summary>链路 → 该链路上参与轮询的设备（「全部设备级退避 → 升级链路级」的判定依据）。</summary>
    private readonly Dictionary<string, List<RuntimeDevice>> _devicesByTransport = new(StringComparer.Ordinal);

    /// <summary>配置里声明过的全部链路 id（含 <c>Transport@enabled=false</c> 的），用于区分「停用」与「不存在」。</summary>
    private readonly HashSet<string> _knownTransports;

    /// <summary>构造调度器。master 由调度器创建与释放（一条链路一个）；cache 由引擎注入共享。</summary>
    public Scheduler(InProcessEventBus bus, PointRegistry registry, ValueCache cache, SamplerConfiguration config)
        : this(bus, registry, cache, config, null)
    {
    }

    /// <summary>构造调度器（可注入链路：测试用假链路替换真实 Modbus 通道，findings B1）。</summary>
    public Scheduler(InProcessEventBus bus, PointRegistry registry, ValueCache cache, SamplerConfiguration config,
        IReadOnlyDictionary<string, IModbusLink>? masters)
    {
        if (config == null) throw new ArgumentNullException(nameof(config));

        // 阶段二闸门（docs/11 §二）：调度器同样只接受已通过加载期全量校验的配置
        ConfigGuard.RequireValidated(config);

        _bus = bus;
        _registry = registry;
        _cache = cache;
        _alarms = new AlarmEngine(bus);
        _global = config.Global;
        _resolveI18n = config.I18n.Resolve;
        _clock = config.Clock ?? SystemRuntimeClock.Instance;
        // 点位脚本（ADR D41）：P('id') 从实时值缓存解析——同一点表可被多设备共用，短 id 在设备内解析。
        // 取值口径与门面路径（SamplerEngine）完全一致：缓存没有好值且依赖是计算点时递归求值
        // （findings D60——此前只查缓存，结果取决于宿主有没有先读过那个计算点）。
        _scripts = new ScriptDecoder(bus, config.Global, ResolveScriptPoint, _resolveI18n);
        _backoff = new BackoffGovernor(config.Global.Reconnect, _clock);

        // Transport@enabled=false：链路整体停用（不建主站、不轮询、手动触发也拒绝）——
        // 此前该属性解析进模型后无人消费，禁用链路上的设备照常发请求（findings D44）。
        var enabledTransports = new HashSet<string>(
            config.Transports.Where(t => t.Enabled).Select(t => t.Id), StringComparer.Ordinal);
        _knownTransports = new HashSet<string>(config.Transports.Select(t => t.Id), StringComparer.Ordinal);

        _masters = masters != null
            ? masters.Where(p => enabledTransports.Contains(p.Key))
                .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal)
            : config.Transports.Where(t => t.Enabled).ToDictionary(
                t => t.Id,
                t => (IModbusLink)new ModbusMaster(ParseVariant(t.Variant), ToTransportLike(t)),
                StringComparer.Ordinal);

        foreach (var device in _registry.Devices)
        {
            if (!IsPolling(device)) continue;

            if (!_devicesByTransport.TryGetValue(device.Config.Transport, out var list))
            {
                list = new List<RuntimeDevice>();
                _devicesByTransport[device.Config.Transport] = list;
            }

            list.Add(device);
        }
    }

    /// <summary>
    /// 设备是否参与轮询：设备启用 + 未检修暂停 + 所属链路启用
    /// （<c>Transport@enabled=false</c> 时整条链路停用，链路上的设备一起停）。
    /// </summary>
    private bool IsPolling(RuntimeDevice device)
        => device.Config.Enabled && !device.Config.Paused && _masters.ContainsKey(device.Config.Transport);

    /// <summary>
    /// 启动全部启用设备的轮询线程。**幂等**：重复调用不再起第二套线程
    /// （宿主重复 Start 会让每个设备的请求数翻倍，docs/11 §五.2 要求重复 Start/Stop 安全）。
    /// </summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;

        foreach (var device in _registry.Devices)
        {
            if (!IsPolling(device)) continue;

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

    /// <summary>报警引擎：门面确认通路（ADR D36）与调度评估共用同一实例。</summary>
    internal AlarmEngine Alarms => _alarms;

    /// <summary>
    /// 点位脚本解码器（ADR D41）：调度器持有，门面（<see cref="SamplerEngine"/>）复用**同一个实例**
    /// （findings W72）。两条取值路径（轮询解码 / 门面按需读与计算点求值）此前各 <c>new</c> 一个解码器，
    /// <see cref="ScriptDecoder"/> 内部「同一点位同类失败只发一条错误事件」的去重状态随之分裂成两份 →
    /// 同一点位同类脚本失败最多发两条事件。去重状态只有一个所有者才成立，故统一到本实例
    /// （内部共享，不改任何公开构造签名）。
    /// </summary>
    internal ScriptDecoder Scripts => _scripts;

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
        var plan = BuildDevicePlan(device);

        // 排程基准一律用**单调钟**（findings D107 / PROD-8）：DueTime 存的是单调刻度，
        // 「还有多久到期」= 到期刻度 - 当前单调刻度。旧实现用 DateTime.UtcNow 存墙钟到期时刻，
        // 系统时间被回拨 1 小时后所有到期时刻都变成「1 小时后」，轮询要等墙钟追上才恢复（冻结式卡死）。
        var now = _clock.NowTicks;
        foreach (var bucket in plan.Buckets) bucket.DueTicks = now;
        foreach (var once in plan.OnceWorks) once.DueTicks = now;

        // 本轮退避会话是否已按 offlineQuality 置位（进入退避即置位一次，恢复后复位）
        var offlineMarked = false;

        while (!token.IsCancellationRequested)
        {
            // ── 退避闸门：链路级或设备级在退避 → 一个请求都不发，睡到到期（按节拍切片，手动重试可打断）──
            if (TryGetBackoffWait(device, out _))
            {
                if (!offlineMarked)
                {
                    ApplyOfflineQuality(device);
                    offlineMarked = true;
                }

                if (!WaitTick(token)) return;
                continue;
            }

            offlineMarked = false;

            now = _clock.NowTicks;
            var nextWake = long.MaxValue;

            foreach (var bucket in plan.Buckets)
            {
                if (now >= bucket.DueTicks)
                {
                    ExecuteWork(device, bucket);
                    // 下一期从「本轮执行完成」起算：不追赶、不堆叠，避免慢设备越积越多
                    bucket.DueTicks = _clock.NowTicks + RuntimeClockMath.MsToTicks(bucket.IntervalMs);
                }

                if (bucket.DueTicks < nextWake) nextWake = bucket.DueTicks;
            }

            // once：Start 后读一次；读到（窗口成功）即摘掉，没读到就等退避到期再试（第四步：正式两层退避）
            foreach (var once in plan.OnceWorks)
            {
                if (once.Done) continue;

                if (now >= once.DueTicks)
                {
                    once.Done = ExecuteWork(device, once) == null;

                    // 失败：退避闸门负责等待；这里只兜住「退避关闭 / 协议异常」两种不进队列的情形，
                    // 用最早一档作为下限节拍，绝不把设备打成忙等。
                    if (!once.Done) once.DueTicks = _clock.NowTicks + RuntimeClockMath.MsToTicks(OnceRetryFloorMs);
                }

                if (!once.Done && once.DueTicks < nextWake) nextWake = once.DueTicks;
            }

            var sleepMs = NextSleepMs(nextWake);
            if (sleepMs <= 0) continue;

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

    /// <summary>退避期间按节拍等待；返回 false 表示已停止（线程应退出）。不用额外定时器/句柄，反复进退避不泄漏。</summary>
    private static bool WaitTick(CancellationToken token)
    {
        try
        {
            return !token.WaitHandle.WaitOne(TickMs);
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    // ─────────────── 两层退避接线 ───────────────

    /// <summary>该设备此刻是否被退避闸门拦住（链路级优先，其次设备级）；true 时 untilTicks 给出到期刻度。</summary>
    private bool TryGetBackoffWait(RuntimeDevice device, out long untilTicks)
    {
        if (_backoff.IsLinkBlocked(device.Config.Transport, out untilTicks)) return true;
        return _backoff.IsDeviceBlocked(device.Id, out untilTicks);
    }

    /// <summary>
    /// once 失败后的重试下限节拍（毫秒）：退避队列最早一档；队列不可用时退回全局默认间隔。
    /// 正常路径下 once 的节奏由退避闸门决定（退避期间根本不发请求），这个下限只在
    /// 「退避被关闭」或「失败是协议异常（不进队列）」时起作用。
    /// </summary>
    private int OnceRetryFloorMs => _global.Reconnect.Delays.Count > 0
        ? _global.Reconnect.Delays[0]
        : _global.DefaultIntervalMs;

    /// <summary>
    /// 退避期间该设备所有启用点位的质量按 <c>Reconnect@offlineQuality</c>（offline|bad）置位；
    /// 值按 <c>Quality@onCommErrorValue</c> 策略（keepLast 保留旧值，否则置空）。
    /// 失败当次的窗口由 <see cref="MarkWindowFailed"/> 顺带置好，这里兜住同一设备的其余点位
    /// （其它窗口、跨节拍的点位、以及链路级退避时没参与失败的设备）。
    /// </summary>
    private void ApplyOfflineQuality(RuntimeDevice device)
    {
        var points = new List<RuntimePoint>();
        foreach (var point in device.Points.Values)
        {
            if (point.Enabled && !point.IsCalculated) points.Add(point);
        }

        ApplyOfflineQuality(points, _clock.UtcNow);
    }

    /// <summary>把一组点位按退避口径置位（质量已等于目标质量时跳过，不重复刷事件）。</summary>
    private void ApplyOfflineQuality(IReadOnlyList<RuntimePoint> points, DateTimeOffset timestamp)
    {
        var quality = OfflineQuality;
        var keepLast = string.Equals(_global.OnCommErrorValue, "keepLast", StringComparison.OrdinalIgnoreCase);

        foreach (var point in points)
        {
            if (!point.Enabled || point.IsCalculated) continue;

            var previous = _cache.Get(point.Key);
            if (previous.Quality == quality) continue; // 已是该质量：不重复刷事件

            var value = keepLast ? previous.Value : null;
            PublishIfChanged(point, new PointValue(value, quality, timestamp, "ss.quality.offline"));
        }
    }

    /// <summary>
    /// 通讯失败 → 退避状态机（读窗口与写管道共用）：
    /// 协议异常不计入（链路通、设备在回话）；超时 → 设备级；其余（IO 失败）→ 链路级。
    /// </summary>
    internal void NotifyCommFailure(string deviceId, ModbusFailureKind kind)
    {
        if (!_registry.TryGetDevice(deviceId, out var device) || device == null) return;
        if (!_backoff.Enabled) return;

        switch (kind)
        {
            case ModbusFailureKind.Protocol:
                return; // 不计入退避：既不进退避，也不重置队列

            case ModbusFailureKind.Timeout:
                EnterDeviceBackoff(device, "MODBUS.TIMEOUT");
                return;

            default:
                EnterLinkBackoff(device.Config.Transport, "MODBUS.LINK");
                return;
        }
    }

    /// <summary>一次成功通讯（读窗口成功 / 写成功）→ 该设备与所属链路的退避队列一起归零。</summary>
    internal void NotifyCommSuccess(string deviceId)
    {
        if (!_registry.TryGetDevice(deviceId, out var device) || device == null) return;
        NotifyCommSuccess(device);
    }

    private void NotifyCommSuccess(RuntimeDevice device)
    {
        if (!_backoff.Enabled) return;

        if (_backoff.OnSuccess(BackoffScope.Device, device.Id) is { } deviceRecovery)
        {
            EmitBackoffRecovered(BackoffScope.Device, device.Id, device.Config.Transport, device.UnitId, deviceRecovery);
        }

        if (_backoff.OnSuccess(BackoffScope.Link, device.Config.Transport) is { } linkRecovery)
        {
            EmitBackoffRecovered(BackoffScope.Link, null, device.Config.Transport, null, linkRecovery);
        }
    }

    /// <summary>设备级退避（读超时）：只排该从站；随后检查是否该升级为链路级。</summary>
    private void EnterDeviceBackoff(RuntimeDevice device, string reasonCode)
    {
        var step = _backoff.OnFailure(BackoffScope.Device, device.Id);
        if (step.Attempt == 0) return; // 退避已关闭

        EmitBackoffEntered(BackoffScope.Device, device.Id, device.Config.Transport, device.UnitId, step, reasonCode);
        EscalateIfAllDevicesBackingOff(device.Config.Transport);
    }

    /// <summary>
    /// 链路级退避：作废该链路上各设备的设备级进度（整条链路一起等），关闭连接（到期后懒重连）。
    /// 「同链路全部从站都处于设备级退避 → 升级」也走这里（防半开假死的第二道防线）。
    /// </summary>
    private void EnterLinkBackoff(string transportId, string reasonCode)
    {
        foreach (var device in DevicesOf(transportId)) _backoff.Clear(BackoffScope.Device, device.Id);

        var step = _backoff.OnFailure(BackoffScope.Link, transportId);
        if (step.Attempt == 0) return;

        if (_masters.TryGetValue(transportId, out var master) && master != null) master.Close();
        EmitBackoffEntered(BackoffScope.Link, null, transportId, null, step, reasonCode);
    }

    /// <summary>
    /// 防「假死」第二道防线：同链路上**所有**参与轮询的从站都处于设备级退避 → 不是单个从站的问题，
    /// 升级为链路级退避（清掉各设备进度、关连接重建）。
    /// </summary>
    private void EscalateIfAllDevicesBackingOff(string transportId)
    {
        var devices = DevicesOf(transportId);
        if (devices.Count == 0) return;

        foreach (var device in devices)
        {
            if (!_backoff.IsDeviceBlocked(device.Id, out _)) return;
        }

        EnterLinkBackoff(transportId, "SS.BACKOFF.ALL_DEVICES");
    }

    private List<RuntimeDevice> DevicesOf(string transportId)
        => _devicesByTransport.TryGetValue(transportId, out var devices) ? devices : new List<RuntimeDevice>();

    private void EmitBackoffEntered(BackoffScope scope, string? deviceId, string transportId, int? unitId,
        BackoffStep step, string reasonCode)
    {
        _bus.Emit(new BackoffEnteredEvent(scope, deviceId, transportId, unitId,
            step.Attempt, step.DelayMs, step.NextRetryAt, reasonCode));
    }

    private void EmitBackoffRecovered(BackoffScope scope, string? deviceId, string transportId, int? unitId,
        BackoffRecovery recovery)
    {
        _bus.Emit(new BackoffRecoveredEvent(scope, deviceId, transportId, unitId,
            recovery.Attempts, recovery.DurationMs));
    }

    // ─────────────── 手动重试（门面，ADR D40）───────────────

    /// <summary>
    /// 手动重试单台设备：打断该设备的退避等待并立即按其轮询计划采集一次
    /// （每个 auto 桶各一次 + 每个 once 项各一次；onDemand 不在范围内）。
    /// once 项的「已完成」状态只活在轮询线程自己的计划里，所以手动重试会照原计划把 once 也再读一次
    /// （对宿主是「重试一次 = 把该设备的计划整跑一遍」，属于运维语义，不是重复轮询）。
    /// 成功一次 → 该设备与所属链路的队列一起归零；失败 → 退避继续（档位加深一档）。
    /// </summary>
    internal ManualRetryResult RetryDevice(RuntimeDevice device)
    {
        if (!_backoff.ManualRetryEnabled) return Unsupported();
        // 链路被 Transport@enabled=false 停用：不发任何通讯（与 manualRetry=false 同口径）
        if (!_masters.ContainsKey(device.Config.Transport)) return Unsupported();

        var interrupted = _backoff.Interrupt(BackoffScope.Device, device.Id);
        var failure = ExecutePlanOnce(device);

        return new ManualRetryResult(
            failure == null ? ManualRetryOutcome.Succeeded : ManualRetryOutcome.Failed,
            interrupted,
            failure == null ? null : BuildRetryError(device, failure));
    }

    /// <summary>
    /// 手动重试整条链路：清空该链路与链路上全部从站的退避 → 关闭连接并立即重连
    /// （对每个参与轮询的设备**各按计划尝试一次**，一台失败不跳过其余设备——findings D97，
    /// 与 D73 同一口径：发请求才是重试，跳过设备等于没重试）。
    /// 任一设备失败即整体判失败，错误上下文取**首个失败设备**（不再无条件取 devices[0]）。
    /// </summary>
    internal ManualRetryResult RetryLink(string transportId)
    {
        if (!_masters.ContainsKey(transportId))
        {
            // 「停用」与「不存在」要分开：停用链路不发通讯（配置语义），未知链路是编程错误
            if (_knownTransports.Contains(transportId)) return Unsupported();
            throw new KeyNotFoundException("链路不存在：" + transportId);
        }

        if (!_backoff.ManualRetryEnabled) return Unsupported();

        var devices = DevicesOf(transportId);
        var interrupted = _backoff.Interrupt(BackoffScope.Link, transportId);
        foreach (var device in devices)
        {
            if (_backoff.Interrupt(BackoffScope.Device, device.Id)) interrupted = true;
        }

        // 「关闭连接并重连」：关掉旧连接（懒重连在下一次请求完成），避免半开连接继续用
        _masters[transportId].Close();

        ModbusReply? failure = null;
        RuntimeDevice? failedDevice = null;
        foreach (var device in devices)
        {
            var attempt = ExecutePlanOnce(device);
            if (attempt != null && failure == null)
            {
                failure = attempt;
                failedDevice = device;
            }
        }

        var error = failure != null && failedDevice != null ? BuildRetryError(failedDevice, failure) : null;
        return new ManualRetryResult(
            failure == null ? ManualRetryOutcome.Succeeded : ManualRetryOutcome.Failed,
            interrupted,
            error);
    }

    private static ManualRetryResult Unsupported()
        => new(ManualRetryOutcome.NotSupported, false, null);

    /// <summary>
    /// 按设备计划立即执行一轮（每个 auto 桶、每个未完成的 once 项各一次）；返回首个失败应答，null 表示全成功。
    /// 每个桶独立执行、独立失败（findings D97，与 D73 同一口径）：一处失败不跳过其余桶与 once 项，
    /// 否则宿主点「重试一次」时，同设备其余数据既不发请求也不刷新（与「把该设备的计划整跑一遍」不符）。
    /// </summary>
    private ModbusReply? ExecutePlanOnce(RuntimeDevice device)
    {
        var plan = BuildDevicePlan(device);
        ModbusReply? firstFailure = null;

        foreach (var bucket in plan.Buckets)
        {
            var failure = ExecuteWork(device, bucket);
            firstFailure ??= failure;
        }

        foreach (var once in plan.OnceWorks)
        {
            var failure = ExecuteWork(device, once);
            firstFailure ??= failure;
        }

        return firstFailure;
    }

    private static ErrorInfo BuildRetryError(RuntimeDevice device, ModbusReply reply)
    {
        var code = reply.Kind == ModbusFailureKind.Protocol
            ? "MODBUS.EXCEPTION." + reply.ExceptionCode.ToString("X2")
            : reply.Kind == ModbusFailureKind.Timeout ? "MODBUS.TIMEOUT" : "MODBUS.LINK";

        return new ErrorInfo(
            code,
            EventCategory.Device,
            EventLevel.Error,
            ErrorSource.Device,
            "ss.error.comm",
            new ErrorContext(DeviceId: device.Id, TransportId: device.Config.Transport,
                UnitId: device.UnitId, ElapsedMs: reply.ElapsedMs));
    }

    /// <summary>
    /// 睡到下一个到期时刻（<b>单调刻度</b>，findings D107），最多一个调度节拍（<see cref="TickMs"/>）——
    /// 宁可多醒几次，也不让一天只有一次的窗口晚到，同时保证「实际周期误差 ≤ 一个节拍」。
    /// 单调钟不会倒退，故剩余量为负只可能来自「已经过了到期时刻」，取 0 让调用方立刻重算
    /// （绝不把负值交给 WaitOne）。
    /// </summary>
    private int NextSleepMs(long nextWakeTicks)
    {
        if (nextWakeTicks == long.MaxValue) return TickMs;

        var remainingTicks = nextWakeTicks - _clock.NowTicks;
        if (remainingTicks <= 0) return 0;

        var remainingMs = remainingTicks * 1000.0 / Stopwatch.Frequency;
        return remainingMs >= TickMs ? TickMs : (int)Math.Ceiling(remainingMs);
    }

    // ─────────────── 计划（分桶 + 自动分组）───────────────

    /// <summary>
    /// 为设备建立轮询计划：auto 的块/散点组按 <c>intervalMs</c> 分桶，once 的各自成项，
    /// onDemand 完全不进计划（只由门面手动触发）。
    /// </summary>
    private DevicePlan BuildDevicePlan(RuntimeDevice device)
    {
        var plan = new DevicePlan();

        var autoBlocks = device.Blocks.Where(b => b.Enabled && IsAuto(b.Mode)).ToList();
        var onceBlocks = device.Blocks.Where(b => b.Enabled && IsOnce(b.Mode)).ToList();

        var standalone = device.Points.Values
            .Where(p => p.Enabled && !p.IsCalculated && !p.IsInBlock)
            .ToList();

        var autoPoints = standalone.Where(p => IsAuto(p.Mode)).ToList();
        var oncePoints = standalone.Where(p => IsOnce(p.Mode)).ToList();

        // auto：按 intervalMs 分桶（同间隔同节拍），桶内再按 (从站, 数据区, 连续性) 自动分组
        foreach (var bucketPoints in autoPoints.GroupBy(EffectiveIntervalMs))
        {
            var bucket = new Bucket { IntervalMs = bucketPoints.Key };
            foreach (var block in autoBlocks.Where(b => EffectiveIntervalMs(b) == bucketPoints.Key))
            {
                bucket.Blocks.Add(BlockPlan.Of(block));
            }

            bucket.Groups.AddRange(BuildGroups(bucketPoints));
            if (bucket.Blocks.Count > 0 || bucket.Groups.Count > 0) plan.Buckets.Add(bucket);
        }

        // 只声明了块、没有散点的设备：块自己撑起桶
        foreach (var bucketBlocks in autoBlocks.GroupBy(EffectiveIntervalMs))
        {
            if (plan.Buckets.Any(b => b.IntervalMs == bucketBlocks.Key)) continue;

            var bucket = new Bucket { IntervalMs = bucketBlocks.Key };
            bucket.Blocks.AddRange(bucketBlocks.Select(BlockPlan.Of));
            plan.Buckets.Add(bucket);
        }

        // once：块与散点组各自成一项，互不牵连（一项失败只重试它自己）
        foreach (var block in onceBlocks) plan.OnceWorks.Add(OnceWork.ForBlock(block));
        foreach (var group in BuildGroups(oncePoints)) plan.OnceWorks.Add(OnceWork.ForGroup(group));

        return plan;
    }

    /// <summary>
    /// 自动分组：先按 (从站号, 数据区) 分开（ADR D33：不同 unitId 的点位绝不能进同一请求），
    /// 再按地址排序，把「空洞 ≤ ignoreGap」的连续点串成一组；组内超地址组上限则切成多次请求。
    /// 排序与空洞判定按 <see cref="RuntimePoint.SpanStart"/>/<see cref="RuntimePoint.SpanEnd"/>：
    /// &lt;Slices&gt; 点位占的是「最小片段地址 → 最大片段末地址」的整段（片段之间的空洞必须一起读回来）。
    /// </summary>
    private List<GroupPlan> BuildGroups(IEnumerable<RuntimePoint> points)
    {
        var groups = new List<GroupPlan>();

        foreach (var slaveArea in points
                     .GroupBy(p => new { p.UnitId, p.Area })
                     .OrderBy(g => g.Key.UnitId)
                     .ThenBy(g => (int)g.Key.Area))
        {
            var limit = slaveArea.Key.Area is RuntimeArea.Coil or RuntimeArea.DiscreteInput
                ? _global.GroupLimitBits
                : _global.GroupLimitRegisters;

            var run = new List<RuntimePoint>();
            var runEnd = 0;

            foreach (var point in slaveArea.OrderBy(p => p.SpanStart).ThenBy(p => p.Length))
            {
                if (run.Count > 0)
                {
                    var gap = point.SpanStart - runEnd;
                    if (gap > 0 && gap > _global.IgnoreGap)
                    {
                        groups.Add(GroupPlan.Of(slaveArea.Key.UnitId, slaveArea.Key.Area, run, limit));
                        run = new List<RuntimePoint>();
                    }
                }

                run.Add(point);
                runEnd = Math.Max(runEnd, point.SpanEnd);
            }

            if (run.Count > 0) groups.Add(GroupPlan.Of(slaveArea.Key.UnitId, slaveArea.Key.Area, run, limit));
        }

        return groups;
    }

    /// <summary>取生效间隔：显式 <c>intervalMs</c> 优先，未写用全局默认。</summary>
    private int EffectiveIntervalMs(RuntimePoint point) => point.IntervalMs ?? _global.DefaultIntervalMs;

    private int EffectiveIntervalMs(RuntimeBlock block) => block.IntervalMs ?? _global.DefaultIntervalMs;

    private static bool IsAuto(string mode) => string.Equals(mode, "auto", StringComparison.OrdinalIgnoreCase);

    private static bool IsOnce(string mode) => string.Equals(mode, "once", StringComparison.OrdinalIgnoreCase);

    private static bool IsOnDemand(string mode) => string.Equals(mode, "ondemand", StringComparison.OrdinalIgnoreCase);

    // ─────────────── 节拍执行 ───────────────

    /// <summary>
    /// 执行一个节拍的全部请求（块窗口 + 各自动分组窗口）。
    /// 返回 null = 本轮所有读请求都成功；否则返回首个失败的应答（once 用返回值判定是否要重试）。
    /// <para>
    /// **同节拍内每个窗口独立执行、独立失败**（findings D73）：此前用 <c>firstFailure ??= ReadWindow(...)</c>，
    /// 首个窗口失败后同节拍的其它窗口**一个请求都不发**——被跳过窗口的点位既不降质量也不发错误事件，
    /// 宿主看到的是「好值的陈旧数据」（只能靠 <c>GetValueAge</c> 察觉），
    /// 且与「一组数据必须来自同一时刻」的口径冲突（docs/01 §6.1）。
    /// 现在每个窗口都发请求、各自按「一次窗口失败 = 一条错误事件」聚合上报，
    /// 质量降级与退避状态机各按既有口径走（<see cref="ReadWindow"/>），返回值只用于
    /// 「本轮是否全部成功」的判定（once 重试）。
    /// </para>
    /// 单轮里绝不抛出去：未归类异常按 GATE-4/GATE-5 上报并保住轮询线程。
    /// </summary>
    private ModbusReply? ExecuteWork(RuntimeDevice device, WorkUnit work)
    {
        try
        {
            var master = _masters[device.Config.Transport];
            ModbusReply? firstFailure = null;

            foreach (var block in work.Blocks)
            {
                // 块 = 显式声明的一段窗口，一次请求读完，块内点位共享结果
                var failure = ReadWindow(device, master, block.Area, block.Start, block.Count, block.UnitId,
                    block.Points, block.Start);
                firstFailure ??= failure;
            }

            foreach (var group in work.Groups)
            {
                // 同一组的所有窗口在同一节拍内背靠背读完（超地址组上限的切分不跨节拍）：
                // 一个窗口失败不跳过同节的其它窗口（否则一组数据会来自不同时刻，findings D73）
                foreach (var window in group.Windows)
                {
                    var failure = ReadWindow(device, master, group.Area, window.Address, window.Count, group.UnitId,
                        window.Points, window.Address);
                    firstFailure ??= failure;
                }
            }

            return firstFailure;
        }
        catch (Exception ex)
        {
            // 单节拍失败不终止轮询线程（GATE-5）；但绝不静默（GATE-4，findings D24）
            ReportUnexpectedFailure(device, work.Points, ex);
            return ModbusReply.Fail(ModbusFailureKind.LinkDown, 0, "unexpected scheduler failure", 0);
        }
    }

    /// <summary>
    /// 手动触发一个设备上**全部** <c>mode="onDemand"</c> 的块与点位（D：IModbusDebugTool 的整批触发）。
    /// 只处理 onDemand 项；auto/once 不在范围内（单点/单块触发走 TriggerReadAsync / TriggerBlockReadAsync）。
    /// 返回本次成功刷新的点位数（失败窗口内的点位不算）。
    /// </summary>
    internal int TriggerOnDemand(RuntimeDevice device)
    {
        // Transport@enabled=false：链路上没有主站（构造期已按 enabled 过滤）——停用链路上的设备
        // 一律不发通讯，「整批触发」同样是无事发生（返回 0），不能抛内部字典异常（findings D44 的停用口径）。
        if (!_masters.TryGetValue(device.Config.Transport, out var master)) return 0;

        var updated = 0;

        foreach (var block in device.Blocks.Where(b => b.Enabled && IsOnDemand(b.Mode)))
        {
            var plan = BlockPlan.Of(block);
            if (ReadWindow(device, master, plan.Area, plan.Start, plan.Count, plan.UnitId, plan.Points, plan.Start) == null)
            {
                updated += plan.Points.Count(p => p.Enabled);
            }
        }

        var points = device.Points.Values
            .Where(p => p.Enabled && !p.IsCalculated && !p.IsInBlock && IsOnDemand(p.Mode))
            .ToList();

        foreach (var group in BuildGroups(points))
        {
            foreach (var window in group.Windows)
            {
                if (ReadWindow(device, master, group.Area, window.Address, window.Count, group.UnitId,
                        window.Points, window.Address) == null)
                {
                    updated += window.Points.Count(p => p.Enabled);
                }
            }
        }

        return updated;
    }

    // ─────────────── 单窗口读取与解码 ───────────────

    /// <summary>
    /// 读一个窗口并解码落缓存。返回 null 表示成功；否则返回失败应答（已发聚合错误事件、
    /// 已按 <c>Quality@onCommError</c> 置值、已喂给两层退避状态机）。
    /// </summary>
    private ModbusReply? ReadWindow(
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

        var timestamp = _clock.UtcNow;

        if (!reply.Success)
        {
            EmitAggregateError(device, area, address, unitId, reply, points.Count);

            // 先喂退避状态机，再定质量：这一步之后该设备/链路是否在退避，决定用哪套质量口径——
            // 退避中（= 这段失败会变成持续离线）用 Global/Reconnect@offlineQuality；
            // 不进队列（协议异常、退避被关闭）才用 Global/Quality@onCommError。
            // 反过来会震荡：失败置 Bad → 退避置 Offline → 下次失败又置 Bad，每次都刷一条事件。
            NotifyCommFailure(device.Id, reply.Kind);
            MarkWindowFailed(points, timestamp, TryGetBackoffWait(device, out _));
            return reply;
        }

        foreach (var point in points)
        {
            if (!point.Enabled)
            {
                continue; // 停用的点位不写缓存、不报警
            }

            // Slices 点位与普通点位走同一条路：窗口按 SpanStart/SpanEnd 覆盖住片段（含中间空洞），
            // 这里按片段把值拼出来再解码——不再为片段点位单独发多次请求（旧限制已删除，docs/01 §4.2）
            if (!point.TryExtract(reply.Registers, windowStart, out var slice))
            {
                _cache.Set(point.Key, PointValue.Bad("ss.reason.shortFrame", timestamp));
                continue;
            }

            // 解码 + 点位脚本（ADR D41）：有脚本时脚本返回值即工程值；脚本失败置 Bad 并在 ScriptDecoder 里记错误事件
            var value = DecodePoint(point, slice, timestamp);
            PublishIfChanged(point, value);
            _cache.MarkAcquired(point.Key, timestamp);   // 「上次成功采集」：GetValueAge 的数据来源
            _alarms.Evaluate(point, value);
        }

        NotifyCommSuccess(device);   // 成功一次即归零该设备与所属链路的退避队列
        return null;
    }

    /// <summary>
    /// 单点位解码（轮询与按需触发共用）：标准编解码 + 点位脚本（ADR D41）。
    /// 无脚本时就是 <see cref="PointCodec.Decode(RuntimePoint, ushort[], DateTimeOffset)"/>；有脚本时把原始寄存器/缩放前数值交给脚本，
    /// 脚本返回值即工程值；脚本失败按 onError 置 Bad 并记错误事件，绝不抛。
    /// </summary>
    private PointValue DecodePoint(RuntimePoint point, ushort[] registers, DateTimeOffset timestamp)
    {
        var value = PointCodec.Decode(point, registers, timestamp, out var rawValue);
        return _scripts.ApplyDecode(point, registers, value, rawValue, timestamp);
    }

    /// <summary>
    /// 脚本里 <c>P('id')</c> 的点位解析：短 id 在本设备内查，含 '/' 的按 "deviceId/pointId" 跨设备查。
    /// 与门面路径（<see cref="SamplerEngine"/>，同一个 <see cref="Scripts"/> 实例，findings W72）
    /// 共用同一份实现 <see cref="CalculatedPointResolver.Resolve"/>（findings D60）：
    /// 只认**好值**；缓存没有好值且依赖是计算点时**递归求值**（表达式型 / 脚本型），
    /// 依赖不是计算点则返回 null（脚本里拿到 undefined，自行判断——与表达式求值器同一口径）。
    /// 递归带防环与深度上限、坏值沿链传染，且**纯内存、不发任何通讯**。
    /// </summary>
    private PointValue? ResolveScriptPoint(string deviceId, string id)
        => CalculatedPointResolver.Resolve(deviceId, id, _registry, _cache, _scripts, _resolveI18n);

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

    /// <summary>
    /// 失败窗口的置值。不进退避时按 <c>Quality@onCommError</c>（bad|offline|uncertain）与
    /// <c>Quality@onCommErrorValue</c>；<paramref name="backingOff"/> = 这次失败让该目标进了退避，
    /// 改用退避口径 <c>Reconnect@offlineQuality</c>（docs/01 §6.3）——
    /// 否则会出现「失败置 Bad ↔ 退避置 Offline」来回震荡、每次都刷一条事件。
    /// keepLast 且不退避时缓存完全不动（界面结合 staleAfterMs 置灰）。
    /// </summary>
    private void MarkWindowFailed(IReadOnlyList<RuntimePoint> points, DateTimeOffset timestamp, bool backingOff)
    {
        if (backingOff)
        {
            ApplyOfflineQuality(points, timestamp);
            return;
        }

        if (string.Equals(_global.OnCommErrorValue, "keepLast", StringComparison.OrdinalIgnoreCase)) return;

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

    /// <summary>退避期间的质量等级（<c>Reconnect@offlineQuality</c>：offline | bad）。</summary>
    private PointQuality OfflineQuality => string.Equals(_backoff.OfflineQuality, "bad", StringComparison.OrdinalIgnoreCase)
        ? PointQuality.Bad
        : PointQuality.Offline;

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

    /// <summary>设备轮询计划：按间隔分桶的周期项 + 各自独立的 once 项。</summary>
    private sealed class DevicePlan
    {
        public List<Bucket> Buckets { get; } = new();
        public List<OnceWork> OnceWorks { get; } = new();
    }

    /// <summary>一个节拍要执行的全部请求（周期桶与 once 项共用）。</summary>
    private abstract class WorkUnit
    {
        public List<BlockPlan> Blocks { get; } = new();
        public List<GroupPlan> Groups { get; } = new();

        /// <summary>本项涉及的全部点位（兜底异常上报用）。</summary>
        public IEnumerable<RuntimePoint> Points
            => Blocks.SelectMany(b => b.Points).Concat(Groups.SelectMany(g => g.Points));
    }

    /// <summary>周期桶：同一 intervalMs 的块与散点组共用一个节拍。</summary>
    private sealed class Bucket : WorkUnit
    {
        public int IntervalMs { get; set; }

        /// <summary>下次到期时刻（**单调刻度**，findings D107；不是墙钟时刻，系统时间跳变不影响）。</summary>
        public long DueTicks { get; set; }
    }

    /// <summary>once 项：Start 后读一次，成功一次即 Done（失败按全局默认间隔重试）。</summary>
    private sealed class OnceWork : WorkUnit
    {
        public bool Done { get; set; }

        /// <summary>下次到期时刻（**单调刻度**，口径同 <see cref="Bucket.DueTicks"/>）。</summary>
        public long DueTicks { get; set; }

        public static OnceWork ForBlock(RuntimeBlock block)
        {
            var work = new OnceWork();
            work.Blocks.Add(BlockPlan.Of(block));
            return work;
        }

        public static OnceWork ForGroup(GroupPlan group)
        {
            var work = new OnceWork();
            work.Groups.Add(group);
            return work;
        }
    }

    /// <summary>块窗口：块是显式声明的一段地址，一次请求读完，块内点位共享结果。</summary>
    private sealed class BlockPlan
    {
        public RuntimeArea Area { get; private set; }
        public int Start { get; private set; }
        public int Count { get; private set; }
        public int UnitId { get; private set; }
        public IReadOnlyList<RuntimePoint> Points { get; private set; } = Array.Empty<RuntimePoint>();

        public static BlockPlan Of(RuntimeBlock block)
        {
            return new BlockPlan
            {
                Area = block.Area,
                Start = block.Start,
                Count = block.Count,
                UnitId = block.UnitId,
                Points = block.Points,
            };
        }
    }

    /// <summary>自动分组的一个地址组：同一 (从站, 区) 的连续点位；可能由多个窗口组成（超地址组上限被切分）。</summary>
    private sealed class GroupPlan
    {
        public RuntimeArea Area { get; private set; }
        public int UnitId { get; private set; }
        public IReadOnlyList<WindowPlan> Windows { get; private set; } = Array.Empty<WindowPlan>();

        public IEnumerable<RuntimePoint> Points => Windows.SelectMany(w => w.Points);

        /// <summary>
        /// 把一串已按地址排序的点位装成窗口：贪心累加，装不下（跨度超地址组上限）就开新窗口。
        /// 窗口跨度为 [首个点位 SpanStart, 末个点位 SpanEnd)——中点的空洞（≤ ignoreGap）被一次请求一并读回；
        /// &lt;Slices&gt; 点位同样按 SpanStart/SpanEnd 计入，保证它的全部片段落在同一个窗口里。
        /// </summary>
        public static GroupPlan Of(int unitId, RuntimeArea area, IReadOnlyList<RuntimePoint> run, int limit)
        {
            var windows = new List<WindowPlan>();
            var pending = new List<RuntimePoint>();
            var start = 0;
            var end = 0;

            foreach (var point in run)
            {
                var pointEnd = point.SpanEnd;

                if (pending.Count > 0 && pointEnd - start > limit)
                {
                    windows.Add(WindowPlan.From(start, end, pending));
                    pending = new List<RuntimePoint>();
                }

                if (pending.Count == 0)
                {
                    start = point.SpanStart;
                    end = pointEnd;
                }
                else if (pointEnd > end)
                {
                    end = pointEnd;
                }

                pending.Add(point);
            }

            if (pending.Count > 0) windows.Add(WindowPlan.From(start, end, pending));

            return new GroupPlan { Area = area, UnitId = unitId, Windows = windows };
        }
    }

    /// <summary>一个读取窗口（地址 + 长度 + 落在窗口内的点位）。</summary>
    private sealed class WindowPlan
    {
        public int Address { get; private set; }
        public int Count { get; private set; }
        public IReadOnlyList<RuntimePoint> Points { get; private set; } = Array.Empty<RuntimePoint>();

        /// <summary>
        /// 快照点位列表（ToArray）：调用方复用同一个 pending 列表，
        /// 直接存引用会让前面所有窗口的 Points 变成最后一个窗口的内容（D15，集成实测抓出）。
        /// </summary>
        public static WindowPlan From(int address, int end, IReadOnlyList<RuntimePoint> points)
        {
            return new WindowPlan
            {
                Address = address,
                Count = end - address,
                Points = points.ToArray(),
            };
        }
    }

    /// <summary>
    /// 兜底：一个节拍执行抛出未归类异常时上报（findings D24）。驱动层异常本该在通道边界
    /// （<c>ModbusChannel.Execute</c>）就包装成 <see cref="ModbusIoException"/> 并给出分类（ADR D37），
    /// 这里只处理「漏网」的意外：发一条链路错误事件 + 按 <c>Global/Quality@onCommError</c> 策略
    /// 把该项点位置坏，同时保证轮询线程存活（GATE-5）。绝不静默吞掉（GATE-4）。
    /// 按框架约定事件不携带人类语言句子，异常细节不进 ErrorInfo，只用于区分错误码。
    /// </summary>
    private void ReportUnexpectedFailure(RuntimeDevice device, IEnumerable<RuntimePoint> points, Exception exception)
    {
        var timestamp = _clock.UtcNow;
        var affected = points.Where(p => p.Enabled).ToList();

        var isIoFailure = exception is IOException or SocketException or ModbusIoException or ObjectDisposedException;

        // 兜底异常同样喂退避状态机（IO 类 → 链路级；其余按设备级），质量口径与正常失败路径一致
        NotifyCommFailure(device.Id, isIoFailure ? ModbusFailureKind.LinkDown : ModbusFailureKind.Timeout);
        MarkWindowFailed(affected, timestamp, TryGetBackoffWait(device, out _));

        var info = new ErrorInfo(
            isIoFailure ? "MODBUS.LINK" : "SS.SCHEDULER.UNEXPECTED",
            EventCategory.Device,
            EventLevel.Error,
            ErrorSource.Transport,
            "ss.error.comm",
            new ErrorContext(
                DeviceId: device.Id,
                TransportId: device.Transport.Id,
                UnitId: device.UnitId,
                ConsecutiveFailures: affected.Count));

        _bus.Emit(new LinkError(info));
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
