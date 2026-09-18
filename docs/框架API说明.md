# SuperSampler 框架 API 说明

> 本文档是 SuperSampler 框架暴露给宿主的**全部 API**：三层身份（寻址模型）、
> 两个门面接口（`IDeviceManager` / `IModbusDebugTool`）的方法签名与语义、值模型与质量四态、
> 写入四态结果、事件订阅 API。
> 契约位于 `SuperSampler.Abstractions` 程序集；宿主**拿不到**传输层、驱动、调度器、编解码、
> Block 中的任何一个——门面就是全部。
>
> 配套文档：
> - [`使用说明.md`](使用说明.md) —— 框架整体使用说明（架构、接入方式、引擎生命周期）
> - [`配置XML说明.md`](配置XML说明.md) —— 配置 XML 逐节点逐属性字段说明
> - [`测试与验收/`](测试与验收/) —— 测试计划与验收报告归档
>
> 关键命名空间：门面契约在 `SuperSampler.Abstractions.Facade`、值模型在 `SuperSampler.Abstractions.Values`、
> 事件契约在 `SuperSampler.Abstractions.Events`、错误模型在 `SuperSampler.Abstractions.Errors`。
> 引擎实现类 `SamplerEngine`（`SuperSampler.Core.Runtime`）同时实现两个门面接口。

---

## 1. 三层身份（寻址模型）

| 层 | 是什么 | 唯一键 | 暴露为 |
|---|---|---|---|
| **设备/从站** | 一条链路上的一个从站 | `transport + unitId`（串口 或 IP:端口 + 从站号） | 配置 `Device`；门面的 `deviceId` |
| **物理寄存器** | 线上一个匿名地址 | `device + area + address` | **不做配置身份**；仅调试工具按此寻址 |
| **点位** | 寄存器的命名视图：类型/字序/缩放/枚举等解析规则 | `device + 点名` | `GetValue` / `SetValue` 的主键 |

**点位与物理寄存器是 N:M 的关系**，这是点表存在的根本原因：

- 同一物理地址支撑多个点：`Holding@10` 同时是 `status`（整字枚举）和 `running`（bit0）
- 一个点横跨多个地址：`float32` 占 2 个寄存器；`slices` 非连续两段拼一个值

由此得出的两条配置规则：

1. **`Point@id` 在点表（=设备）内唯一**，不要求全局唯一
2. **同一 `(transport, unitId)` 不得被两个启用的 Device 声明**，否则重复轮询同一从站（加载期报错）

> 门面寻址主键始终是 `(deviceId, pointId)`；物理层操作（`RawReadAsync`/`RawWriteAsync`）按
> `(deviceId, ModbusArea, address)` 寻址。

```csharp
var text = mgr.GetValue("Mold01", "mold.temp");     // 设备 + 点位 两层主键
var detail = mgr.GetValueDetail("Mold01", "mold.temp");
```

---

## 2. `IDeviceManager` —— 宿主日常入口（7 个方法）

| # | 签名 | 一句话语义 |
|---|---|---|
| 1 | `string GetValue(string deviceId, string pointId)` | 解析后的**显示字符串**（缩放/枚举映射/前后缀/i18n 已完成）；质量非 Good 一律返回 `Global@nullText`，绝不把旧值当好值显示 |
| 2 | `PointValue GetValueDetail(string deviceId, string pointId)` | 完整三元组 `{值, 质量, 时间戳}`；**读实时缓存，不发起任何通讯**，可高频调用（计算点例外：按需实时求值一次表达式/脚本，仍不发通讯） |
| 3 | `Task<WriteResult> SetValueAsync(string deviceId, string pointId, object? value, CancellationToken ct = default)` | 基础写入，走完整写管道：可写性 → 数据区 → 范围 → 类型容量 → 编码 → 下发 →（`verify=true` 时）回读；无 user 重载以内置身份 `Local` 记审计 |
| 4 | `Task<WriteResult> SetValueAsync(string deviceId, string pointId, object? value, ActingUser user, CancellationToken ct = default)` | 带操作者身份的写入：`ActingUser` 用于**审计记账**（写审计与报警确认事件带操作者名，由宿主认证后传入）；**角色授权未实现**——`Write@permission` 当前不生效，是否允许写入由宿主自行把关 |
| 5 | `Task<AlarmAckResult> AcknowledgeAlarmAsync(string deviceId, string pointId, string alarmId, CancellationToken ct = default)` | 确认报警：清 AckPending → 报警回到可重触发状态 → 发 `AlarmAcknowledgedEvent`；`alarmId` 与 `AlarmRaisedEvent.AlarmId` 一致（点位未写 `Alarm@id` 时 = `"pointId#type"`） |
| 6 | `Task<AlarmAckResult> AcknowledgeAlarmAsync(string deviceId, string pointId, string alarmId, ActingUser user, CancellationToken ct = default)` | 带操作者身份的报警确认，确认事件与审计按该用户记账 |
| 7 | `TimeSpan? GetValueAge(string deviceId, string pointId)` | 「多久没刷新」：距该点位**上次成功采集**的时长；`null` = 从未成功采集过（引擎刚启动、一直读不到等） |

**语义细节**：

- `GetValue` / `GetValueDetail` / `SetValueAsync` / `AcknowledgeAlarmAsync` / `GetValueAge`
  对**未知 id 抛 `KeyNotFoundException`**（配置错误尽早暴露，不静默返 null）。
- `GetValueAge` 的「成功采集」口径：该点所在读窗口通讯成功且该点完成解码落缓存
  （解码降级为 `Uncertain`/`Bad` 也算采集成功——那说明通讯是通的）；**通讯失败置坏不算**成功采集。
  计算点按「上次求值成功」计。框架**不做陈旧判定**（阈值/置灰/提示由宿主定）。
- 写路径**无生命周期门禁**：未 Start / 已 Stop / 已 Dispose 之后调用写仍会走写调用（成败/异常由链路状态决定）；
  退避期间宿主写入不受闸门约束（退避只约束轮询）。另：`Dispose` 之后写审计事件会被已停止的事件总线丢弃，
  若用写审计做「停机上账」，必须在停机前把在途写做完。

```csharp
// 读显示串 + 读三元组（按质量分支处理）
string text  = mgr.GetValue("Mold01", "mold.temp");          // 质量非 Good → nullText
PointValue pv = mgr.GetValueDetail("Mold01", "mold.pressure");
if (pv.IsGood) { /* 用 pv.Value */ } else { /* 置灰 / 判联锁 */ }

// 带操作者写入
var acting = new ActingUser("zhangsan", "Operator");
var wr = await mgr.SetValueAsync("Mold01", "mold.setTemp", 123.4, acting);

// 报警确认（alarmId 与 AlarmRaisedEvent.AlarmId 一致）
var ack = await mgr.AcknowledgeAlarmAsync("Mold01", "mold.temp", "mold.temp#high", acting);

// 陈旧判断（阈值由宿主定）
var age = mgr.GetValueAge("Mold01", "mold.temp");
if (age is { } t && t > TimeSpan.FromSeconds(5)) { /* 提示陈旧 */ }
```

---

## 3. `IModbusDebugTool` —— 调试与诊断（7 个方法）

三条能力对应三层身份：

1. **物理层**（RawRead / RawWrite）：绕过点表语义按 `(area, address)` 直读直写，返回原始寄存器不解析；
   受配置 `Diagnostics@allowRawAccess` 开关控制（**默认 false**；原始写绕过写保护，生产必须关）。
2. **语义层**（TriggerRead / TriggerBlockRead / TriggerOnDemandRead）：走点位定义的完整解析管道，结果写实时缓存。
3. **运维层**（RetryDevice / RetryLink）：打断退避、立即重试——**与 Trigger 同级，不受 `allowRawAccess` 门禁**
   （它是运维动作，不是裸读写）。

| # | 签名 | 一句话语义 |
|---|---|---|
| 1 | `Task<RawReadResult> RawReadAsync(string deviceId, ModbusArea area, ushort address, ushort count, CancellationToken ct = default)` | 原始读：直读一段线圈/离散量/寄存器，不做解析（`allowRawAccess=false` 时拒绝） |
| 2 | `Task<RawWriteResult> RawWriteAsync(string deviceId, ModbusArea area, ushort address, IReadOnlyList<ushort> values, CancellationToken ct = default)` | 原始写：直写线圈/寄存器；位区 `values` 以 0/1 表示位（`allowRawAccess=false` 时拒绝） |
| 3 | `Task<PointValue> TriggerReadAsync(string deviceId, string pointId, CancellationToken ct = default)` | 手动触发某点位定义的读取（走完整解析管道），结果写入缓存并返回；未知 id 抛 `KeyNotFoundException` |
| 4 | `Task<int> TriggerBlockReadAsync(string blockId, CancellationToken ct = default)` | 手动触发整块读取（配置里的 Block），返回本块读到的寄存器数量；未知 blockId 抛异常 |
| 5 | `Task<int> TriggerOnDemandReadAsync(string deviceId, CancellationToken ct = default)` | 手动触发设备上**全部** `mode="onDemand"` 的块与点位整批刷一遍；返回成功刷新的点位数；`auto`/`once` 项不在范围内 |
| 6 | `Task<ManualRetryResult> RetryDeviceAsync(string deviceId, CancellationToken ct = default)` | **手动重试单台设备**：立刻打断该设备退避，按其轮询计划采集一次（auto 桶 + 未完成 once 项）；成功一次 → 设备与链路退避队列**一起归零** |
| 7 | `Task<ManualRetryResult> RetryLinkAsync(string transportId, CancellationToken ct = default)` | **手动重试整条链路**：清空该链路与全部从站退避 → 关闭连接并立即重连，对每个启用设备各尝试一次采集 |

**门禁与语义细节**：

- `Global/Reconnect@manualRetry=false` 时 `RetryDeviceAsync` / `RetryLinkAsync` 返回
  `ManualRetryOutcome.NotSupported`（**不发任何通讯、不打断退避**）；未知 `deviceId`/`transportId` 抛 `KeyNotFoundException`。
- 调试请求与轮询**共用同一条通道队列，排队执行，不插队**、不破坏轮询顺序。
- `RetryDeviceAsync`/`RetryLinkAsync` 与后台轮询并发安全：通道内部严格串行，状态机加锁。

```csharp
// 物理层（门禁之后）——按 (area, address) 直读原始寄存器
var raw = await dbg.RawReadAsync("Mold01", ModbusArea.HoldingRegister, 0, 8);
if (raw.Success) { /* raw.Registers 为 ushort[]，raw.RawBytes 为大端拼接 */ }

// 语义层——强制刷新一个 onDemand 点位
var pv = await dbg.TriggerReadAsync("Mold01", "mold.temp");

// 运维层（不受门禁）——现场「立刻再试一次」
var retry = await dbg.RetryDeviceAsync("Mold01");
if (retry.Outcome == ManualRetryOutcome.Succeeded) { /* 退避已归零 */ }
```

---

## 4. 值模型：`PointValue` 与质量四态

`PointValue` 是框架内一切消费方（界面、历史、报警、报表、导出）统一使用的三元组
**`{值, 质量, 时间戳}`**，语义对齐 OPC UA：值永远伴随质量与时间戳，三者不可分割。不可变结构体。

| 成员 | 说明 |
|---|---|
| `object? Value` | 工程值：`bool` / `int` / `long` / `double` / `string` / `DateTime` / `byte[]`，由点位 dataType 决定 |
| `PointQuality Quality` | 质量四态（见下表） |
| `DateTimeOffset Timestamp` | 采集/产生时间 |
| `string? Reason` | 质量非 Good 时的坏值原因（**i18n key**）；Good 时为 null |
| `bool IsGood` | `Quality == PointQuality.Good`；界面/报警/历史都应先看这个 |
| `bool TryGetValue<T>(out T value)` | 按期望类型取值；类型不符或值为空返回 false，不抛异常 |

**质量四态（`PointQuality`）**：

| 值 | 含义 | 使用建议 |
|---|---|---|
| `Good` | 数据可信 | 正常显示/记录/报警 |
| `Uncertain` | 数据可疑（如 NaN、越界、换算异常），值可能仍可用但需谨慎 | 显示但带警告标注；不用于联锁 |
| `Bad` | 数据不可用（解析失败、被策略丢弃等），值可能是旧值或不存在 | 置灰；配合 `Reason` 显示原因 |
| `Offline` | 链路或设备离线，值是旧的或不存在 | 明确标注离线状态 |

**工厂方法**（用于构造，宿主一般只读不构造）：`PointValue.Good(value, ts)`、
`PointValue.Uncertain(value, reason, ts)`、`PointValue.Bad(reason, ts)`（不带旧值）、
`PointValue.Bad(reason, ts, lastValue)`（保留最后一次可信值）、`PointValue.Offline(ts)`。

> 坏值原因码是 i18n key（如 `ss.quality.offline`、脚本失败时的 `ss.reason.scriptTimeout`），
> 由宿主本地化成具体语言句子；框架不产出中英文字句。

```csharp
PointValue pv = mgr.GetValueDetail("Mold01", "mold.temp");
if (pv.IsGood && pv.TryGetValue<double>(out var v)) { chart.Feed(v); }
else { statusLabel.Show(pv.Quality, pv.Reason); }
```

---

## 5. 写入四态结果与处置建议

`SetValueAsync` 返回 `WriteResult(WriteOutcome Outcome, ErrorInfo? Error, PointValue? Readback, bool VerifyMismatch = false)`。
**业务失败用结果对象表达，不抛异常**；只有编程错误（null 参数、未知 id）才抛异常。

| `WriteOutcome` | 出现条件 | 含义 | 处置建议 |
|---|---|---|---|
| `Succeeded` | 应答成功且（`verify=true` 时）回读一致；或写超时后回读确认已生效 | 写入成功 | 正常刷新界面 |
| `Failed` | 设备拒绝 / 通讯错误；或写超时后回读确认未生效 | 明确失败 | **可安全重试** |
| `Indeterminate` | 写超时且回读失败，无法定论 | 不确定：**写入可能已生效** | **绝不自动重试**，须回读确认（人工判断后补一条查询） |
| `Rejected` | 可写性 / 数据区 / 范围 / 类型容量 / 编码等校验未通过 | 被拒绝：**未发出任何通讯** | 提示用户原因，无需重试 |

**`VerifyMismatch` 语义**：仅当 `verify=true` 且通讯成功时置位——设备应答成功，但回读值与写入值不一致
（如设备自行钳位）。此时 `Outcome` 仍为 `Succeeded`（通讯确实成功）；宿主应据此在界面明确提示
「实际值 X 与写入值不同」，且**不要自动重试**。

**`Readback`**：`verify=true` 的点位写入后回读所得，或 `Indeterminate` 之后的回读结果。

```csharp
var wr = await mgr.SetValueAsync("Mold01", "mold.setTemp", 150.0, acting);
switch (wr.Outcome)
{
    case WriteOutcome.Succeeded when wr.VerifyMismatch:
        Show($"设备已钳位：实际值 {wr.Readback?.Value} 与写入值不同");
        break;
    case WriteOutcome.Succeeded: break;
    case WriteOutcome.Failed:    MaybeRetry(wr); break;
    case WriteOutcome.Indeterminate: AskUserToVerify(); break;   // 绝不自动重试
    case WriteOutcome.Rejected:  Notify(wr.Error); break;
}
```

---

## 6. 报警确认：`AlarmAckOutcome`

`AcknowledgeAlarmAsync` 返回 `AlarmAckResult(AlarmAckOutcome Outcome)`。

| 值 | 含义 |
|---|---|
| `Acknowledged` | 确认已记录：AckPending 清除（报警回到可重触发状态），并已发出 `AlarmAcknowledgedEvent`。**只要该报警触发过**，重复确认、确认已恢复正常的报警都算成功（确认是幂等运维动作，宿主不必自己判断） |
| `NotPending` | 该报警当前没有可确认的状态：**从未触发过**，或该 `(deviceId, pointId, alarmId)` 从未被评估过 / 未配置该 id。未发出任何事件；**这不是失败** |

未知 `deviceId`/`pointId` 仍抛 `KeyNotFoundException`。确认只解 latch 门控，不改报警值语义。

---

## 7. 事件订阅 API

框架的事件总线是**进程内**的：框架只 `Emit`，宿主 `Subscribe` 自己关心的类型或基接口。
总线实现类 `InProcessEventBus` 实现 `IEventBus`（契约在 `SuperSampler.Abstractions.Events`）；
宿主通过 `SamplerEngine.Bus` 拿到它。

### 7.1 订阅签名

```csharp
ISubscription Subscribe<TEvent>(
    Action<IEventEnvelope<TEvent>> handler,
    DeliveryMode mode = DeliveryMode.Queued,
    OverflowPolicy overflow = OverflowPolicy.DropOldest,
    int? queueCapacity = null) where TEvent : IEvent;

ISubscription SubscribeBatch<TEvent>(
    Action<IReadOnlyList<IEventEnvelope<TEvent>>> handler,
    int maxBatchSize,
    TimeSpan maxBatchDelay,
    DeliveryMode mode = DeliveryMode.Queued,
    OverflowPolicy overflow = OverflowPolicy.DropOldest,
    int? queueCapacity = null) where TEvent : IEvent;
```

- `TEvent` 可以是具体事件类型，也可以是**基接口**（如 `IErrorEvent`）——一次订阅收齐一族事件。
- `IEventEnvelope<TEvent>`：`{ Seq, Time, Body }`。`Seq` 是全局单调递增序号，宿主据此检测事件丢失；
  `Time` 是总线分配的投递时间（UTC）；事件本体保持不可变。
- **批量订阅特例**：`SubscribeBatch` 攒批依赖队列，传 `DeliveryMode.Inline` 无意义——**一律按 `Queued` 处理**。
- **`queueCapacity`**：`null` = 默认 4096；`Queued` 下传 `<1` 抛 `ArgumentOutOfRangeException`；`Inline` 下不校验（传 0 不抛）。

### 7.2 `DeliveryMode`（投递模式）

| 值 | 行为 | 适用 |
|---|---|---|
| `Queued`（默认） | 进入该订阅者**专属有界队列**，异步投递；慢消费者不拖累采集线程；单订阅内严格保序（FIFO） | 通用；一个 Queued 订阅对应一条后台投递线程（订阅数量级的资源开销） |
| `Inline` | 在发出方线程上**同步执行**；**没有队列**，`queueCapacity`/`overflow` 被忽略，`Dropped` 恒为 0 | 订阅者必须极快（计数、置标志） |

### 7.3 `OverflowPolicy`（队列满时的溢出策略）

| 值 | 行为 | 适用 |
|---|---|---|
| `DropOldest`（默认） | 挤掉队列里最旧的一条，放入新事件 | 高频类别默认 |
| `DropNewest` | 丢弃正在入队的新事件 | 保留已有队列 |
| `Wait` | 阻塞发出方直到有空位（**永不丢弃**） | **仅限低频且不可丢的类别**（alarm/audit 建议 wait） |

> **丢弃必须可见**：`ISubscription.Dropped` / `IEventBusMetrics.TotalDropped` 计入两类——① 队列满按策略丢弃；
> ② 退订（`Dispose`）或总线停止时队列里尚未投递的条目。绝不静默丢弃（否则宿主的审计/报警会漏记而不自知）。

### 7.4 `ISubscription`（订阅句柄）

| 成员 | 说明 |
|---|---|
| `long Delivered` | 已成功投递给本订阅的事件数 |
| `long Dropped` | 丢弃计数（口径见上；Inline 订阅恒为 0） |
| `long Faults` | 订阅者处理时抛异常的次数（订阅者异常被总线吞掉并计数，绝不影响框架） |
| `bool IsActive` | 是否仍在接收事件 |
| `void Dispose()` | **退订**；同时释放该订阅的队列与泵线程 |

总线整体计数：`IEventBus.Metrics`（`TotalEmitted` / `TotalDropped` / `TotalFaults` / `ActiveSubscriptions`）；
总线实例还暴露 `HandlerFaulted` 事件（订阅者处理抛异常时触发）。

### 7.5 可订阅事件类型

| 事件 | 类别 | 关键字段 | 说明 |
|---|---|---|---|
| `PointValueChangedEvent` | Value | `DeviceId`, `PointId`, `Value` | 点位值变化（仅在值/质量相对缓存变化时发出；高频，用批量订阅） |
| `AlarmRaisedEvent` / `AlarmClearedEvent` / `AlarmAcknowledgedEvent` | Alarm | `AlarmId`, `DeviceId`, `PointId` | 报警激活/清除/确认；均实现 `IAlarmEvent`，一条订阅收齐 |
| `BackoffEnteredEvent` | Device / Connection | `Scope`, `DeviceId`, `TransportId`, `UnitId`, `Attempt`, `DelayMs`, `NextRetryAt`, `ReasonCode` | 进入退避；`ReasonCode` 三值：`MODBUS.TIMEOUT`（设备级）/ `MODBUS.LINK`（链路级）/ `SS.BACKOFF.ALL_DEVICES`（同链路全部从站退避→升级为链路级）；`NextRetryAt` 为下次重试时刻，宿主可直接显示 |
| `BackoffRecoveredEvent` | Device / Connection | `Scope`, `TransportId`, `Attempts`, `DurationMs` | 退避恢复：一次成功通讯后退避归零；`Attempts` = 本次会话连续失败次数，`DurationMs` = 会话时长 |
| `PointWrittenEvent` | Write | `User`, `Outcome`, `Message`, `ElapsedMs`, `DidReadback`, `Readback`, `VerifyMismatch` | 写审计事件，**所有写操作（无论成败）都发一条**；已带回读与钳位标记，宿主可直接从事件闭环校验写入效果 |
| 错误事件族 | 按来源 | `Info`（`ErrorInfo`） | 基接口 `IErrorEvent` 一条订阅收齐一族；每个具体类型挂一个标记接口表示可恢复性（见下） |

错误事件共 **8 个类型**，挂的标记接口即「可恢复性」分类，宿主据此决定处置：

| 事件类型 | 标记接口 | 含义 |
|---|---|---|
| `TimeoutError` | `ITransientError` | 请求/应答超时，**可重试** |
| `ProtocolError` | `ITransientError` | 协议帧错误（CRC/帧长/事务号），**可重试** |
| `LinkError` | `ILinkError` | 链路故障（断开/被拒/IO 失败），**需要重连** |
| `DeviceExceptionError` | `IPermanentError` | 设备返回异常码（`ExceptionCode`），**永久**（重试无意义） |
| `DecodeError` | `IPermanentError` | 解码失败（长度/编码/BCD 非法），**永久** |
| `ConfigError` | `IPermanentError` | 配置非法（悬空引用/地址越界），**永久** |
| `PolicyError` | `IPolicyError` | 策略性放弃（预算耗尽/队列溢出），**策略** |
| `WriteIndeterminateError` | `IIndeterminateError` | 写结果不确定，**绝不自动重试**，须回读校验 |

事件都有 `Category`（`EventCategory`：Lifecycle/Connection/Device/Value/Write/Command/Alarm/Audit/Comm/Performance）与
`Level`（`EventLevel`：Trace/Debug/Info/Warn/Error）。

```csharp
// 单条订阅（Queued + DropOldest 默认）
using var sub1 = bus.Subscribe<AlarmRaisedEvent>(env =>
{
    Log($"报警 {env.Body.DeviceId}/{env.Body.PointId} [{env.Body.AlarmId}] {env.Body.Message}");
});

// 批量订阅高频类别（攒满 100 条或 500ms 回调一次）
using var sub2 = bus.SubscribeBatch<PointValueChangedEvent>(batch =>
{
    foreach (var env in batch) Draw(env.Body.DeviceId, env.Body.PointId, env.Body.Value);
}, maxBatchSize: 100, maxBatchDelay: TimeSpan.FromMilliseconds(500));

// 按基接口收齐一族错误事件
using var sub3 = bus.Subscribe<IErrorEvent>(env =>
{
    var e = env.Body;                       // ErrorInfo: Code / Category / Level / Source / MessageKey / Context
    if (env.Body is ILinkError) OnLinkDown(e.Code);
});

// 审计/报警类建议 Wait（不可丢）+ 同步落盘
using var audit = bus.Subscribe<PointWrittenEvent>(env => FlushAudit(env.Body),
    mode: DeliveryMode.Queued, overflow: OverflowPolicy.Wait, queueCapacity: 8192);

// 监控丢弃
var m = bus.Metrics;
if (m.TotalDropped > prev) Warn("事件总线有丢弃！");
```

---

## 8. 其它契约类型速查

| 类型 | 说明 |
|---|---|
| `ActingUser(string Name, string RoleId)` | 宿主认证后的操作者，**只用于审计记账**（事件带 `user.Name`）；**角色授权未实现**——`Write@permission` 当前不生效，是否允许写入由宿主把关；`RoleId` 对应配置 `Users/Role@id` |
| `ModbusArea` | `Coil` / `DiscreteInput` / `InputRegister` / `HoldingRegister`，对应配置里点位 `area` 取值：`Coil` ↔ `coil`、`DiscreteInput` ↔ `discrete`、`InputRegister` ↔ `input`、`HoldingRegister` ↔ `holding` |
| `RawReadResult(Success, Registers, RawBytes, Error, ElapsedMs)` | 原始读结果：`Registers` 原始寄存器（位区每元素 0/1）、`RawBytes` 大端拼接、`Error` 失败时错误信息 |
| `RawWriteResult(Success, Error, ElapsedMs)` | 原始写结果 |
| `ManualRetryOutcome` | `Succeeded`（退避归零）/ `Failed`（退避继续、档位加深）/ `NotSupported`（`manualRetry=false`，未发通讯） |
| `ManualRetryResult(Outcome, InterruptedBackoff, Error)` | 手动重试结果；`InterruptedBackoff=true` 表示调用前该目标确实在退避中 |
| `ErrorInfo(Code, Category, Level, Source, MessageKey, Context)` | 错误的稳定标识：`Code` 与 `MessageKey` 跨版本稳定；`Source`（Transport/Protocol/Device/Codec/Policy/Config）指明找谁排查；`Context` 携带结构化上下文（设备/链路/从站/功能码/尝试次数/耗时/收发帧…）。常用码族：`MODBUS.TIMEOUT`（读超时）、`MODBUS.LINK`（链路失败）、`MODBUS.EXCEPTION.XX`（设备异常码，如 `MODBUS.EXCEPTION.02`）、`SS.WRITE.REJECTED`（写管道拒绝）、`SS.BACKOFF.ALL_DEVICES`（退避升级 ReasonCode）、`SS.SCRIPT.TIMEOUT` / `SS.SCRIPT.FAILED`（点位脚本超时/失败） |
