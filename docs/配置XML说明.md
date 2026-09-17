# SuperSampler 配置 XML 说明（schemaVersion 3.0）

> 本文档是 SuperSampler 框架配置 XML 的**逐节点、逐属性字段参考**（字段字典）：
> 从根节点 `HostConfig` 到叶子节点（点位的 `Scale` / `Format` / `Alarm` / `Write` / `Script` 等），
> 覆盖每一个字段的名称、默认值、语义与校验规则（CGV-1 ~ CGV-38 全表），以及 v2 → v3 迁移对照。
> 字段语义以源码加载器为准（`src/SuperSampler.Core/Config/SamplerConfigLoader*.cs`），
> 权威样例见仓库 `Config/上位机配置.v3.xml`。
>
> 配套文档：
> - [`使用说明.md`](使用说明.md) —— 框架整体使用说明（架构、接入方式、引擎生命周期、读写与事件）
> - [`框架API说明.md`](框架API说明.md) —— 门面 API（IDeviceManager / IModbusDebugTool）签名与语义
> - [`测试与验收/`](测试与验收/) —— 测试计划与验收报告归档
>
> 使用建议：先按「附录 A 最小可用配置样例」搭出一个能跑的骨架，再对照第 1–15 节逐段补字段；
> 加载不过时对照第 16 节校验规则表查触发示例；从旧版升级时对照「附录 B 历史与迁移」。

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

- `Transport/Device/PointSet/Command/Role` 的 id **全局唯一**
- `Point@id` 在**点表内**唯一；点位主键 = `deviceId/pointId`（见 [`框架API说明.md`](框架API说明.md) 三层身份）

### 0.4 地址模型

- `addrFormat="protocol"`（默认）：address 即 0 基协议偏移
- `addrFormat="plc"`：address 写 4xxxx/3xxxx/1xxxx/0xxxx，加载时换算为区+0 基偏移，且校验与 area 一致

### 0.5 值处理管道（顺序固定）

```
寄存器 → 字序(swap) → 位提取(bit/bitRange) → 类型解释 → [原始数值]
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

**属性**：`language`（默认 zh_CN）、`fallbackLanguage`（en_US）、`timeZone`、`nullText`（坏值占位，默认两个连字符）、`swap`（默认 word）

> **已接线**：`nullText` 接入 `GlobalOptions.NullText`（门面 `GetValue` 对非 Good 质量返回它）；
> `swap` 接入 `GlobalOptions.DefaultSwap`，并作为 `Device@swap` 与非显式点位 swap 的最终兜底。
> **已接线**：`language` / `fallbackLanguage` 决定加载哪个 `I18n/Files/File`（主语言 → 兜底语言 → 全部）。
> **写了显式告警**：`timeZone` 只进模型、没有任何消费者——
> 框架无显示层，日期时间按**机器本地时区**解码，时区显示归宿主；写了进 `Warnings`，不再静默忽略。

| 子元素 | 属性 | 说明 |
|---|---|---|
| Polling | defaultIntervalMs=1000, requestTimeoutMs=1000 | 轮询兜底值。**defaultIntervalMs 是未写 `intervalMs` 的点位/块的周期**，最小 50（= 调度节拍，小于它加载报错）。旧名 `rateMs` 已删除 |
| Retry | count=2, intervalMs=100, backoff, maxBackoffMs, onLinkError, onException, escalateAfter, budgetMs | **已接线**：只有 count/intervalMs 生效，优先级 设备`<Retry>` > 链路`<Retry>` > 全局。**其余六项写了显式告警（未实现）**：`backoff`/`escalateAfter`/`budgetMs` 进模型但不消费；`maxBackoffMs`/`onLinkError`/`onException` 连解析都没有——退避与升级语义由 `Global/Reconnect@delays` 承担，重试间隔恒为 `intervalMs`、重试次数只按 `count` 计次 |
| Reconnect | enabled=true, delays="300,1000,3000,10000,30000", manualRetry=true, offlineQuality=offline | **已接线**：两层退避 + 手动重试 + 退避期间质量置位，见 §3.1；早期属性名（initialDelayMs/maxDelayMs/backoff/keepAliveMs/keepAliveMode/flushRx/resetTxn/failInFlight/resetRetry）**一律报错**（CGV-26） |
| Scheduler | groupLimitRegisters=125, groupLimitBits=2000, ignoreGap=0 | **已接线**：地址组上限（超过即自动切成多次请求，同节拍读完）；`ignoreGap` 是自动分组的地址空洞容差（0=严格连续）。旧名 `maxRegistersPerRead`/`maxBitsPerRead`/`mergeGap` 已删除 |
| Script | timeoutMs=50, onError=markBad | **已接线**：点位未写 `Script@timeoutMs`/`@onError` 时的默认值——超时中断脚本执行、失败按 onError 置质量。`maxMemoryMb` 已删除（Jint 2.x 无内存上限 API），写了报错（CGV-32） |
| Quality | staleAfterMs=5000, onCommError=bad, onCommErrorValue=keepLast | **已接线**：onCommError 决定通信失败时的质量等级（bad/offline/uncertain）；onCommErrorValue 决定值策略（**只认 keepLast / null**）。`onCommError`/`onCommErrorValue` 取值穷举校验（CGV-37），`staleAfterMs ≥ 0`（CGV-38）——此前拼错会静默落回 bad / 置空。`staleAfterMs` 供宿主判陈旧（框架不做判定，配合门面 `GetValueAge`） |

### 3.1 `Global/Reconnect`（两层退避）

```xml
<Global>
  <Reconnect enabled="true" delays="300,1000,3000,10000,30000"
             manualRetry="true" offlineQuality="offline" />
</Global>
```

| 属性 | 默认 | 语义 |
|---|---|---|
| `enabled` | `true` | `false` = 关闭两层退避（保持旧行为：每拍都照常发请求、不做链路重建）。keepalive 仍设置（它只是让系统能发现假死连接，不属于退避策略） |
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
空闲 **10s** 开始探测、每 **3s** 一次、**3** 次不通由系统关闭连接 → 之后的写报 IO 错 → 自动升级为链路级退避。
net46 实现：`SO_KEEPALIVE` + `IOControl(KeepAliveValues)` 设空闲/间隔，`TCP_KEEPCNT=16` 设探测次数
（Windows 10 1703+ 支持，旧系统设置失败静默降级为系统默认次数）。

**校验（CGV-26）**：`delays` 不得为空、不得含 0 或负数、项数不得超过 **32**、每项必须是合法整数；
`offlineQuality` 只接受 `offline` / `bad`；以上任一不满足 → **拒绝加载**。
早期属性名（`initialDelayMs`/`maxDelayMs`/`backoff`/`keepAliveMs`/`keepAliveMode`/`flushRx`/`resetTxn`/`failInFlight`/`resetRetry`）
已删除：写了直接报错，不做兼容解析（同 CGV-25 口径）。

---

## 4. `I18n`

| 字段 | 说明 |
|---|---|
| Files/File@lang,@path | 资源文件；路径相对**程序工作目录**（默认语言见 `Global@language`） |
| 资源格式 | `KEY = "值"`，# 开头为注释 |

---

## 5. `Drivers`（**已删除**）

驱动由 `Transport@variant` 表达，无需声明段。见「附录 B-2 已删除字段」。

---

## 6. 间隔与模式（原 `ScanGroups`，**已删除**）

命名的扫描组已整段删除。节奏不再靠「组名引用」，而是直接写在**点位/块自己身上**：

| 字段 | 默认 | 说明 |
|---|---|---|
| `Point@intervalMs` / `Block@intervalMs` | `Global/Polling@defaultIntervalMs`（1000） | 轮询间隔，**整数毫秒**。必须 ≥ 50（= 调度节拍，也是最小间隔），小于 50 或 ≤ 0 加载即报错（CGV-19） |
| `Point@mode` / `Block@mode` | `auto` | `auto`（周期读）\| `onDemand`（只由门面手动触发）\| `once`（引擎 Start 后读一次） |

**调度语义**：

- **分桶**：同一设备内，相同 `intervalMs` 的点位组与块自动走同一节拍（一个桶一轮），不同间隔各走各的节拍；
  每设备仍只有一条轮询线程，实际周期误差 ≤ 一个节拍（50ms）。
- **`auto`**：周期读，未写 `intervalMs` 时取 `Global/Polling@defaultIntervalMs`。
- **`onDemand`**：完全不参与轮询。手动触发有两条路：单点 `TriggerReadAsync` / 单块 `TriggerBlockReadAsync`，
  或整台设备一次刷完 `IModbusDebugTool.TriggerOnDemandReadAsync(deviceId)`。
- **`once`**：Start 后读一次；**第一次没读到（通讯失败）会一直重试到成功一次为止**，成功后不再读。
  故 `once` 不得同时写 `intervalMs`（CGV-21）——间隔无意义。
- **同一组必须在一个节拍内读完**：超地址组上限被切成的多次请求背靠背发在同一轮里，绝不跨节拍。

**自动分组**（默认行为，无需任何开关）：同一设备、同一从站（`unitId`）、同一数据区，地址空洞不超过
`Global/Scheduler@ignoreGap` 的点位自动归为一组，一次请求读回；组内跨度超地址组上限
（`groupLimitRegisters` / `groupLimitBits`）时自动切成多次请求，读回后仍是同一组。
`<Block>` 退化为「显式声明一段窗口 + 该段的间隔与模式」，不参与自动分组。

**非连续片段（`Point/Slices`）并入自动分组**：片段（含片段之间的空洞）合成一段
**随所在组一次请求读回**，读回后按片段拼值——绝不为了一个点单独发多次请求。轮询路径与手动触发路径
（`TriggerReadAsync` / `TriggerBlockReadAsync`）都支持；此前「Slices 点位只在手动触发读时支持、轮询跳过」
的旧限制**已删除**。片段跨度超地址组上限在加载期报错（CGV-28）。

**已删除**：`<ScanGroups>` 段、`ScanGroup@id/@mode/@rateMs/@jitterMs`、`ScanGroup/Trigger`、
`Device@scanGroup`、`Block@scanGroup`、`Point@scanGroup`、`PointSet/Defaults@scanGroup`。
写了任何一个都**直接报错**并提示改用 `intervalMs`（CGV-25），不做兼容解析。

---

## 7. `Transports`（管「怎么连」）

| 属性 | 适用 | 默认 | 说明 |
|---|---|---|---|
| id / variant | 全部 | — | variant：tcp \| rtuovertcp \| udp \| rtu \| ascii；串口服务器多数是 rtuovertcp 不是 tcp |
| enabled | 全部 | true | **已接线**：`false` = 整条链路停用——不建主站、链路上的设备一起不轮询，`IModbusDebugTool.RetryLinkAsync` 返回 `NotSupported` 且不发任何通讯 |
| host / port | 网络 | — / 502 | |
| connectTimeoutMs / requestTimeoutMs | 全部 | 3000 / 1000 | `requestTimeoutMs` 在这里是**通道 socket 发送超时**；「每请求超时」由 `Device@requestTimeoutMs`（缺省取 `Global/Polling@requestTimeoutMs`）决定 |
| gapMs | 全部 | 0 | 帧间隔；串口为 3.5 字符时间。同链路恒为串行（并发不可配） |
| portName / baudRate / dataBits / parity / stopBits / handshake / dtr / rts | 串口 | — / 9600 / 8 / none / one / none / false / false | 现场坑：仪表常为 7/Even/1，按手册。**校验（CGV-37/38）**：`handshake` 只认 `none`/`xonxoff`/`rtscts`/`dtrdsr`（拼错此前静默按 none = 流控失效）、`baudRate ≥ 1`、`dataBits` 5..8、`port` 1..65535、`connectTimeoutMs ≥ 1`、`gapMs`/`readTimeoutMs`/`writeTimeoutMs` ≥ 0 |
| readTimeoutMs / writeTimeoutMs | 串口 | 500 | |
| （子元素）Retry | 全部 | — | **已接线**：覆盖全局重试（设备`<Retry>` 优先于本项） |
| ~~driver / maxConcurrent~~ | — | — | **已删除**：写了进 `Warnings`（显式告警），不再静默忽略 |
| ~~（子元素）Reconnect~~ | — | — | **未解析**：退避参数只走 `Global/Reconnect`；写了进 `Warnings` |

---

## 8. `DeviceTemplates` / `PointTemplates`

模板与实例**同元素同名属性集**（v2 的两套写法已统一）。
实例属性覆盖模板属性；模板独有子元素（Scale/Format/Alarm/Retry…）会垫入实例。

- 设备模板 id 被 `Device@template` 引用
- 点位模板 id 被 `Point@template` 引用

---

## 9. `Devices`（管「连哪台」）

| 属性 | 默认 | 说明 |
|---|---|---|
| id / name / desc | — / — / — | id 必填；name 可 ${}；desc **已解析进 `DeviceConfig.Desc`**，与 `Point@desc` 同口径的说明性字段，供宿主提示 |
| enabled | true | false 完全不轮询 |
| template | — | 设备模板 id |
| transport | — | **必填**，链路 id（协议与连接参数都在链路上，设备不写 driver/variant） |
| unitId | 1 | 从站地址，**整数**（别写 "01"） |
| pointSet | — | 点表 id（必填） |
| swap | 继承 | 设备级字序缺省（串口设备常为 none）。**已接线**：点位未显式声明 swap 时按 `Device@swap` 解码，再兜底 `Global@swap` |
| requestTimeoutMs | 继承 | |
| generateDiagnostics | true | 自动生成 $device.online / .lastError / .errorCount / .latencyMs |

> `Device@scanGroup` 已删除：节奏写在点位/块上（`Point@intervalMs` / `Block@intervalMs` 与 `mode`）。
> 设备不再决定默认节奏；整台设备一个节奏时把 `intervalMs` 写到各点/块，或调 `Global/Polling@defaultIntervalMs`。

子元素：`Retry`、`Pause@maintenance`（检修暂停，保留配置）、`Simulate@profile`（random/ramp/fixed）。

---

## 10. `PointSets`（核心）

`PointSet` 子元素四段平级：`Defaults`、`Blocks`、`Points`、`Calculated`。

### 10.1 `Block`（块读）

| 属性 | 说明 |
|---|---|
| id / area / start / count | 必填；count 不得超地址组上限（寄存器 `groupLimitRegisters`=125、位 `groupLimitBits`=2000，加载期报错 CGV-11） |
| intervalMs / mode | 本段的周期（毫秒，≥50，未写取全局默认）与模式（auto/onDemand/once）；**块内点位不得写**（CGV-20） |
| unitId / swap / enabled | 缺省继承设备 |

**四条铁律**：块内点位写**绝对地址**且必须落在 `[start, start+count)`；
块内不得覆盖 `area/unitId/intervalMs/mode`（决定「怎么读、多久读」，CGV-7/CGV-20），但 `swap` 可以（只是解码方式）；
块内点位地址必须**连续**（空洞不得超过 `Global/Scheduler@ignoreGap` 的忽略间隔数，默认 0 = 严格连续），
即整块能用**一次请求**覆盖——窗口尾部可以留白，点位之间不许留洞，否则加载即报错（CGV-16）；
块是「显式声明的一次请求」，故 `count` 超上限是错误（散点自动分组才会自动切分）。

### 10.2 `Point` 属性

| 属性 | 默认 | 说明 |
|---|---|---|
| id / name | — / — | id 点表内唯一必填；name 可 ${} |
| desc | — | 说明（已解析，供宿主提示；框架不消费——说明性字段，写了**不**告警） |
| enabled | true | **已接线**：false 的点不参与轮询 |
| access | read | read \| write \| readwrite；**可写性唯一来源**；非法值报错 |
| area / address / addrFormat / length | 继承 / — / protocol / 按 dataType | length 仅 string/raw 需写；bcd/datetime 按类型参数推导（见 §10.3） |
| dataType | 继承 | 见 0.6；多字值必须显式声明整型或浮点 |
| swap / bit / bitRange | 继承 / — / — | bit 0 起；bitRange 形如 4-7。**bitRange 范围受检（CGV-30）**：`from`/`to` 必须 0..15 且 `from ≤ to`，非法即报错（不再静默按「不声明位」处理）。swap 兜底链：`Point > PointSet/Defaults > Block（块内） > Device > Global`；点位未显式写时才取设备级 |
| unit / range | — / — | 工程单位（已解析，供宿主显示）。`range` 量程 `"0..300"` 已解析，但**界面刻度/百分比死区未实现**——写了进 `Warnings`（显式告警） |
| intervalMs / mode | 全局默认 / auto | **多久读一次**：间隔毫秒（≥50，未写取 `Global/Polling@defaultIntervalMs`）；模式 auto/onDemand/once。块内不得写（CGV-20）；`once` 不得带 `intervalMs`（CGV-21） |
| unitId / template | 继承 | **unitId 覆盖已生效**：该点由自身 `unitId` 所属从站轮询（网关场景一条链路读多个从站）；未写时取 `PointSet/Defaults@unitId`，再取 `Device@unitId` |
| readonlyBy | — | 只读原因说明（已解析，仅界面提示；框架不消费——说明性字段，写了**不**告警） |

### 10.3 `Point` 子元素

| 元素 | 属性 | 说明 |
|---|---|---|
| Scale | mode, factor, offset / rawLow, rawHigh, scaledLow, scaledHigh | 工程值=原始×factor+offset，或双点映射；子元素 Clamp@mode(low/high/both)@low@high。**`bcd` 点位同样吃 Scale**（BCD 的「类型解释」结果是一个数值；此前解码漏缩放、而写入却做逆缩放，读写不对称） |
| Format | decimals=0, prefix, suffix, thousands=false, mapOn=engineering, pattern | **`mapOn` 只认 `engineering`/`raw`（CGV-37）、`decimals` ∈ 0..15（CGV-38）**； Map/Item@key→文本；key 按缩放后工程值（mapOn=raw 则按原始值）；布尔点 key 为 true/false。**`decimals` 对整型工程值同样生效**（此前只认 `thousands`，`dataType="bcd"` 上的 `decimals="2"` 显示不出小数） |
| Bits | Bit@index@name@text / Field@from@to@name@text + Map/Item | **位映射展开（CGV-27）**：加载期把每个条目展开成子点位 `整字点位id.位名`，与普通点一样可读、可订阅、可出现在报表/调试输出；**整数值点位保留**（两种都能用）。`Bit` = 单一位（子点位为 bool）；`Field` = 连续位域（等价于 bitRange），可带 `Map` 做枚举显示。**子点位不挂报警、不做任何业务判断**——框架只负责取值。可写性继承整字点位（整字可写 → 子点位可写，写入走位读改写只翻目标位/位域） |
| Slices | Slice@address@length | **非连续片段**：该点由多个片段拼成，运行期把片段（含中间空洞）合成**一段读回来**再按片段拼值，**不为它单独发多次请求**（与自动分组协同，同节拍的其它点位一起读）。也可用于「连写多段即为一次请求」的多寄存器点。**片段跨度（最小 address → 最大 address+length）超过地址组上限（`Scheduler@groupLimitRegisters`，位区为 `groupLimitBits`）→ 加载报错**（CGV-28，文案说明「片段太分散，无法一次请求」并给出跨度与上限）。Slices 点位的有效字长 = 各片段长度之和 |
| String | encoding=ascii, padding=0x00, trimNull=true, left=true | 字符串点用。**padding**：设备的补齐字节（`0x00` 或 `0x20` 等 0x00–0xFF），解码时按它裁掉尾部/头部补齐；**left**：`true`（默认）= 字符串靠左、补齐在右（`"ABC  "`），`false` = 靠右、补齐在左（`"  ABC"`）；**trimNull**（默认 true）= 是否执行上述裁剪。`padding` 非法（超出 0x00–0xFF / 非数值）与 `left` 非布尔**报错**（CGV-29）；`perByte`（字节紧凑排列）**未实现，写了直接报错**，`byteAligned` 已删除（同 `perByte` 报错） |
| Bcd | digits=4 | BCD 点用。**字长按 digits 推导**：每寄存器 4 位十进制，`digits=8` → 占 2 字（非 4 倍数向上取整）；轮询按推导字长读满。**写路径按 BCD 字位编码**（每寄存器 4 位十进制，与解码严格对称；此前写出去的是二进制值）。支持 `<Scale>`（乘/除后落到十进制字位） |
| DateTime | format=plc6 | plc6/plc4/unixSec/unixMs/custom。**字长按 format 推导**：plc6=6 字（年/月/日/时/分/秒）、plc4=4 字（年/月/日/时）、unixSec=2 字（32 位秒）、unixMs=**4 字（64 位毫秒）**——毫秒时间戳必然超出 32 位，2 字装不下 |
| Script | language=js, timeoutMs=继承 Global/Script, onError=继承 Global/Script | **点位脚本**：解码该点位时把**原始寄存器**（`raw`，该点声明字长的寄存器数组）与**原始数值**（`rawValue`，按 dataType/swap 解出的缩放前值）交给脚本，**脚本返回值即工程值**（数值/字符串/布尔皆可，不再叠加 Scale）。正文用 CDATA：可写**表达式**（最后一个表达式的值，如 `rawValue * 0.1`）或含 `return` 的**方法体**（自动按函数体包裹）。可用绑定：`raw`/`rawValue`/`args`（恒空）/`P('id')`/`T('key')`/`device`/`point`/`timestamp`。**失败口径**：返回 null/undefined → `Bad(ss.reason.scriptNull)`；超时 → `Bad(ss.reason.scriptTimeout)` + `SS.SCRIPT.TIMEOUT` 错误事件；抛异常（含 ES5.1 不支持的语法）→ `Bad(ss.reason.scriptError)` + `SS.SCRIPT.FAILED` 错误事件；非有限浮点降级 Uncertain。**绝不阻塞采集线程**：超时/异常只影响该点位本轮的值。**校验（CGV-32）**：`language` 只认 `js`（ES5.1，无箭头函数/let/const）、`timeoutMs` 必须 > 0、`onError` 只认 `markBad`、正文不得为空、未知属性（含已删除的 `maxMemoryMb`）报错。**边界**：脚本内存不受限（Jint 2.x 无内存上限 API） |
| Alarm（可多条） | id, type(high/highHigh/low/lowLow/digital), limit, delayMs=0, deadband=0, priority, message, latch=false, ackRequired=false | 见 AlarmEngine。**加载期规则（CGV-36）**：`type` 穷举（未知值运行期永不触发）；限值型（high/highHigh/low/lowLow）必须给 `limit` 且必须是有限数（NaN/±Inf 报错，digital 不需要）；`deadband ≥ 0`、`delayMs ≥ 0`；**同一点位的报警状态键必须唯一**（键 = `Alarm@id`，缺省 `pointId#type`）——两条同类型都不写 id（或写了同一个 id）会在加载期报「报警键冲突」；**计算点不得挂 `<Alarm>`**（计算点不在采集轮次里，报警永不被评估）。`latch`/`ackRequired` 的确认走门面 `IDeviceManager.AcknowledgeAlarmAsync(deviceId, pointId, alarmId, user)`；`alarmId` 与 `AlarmRaisedEvent.AlarmId` 一致（缺省 id 时为 `pointId#type`） |
| History | enabled, mode(deadband), deadband, intervalMs, retentionDays | 归档策略 |
| Write | min, max, step, pulseMs, confirm=false, permission, verify=false | 只管约束授权；能不能写看 access。**写管道真正消费的是 `min`/`max`（拒绝且不发通讯）、`pulseMs`（脉冲写回）、`verify`（写后回读）**。`verify=true` 时写后回读：一致则正常成功；**不一致返回 `Succeeded` 且 `WriteResult.VerifyMismatch=true`**（设备应答成功但值不符，如自行钳位；界面应提示实际值且**不要自动重试**）。`confirm`（界面二次确认）/`step`（界面步进）/`permission`（角色授权，`Users` 段未实现）写了进 `Warnings`（显式告警） |
| Tags/Tag | — | 自定义元数据 |

### 10.4 `Calculated`（计算点）

`Point` + `<Expression>`（如 `P('mold.barrelTemp') - P('mold.setTemp')`）
或 `Point` + `<Script>`（脚本型计算点；无寄存器可喂，用 `P('id')` 取其它点位值）。
不占地址、不轮询；GetValue 时按需求值并回写缓存。引用：同点表短 id，跨点表 `deviceId/pointId`。
**校验（CGV-33）**：计算点必须恰有 `<Expression>` 或 `<Script>` 之一——两者都写或都不写在加载期报错
（此前「都没有」只在运行期静默给 `Bad(ss.reason.notSupported)`）。
早期设计的「parser 引用顶层 `Parsers` 段」形态**不做**（顶层 `Parsers` 已删除），脚本正文直接写在点位里。
脚本型计算点的失败口径与解码脚本完全一致（markBad + 错误事件 + 超时保护）。

子元素 `<DependsOn><PointRef>id</PointRef>…</DependsOn>`：显式声明依赖（脚本型计算点必须写全）。
**`PointRef` 已解析**（`PointConfig.DependsOn`）并**与表达式里的 `P('id')` 一起参与成环判定（CGV-31）**——
跨点表引用（`deviceId/pointId`）同样参与，报错给出环路径；引用的点位不存在也报错（悬空引用）。

---

## 11. `AlarmClasses`

`AlarmClass@id,@name,@color,@sound,@escalateAfterMs`。被 `Alarm@priority` 引用；升级动作后续迭代。

---

## 12. `Storage`

| 元素 | 关键属性 |
|---|---|
| History | enabled, provider(sqlite/file/mysql/influxdb), connectionString, defaultMode(deadband), defaultIntervalMs, defaultDeadband, defaultDeadbandMode(absolute/percent), retentionDays, batchSize, flushMs, bufferOnOffline |
| Events | enabled, provider, connectionString, retentionDays（审计建议 ≥365） |

语义：**存储介质与保留**归这里；「记什么、级别、能否丢」归事件总线（框架 v1 提供可选内置 sink，宿主可自建）。

---

## 13. `Commands`

| 字段 | 说明 |
|---|---|
| Command@id,@name,@permission,@confirm,@confirmText,@onFailure(rollback/abort/continue) | 门面 ExecuteAsync 引用 |
| Params/Param@name,@type,@min,@max,@default,@required,@label | 入参声明，步骤用 #{name} 引用 |
| Steps/Step@action(write/writeBlock/wait),@point,@value,@delayAfterMs,@ms | 按序执行 |
| Verify@point,@expected,@timeoutMs | 执行后回读校验 |

注意：命令**执行引擎** v1 未实现（配置解析与校验已完成）。

---

## 14. `Ui` / `Users`

- **Ui**：`@startScreen`；`Screen@id,@title,@background`；`Widget@type(label/value/gauge/indicator/bitlamp/trend/button/numeric/alarmlist/table),@x,@y,@w,@h,@point,@bit,@command,@text,@fontSize,@min,@max,@decimals,@unit,@onColor,@offColor,@windowSec,@refreshMs,@permission`；Trend/Table 用 `Points/PointRef`。控件只绑 Point@id。
- **Users**：`@auditWrites`；`Role@id,@name,@canWrite,@canCommand,@canEditConfig` + `AllowedPoints/PointRef` 白名单（不写=全部可写）；`Account@user,@passwordHash,@role,@enabled`（只存哈希；离职置 false 不删除）。

---

## 15. `Diagnostics`

| 元素/属性 | 说明 |
|---|---|
| @allowRawAccess | **默认 false**。调试工具 RawRead/RawWrite 的总开关（绕过写保护，生产必须关） |
| Trace | 框架内部追踪，默认关；enabled/categories(scheduler,codec,retry)/ringSize/echoToLogger |
| Events | 事件总线缺省策略：enabled/defaultMode/queueSize/onQueueFull/reportDropped + Category@name,@level,@batch,@overflow（alarm/audit 必须 wait） |
| Recovery | autoRecover=true, retryOfflineMs=10000 |

注意：v3 已删除 `logLevel/logFile/logFrames`——日志落盘由宿主 ILogger 决定，框架不写日志。

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

### 16.1 规则总表

| 编号 | 级别 | 一句话说明 | 触发示例 |
|---|---|---|---|
| CGV-1 | 错误 | `schemaVersion` 必填且为 `3.x` | `<HostConfig>`（缺属性） |
| CGV-2 | 错误 | XML 必须合法且有根元素 | 文件被截断 → "配置文件不是合法 XML" |
| CGV-3 | 错误 | 引用完整：`template` / `transport` / `pointSet` / `priority` / 命令步骤点位可解析 | `<Device pointSet="不存在"/>` |
| CGV-4 | 错误 | `${KEY}` 必须在默认语言 i18n 资源里存在 | `name="${NO_SUCH_KEY}"` |
| CGV-5 | 错误 | id 唯一：Transport/Device/PointSet 全局唯一；`Point@id` 点表内唯一 | 同一 `PointSet` 里两个 `id="p"` |
| CGV-6 | 错误 | 块窗口包含点位：`address + 有效长度 ∈ [start, start+count)` | 块 `count=4`，点位 `address=9` |
| CGV-7 | 错误 | 块内点位不得覆盖 `area` / `unitId` | `<Block area="input">` 内 `<Point area="holding">` |
| CGV-8 | 错误 | 显式 `length` 与 `dataType` 位宽一致；bcd/datetime 按 `Bcd@digits` / `DateTime@format` 推导 | `float32` 配 `length="3"` |
| CGV-9 | 错误 | `addrFormat="plc"` 时地址前缀与 `area` 一致 | `40001` 配 `area="input"` |
| CGV-10 | 错误 | `access="read"` 的点不得声明 `<Write>` | `access="read"` + `<Write/>` |
| CGV-11 | 错误 | `Block@count` 不超地址组上限（寄存器 `groupLimitRegisters`=125 / 位 `groupLimitBits`=2000） | `count="126"`（holding 块） |
| CGV-12 | 错误 | 同一 `(transport, unitId)` 不得被两个启用的 Device 声明 | 两个 `Device` 都写 `transport="eth_main" unitId="1"` |
| CGV-13 | 错误 | `Alarm@priority` 指向存在的 `AlarmClass` | `priority="CRITICAL"` 但没定义该等级 |
| **CGV-14** | **错误** | **同一点位多条报警的限值大小关系必须成立：`lowLow < low`、`low < high`、`high < highHigh`** | `<Alarm type="high" limit="90"/><Alarm type="highHigh" limit="80"/>` |
| **CGV-15** | **错误** | **计算点 `P('id')` 引用不得成环（含间接环与自引用），报错给环路径** | `c.a → c.b → c.a` |
| **CGV-16** | **错误** | **块内点位地址必须连续（空洞 ≤ `Scheduler@ignoreGap`），即能用一次请求覆盖** | 块内点位 `address=0` 与 `address=2`（空洞 1 个地址、`ignoreGap=0`） |
| **CGV-17** | **错误** | **同一寄存器同一位不得被两个点位重复声明（同址不同位、整字点位与位点位并存都允许）** | 同址两点都写 `bit="0"`；`bitRange="4-7"` 与 `bitRange="6-9"` |
| **CGV-18** | **错误** | **点位地址 + 有效长度不得越出区容量 65536（`<Slices>` 的每个片段同样受检）；地址不得为负** | `address="65535" dataType="uint32"`（65535 + 2 > 65536） |
| **CGV-19** | **错误** | **间隔、超时与地址组上限非法：`Point@intervalMs` / `Block@intervalMs` / `Global/Polling@defaultIntervalMs` 必须 ≥ 50 且 > 0；**请求超时必须 ≥ 1ms（`Global/Polling@requestTimeoutMs`、`Transport@requestTimeoutMs`、`Device@requestTimeoutMs` 三处；0/负数会让驱动对每个请求立即判超时 → 全点位离线）**；`groupLimitRegisters` / `groupLimitBits` 必须 > 0** | `intervalMs="49"`、`intervalMs="0"`、`requestTimeoutMs="0"`、`groupLimitRegisters="0"` |
| **CGV-20** | **错误** | **块内点位不得写 `intervalMs` / `mode`（块统一决定该段的节奏；点位属性与模板带入的都算）** | `<Block start="0" count="4">` 内 `<Point intervalMs="500"/>` |
| **CGV-21** | **错误** | **`mode="once"` 不得同时写 `intervalMs`（只读一次，间隔无意义）** | `<Point mode="once" intervalMs="500"/>` |
| CGV-22 | 错误 | 枚举非法值报错：`area` / `dataType` / `swap` / `access` / `variant` / `parity` / `stopBits` / `Point@mode` / `Block@mode` / `mapOn` | `dataType="bogus"`、`mode="bogus"` |
| CGV-23 | 错误 | 非法数值/布尔报错（带「点位/实体 + 属性」定位），不是裸 `FormatException` | `bit="x"`、`limit="true"`、`unitId="one"` |
| CGV-24 | 警告 | 未实现的配置段/字段按 `HostConfig@unsupportedPolicy` 处理：`warn`（默认，记入 `Warnings`）/ `error`（拒绝加载）/ `ignore` | 写了 `Storage` / `Ui` / `Commands` / `Diagnostics/Trace` |
| **CGV-25** | **错误** | **已删除/已改名的旧写法一律报错（不兼容、不迁移）：`ScanGroups` 段与任何 `scanGroup` 属性、`Global/Polling@rateMs`、`Global/Scheduler@mergeGap`/`maxRegistersPerRead`/`maxBitsPerRead`、`PointSet/Defaults@intervalMs`/`@mode`** | `<ScanGroups>`；`Point scanGroup="fast"`；`<Scheduler mergeGap="2"/>` |
| **CGV-26** | **错误** | **退避队列非法：`Global/Reconnect@delays` 为空（或无有效项）/ 含 0 或负数 / 项数 > 32 / 含非整数；`offlineQuality` 不是 `offline`/`bad`；早期属性名（`initialDelayMs`/`maxDelayMs`/`backoff`/`keepAliveMs`/`keepAliveMode`/`flushRx`/`resetTxn`/`failInFlight`/`resetRetry`）已删除** | `<Reconnect delays="1000,0"/>`；`<Reconnect delays=""/>`；`<Reconnect offlineQuality="uncertain"/>`；`<Reconnect keepAliveMs="5000"/>` |
| **CGV-27** | **错误** | **位映射展开（`<Bits>`）非法，逐条带「点表/点位 + 条目」定位：`Bit@index` 越界（非 0..15）、`Field@from`/`@to` 越界（非 0..15）或 `from > to`、条目缺 `name` 或位名含 `.`/`/`/空白、展开子点位 id（`点位id.位名`）与现有点位冲突、`<Bits>` 与 `bit`/`bitRange`/`<Slices>` 同点并存（位只能长在第一个寄存器的整字上）、整字点位不是单字整数类型** | `<Bit index="16" name="x"/>`；`<Field from="7" to="4" name="a"/>`；`<Bit index="0" name="p.running"/>`；点位已叫 `s.running` 又展开出 `s.running` |
| **CGV-28** | **错误** | **`<Slices>` 片段跨度（最小 `address` → 最大 `address+length`）与**单点一次请求宽度**（连续点 = 有效长度；Slices 点 = 含空洞的跨度）不得超过地址组上限 → 无法一次请求取回：寄存器区用 `Scheduler@groupLimitRegisters`（默认 125）、位区用 `groupLimitBits`（默认 2000）；片段长度 ≤ 0 也报错。单点不可切分，超限会发出 `count > 125` 的一次请求** | `<Slice address="0" length="1"/><Slice address="200" length="1"/>`（跨度 201 > 125）；`dataType="string" length="200"` |
| **CGV-29** | **错误** | **字符串口径非法：`String@padding` 不是 0x00–0xFF 的数值（支持 `0x20`/`32` 写法）、`String@left` 不是布尔；`String@perByte` 与 `String@byteAligned` 未实现（写了即报错「未实现，请勿使用」）** | `<String padding="0x100"/>`；`<String left="yes"/>`；`<String perByte="true"/>` |
| **CGV-30** | **错误** | **`bitRange` 范围非法：`from`/`to` 缺失、非整数、越出 0..15 或 `from > to`（此前静默按「不声明位」处理整字）** | `bitRange="7-4"`、`bitRange="0-16"`、`bitRange="x-y"` |
| **CGV-31** | **错误** | **计算点成环判定覆盖 `<DependsOn><PointRef>` 与表达式 `P('id')` 两类引用（含跨点表 `deviceId/pointId`），报错给环路径；`PointRef` 引用不存在的点位（悬空引用）同样报错** | `<PointRef>c.b</PointRef>` 且 `c.b` 依赖 `c.a`；`P('d2/x')` 经 d2 点表回到本表 |
| **CGV-32** | **错误** | **脚本字段非法：`Point/Script@language` 不是 `js`（仅支持 js/ES5.1）、`Global/Script@timeoutMs` 或 `Point/Script@timeoutMs` ≤ 0 或非整数、`onError` 不是 `markBad`、正文为空、`Point/Script` 上的未知属性（含已删除的 `maxMemoryMb`）、`Global/Script` 写 `language` 或脚本正文** | `<Script language="javascript">…</Script>`；`<Script timeoutMs="0">…</Script>`；`<Script onError="keepLast">…</Script>`；`<Script> </Script>`；`<Global><Script maxMemoryMb="8"/></Global>` |
| **CGV-33** | **错误** | **计算点必须恰有 `<Expression>` 或 `<Script>` 之一**：两者都写（语义冲突）或都不写（没有求值依据）都在加载期报错 | `<Point id="c"><Expression>P('p')</Expression><Script>P('p')</Script></Point>`；`<Point id="c"/>`（在 `<Calculated>` 内） |

| **CGV-34** | **错误 / 警告** | **词汇校验：未知元素名（拼错段名/属性名此前被静默忽略）——`<Ponits>` 打错会让点表变成零点位、零错误零告警。未知**元素**（含「已知元素放错父节点」）恒为**加载错误**（发现后不深入其子树，避免级联刷屏）；未知**属性**按 `HostConfig@unsupportedPolicy` 处置（`warn` 记 Warnings / `error` 拒绝 / `ignore` 静默）。`<Comment>` 是全文档通用的注释元素，任何位置都允许；未实现的整段（`Storage`/`Ui`/`Users`/`Commands`/`Drivers`/`Parsers`/`Diagnostics/Trace|Events|Recovery`、`Transport/Reconnect`、`Point/History`、`Point/Tags`、`Device/Simulate`）不深入校验内部结构** | `<Ponits><Point .../></Ponits>`（元素）；`<Point intervallMs="500"/>`（属性，默认 warn） |
| **CGV-35** | **错误** | **数据区与宽度：①只读区（`input`/`discrete`）不得声明可写（`access="write"`/`readwrite`，含经 `PointSet/Defaults@access` 继承）——Modbus 的 03/04 区没有写功能码，真驱动发帧前即判协议失败；②非寄存器区（`coil`/`discrete`）不得声明多字点位（32/64 位、`string`、`bcd`、`datetime`，或 `length>1`/`<Slices>` 多片段）——位区每个地址只有 1 位，写路径按区走单写**只发首字**（其余静默丢弃）、读也拼不出正确值** | `<Point area="input" access="write"/>`；`<Point area="coil" dataType="string" length="2"/>` |
| **CGV-36** | **错误** | **报警配置：①计算点不得挂 `<Alarm>`（计算点不在采集轮次里，报警永不被评估 = 静默失效）；②`Alarm@type` 必须穷举合法（`high`/`highHigh`/`low`/`lowLow`/`digital`，未知类型在运行期永不变判）；③限值型报警（high/highHigh/low/lowLow）必须有 `limit`，且任何 `limit`/`deadband` 都必须是**有限数值**（NaN/±Inf 报错）；④`deadband ≥ 0`（负死区把清除阈值推到限值另一侧 → 假清除）、`delayMs ≥ 0`；⑤同一点位的**报警状态键**必须唯一（运行期键 = `Alarm@id`，缺省为 `pointId#type`）——两条同类型且都不写 id（或写同一个 id）会共用状态，导致漏报与假清除** | `<Alarm type="foo"/>`；`<Alarm type="high"/>`（缺 limit）；`<Alarm type="high" limit="10" deadband="-5"/>`；同点位两条 `<Alarm type="high" limit="10"/>`/`<Alarm type="high" limit="20"/>`；`<Calculated><Point><Expression>2+2</Expression><Alarm .../></Point></Calculated>` |
| **CGV-37** | **错误** | **枚举穷举补齐（此前静默回落成缺省值）：`HostConfig@unsupportedPolicy`（warn/error/ignore）、`Global@swap` / `Device@swap` / `Block@swap` / `PointSet/Defaults@swap`、`PointSet/Defaults@area|dataType|access`、`Block@area`、`Format@mapOn`（engineering/raw）、`DateTime@format`（plc6/plc4/unixsec/unixms）、`Scale/Clamp@mode`（none/low/high/both）、`Global/Quality@onCommError`（bad/offline/uncertain）与 `@onCommErrorValue`（keepLast/null）、`Transport@handshake`（none/xonxoff/rtscts/dtrdsr）** | `swap="bogus"`（Device/Global/Block/Defaults 任一处）；`<Defaults access="rw"/>`；`<Format mapOn="eng"/>`；`<DateTime format="custom"/>`；`<Clamp mode="high2"/>`；`<Quality onCommErrorValue="bad"/>`；`<Transport handshake="rts"/>` |
| **CGV-38** | **错误** | **数值边界（`bit` 越界此前被静默当成整字点位）：`bit` ∈ 0..15、`length` ≥ 1（0 表示按 dataType 推导，写 0 直接报错）、`unitId` ∈ 0..255（Device/Block/Point/Defaults）、`Format@decimals` ∈ 0..15、`Write@pulseMs` ≥ 0、`Bcd@digits` ∈ 1..64、`Block@start` ≥ 0、`Global/Quality@staleAfterMs` ≥ 0、`Global/Retry@count|@intervalMs` ≥ 0、`Global/Scheduler@ignoreGap` ≥ 0、`Transport@port` ∈ 1..65535、`@baudRate` ≥ 1、`@dataBits` ∈ 5..8、`@connectTimeoutMs` ≥ 1、`@gapMs|@readTimeoutMs|@writeTimeoutMs` ≥ 0、`Transport/Retry@count|@intervalMs` ≥ 0；`Write@min ≤ @max`；`Scale@factor|offset|rawLow|rawHigh|scaledLow|scaledHigh`、`Clamp@low|high|min|max`、`Write@min|max|step`、`Point@range` 端值必须是**有限数值**（NaN/±Inf 会让该点的值恒为坏/不确定）** | `bit="16"`；`length="0"`；`unitId="300"`；`decimals="-1"`；`pulseMs="-1"`；`<Write min="10" max="1"/>`；`<Scale factor="NaN"/>`；`range="0..NaN"` |

> 编号与代码注释里的 `CGV-*` 一致；规则分「错误」（拒绝加载）与「警告」（记入 Warnings，
> `unsupportedPolicy=error` 时升级为拒绝加载）两级。CGV-14~18 为全量校验新增；CGV-19/20/21 为间隔/节奏相关；
> CGV-22/23/24 为枚举/非法数值/未实现段；CGV-25~38 为删除字段/退避/位映射/Slices/字符串/脚本/计算点/词汇/数据区/报警/枚举/数值边界。

### 16.2 一次性汇总全部错误

- 每条错误都带定位（点表/设备/链路 + 点位 + 属性 + 原因）。
- 解析期的非法数值/布尔**不中断收集**（转成带定位的错误后继续跑后面的规则）——一次报全所有矛盾，
  不会遇到第一个错误就停。

---

## 附录 A. 最小可用配置样例

> 取自仓库 `Config/简单样例.xml`（一台 Modbus TCP 设备 + 6 个典型点位）。
> **注意**：原文件是较早 schemaVersion 2.0 的写法（`wordOrder`/`defaultRateMs`/`Polling` 下的 `defaultRateMs`
> 等旧字段名，以及 `Alarm@high/low`、`Alarm@enabled`、`Write@enabled` 这类不在本字典 v3 字段表里的属性），
> 与本文档的 schemaVersion 3.0 字段字典存在差异，**不能直接当作 v3 配置加载**。此处按原文收录供入门参考，
> v3 对应的字段名对照见附录 B-1/B-3。

```xml
<?xml version="1.0" encoding="utf-8"?>
<HostConfig schemaVersion="2.0.0">

  <Meta>
    <ProjectName>${PROJECT_NAME}</ProjectName>
    <Comment>最小可用样例</Comment>
    <CreatedAt>2026-09-11</CreatedAt>
  </Meta>

  <Global language="zh_CN" defaultWordOrder="CDAB">
    <Polling defaultRateMs="1000" requestTimeoutMs="1000"/>
  </Global>

  <I18n default="zh_CN">
    <Files>
      <File lang="zh_CN" path="简单样例.i18n"/>
    </Files>
  </I18n>

  <!-- 一个 Modbus TCP 通道 -->
  <Transports>
    <Transport id="eth1" driver="modbus" variant="Tcp"
               host="192.168.0.1" port="502" requestTimeoutMs="1000"/>
  </Transports>

  <!-- 一台设备 -->
  <Devices>
    <Device id="Mold01" name="${DEV_MOLD_01}"
            driver="modbus" variant="Tcp" transport="eth1"
            unitId="1" pointSet="Mold01_Points" wordOrder="CDAB">
      <Scan mode="poll" pollingRateMs="1000"/>
    </Device>
  </Devices>

  <!-- 点表：六个典型点位 -->
  <PointSets>
    <PointSet id="Mold01_Points">
      <Defaults area="HoldingRegister"/>

      <Points>

        <!-- ① 单寄存器 + 缩放：0.1 分辨率 → 12.3 ℃ -->
        <Point id="mold.temp" name="${PT_TEMP}"
               area="InputRegister" address="0" dataType="Int16" access="Read">
          <Decode scale="0.1" offset="0"/>
          <Format decimals="1" suffix="${UNIT_C}"/>
          <Alarm enabled="true" high="80" low="-20"/>
        </Point>

        <!-- ② 整字枚举：0→待机 1→运行 2→故障 -->
        <Point id="mold.status" name="${PT_STATUS}"
               area="HoldingRegister" address="1" dataType="UInt16">
          <Format>
            <Map>
              <Item key="0">${ST_IDLE}</Item>
              <Item key="1">${ST_RUNNING}</Item>
              <Item key="2">${ST_FAULT}</Item>
            </Map>
          </Format>
        </Point>

        <!-- ③ 取某一位：寄存器 1 的第 0 位 -->
        <Point id="mold.running" name="${PT_RUNNING}"
               area="HoldingRegister" address="1" dataType="Bool" bit="0"/>

        <!-- ④ 多寄存器浮点：占 2 个字，字交换 CDAB -->
        <Point id="mold.pressure" name="${PT_PRESSURE}"
               area="InputRegister" address="10" length="2"
               dataType="Float32" wordOrder="CDAB">
          <Format decimals="2" suffix="${UNIT_MPA}"/>
        </Point>

        <!-- ⑤ 可写设定值：0~300，需确认，Operator 权限 -->
        <Point id="mold.setTemp" name="${PT_SET_TEMP}"
               area="HoldingRegister" address="20" dataType="Int16" access="ReadWrite">
          <Decode scale="0.1"/>
          <Format decimals="1" suffix="${UNIT_C}"/>
          <Write enabled="true" min="0" max="300" step="0.1"
                 requiresConfirm="true" permission="Operator" verifyAfterWrite="true"/>
        </Point>

        <!-- ⑥ 线圈读写：启动/停止 -->
        <Point id="mold.start" name="${PT_START}"
               area="Coil" address="0" dataType="Bool" access="ReadWrite">
          <Write enabled="true" requiresConfirm="true" permission="Operator"/>
        </Point>

      </Points>
    </PointSet>
  </PointSets>

</HostConfig>
```

**样例会演示的 6 种场景**：单寄存器 + 缩放（温度）、整字枚举（状态字）、取某一位（运行标志）、
多寄存器浮点（压力，CDAB 字序）、可写设定值（带范围与权限）、线圈读写（启停）。

> 用 v3 字段字典核对上述样例时，涉及改名的字段为：`Global@defaultWordOrder` → `Global@swap`、
> `Polling@defaultRateMs` → `Polling@defaultIntervalMs`、`Device@wordOrder` → `Device@swap`、
> `Point@wordOrder` → `Point@swap`、`Point@dataType` 等枚举值需与 §0.6 一致（如 `Int16` → `int16`）、
> `<Scan pollingRateMs>` → 由 `Point@intervalMs`/`mode` 承担、`Alarm@high/low` → `Alarm@type+limit`、
> `Decode@scale/offset` → `Scale@factor/offset`。其余语义不变。更完整的 v2→v3 对照见附录 B-1。

---

## 附录 B. 历史与迁移

### B-1 v2 → v3 字段对照（迁移速查）

| v2（旧 JSON/配置） | v3 |
|---|---|
| slave[].slaveName | Device@id |
| slaveDeviceInfo.type | Transport@variant（设备不再写连接属性） |
| connectInfo.ip/port/Serial | Transport@host/@port/@portName |
| connectInfo.slaveId | Device@unitId（整数） |
| slave[].modbusPointMapping | Device@pointSet → PointSets 具名点表 |
| point.absoluteAddress(30001) | @address + @addrFormat="plc" |
| point.region+address | @area + @address（0 基） |
| point.pollingRate | `Point@intervalMs` / `Block@intervalMs`（毫秒）+ `Point@mode` / `Block@mode` |
| parser.numberParser.dotInt | Scale@factor（=10 的负 dotInt 次方） |
| parser.*.sign | dataType 带符号类型（Int16/Int32） |
| parser.*.prefix/suffix | Format@prefix/@suffix |
| parser.*.resultMapping | Format/Map/Item |
| parser.multiWord.type(ABCD…) | @swap（none/byte/word/word_byte 别名） |
| parser.*（整个 parser 段） | 已删除：内联 Scale/Format 或 PointTemplates 复用 |
| point.exposeBits | 删除：逐位显式声明（模板批量生成） |
| Diagnostics@logLevel/logFile/logFrames | 删除：日志归宿主，框架只发事件 |
| Diagnostics@statsIntervalMs | 事件总线 performance 类别 |
| byteOrder+wordOrder | 单一 @swap |
| Quality@onCommError=keepLast | 拆为 onCommError（质量）+ onCommErrorValue（值策略） |
| ImportMapping 段 | 移出运行期配置（迁移工具职责） |

### B-2 已删除字段（相对早期 v3 草案）

| 字段 | 删除理由 |
|---|---|
| `Drivers` 整段 + `Transport@driver` | 只有一个驱动，`variant` 已表达一切；加第二个协议时再引入 |
| `Transport@maxConcurrent` | Modbus 同链路并发必致应答错序，旋钮本身是坑（恒串行） |
| `Global/Polling@gapMs` | 与 `Transport@gapMs` 重复 |
| `I18n@default` | 与 `Global@language` 重复 |
| `I18n@reloadOnChange` | 热重载已决定不做 |
| `Global/Script@maxMemoryMb` | Jint 2.x 无内存上限 API，留着即骗人。**写在 `Global/Script` 或 `Point/Script` 上都报错（CGV-32）**，脚本内存不受限是已知边界 |
| `Global/Scheduler@strictOrder` | v1 只有一条批处理路径，无可配的序 |
| `ScanGroups` 整段 + `ScanGroup@id/@mode/@rateMs/@jitterMs` + `ScanGroup/Trigger` | 命名的扫描节奏绕了一层：点位/块直接写毫秒 `intervalMs` + `mode` 更直白；抖动（jitterMs）先删，需要再加 |
| `Device@scanGroup` / `Block@scanGroup` / `Point@scanGroup` / `PointSet/Defaults@scanGroup` | 同上；节奏不再有设备/点表级默认，只有 `Point@intervalMs` / `Block@intervalMs` 与全局 `defaultIntervalMs` 两级 |
| `PointSet/Defaults@intervalMs` / `@mode` | 不设这一层：避免「写了不生效」的中间继承层，写了直接报错（CGV-25） |
| `String@byteAligned` | 从未实现（框架按寄存器对齐，无字节紧凑排列）。写了直接报错（CGV-29），不保留空转的开关 |
| `String@perByte` | 未实现（字节紧凑排列）。写了直接报错（CGV-29） |
| `Transport@driver` / `Transport@maxConcurrent` | 只有一条驱动路径、同链路恒为串行；**写了进 `Warnings`**（显式告警，不再静默忽略） |

### B-3 同版本内改名（旧名一律报错，不做兼容）

| 旧名 | 新名 | 说明 |
|---|---|---|
| `Global/Polling@rateMs` | `Global/Polling@defaultIntervalMs` | 单位仍是毫秒，默认 1000；现在承载「未写 intervalMs 的点位/块的周期」 |
| `Global/Scheduler@mergeGap` | `Global/Scheduler@ignoreGap` | 语义按用户口语命名：忽略间隔数（0=严格连续，1=中间空一个地址也算连续） |
| `Global/Scheduler@maxRegistersPerRead` | `Global/Scheduler@groupLimitRegisters` | 地址组上限（寄存器），默认 125 |
| `Global/Scheduler@maxBitsPerRead` | `Global/Scheduler@groupLimitBits` | 地址组上限（位），默认 2000 |
| `Global/Reconnect@initialDelayMs` / `@maxDelayMs` / `@backoff` | `Global/Reconnect@delays` | 退避改成显式队列（逗号分隔毫秒，逐项等待、用完后用最后一个值循环），不再用「初始值 + 上限 + 倍率」推导 |
| `Global/Reconnect@keepAliveMs` / `@keepAliveMode` | （无） | keepalive 参数固定（空闲 10s / 间隔 3s / 3 次，见 §3.1），不做应用层心跳 |
| `Global/Reconnect@flushRx` / `@resetTxn` / `@failInFlight` / `@resetRetry` | （无） | 重连后清理改为无条件执行：连接对象整体重建（清缓冲、重置事务号）、在途请求不续做、退避队列由成功通讯归零 |

> 删除遵循「现在没有外部用户，删的成本最低」；以上能力若日后需要，按 B-2 记录重新引入。

### B-4 仅解析、无消费者（写了显式告警）

这些字段的语义是「改变行为或数值」，但框架里没有任何消费者（或压根没解析）。
按下表处置：**不静默忽略**——写了进 `SamplerConfiguration.Warnings`，受 `HostConfig@unsupportedPolicy` 控制
（`error` 时升级为拒绝加载）；不写不告警。判据与逐字段「解析生效 + 行为生效」全表见
[`测试与验收/字段行为矩阵.md`](测试与验收/字段行为矩阵.md)。

| 字段 | 现状 |
|---|---|
| `Global@timeZone` | 进模型、无消费者（框架无显示层，日期时间按机器本地时区解） |
| `Global/Retry@backoff` | 进模型、无消费者（重试间隔恒为 `intervalMs`） |
| `Global/Retry@escalateAfter` | 进模型、无消费者（设备级→链路级升级由 `Global/Reconnect` 承担） |
| `Global/Retry@budgetMs` | 进模型、无消费者（重试只按 `count` 计次） |
| `Global/Retry@maxBackoffMs` / `@onLinkError` / `@onException` | 根本没解析 |
| `Point@range` | 进模型（`RangeLow/RangeHigh`）、无消费者（界面刻度/百分比死区未实现） |
| `Point/Write@confirm` | 进模型、无消费者（宿主界面二次确认） |
| `Point/Write@step` | 进模型、无消费者（界面步进；写管道只做 min/max 范围校验） |
| `Point/Write@permission` | 进模型、无消费者（`Users` 角色体系未实现，写管道按放行处理） |
| `Transport/Reconnect` | 整段未解析（退避只走 `Global/Reconnect`） |

> 说明性字段（`Meta/*`、`Point@desc/@unit/@readonlyBy`、`Device@name/@desc`）**不属于**本表：
> 它们只进模型供宿主读取，语义就是「说明文字/单位」，写了不告警。
