# SuperSampler 配置 XML 说明（schemaVersion 3.0）

> 本文档是 SuperSampler 框架配置 XML 的**逐节点、逐属性字段参考**（字段字典）：
> 从根节点 `HostConfig` 到叶子节点（点位的 `Scale` / `Format` / `Alarm` / `Write` / `Script` 等），
> 覆盖每一个字段的名称、默认值、语义与加载期校验规则，以及未实现项的说明。
> 字段行为以框架实际加载行为为准；完整可运行样例见仓库 `samples/InjectionLineMonitor/line.xml`。
>
> 配套文档：
> - [`使用说明.md`](使用说明.md) —— 框架整体使用说明（架构、接入方式、引擎生命周期、读写与事件）
> - [`框架API说明.md`](框架API说明.md) —— 门面 API（IDeviceManager / IModbusDebugTool）签名与语义
> - [`测试与验收/`](测试与验收/) —— 测试计划与验收报告归档
>
> 使用建议：先按「附录 A 最小可用配置样例」搭出一个能跑的骨架，再对照第 1–15 节逐段补字段；
> 加载不过时对照第 16 节校验规则表查触发示例。

---

## 0. 通用约定

### 0.1 变量语法（三种，不可混用）

| 语法 | 含义 | 出现位置 |
|---|---|---|
| `${KEY}` | i18n 文本，加载时替换 | 任意配置字符串（name/prefix/suffix/message…） |
| `#{name}` | 命令运行时参数 | Commands/Step@value |
| `P('id')` / `T('key')` | 取点值 / 取 i18n | 表达式与脚本 |

### 0.2 继承（覆盖，不是叠加）

```
Point → Block → PointSet/Defaults → Device → DeviceTemplate → Global
```
就近优先；只取一份，绝不逐级累加（如 Retry 次数不会跨层相乘）。

### 0.3 唯一性

- `Transport/Device/PointSet` 的 id **全局唯一**（重复即报错）
- `Point@id` 在**点表内**唯一；点位主键 = `deviceId/pointId`（见 [`框架API说明.md`](框架API说明.md) 三层身份）
- `Commands` / `Users` 段未实现，无 id 唯一性校验

### 0.4 地址模型

- `addrFormat="protocol"`（默认）：address 即 0 基协议偏移
- `addrFormat="plc"`：address 写 4xxxx/3xxxx/1xxxx/0xxxx，加载时换算为区+0 基偏移，且校验与 area 一致
  例：`address="40001" addrFormat="plc" area="holding"` ⇔ `address="0" addrFormat="protocol" area="holding"`
  （4xxxx→holding、3xxxx→input、1xxxx→discrete、0xxxx（1..9999）→coil，偏移 = 手册号 − 区首号）

### 0.5 值处理管道（顺序固定）

```
寄存器 → 字序(swap) → 类型解释 → [原始数值]
   位点/位域(bit/bitRange)：直接从第一寄存器取位，**不经字序**（与 swap 无关）
      → 有 Point/Script：脚本(raw/rawValue → 工程值)
      → 无脚本：Scale(缩放+钳制)
      →【工程值】→ Format(decimals/Map/前后缀) →【显示文本】
```
写值、报警、历史只消费【工程值】；【显示文本】只给界面。
**脚本点位的工程值就是脚本返回值**（脚本产出即工程值，不再叠加 Scale——否则会二次换算）。

### 0.6 关键枚举

- **area**：`coil` | `discrete` | `input` | `holding`
- **dataType**：`bool` `int16` `uint16` `int32` `uint32` `int64` `uint64` `float32` `float64` `string` `bcd` `datetime` `raw`
- **swap**：`none`(ABCD) `byte`(BADC) `word`(CDAB，国内最常见) `word_byte`(DCBA)；四字母名可用作别名
- **access**：`read` `write` `readwrite`（可写性的唯一来源）

---

## 1. `HostConfig`（根）

| 属性 | 必填 | 说明 |
|---|---|---|
| schemaVersion | 是 | 结构版本，当前支持 `3.x`；缺失或不受支持**加载即报错** |
| unsupportedPolicy | 否 | 遇到未实现配置段时的策略：`warn`（默认，记入 Warnings）/ `error`（加载失败）/ `ignore` |

子元素（顺序即推荐书写顺序）：`Meta` `Global` `I18n` `Drivers` `Transports`
`DeviceTemplates` `PointTemplates` `Devices` `PointSets` `AlarmClasses` `Storage` `Commands`
`Ui` `Users` `Diagnostics`。

---

## 2. `Meta`

| 元素 | 说明 |
|---|---|
| ProjectName | 工程名，可 ${} |
| Comment / Author | 备注 / 维护人 |
| CreatedAt | 创建日期，ISO 8601（yyyy-MM-dd） |
| Revision | 整数，保存自增，用于比对回滚 |
| Tags/Tag | 分类标签，可多个 |

---

## 3. `Global`

**属性**：`language`（默认 zh_CN）、`fallbackLanguage`（en_US）、`nullText`（坏值占位，默认两个连字符）、`swap`（默认 word）、`timeZone`（**未实现**，见下）

> **生效项**：`nullText`——门面 `GetValue` 对非 Good 质量返回它；`swap`——作为 `Device@swap` 与未显式声明点位 swap 的最终兜底；
> `language` / `fallbackLanguage`——决定加载哪个 `I18n/Files/File`（主语言 → 兜底语言 → 全部，见 §4）。
> **未实现项**：`timeZone` 只进模型、没有任何消费者——框架无显示层，日期时间按**机器本地时区**解码，时区显示归宿主；写了进 `Warnings`（按 `unsupportedPolicy`）。

**子元素生效项**：

| 子元素 | 属性（默认值） | 说明 |
|---|---|---|
| Polling | defaultIntervalMs=1000, requestTimeoutMs=1000 | 轮询兜底值。**defaultIntervalMs 是未写 `intervalMs` 的点位/块的周期**，最小 50（= 调度节拍，小于它加载报错）。写 `rateMs`（旧名）会报错 |
| Retry | count=2, intervalMs=100 | 请求级重试，优先级：设备 `<Retry>` > 链路 `<Retry>` > 全局。**其余六项未实现，写了进 `Warnings`**：`backoff`/`escalateAfter`/`budgetMs` 进模型但不消费；`maxBackoffMs`/`onLinkError`/`onException` 不解析——退避与升级语义由 `Global/Reconnect@delays` 承担，重试间隔恒为 `intervalMs`、次数只按 `count` 计 |
| Reconnect | enabled=true, delays="300,1000,3000,10000,30000", manualRetry=true, offlineQuality=offline | 两层退避 + 手动重试 + 退避期间质量置位，见 §3.1。`initialDelayMs`/`maxDelayMs`/`backoff`/`keepAliveMs`/`keepAliveMode`/`flushRx`/`resetTxn`/`failInFlight`/`resetRetry` 这些属性名不被接受，写了直接报错（见 §16 规则表） |
| Scheduler | groupLimitRegisters=125, groupLimitBits=2000, ignoreGap=0 | 地址组上限（超过即自动切成多次请求，同节拍读完）；`ignoreGap` 是自动分组的地址空洞容差（0=严格连续）。`maxRegistersPerRead`/`maxBitsPerRead`/`mergeGap`（旧名）写了会报错 |
| Script | timeoutMs=50, onError=markBad | 点位未写 `Script@timeoutMs`/`@onError` 时的默认值——超时中断脚本执行、失败按 onError 置质量。`Global/Script` 上写 `language` 或脚本正文会报错；`maxMemoryMb` 不被接受，写了报错 |
| Quality | staleAfterMs=5000, onCommError=bad, onCommErrorValue=keepLast | onCommError 决定通信失败时的质量等级（bad/offline/uncertain）；onCommErrorValue 决定值策略（只认 keepLast / null）。取值穷举校验（写错报错），`staleAfterMs ≥ 0`。`staleAfterMs` 供宿主判陈旧（框架不做判定，配合门面 `GetValueAge`） |

### 3.1 `Global/Reconnect`（两层退避）

```xml
<Global>
  <Reconnect enabled="true" delays="300,1000,3000,10000,30000"
             manualRetry="true" offlineQuality="offline" />
</Global>
```

| 属性 | 默认 | 语义 |
|---|---|---|
| `enabled` | `true` | `false` = 关闭两层退避：每拍照常发请求、不做链路重建。keepalive 仍设置（它只是让系统能发现假死连接，不属于退避策略） |
| `delays` | `300,1000,3000,10000,30000` | 退避队列（毫秒，逗号分隔，**按序逐项等待**）。**用完后一直用最后一个值循环**；**任何一次成功通讯即归零**（下次失败从第一项重来）。失败**一次**即进队列，不设「连续 N 次」门槛 |
| `manualRetry` | `true` | `false` = 关闭手动重试门面：`IModbusDebugTool.RetryDeviceAsync` / `RetryLinkAsync` 返回 `NotSupported`（不发任何通讯，不打断退避） |
| `offlineQuality` | `offline` | 退避期间该目标所有点位的质量：`offline`（`PointQuality.Offline`）或 `bad`（`PointQuality.Bad`）。值按 `Global/Quality@onCommErrorValue` 策略（`keepLast` 保留旧值，否则置空） |

**两层（哪些失败进退避）**：

| 失败 | 层级 | 影响范围 |
|---|---|---|
| 读超时（连接在、设备不回） | **设备级** | 只有该从站进退避；同链路其它从站照常轮询；该设备在退避期间**不发任何请求** |
| IO 层失败（连接断开 / 写不出去 / socket 异常 / 帧错与 CRC 错） | **链路级** | 整条链路：**关闭连接** → 按队列等待 → 重连（惰性重连在下一次请求时完成）；该链路所有从站一起等 |
| 设备明确回**异常码**（协议异常） | **不计入** | 链路通、设备在回话，既不进退避也不重置队列 |
| 同一链路上**所有**从站都处于设备级退避 | **升级为链路级** | 防「半开假死」的第二道防线：设备级退避说明「不是单个从站的问题」，直接关连接重建 |

**防「假死」第一道防线**（系统级 TCP keepalive，TCP / RTU-over-TCP 通道自动设置）：
空闲 **10s** 开始探测、每 **3s** 一次、连续 **3** 次不通由系统关闭连接 → 之后的写报 IO 错 → 自动升级为链路级退避。
net46 实现：`SO_KEEPALIVE` + `IOControl(KeepAliveValues)` 设空闲/间隔，探测次数走 `TCP_KEEPCNT` 选项（编号 16，设 3 次；
Windows 10 1703+ 支持，旧系统设置失败静默降级为系统默认次数）。

**校验**：`delays` 不得为空、不得含 0 或负数、项数不得超过 **32**、每项必须是合法整数；
`offlineQuality` 只接受 `offline` / `bad`；以上任一不满足 → **拒绝加载**。
`initialDelayMs`/`maxDelayMs`/`backoff`/`keepAliveMs`/`keepAliveMode`/`flushRx`/`resetTxn`/`failInFlight`/`resetRetry`
这些属性名不被接受，写了直接报错，不做兼容解析。

---

## 4. `I18n`

| 字段 | 说明 |
|---|---|
| Files/File@lang,@path | 资源文件；`path` 相对**配置文件所在目录**（加载器按配置文件所在目录解析相对路径）。文件缺失不算致命，`${KEY}` 原样保留 |
| 资源格式 | 每行一个条目，`KEY = "值"`，# 开头为注释，空行忽略 |

资源文件内容示例（`KEY = "值"`，# 开头为注释）：

```
# 界面文本
PROJECT_NAME = "注塑产线监控"
PT_TEMP      = "料筒温度"
UNIT_C       = "℃"
```

**选中规则**：优先加载与 `Global@language` 匹配的 `File`；没有该语言的文件时，用 `Global@fallbackLanguage`
兜底；仍然没有才全部加载。配置里出现的 `${KEY}` 必须在默认语言资源里存在，否则加载报错（见 §16 规则表）。

---

## 5. `Drivers`

驱动由 `Transport@variant` 表达，无需声明段。写了 `<Drivers>` 段会进 `Warnings`（按 `unsupportedPolicy`）。

---

## 6. 间隔与模式

节奏直接写在**点位/块自己身上**（`Point@intervalMs` / `Block@intervalMs` 与 `mode`）：

| 字段 | 默认 | 说明 |
|---|---|---|
| `Point@intervalMs` / `Block@intervalMs` | `Global/Polling@defaultIntervalMs`（1000） | 轮询间隔，**整数毫秒**。必须 ≥ 50（= 调度节拍，也是最小间隔），小于 50 或 ≤ 0 加载即报错 |
| `Point@mode` / `Block@mode` | `auto` | `auto`（周期读）\| `onDemand`（只由门面手动触发）\| `once`（引擎 Start 后读一次） |

**调度语义**：

- **分桶**：同一设备内，相同 `intervalMs` 的点位组与块自动走同一节拍（一个桶一轮），不同间隔各走各的节拍；
  每设备仍只有一条轮询线程，实际周期误差 ≤ 一个节拍（50ms）。
- **`auto`**：周期读，未写 `intervalMs` 时取 `Global/Polling@defaultIntervalMs`。
- **`onDemand`**：完全不参与轮询。手动触发有两条路：单点 `TriggerReadAsync` / 单块 `TriggerBlockReadAsync`，
  或整台设备一次刷完 `IModbusDebugTool.TriggerOnDemandReadAsync(deviceId)`。
- **`once`**：Start 后读一次；**第一次没读到（通讯失败）会一直重试到成功一次为止**，成功后不再读。
  故 `once` 不得同时写 `intervalMs`——间隔无意义（写了报错）。
- **同一组必须在一个节拍内读完**：超地址组上限被切成的多次请求背靠背发在同一轮里，绝不跨节拍。

**自动分组**（默认行为，无需任何开关）：同一设备、同一从站（`unitId`）、同一数据区，地址空洞不超过
`Global/Scheduler@ignoreGap` 的点位自动归为一组，一次请求读回；组内跨度超地址组上限
（`groupLimitRegisters` / `groupLimitBits`）时自动切成多次请求，读回后仍是同一组。
`<Block>` 退化为「显式声明一段窗口 + 该段的间隔与模式」，不参与自动分组。

**非连续片段（`Point/Slices`）并入自动分组**：片段（含片段之间的空洞）合成一段
**随所在组一次请求读回**，读回后按片段拼值——绝不为了一个点单独发多次请求。轮询路径与手动触发路径
（`TriggerReadAsync` / `TriggerBlockReadAsync`）都支持。片段跨度超地址组上限在加载期报错（见 §16 规则表）。

**不接受的旧写法**：`<ScanGroups>` 段、`ScanGroup` 各属性、任何 `scanGroup` 属性、
`PointSet/Defaults@intervalMs`/`@mode`——写了任何一个都**直接报错**并提示改用 `intervalMs`（不做兼容解析）。

---

## 7. `Transports`（管「怎么连」）

> **TCP ⇄ RTU 只改这一个元素**：`variant` 决定承载——`tcp` 填 `host`/`port`；`rtu`（原生串口）改填 `portName` + 串口参数（下表"串口"列）；`rtuovertcp`（RTU 帧走 TCP，网关/调试）跟 TCP 一样填 `host`/`port`。`variant` 之外的属性（`enabled`/超时/`gapMs`/子元素 `Retry`）各种承载通用；**`<Device>`/`<PointSet>`/`<Point>` 与承载无关，一字不用改**（寄存器地址、类型、缩放、报警、写校验语义相同）。新手对照示例见 `docs/教程.md` §2.3.1。

| 属性 | 适用 | 默认 | 说明 |
|---|---|---|---|
| id / variant | 全部 | — | variant：tcp \| rtuovertcp \| rtu（已实现）\| udp \| ascii（**未实现**：枚举合法，但按 TCP/MBAP 通道收发，写了进 `Warnings`）。串口服务器多数是 rtuovertcp 不是 tcp |
| enabled | 全部 | true | `false` = 整条链路停用——不建主站、链路上的设备一起不轮询，`IModbusDebugTool.RetryLinkAsync` 返回 `NotSupported` 且不发任何通讯 |
| host / port | 网络 | — / 502 | |
| connectTimeoutMs / requestTimeoutMs | 全部 | 3000 / 1000 | `requestTimeoutMs` 在这里是**通道 socket 发送超时**；「每请求超时」由 `Device@requestTimeoutMs`（缺省取 `Global/Polling@requestTimeoutMs`）决定 |
| gapMs | 全部 | 0 | 帧间隔；串口为 3.5 字符时间。同链路恒为串行（并发不可配） |
| portName / baudRate / dataBits / parity / stopBits / handshake / dtr / rts | 串口 | — / 9600 / 8 / none / one / none / false / false | 现场坑：仪表常为 7/Even/1，按手册。**校验**：`handshake` 只认 `none`/`xonxoff`/`rtscts`/`dtrdsr`（写错报错）、`baudRate ≥ 1`、`dataBits` 5..8、`port` 1..65535、`connectTimeoutMs ≥ 1`、`gapMs`/`readTimeoutMs`/`writeTimeoutMs` ≥ 0 |
| readTimeoutMs / writeTimeoutMs | 串口 | 500 | |
| （子元素）Retry | 全部 | — | 覆盖全局重试（设备 `<Retry>` 优先于本项） |
| driver / maxConcurrent | — | — | 不支持：写了进 `Warnings`（驱动由 variant 表达、同链路恒为串行） |
| （子元素）Reconnect | — | — | 未实现：退避参数只走 `Global/Reconnect`；写了进 `Warnings` |

**串口多从站示例**：一条串口链路（一个 `portName`）可挂多台设备，设备之间用 `unitId` 区分：

```xml
<Transports>
  <Transport id="rs485_1" variant="rtu" portName="COM3" baudRate="9600"
             dataBits="8" parity="even" stopBits="one"/>
</Transports>
<Devices>
  <Device id="meterA" transport="rs485_1" unitId="1" pointSet="MeterA_Points"/>
  <Device id="meterB" transport="rs485_1" unitId="2" pointSet="MeterB_Points"/>
</Devices>
```

注意：同一链路不能有两个设备声明相同的 `(transport, unitId)`（会重复轮询同一从站，加载报错）。

---

## 8. `DeviceTemplates` / `PointTemplates`

模板与实例**同元素同名属性集**。实例属性覆盖模板属性；模板独有子元素（Scale/Format/Alarm/Retry…）会垫入实例。

- 设备模板 id 被 `Device@template` 引用
- 点位模板 id 被 `Point@template` 引用

---

## 9. `Devices`（管「连哪台」）

| 属性 | 默认 | 说明 |
|---|---|---|
| id / name / desc | — / — / — | id 必填；name 可 ${}；desc 是说明性字段，供宿主提示，写了不告警 |
| enabled | true | false 完全不轮询 |
| template | — | 设备模板 id |
| transport | — | **必填**，链路 id（协议与连接参数都在链路上，设备不写 driver/variant） |
| unitId | 1 | 从站地址，**整数**（别写 "01"） |
| pointSet | — | 点表 id（必填） |
| swap | 继承 | 设备级字序缺省（串口设备常为 none）。点位未显式声明 swap 时按 `Device@swap` 解码，再兜底 `Global@swap` |
| requestTimeoutMs | 继承 | |
| generateDiagnostics | true | **未实现**：写了 `="true"` 进 `Warnings`，不生成任何 `$device.*` 诊断点 |

> 节奏写在点位/块上（`Point@intervalMs` / `Block@intervalMs` 与 `mode`），设备不决定默认节奏；
> 整台设备一个节奏时把 `intervalMs` 写到各点/块，或调 `Global/Polling@defaultIntervalMs`。
> 设备上写 `scanGroup` 属性会被拒绝（加载报错）。

子元素：`Retry`（覆盖全局重试）、`Pause@maintenance`（检修暂停：设为 true 时该设备不轮询，配置保留）。
`Simulate@profile`（random/ramp/fixed）**未实现**：写了进 `Warnings`。

---

## 10. `PointSets`（核心）

`PointSet` 子元素四段平级：`Defaults`、`Blocks`、`Points`、`Calculated`。

### 10.1 `Block`（块读）

| 属性 | 说明 |
|---|---|
| id / area / start / count | 必填；count 不得超地址组上限（寄存器 `groupLimitRegisters`=125、位 `groupLimitBits`=2000，加载期报错） |
| intervalMs / mode | 本段的周期（毫秒，≥50，未写取全局默认）与模式（auto/onDemand/once）；**块内点位不得写**（写了报错） |
| unitId / swap / enabled | 缺省继承设备 |

**四条铁律**：块内点位写**绝对地址**且必须落在 `[start, start+count)`；
块内不得覆盖 `area/unitId/intervalMs/mode`（决定「怎么读、多久读」，写了报错），但 `swap` 可以（只是解码方式）；
块内点位地址必须**连续**（空洞不得超过 `Global/Scheduler@ignoreGap` 的忽略间隔数，默认 0 = 严格连续），
即整块能用**一次请求**覆盖——窗口尾部可以留白，点位之间不许留洞，否则加载即报错；
块是「显式声明的一次请求」，故 `count` 超上限是错误（散点自动分组才会自动切分）。

### 10.2 `Point` 属性

| 属性 | 默认 | 说明 |
|---|---|---|
| id / name | — / — | id 点表内唯一必填；name 可 ${} |
| desc | — | 说明（已解析，供宿主提示；框架不消费——说明性字段，写了**不**告警） |
| enabled | true | false 的点不参与轮询 |
| access | read | read \| write \| readwrite；**可写性唯一来源**；非法值报错 |
| area / address / addrFormat / length | 继承 / — / protocol / 按 dataType | length 仅 string/raw 需写；bcd/datetime 按类型参数推导（见 §10.3） |
| dataType | 继承 | 见 0.6；多字值必须显式声明整型或浮点 |
| swap / bit / bitRange | 继承 / — / — | bit 0 起；bitRange 形如 4-7，`from`/`to` 必须 0..15 且 `from ≤ to`，非法报错。**swap 兜底链**：`Point@swap > Block@swap（仅块显式声明时）> PointSet/Defaults@swap > Device@swap > Global@swap`；块未显式声明 swap 时，块内点位走 Defaults → Device → Global 正常兜底 |
| unit / range | — / — | 工程单位（供宿主显示，写了不告警）。`range` 量程（形如 `"0..300"`）只进模型，**界面刻度/百分比死区未实现**——写了进 `Warnings` |
| intervalMs / mode | 全局默认 / auto | **多久读一次**：间隔毫秒（≥50，未写取 `Global/Polling@defaultIntervalMs`）；模式 auto/onDemand/once。块内不得写；`once` 不得带 `intervalMs` |
| unitId / template | 继承 | **unitId 覆盖**：该点由自身 `unitId` 所属从站轮询（网关场景一条链路读多个从站）；未写时取 `PointSet/Defaults@unitId`，再取 `Device@unitId` |
| readonlyBy | — | 只读原因说明（已解析，仅界面提示；框架不消费——说明性字段，写了**不**告警） |

### 10.3 `Point` 子元素

**`<Scale>` 缩放与钳制**

- 属性：`factor`（乘数，默认 1）、`offset`（偏移，默认 0）、`rawLow/rawHigh/scaledLow/scaledHigh`（双点映射）。
  工程值 = 原始值 × factor + offset，写路径做逆运算。
- 子元素 `<Clamp mode="none|low|high|both" low="…" high="…">`：钳制在缩放后的范围内。
- `mode` 当前仅支持 `linear`（默认），写其它值进 `Warnings`。
- `bcd` 点位同样吃 Scale（BCD 解出的数值再缩放；写路径对称做逆缩放）。
- 示例：

```xml
<Scale factor="0.1" offset="0"/>
<!-- 双点映射 + 钳位：raw 0..10000 → -50..200 ℃，超出钳到边界 -->
<Scale rawLow="0" rawHigh="10000" scaledLow="-50" scaledHigh="200">
  <Clamp mode="both"/>
</Scale>
```

**`<Format>` 显示文本**

- 属性：`decimals=0`、`prefix`、`suffix`、`thousands=false`、`mapOn=engineering|raw`、`pattern`。
- 子元素 `<Map><Item key="...">文本</Item></Map>`：枚举映射；key 按缩放后工程值（`mapOn="raw"` 按原始值）。
- `mapOn` 只认 engineering/raw，`decimals` ∈ 0..15（写错报错）；`decimals` 对整型工程值同样生效。
- 示例：

```xml
<Format decimals="1" suffix=" ℃"/>
<Format mapOn="engineering">
  <Map><Item key="0">待机</Item><Item key="1">运行</Item></Map>
</Format>
```

**`<Bits>` 位映射展开**

- `Bit@index@name@text` 单一位；`Field@from@to@name@text` 连续位域（可带 `<Map>` 做枚举显示）。
- 加载期把每个条目展开成子点位 `整字点位id.位名`，与普通点一样可读、可订阅、可出现在报表/调试输出；
  整字点位保留（两种都能用）。`Bit` 子点位为 bool，`Field` 等价于 bitRange。
- 子点位不挂报警、不做任何业务判断——框架只负责取值。可写性继承整字点位（写入走位读改写只翻目标位/位域）。
- 校验：`Bit@index` / `Field@from`/`@to` 必须 0..15 且 `from ≤ to`；条目必须有名，位名不得含 `.`/`/`/空白；
  展开出的子点位 id 不得与现有点位冲突；`<Bits>` 与 `bit`/`bitRange`/`<Slices>` 同点并存报错；
  整字点位必须是单字整数类型。
- 示例：

```xml
<Point id="s.status" area="holding" address="1" dataType="uint16">
  <Bits>
    <Bit index="0" name="power" text="电源"/>
    <Field from="4" to="7" name="stage">
      <Map><Item key="0">待机</Item><Item key="1">运行</Item></Map>
    </Field>
  </Bits>
</Point>
```

**`<Slices>` 非连续片段**

- `Slice@address@length`：该点由多个片段拼成，运行期把片段（含中间空洞）合成一段读回来再按片段拼值，
  不为它单独发多次请求（与自动分组协同，同节拍的其它点位一起读）。
- 片段跨度（最小 `address` → 最大 `address+length`）超过地址组上限（寄存器 `Scheduler@groupLimitRegisters`=125、
  位区 `groupLimitBits`=2000）→ 加载报错；片段长度必须 > 0。
- Slices 点位的有效字长 = 各片段长度之和。
- 示例：

```xml
<Point id="p.energy" area="holding" dataType="uint32">
  <Slices>
    <Slice address="0" length="2"/>
    <Slice address="4" length="2"/>
  </Slices>
</Point>
```

**`<String>` 字符串**

- `encoding=ascii`、`padding=0x00`、`trimNull=true`、`left=true`。
- `padding`：设备的补齐字节（`0x00` 或 `0x20` 等，0x00–0xFF），解码时按它裁掉补齐；
  `left=true`（默认）= 靠左、补齐在右（`"ABC  "`），`false` = 靠右、补齐在左（`"  ABC"`）；
  `trimNull` 决定是否执行上述裁剪。
- 校验：`padding` ∈ 0x00..0xFF（支持 `0x20`/`32` 写法）、`left` 布尔；`perByte`/`byteAligned` 不支持，写了直接报错。

**`<Bcd>`**

- `digits=4`：每寄存器 4 位十进制，字长按 digits 推导（`digits=8` → 2 字，非 4 的倍数向上取整）；
  轮询按推导字长读满，写路径按 BCD 字位编码。支持 `<Scale>`。

**`<DateTime>`**

- `format=plc6|plc4|unixsec|unixms|custom`，字长按 format 推导：plc6=6 字（年/月/日/时/分/秒）、
  plc4=4 字（年/月/日/时）、unixsec=2 字（32 位秒）、unixms=4 字（64 位毫秒——毫秒时间戳必然超出 32 位，2 字装不下）。

**`<Script>` 点位脚本**

- `language=js`、`timeoutMs=继承 Global/Script`、`onError=继承 Global/Script`。
- 解码时把原始寄存器（`raw`，该点声明字长的寄存器数组）与原始数值（`rawValue`，按 dataType/swap 解出的
  缩放前值）交给脚本，**脚本返回值即工程值**（数值/字符串/布尔皆可，不再叠加 Scale）。
- 正文用 CDATA：可写表达式（最后一个表达式的值，如 `rawValue * 0.1`）或含 `return` 的方法体。
  可用绑定：`raw`/`rawValue`/`args`（恒空）/`P('id')`/`T('key')`/`device`/`point`/`timestamp`。
- 失败口径：返回 null/undefined → Bad；超时 → Bad + 超时错误事件；抛异常（含 ES5.1 不支持的语法）→ Bad + 脚本失败事件；
  非有限浮点降级 Uncertain。绝不阻塞采集线程：超时/异常只影响该点位本轮的值。
- 校验：`language` 只认 `js`（ES5.1，无箭头函数/let/const）、`timeoutMs` > 0、`onError` 只认 `markBad`、
  正文不得为空、未知属性（含 `maxMemoryMb`）报错。脚本内存不受限（Jint 2.x 无内存上限 API）。
- 示例：

```xml
<Script><![CDATA[ rawValue * 0.1 ]]></Script>
```

**`<Alarm>` 报警（可多条）**

- 属性：`id`、`type(high/highHigh/low/lowLow/digital)`、`limit`、`delayMs=0`、`deadband=0`、
  `priority`、`message`、`latch=false`、`ackRequired=false`。
- **digital 报警**：点位值为 true（或非零）时触发；写 `limit="1"` 表示 true/非零匹配、`limit="0"` 表示 false/零匹配，
  不写 limit 则非零即触发。
- `latch=true`：激活后即使值回到安全区也保持报警，直到确认；`ackRequired=true` 表示该报警需要确认。
  确认走门面 `IDeviceManager.AcknowledgeAlarmAsync(deviceId, pointId, alarmId, user)`。
- 校验：`type` 必须合法（未知值运行期永不触发）；限值型（high/highHigh/low/lowLow）必须给有限数值的 `limit`
  （digital 不需要）；`deadband ≥ 0`、`delayMs ≥ 0`；同一点位的报警状态键（`Alarm@id`，缺省 `pointId#type`）必须唯一；
  计算点不得挂 `<Alarm>`。同一点位多条限值报警的大小关系必须成立（`lowLow < low`、`low < high`、`high < highHigh`）。
- 示例：

```xml
<Alarm id="env.highTemp" type="high" limit="27" delayMs="2000" deadband="0.3"
       priority="Critical" latch="true" ackRequired="true" message="${AL_HIGH_TEMP}"/>
<Alarm id="fault" type="digital" latch="true" ackRequired="true"/>
```

**`<Write>` 写约束**

- 属性：`min`/`max`/`step`/`pulseMs`/`confirm=false`/`permission`/`verify=false`。能不能写看 `access`。
- 写管道真正消费：`min`/`max`（超范围拒绝且不发通讯）、`pulseMs`（脉冲写：写 true 后延时自动写回 false）、
  `verify`（写后回读：不一致返回 `Succeeded` 且 `WriteResult.VerifyMismatch=true`——设备应答成功但值不符，
  如自行钳位；界面应提示实际值且**不要自动重试**）。
- `confirm`（界面二次确认）/`step`（界面步进）/`permission`（角色授权，`Users` 段未实现）**未实现**，写了进 `Warnings`。
- 校验：`min ≤ max`、所有值必须有限、`pulseMs ≥ 0`；只读点（`access="read"`）不得声明 `<Write>`。
- 示例：

```xml
<Point id="setTemp" area="holding" address="2" dataType="int16" access="readwrite">
  <Scale factor="0.1"/>
  <Write min="0" max="300" verify="true"/>
</Point>
```

**`<History>` / `<Tags>`（未实现）**

- `History`（enabled, mode, deadband, intervalMs, retentionDays 等归档策略）与 `Tags/Tag`（自定义元数据）
  均未实现：写了进 `Warnings`（按 `unsupportedPolicy`）。

### 10.4 `Calculated`（计算点）

`Point` + `<Expression>`（如 `P('mold.barrelTemp') - P('mold.setTemp')`）
或 `Point` + `<Script>`（脚本型计算点；无寄存器可喂，用 `P('id')` 取其它点位值）。
不占地址、不轮询；GetValue 时按需求值并回写缓存。引用：同点表短 id，跨点表 `deviceId/pointId`。
**校验**：计算点必须恰有 `<Expression>` 或 `<Script>` 之一——两者都写或都不写在加载期报错。
顶层 `Parsers` 段不支持（写了进 `Warnings`），求值逻辑直接写在点位里。
脚本型计算点的失败口径与解码脚本完全一致（markBad + 错误事件 + 超时保护）。

子元素 `<DependsOn><PointRef>id</PointRef>…</DependsOn>`：显式声明依赖（建议写全；脚本正文里的 `P('id')`
同样参与成环判定，不写也不会报错）。
`PointRef` 与表达式/脚本里的 `P('id')` 一起参与成环判定——跨点表引用（`deviceId/pointId`）同样参与，
报错给出环路径；引用的点位不存在也报错（悬空引用）。

示例：

```xml
<Point id="mold.tempDev" dataType="float64" unit=" ℃">
  <Expression>P('mold.setTemp') - P('mold.temp')</Expression>
  <Format decimals="1" suffix=" ℃"/>
</Point>
```

---

## 11. `AlarmClasses`

`AlarmClass@id` 被 `Alarm@priority` 引用（必须指向存在的等级，否则加载报错）。
`name`/`color`/`sound`/`escalateAfterMs` 是宿主渲染用的展示属性，框架只校验 `@id` 引用、不消费这些值，
写了进 `Warnings`（按 `unsupportedPolicy`）。

---

## 12. `Storage`

| 元素 | 关键属性 |
|---|---|
| History | enabled, provider(sqlite/file/mysql/influxdb), connectionString, defaultMode(deadband), defaultIntervalMs, defaultDeadband, defaultDeadbandMode(absolute/percent), retentionDays, batchSize, flushMs, bufferOnOffline |
| Events | enabled, provider, connectionString, retentionDays |

**未实现段**：写了 `<Storage>` 进 `Warnings`（`unsupportedPolicy=error` 时拒绝加载）。字段定义保留供参考；
「记什么、级别、能否丢」的事件总线策略同样未实现，宿主可按自身方案采集事件。

---

## 13. `Commands`

| 字段 | 说明 |
|---|---|
| Command@id,@name,@permission,@confirm,@confirmText,@onFailure(rollback/abort/continue) | 命令声明 |
| Params/Param@name,@type,@min,@max,@default,@required,@label | 入参声明，步骤用 #{name} 引用 |
| Steps/Step@action(write/writeBlock/wait),@point,@value,@delayAfterMs,@ms | 按序执行（仅解析） |
| Verify@point,@expected,@timeoutMs | 执行后回读校验（仅解析） |

注意：命令**执行引擎未实现**——写了进 `Warnings`（`unsupportedPolicy=error` 时拒绝加载）；配置仅做解析与校验
（校验步骤引用的点位存在性，见 §16 规则表），没有执行入口。

---

## 14. `Ui` / `Users`

**未实现段**：`Ui` / `Users` 都未实现，写了进 `Warnings`（`unsupportedPolicy=error` 时拒绝加载），字段定义保留供参考。

- **Ui**：`@startScreen`；`Screen@id,@title,@background`；`Widget@type(label/value/gauge/indicator/bitlamp/trend/button/numeric/alarmlist/table),@x,@y,@w,@h,@point,@bit,@command,@text,@fontSize,@min,@max,@decimals,@unit,@onColor,@offColor,@windowSec,@refreshMs,@permission`；Trend/Table 用 `Points/PointRef`。控件只绑 Point@id。
- **Users**：`@auditWrites`；`Role@id,@name,@canWrite,@canCommand,@canEditConfig` + `AllowedPoints/PointRef` 白名单（不写=全部可写）；`Account@user,@passwordHash,@role,@enabled`（只存哈希；离职置 false 不删除）。

---

## 15. `Diagnostics`

| 元素/属性 | 说明 |
|---|---|
| @allowRawAccess | **默认 false**。调试工具 RawRead/RawWrite 的总开关（绕过写保护，生产必须关） |
| Trace | **未实现**：写了进 `Warnings`；字段定义（enabled/categories/ringSize/echoToLogger）保留供参考 |
| Events | **未实现**：写了进 `Warnings`；字段定义（enabled/defaultMode/queueSize/onQueueFull/reportDropped + Category@name,@level,@batch,@overflow）保留供参考 |
| Recovery | **未实现**：写了进 `Warnings`；无生效字段 |

注意：`logLevel/logFile/logFrames` 不被接受，写了报错——日志落盘由宿主 ILogger 决定，框架不写日志文件。

---

## 16. 校验规则清单（加载期拦截）

### 16.0 两阶段加载

```
① 阶段一 解析 + 全量校验   解析 XML，把**全部**矛盾收集成一个错误列表（不是遇到第一个就停）
② 阶段二 零错误才交付      有错 → 一次性抛 ConfigValidationException（Errors 里是全部错误行）
                          无错 → 置 SamplerConfiguration.IsValidated 后返回
③ 实例化                   PointRegistry / Scheduler / SamplerEngine 构造时强制检查 IsValidated，
                          未通过校验的配置**不可能**被用来构造运行时对象（抛 InvalidOperationException）
```

- 每条错误都带定位：哪个点表/设备/链路、哪个点位、哪个属性、为什么。
- 解析期的非法数值/布尔（如 `bit="x"`、`unitId="one"`）**不中断收集**，转成带定位的错误后继续跑后面的规则。
- 规则分两级：`错误`（拒绝加载）；`警告`（记入 `SamplerConfiguration.Warnings`，`unsupportedPolicy=error` 时升级为拒绝）。

### 16.1 规则表

| 级别 | 规则（一句话） | 触发示例 |
|---|---|---|
| 错误 | `schemaVersion` 必填且为 `3.x` | `<HostConfig>`（缺属性） |
| 错误 | XML 必须合法且有根元素 | 文件被截断 → "配置文件不是合法 XML" |
| 错误 | 引用完整：`template` / `transport` / `pointSet` / `Alarm@priority` / 命令步骤点位可解析 | `<Device pointSet="不存在"/>` |
| 错误 | `${KEY}` 必须在默认语言 i18n 资源里存在 | `name="${NO_SUCH_KEY}"` |
| 错误 | id 唯一：Transport/Device/PointSet 全局唯一；`Point@id` 点表内唯一 | 同一 `PointSet` 里两个 `id="p"` |
| 错误 | 块窗口包含点位：`address + 有效长度 ∈ [start, start+count)` | 块 `count=4`，点位 `address=9` |
| 错误 | 块内点位不得覆盖 `area` / `unitId` | `<Block area="input">` 内 `<Point area="holding">` |
| 错误 | 显式 `length` 与 `dataType` 位宽一致；bcd/datetime 按 `Bcd@digits` / `DateTime@format` 推导 | `float32` 配 `length="3"` |
| 错误 | `addrFormat="plc"` 时地址前缀与 `area` 一致 | `40001` 配 `area="input"` |
| 错误 | `access="read"` 的点不得声明 `<Write>` | `access="read"` + `<Write/>` |
| 错误 | `Block@count` 不超地址组上限（寄存器 `groupLimitRegisters`=125 / 位 `groupLimitBits`=2000） | `count="126"`（holding 块） |
| 错误 | 同一 `(transport, unitId)` 不得被两个启用的 Device 声明 | 两个 `Device` 都写 `transport="eth_main" unitId="1"` |
| 错误 | `Alarm@priority` 指向存在的 `AlarmClass` | `priority="CRITICAL"` 但没定义该等级 |
| 错误 | 同一点位多条报警的限值大小关系必须成立：`lowLow < low`、`low < high`、`high < highHigh` | `<Alarm type="high" limit="90"/><Alarm type="highHigh" limit="80"/>` |
| 错误 | 计算点 `P('id')` 引用不得成环（含间接环与自引用），报错给环路径 | `c.a → c.b → c.a` |
| 错误 | 块内点位地址必须连续（空洞 ≤ `Scheduler@ignoreGap`），即能用一次请求覆盖 | 块内点位 `address=0` 与 `address=2`（`ignoreGap=0`） |
| 错误 | 同一寄存器同一位不得被两个点位重复声明（同址不同位、整字点位与位点位并存都允许） | 同址两点都写 `bit="0"`；`bitRange="4-7"` 与 `bitRange="6-9"` |
| 错误 | 点位地址 + 有效长度不得越出区容量 65536（`<Slices>` 每片段同样受检）；地址不得为负 | `address="65535" dataType="uint32"` |
| 错误 | 间隔 ≥ 50 且 > 0；请求超时（`Global/Polling@requestTimeoutMs` / `Transport@requestTimeoutMs` / `Device@requestTimeoutMs`）≥ 1ms；`groupLimitRegisters` / `groupLimitBits` > 0 | `intervalMs="49"`、`requestTimeoutMs="0"` |
| 错误 | 块内点位不得写 `intervalMs` / `mode`（块统一决定节奏；点位属性与模板带入的都算） | `<Block start="0" count="4">` 内 `<Point intervalMs="500"/>` |
| 错误 | `mode="once"` 不得同时写 `intervalMs` | `<Point mode="once" intervalMs="500"/>` |
| 错误 | 枚举非法值报错：`area` / `dataType` / `swap` / `access` / `variant` / `parity` / `stopBits` / `mode` / `mapOn` | `dataType="bogus"`、`mode="bogus"` |
| 错误 | 非法数值/布尔报错（带「点位/实体 + 属性」定位），不是裸 `FormatException` | `bit="x"`、`limit="true"`、`unitId="one"` |
| 警告 | 未实现段/字段按 `HostConfig@unsupportedPolicy` 处理：`warn`（默认，记入 `Warnings`）/ `error`（拒绝加载）/ `ignore` | 写了 `Storage` / `Ui` / `Commands`（全表见 §16.2） |
| 错误 | 旧写法一律报错（不兼容、不迁移）：`ScanGroups` 段与任何 `scanGroup` 属性、`Global/Polling@rateMs`、`Global/Scheduler@mergeGap`/`maxRegistersPerRead`/`maxBitsPerRead`、`PointSet/Defaults@intervalMs`/`@mode` | `<ScanGroups>`；`Point scanGroup="fast"` |
| 错误 | 退避队列非法：`Global/Reconnect@delays` 为空 / 含 0 或负数 / 项数 > 32 / 含非整数；`offlineQuality` 不是 `offline`/`bad`；`initialDelayMs` 等属性名写了报错 | `<Reconnect delays="1000,0"/>`；`<Reconnect keepAliveMs="5000"/>` |
| 错误 | 位映射展开（`<Bits>`）非法，逐条带「点表/点位 + 条目」定位：`Bit@index` 越界、`Field@from`/`@to` 越界或 `from > to`、条目缺 `name` 或位名含 `.`/`/`/空白、展开子点位 id 与现有点位冲突、`<Bits>` 与 `bit`/`bitRange`/`<Slices>` 同点并存、整字点位不是单字整数类型 | `<Bit index="16" name="x"/>`；`<Field from="7" to="4" name="a"/>` |
| 错误 | `<Slices>` 片段跨度（最小 `address` → 最大 `address+length`）超过地址组上限（单点不可切分）；片段长度 ≤ 0 报错 | `<Slice address="0" length="1"/><Slice address="200" length="1"/>`（跨度 201 > 125） |
| 错误 | 字符串口径非法：`String@padding` 不是 0x00..0xFF 的数值（支持 `0x20`/`32` 写法）、`String@left` 不是布尔；`perByte`/`byteAligned` 不支持（写了即报错） | `<String padding="0x100"/>`；`<String perByte="true"/>` |
| 错误 | `bitRange` 范围非法：`from`/`to` 缺失、非整数、越出 0..15 或 `from > to` | `bitRange="7-4"`、`bitRange="0-16"` |
| 错误 | 计算点成环判定覆盖 `<DependsOn><PointRef>` 与表达式/脚本里的 `P('id')`（含跨点表 `deviceId/pointId`），报错给环路径；`PointRef` 悬空引用同样报错 | `<PointRef>c.b</PointRef>` 且 `c.b` 依赖 `c.a` |
| 错误 | 脚本字段非法：`language` 不是 `js`（仅支持 js/ES5.1）、`timeoutMs` ≤ 0 或非整数、`onError` 不是 `markBad`、正文为空、未知属性（含 `maxMemoryMb`）、`Global/Script` 写 `language` 或脚本正文 | `<Script language="javascript">…</Script>`；`<Script timeoutMs="0">…</Script>` |
| 错误 | 计算点必须恰有 `<Expression>` 或 `<Script>` 之一（语义冲突 / 没有求值依据） | 两者都写；或 `<Point id="c"/>`（在 `<Calculated>` 内） |
| 错误 | 词汇校验：未知元素名报错（发现后不深入其子树，避免级联刷屏）；未知属性按 `unsupportedPolicy`（默认 warn）；`<Comment>` 全文档通用注释 | `<Ponits><Point …/></Ponits>`；`<Point intervallMs="500"/>` |
| 错误 | 数据区与宽度：只读区（`input`/`discrete`）不得声明可写（含经 `Defaults@access` 继承）；非寄存器区（`coil`/`discrete`）不得声明多字点位（32/64 位、string、bcd、datetime、`length>1`、`<Slices>`） | `<Point area="input" access="write"/>`；`<Point area="coil" dataType="string" length="2"/>` |
| 错误 | 报警配置：计算点不得挂 `<Alarm>`；`type` 必须合法（high/highHigh/low/lowLow/digital）；限值型必须有有限数值的 `limit`；`deadband ≥ 0`、`delayMs ≥ 0`；同一点位报警状态键（`Alarm@id` 或 `pointId#type`）唯一 | `<Alarm type="foo"/>`；`<Alarm type="high"/>`（缺 limit） |
| 错误 | 枚举穷举：`unsupportedPolicy`、`swap`（Global/Device/Block/Defaults）、`Defaults@area|dataType|access`、`Block@area`、`Format@mapOn`、`DateTime@format`、`Clamp@mode`（none/low/high/both）、`onCommError`/`onCommErrorValue`、`handshake` | `swap="bogus"`；`<Format mapOn="eng"/>`；`<DateTime format="custom"/>` |
| 错误 | 数值边界：`bit` ∈ 0..15、`length` ≥ 1、`unitId` ∈ 0..255、`decimals` ∈ 0..15、`digits` ∈ 1..64、`Write@pulseMs` ≥ 0、`Block@start` ≥ 0、`staleAfterMs`/`Retry@count|@intervalMs`/`ignoreGap`/串口超时 ≥ 0、`port` ∈ 1..65535、`baudRate` ≥ 1、`dataBits` ∈ 5..8、`connectTimeoutMs` ≥ 1、`Write@min ≤ @max`、Scale/Clamp/Write/`range` 数值必须有限 | `bit="16"`；`unitId="300"`；`<Write min="10" max="1"/>`；`<Scale factor="NaN"/>` |

> 规则分「错误」（拒绝加载）与「警告」（记入 `Warnings`，`unsupportedPolicy=error` 时升级为拒绝加载）两级。
> 每条错误都带定位（点表/设备/链路 + 点位 + 属性 + 原因）；解析期的非法数值/布尔**不中断收集**，
> 转成带定位的错误后继续跑后面的规则——一次报全所有矛盾，不会遇到第一个错误就停。

### 16.2 未实现段/字段全表（写了进 `Warnings`）

按 `HostConfig@unsupportedPolicy` 处置：`warn`（默认，记入 `Warnings`）/ `error`（加载失败）/ `ignore`（静默）。

| 位置 | 说明 |
|---|---|
| `Drivers` 段 | 驱动由 `Transport@variant` 表达，无需声明 |
| `Storage` / `Ui` / `Users` / `Commands` 段 | 对应功能未实现（见 §12 / §13 / §14） |
| `Diagnostics/Trace` / `Events` / `Recovery` | 框架内部追踪 / 事件总线策略 / 离线恢复（见 §15） |
| `Parsers` 段 | 脚本/自定义解码器，归宿主 |
| `Device/Simulate@profile` | 仿真模式 |
| `Device@generateDiagnostics` | 自动诊断点（不生成任何 `$device.*` 点位） |
| `Point/History` | 历史归档 |
| `Point/Tags` | 自定义元数据 |
| `Point@range` | 界面刻度/百分比死区（只进模型） |
| `Point/Write@confirm` / `@step` / `@permission` | 界面二次确认 / 界面步进 / 角色授权 |
| `Scale@mode`（非 `linear`） | 缩放模式仅支持 linear |
| `AlarmClass@name` / `@color` / `@sound` / `@escalateAfterMs` | 等级展示属性由宿主消费 |
| `Global@timeZone` | 时区换算（日期时间按机器本地时区解） |
| `Global/Retry@backoff` / `@escalateAfter` / `@budgetMs` / `@maxBackoffMs` / `@onLinkError` / `@onException` | 重试只按 count/intervalMs 生效 |
| `Transport@variant` = `udp` / `ascii` | 未实现，按 TCP/MBAP 通道收发 |
| `Transport@driver` / `@maxConcurrent` | 不支持 |
| `Transport/Reconnect` 段 | 退避只走 `Global/Reconnect` |

说明性字段（`Meta/*`、`Point@desc/@unit/@readonlyBy`、`Device@name/@desc`）**不属于**未实现项：
它们只进模型供宿主读取，语义就是「说明文字/单位」，写了不告警。

---

## 附录 A. 最小可用配置样例（schemaVersion 3.0）

> 一台 Modbus TCP 设备 + 6 个典型点位 + 1 个计算点，字段全部是本文档第 1–15 节的当前写法，
> 可直接作为 v3 配置骨架。更完整的真实样例见仓库 `samples/InjectionLineMonitor/line.xml`。
> 若不需要 `${KEY}` 文本替换，可删除 `I18n` 段。

```xml
<?xml version="1.0" encoding="utf-8"?>
<HostConfig schemaVersion="3.0" unsupportedPolicy="warn">

  <Meta>
    <ProjectName>最小可用样例</ProjectName>
    <Comment>一台 Modbus TCP 设备 + 6 个典型点位</Comment>
    <CreatedAt>2026-09-18</CreatedAt>
  </Meta>

  <Global language="zh_CN" fallbackLanguage="en_US" swap="word">
    <Polling defaultIntervalMs="1000" requestTimeoutMs="1000"/>
  </Global>

  <I18n>
    <Files>
      <File lang="zh_CN" path="简单样例.i18n"/>
    </Files>
  </I18n>

  <!-- 一个 Modbus TCP 通道 -->
  <Transports>
    <Transport id="eth1" variant="tcp" host="192.168.0.1" port="502" requestTimeoutMs="1000"/>
  </Transports>

  <!-- 一台设备（一条链路上的一个从站） -->
  <Devices>
    <Device id="Mold01" name="1号注塑机" transport="eth1" unitId="1" pointSet="Mold01_Points"/>
  </Devices>

  <PointSets>
    <PointSet id="Mold01_Points">
      <Defaults area="input" dataType="uint16" swap="none"/>

      <Points>
        <!-- ① 单寄存器 + 缩放：0.1 分辨率 → 12.3 ℃；high 报警（锁存 + 需确认） -->
        <Point id="mold.temp" name="料筒温度" address="0" dataType="int16" intervalMs="500">
          <Scale factor="0.1"/>
          <Format decimals="1" suffix=" ℃"/>
          <Alarm id="mold.overTemp" type="high" limit="80" priority="Critical"
                 latch="true" ackRequired="true"/>
        </Point>

        <!-- ② 整字枚举：0→待机 1→运行 2→故障 -->
        <Point id="mold.status" name="运行状态" address="1" intervalMs="1000">
          <Format>
            <Map>
              <Item key="0">待机</Item>
              <Item key="1">运行</Item>
              <Item key="2">故障</Item>
            </Map>
          </Format>
        </Point>

        <!-- ③ 取某一位：寄存器 1 的第 0 位 -->
        <Point id="mold.running" name="运行标志" address="1" dataType="bool" bit="0" intervalMs="1000"/>

        <!-- ④ 多寄存器浮点：占 2 个字 -->
        <Point id="mold.pressure" name="射胶压力" address="10" dataType="float32" swap="word" intervalMs="1000">
          <Format decimals="2" suffix=" MPa"/>
        </Point>

        <!-- ⑤ 可写设定值：0~300 范围校验 + 写后回读校验 -->
        <Point id="mold.setTemp" name="料筒设定" address="20" dataType="int16"
               access="readwrite" intervalMs="1000">
          <Scale factor="0.1"/>
          <Format decimals="1" suffix=" ℃"/>
          <Write min="0" max="300" verify="true"/>
        </Point>

        <!-- ⑥ 线圈读写：启动/停止 -->
        <Point id="mold.start" name="启动" area="coil" address="0" dataType="bool"
               access="readwrite" intervalMs="1000"/>
      </Points>

      <Calculated>
        <!-- 计算点：读取时按表达式实时求值，不占地址 -->
        <Point id="mold.tempDev" name="温度偏差" dataType="float64" unit=" ℃">
          <Expression>P('mold.setTemp') - P('mold.temp')</Expression>
          <Format decimals="1" suffix=" ℃"/>
        </Point>
      </Calculated>
    </PointSet>
  </PointSets>

</HostConfig>
```

**样例会演示的 7 种场景**：单寄存器 + 缩放（温度）、限值报警（latch/ackRequired）、整字枚举（状态字）、
取某一位（运行标志）、多寄存器浮点（压力）、可写设定值（范围校验 + verify 回读）、计算点（`P()` 引用）。

---

