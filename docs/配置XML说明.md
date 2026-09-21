# SuperSampler 配置 XML 参考（逐标签逐属性）

> 这份文档是 **HostConfig（schemaVersion 3.0）配置的逐标签逐属性参考**：按 XML 嵌套顺序，每个标签一节，
> 讲清「这个标签表示什么、可以含哪些子标签、每个属性填什么（枚举值逐个列出）、默认值、是否必填」。
> 新手先用 [`docs/教程.md`](教程.md) 第 2 章建立整体印象，再回来查细节；本文是查字典用的。

## 阅读方式

- 标题层级 = XML 嵌套：`## HostConfig（根）` → `### Global` → `#### Global/Polling`。
- 每个标签的模板一致：**是什么 / 子标签 / 属性表（属性 | 含义 | 取值 | 默认 | 必填 | 备注）**。
- 属性表里「取值」列写枚举时逐个列出（如 `none`/`even`/`odd`/`mark`/`space`）；写范围时给区间与单位。
- 没写的属性用默认值；「必填 = ✅」的属性缺了加载期直接报错（所有错误一次报全，见后文校验规则）。

## 通用约定

**继承链**（没显式写的属性沿链向上取）：`Point > Block（仅块显式声明时）> PointSet/Defaults > Device > Global`。
最常用到的是 `swap`（字序）与 `dataType`——点位没写就从 `Defaults` → `Device` → `Global` 找。

**变量语法**（配置里可以引用）：
- `${KEY}`：i18n 文本，加载期从 `I18n` 资源按 `Global@language` 匹配替换（`#` 开头是注释）。
- `P('pointId')`：计算点/脚本里取另一个点位的当前工程值（只认好值，坏值当 undefined）。
- `T('key')`：在脚本里取 i18n 文本。

**地址写法**：`Point@address` 默认 0 基协议地址；`addrFormat="plc"` 时用西门子式写法自动换算——
`4xxxx → 保持寄存器（40001 起）`、`3xxxx → 输入寄存器`、`1xxxx → 离散输入`、`1..9999 → 线圈`。

**关键枚举速查**（每个枚举值代表什么，逐个说清；属性表里的「取值」写「见通用约定」即回到这几张表）：

**`dataType`（数据类型，13 种）**——决定一个点占几个寄存器、解出来是什么工程值：

| dataType | 字长 | 工程值形态 | 典型用途 |
|---|---|---|---|
| `bool` | 1 位 | `bool` | 开关、状态、线圈 |
| `int16` | 1 字 | `short`（有符号 16 位） | 带符号量，如温度（配 Scale ×0.1） |
| `uint16` | 1 字 | `ushort`（无符号 16 位） | 计数、状态字 |
| `int32` | 2 字 | `int` | 有符号 32 位 |
| `uint32` | 2 字 | `uint` | 无符号 32 位（大累计值） |
| `int64` | 4 字 | `long` | 有符号 64 位 |
| `uint64` | 4 字 | `ulong` | 无符号 64 位 |
| `float32` | 2 字 | `float` | 单精度浮点（压力/流量） |
| `float64` | 4 字 | `double` | 双精度浮点 |
| `string` | `length` 字 | `string` | 配方名、批号等文本 |
| `bcd` | `⌈digits/4⌉` 字 | `long` | BCD 编码的数值（如生产批次号） |
| `datetime` | 按格式（6/4/2/4 字） | `DateTime` | 时间戳 |
| `raw` | `length` 字 | `byte[]`（线上 `ushort[]`） | 不做解析的原始寄存器块 |

**`area`（数据区，4 种）**——Modbus 四区，决定用哪个功能码读写：

| area | 读写 | 说明 |
|---|---|---|
| `coil` | 可读写 | 线圈（0 区），1 位/点，功能码 01/05/0F |
| `discrete` | 只读 | 离散输入（1 区），1 位/点，功能码 02 |
| `input` | 只读 | 输入寄存器（3 区），1 字/点，功能码 04 |
| `holding` | 可读写 | 保持寄存器（4 区），1 字/点，功能码 03/06/10 |

**`swap`（字序，4 种）**——多字值（≥2 字）的字节/字排列方式（单字值不受影响）：

| swap | 排列 | 说明 |
|---|---|---|
| `none` | ABCD | 大端，高字节在前（标准 Modbus） |
| `byte` | BADC | 每个字内的高低字节对调 |
| `word` | CDAB | 字的顺序对调（国内设备常见） |
| `word_byte` | DCBA | 字节和字都反（小端） |

**`access`（读写权限，3 种）**：`read` 只读 / `write` 只写 / `readwrite` 可读写。

---

## HostConfig（根）

- **是什么**：整份配置的根元素，一份配置只此一个。schema 版本必须 3.x。
- **子标签**（顺序即推荐书写顺序）：`Meta` → `Global` → `I18n` → `AlarmClasses` → `Transports` → `Devices` → `PointSets`。另有未实现段（`Commands`/`Storage`/`Ui`/`Users`/`Diagnostics` 等）见文末专节。
- **属性**：

| 属性 | 含义 | 取值 | 默认 | 必填 | 备注 |
|---|---|---|---|---|---|
| `schemaVersion` | 配置 schema 版本 | `3.0`（或任何 `3.x`） | — | ✅ | 不是 3.x 直接报错 |
| `unsupportedPolicy` | 未实现段/未消费属性/未知属性的处置 | `warn`（告警）/ `error`（拒绝）/ `ignore`（静默） | warn | 否 | 默认 warn：写了没实现的东西会告警但不挡加载 |

---

## Meta（工程元信息）

- **是什么**：说明性字段，不参与任何采集逻辑，纯给工程归档/人看。
- **子标签**：`Tags` → `Tag`。
- **属性**：

| 属性 | 含义 | 取值 | 默认 | 必填 |
|---|---|---|---|---|
| `ProjectName` | 工程名 | 任意字符串 | — | 否 |
| `Comment` | 备注 | 任意字符串 | — | 否 |
| `Author` | 作者 | 任意字符串 | — | 否 |
| `CreatedAt` | 创建时间 | 任意字符串 | — | 否 |
| `Revision` | 修订号 | 任意字符串 | — | 否 |

`Tags/Tag`：任意个说明性标签（字符串）。

---

## Global（全局默认值）

- **是什么**：全局默认参数——语言、交换、轮询默认节奏、重试、退避、调度上限、脚本默认、质量策略。
- **子标签**：`Polling`、`Retry`、`Reconnect`、`Scheduler`、`Script`、`Quality`（各见下节）。
- **属性**：

| 属性 | 含义 | 取值 | 默认 | 必填 | 备注 |
|---|---|---|---|---|---|
| `language` | 首选语言，决定选哪个 i18n 文件 | 如 `zh_CN` | zh_CN | 否 | 与 `I18n/File@lang` 匹配且磁盘存在才用 |
| `fallbackLanguage` | 首选语言文件缺失时退回的语言 | 如 `en_US` | en_US | 否 | |
| `timeZone` | 时区（当前未消费，写了告警） | 如 `Asia/Shanghai` | — | 否 | 属宿主展示层 |
| `nullText` | 质量非 Good 时 `GetValue` 返回的显示串 | 任意字符串 | `--` | 否 | |
| `swap` | 全局限定字序（被更内层覆盖） | `none`/`byte`/`word`/`word_byte` | word | 否 | 继承链最低层 |

### Global/Polling（轮询默认节奏）

- **是什么**：未写 `intervalMs` 的点位默认轮询间隔，以及全局请求超时。
- **子标签**：无。
- **属性**：

| 属性 | 含义 | 取值 | 默认 | 必填 | 备注 |
|---|---|---|---|---|---|
| `defaultIntervalMs` | 默认轮询间隔（毫秒） | ≥50 的整数 | 1000 | 否 | 调度节拍=50ms；<50 加载期报错 |
| `requestTimeoutMs` | 单次请求超时（毫秒） | ≥1 的整数 | 1000 | 否 | 0/负数会让请求立即判超时（报错） |

### Global/Retry（重试计次）

- **是什么**：读请求的重试策略（次数 + 间隔）。写请求**恒不重试**，与此无关。
- **子标签**：无。
- **属性**：

| 属性 | 含义 | 取值 | 默认 | 必填 | 备注 |
|---|---|---|---|---|---|
| `count` | 重试次数 | ≥0 的整数 | 2 | 否 | 读失败（瞬时错误）按此重发 |
| `intervalMs` | 重试间隔（毫秒） | ≥0 的整数 | 100 | 否 | |
| `backoff` / `escalateAfter` / `budgetMs` | 重试退避参数 | — | — | 否 | **写了进告警**（未消费；重试只按 count/intervalMs 计次） |

### Global/Reconnect（断线两级退避）

- **是什么**：断线/超时的退避策略——失败进显式延迟队列、逐档等待、用尽循环最后一档、成功一次归零。读超时→设备级退避；IO 失败→链路级退避；同链路全部从站退避→升级为链路级。
- **子标签**：无。
- **属性**：

| 属性 | 含义 | 取值 | 默认 | 必填 | 备注 |
|---|---|---|---|---|---|
| `enabled` | 是否启用退避 | `true`/`false` | true | 否 | false = 失败不等待、立即重试 |
| `delays` | 退避延迟队列（毫秒，逗号分隔） | 逐项 >0 的整数，项数 ≤32 | `300,1000,3000,10000,30000` | 否 | 逐档等待，用尽后一直循环最后一档 |
| `manualRetry` | 是否允许 `RetryDevice/RetryLink` 手动重试 | `true`/`false` | true | 否 | false 时手动重试返回 NotSupported |
| `offlineQuality` | 退避期间点位质量 | `offline` / `bad` | offline | 否 | |

### Global/Scheduler（调度上限）

- **是什么**：自动分组与地址空洞容差的调度参数。
- **子标签**：无。
- **属性**：

| 属性 | 含义 | 取值 | 默认 | 必填 | 备注 |
|---|---|---|---|---|---|
| `groupLimitRegisters` | 寄存器区单次请求地址组上限 | >0 的整数 | 125 | 否 | Modbus 协议硬上限；超了自动切多次请求 |
| `groupLimitBits` | 位区（线圈/离散）单次请求上限 | >0 的整数 | 2000 | 否 | |
| `ignoreGap` | 自动分组忽略的地址空洞数 | ≥0 的整数 | 0 | 否 | 空洞 ≤ 此值才合并成一组读 |

### Global/Script（脚本默认参数）

- **是什么**：点位 `<Script>` 未单独声明时用的脚本默认值（Jint ES5.1）。
- **子标签**：无。
- **属性**：

| 属性 | 含义 | 取值 | 默认 | 必填 | 备注 |
|---|---|---|---|---|---|
| `timeoutMs` | 脚本执行超时（毫秒） | >0 的整数 | 50 | 否 | 超时置 Bad（`ss.reason.scriptTimeout`） |
| `onError` | 脚本失败处置 | `markBad` | markBad | 否 | 当前只支持 markBad |
| `language` | 脚本语言 | — | — | — | **在这里写会报错**；语言在点位 `<Script>` 上声明（只支持 `js`） |

### Global/Quality（质量与陈旧策略）

- **是什么**：通讯失败时点位质量与值怎么处理，以及"陈旧"参考时长（宿主用它判显示）。
- **子标签**：无。
- **属性**：

| 属性 | 含义 | 取值 | 默认 | 必填 | 备注 |
|---|---|---|---|---|---|
| `onCommError` | 通讯失败时点位质量 | `bad`（不可用）/ `offline`（链路离线）/ `uncertain`（可疑，值可能仍可用） | bad | 否 | 值按 `onCommErrorValue` 处置 |
| `onCommErrorValue` | 通讯失败时保留还是清空值 | `keepLast`（保留旧值）/ `null`（清空） | keepLast | 否 | |
| `staleAfterMs` | 陈旧参考时长（毫秒） | ≥0 的整数 | 5000 | 否 | 框架不做陈旧判定，宿主用 `GetValueAge` + 此值自己判 |

---

## Diagnostics（调试门禁）

- **是什么**：调试工具的全局开关。
- **子标签**：无。
- **属性**：

| 属性 | 含义 | 取值 | 默认 | 必填 | 备注 |
|---|---|---|---|---|---|
| `allowRawAccess` | 是否允许 `RawRead/RawWrite` 原始读写 | `true`/`false` | false | 否 | false 时调试门面原始读写抛异常 |

---

## I18n（多语言资源）

- **是什么**：文本资源（`${KEY}` 的替换表）。选择规则：先找与 `Global@language` 匹配且磁盘存在的文件 → 否则 `fallbackLanguage` → 否则全部加载。文件缺失静默跳过。
- **子标签**：`Files` → `File`。
- **`File` 属性**：

| 属性 | 含义 | 取值 | 默认 | 必填 |
|---|---|---|---|---|
| `path` | 资源文件路径（**相对配置文件所在目录**） | 路径 | — | ✅ |
| `lang` | 该文件的语言 | 如 `zh_CN` | — | ✅ |

- 文件格式：`key=value` 每行，`#` 开头是注释。配置里出现 `${KEY}` 且默认语言找不到 → 加载期报错。

---

## AlarmClasses（报警等级）

- **是什么**：报警等级集合，点位 `<Alarm@priority>` 引用这里的 `id`。
- **子标签**：`AlarmClass`。
- **`AlarmClass` 属性**：

| 属性 | 含义 | 取值 | 默认 | 必填 | 备注 |
|---|---|---|---|---|---|
| `id` | 等级标识（被 `Alarm@priority` 引用） | 字符串 | — | ✅ | 引用不存在的 id 加载期报错 |
| `name` | 等级名（i18n 可替换） | 字符串 | — | 否 | 展示属性，框架不消费 |
| `color` | 颜色（如 `#D32F2F`） | 字符串 | — | 否 | 展示属性，框架不消费 |
| `sound` / `escalateAfterMs` | 声音/升级延时 | — | — | 否 | 写了进告警（展示属性，宿主消费） |

---

## Transports（链路：怎么连）

- **是什么**：一条链路 = 一种连接（TCP 或串口）。**换承载只改这一个元素**，`Device/PointSet/Point` 与承载无关（见 `docs/教程.md` §2.3.1）。
- **子标签**：`Retry`（覆盖全局重试，优先级：Device/Retry > Transport/Retry > Global/Retry）。
- **属性**：

| 属性 | 含义 | 取值 | 默认 | 必填 | 备注 |
|---|---|---|---|---|---|
| `id` | 链路标识（被 `Device@transport` 引用） | 字符串 | — | ✅ | 全局唯一 |
| `variant` | 承载方式 | `tcp` / `rtuovertcp` / `rtu`（真实现）；`udp` / `ascii`（**未实现**，写了告警） | tcp | 否 | `rtu`=原生串口填串口参数；`rtuovertcp`=RTU 帧走 TCP 填 host/port |
| `enabled` | 是否启用 | `true`/`false` | true | 否 | false = 整条链路停用（不建主站、不轮询） |
| `host` / `port` | TCP 目标地址/端口 | IP + 1..65535 | — / 502 | TCP 时 ✅ | `rtu` 串口不需要 |
| `portName` | 串口名（如 `COM3`） | 字符串 | — | `rtu` 时 ✅ | |
| `baudRate` | 波特率 | ≥1 的整数 | 9600 | 否 | |
| `dataBits` | 每字节数据位数 | 5..8（整数） | 8 | 否 | 现场仪表常为 7 |
| `parity` | 校验位 | `none`（无校验）/ `even`（偶校验）/ `odd`（奇校验）/ `mark`（校验位恒 1）/ `space`（校验位恒 0） | none | 否 | 现场仪表常为 Even |
| `stopBits` | 停止位个数 | `one`（1 位）/ `onepointfive`（1.5 位）/ `two`（2 位） | one | 否 | |
| `handshake` | 流控方式 | `none`（无）/ `xonxoff`（XON/XOFF 软件流控）/ `rtscts`（RTS/CTS 硬件流控）/ `dtrdsr`（DTR/DSR 硬件流控） | none | 否 | |
| `dtr` / `rts` | 流控信号 | `true`/`false` | false | 否 | RS485 用**自动流向**转换器（框架不做每帧 RTS/DE 翻转） |
| `connectTimeoutMs` | 连接超时（毫秒） | ≥1 | 3000 | 否 | |
| `requestTimeoutMs` | 通道发送超时（毫秒） | ≥1 | 1000 | 否 | 每请求超时由 `Device@requestTimeoutMs` 决定 |
| `gapMs` | 帧间静默间隔（毫秒） | ≥0 | 0 | 否 | 串口建议 20~50ms（RTU 靠帧间停顿分帧） |
| `readTimeoutMs` / `writeTimeoutMs` | 串口读写超时 | ≥0 | 500 | 否 | 仅串口 |

- `driver` / `maxConcurrent`：不支持，写了进告警。

---

## Devices（设备：连哪台）

- **是什么**：链路上的一个从站。同一 (transport, unitId) 只允许一个**启用**设备。
- **子标签**：`Retry`（设备级重试覆盖）、`Pause`（`maintenance="true"` 检修暂停，暂停期间不轮询）。
- **属性**：

| 属性 | 含义 | 取值 | 默认 | 必填 | 备注 |
|---|---|---|---|---|---|
| `id` | 设备标识（门面寻址主键 `deviceId`） | 字符串 | — | ✅ | 全局唯一 |
| `name` | 设备名（i18n 可替换） | 字符串 | — | 否 | |
| `desc` | 说明 | 字符串 | — | 否 | 说明性，不参与逻辑 |
| `enabled` | 是否启用 | `true`/`false` | true | 否 | |
| `transport` | 挂哪条链路 | 链路 id | — | ✅ | 必须存在 |
| `unitId` | 从站号 | 0..255 整数 | 1 | 否 | 别写字符串 "01" |
| `pointSet` | 读哪张点表 | 点表 id | — | ✅ | |
| `swap` | 设备级字序（被点位覆盖） | `none`/`byte`/`word`/`word_byte`（含义见通用约定·swap 全解） | 继承 Global | 否 | |
| `requestTimeoutMs` | 每请求超时（毫秒） | ≥1 | 1000 | 否 | 覆盖全局 |
| `generateDiagnostics` | 自动生成诊断点位 | `true`/`false` | — | 否 | **未实现**，写了进告警 |

---

## PointSets（点表：读什么、怎么解）

- **是什么**：一组点位的集合，被 `Device@pointSet` 引用。一张点表可被多台同类设备复用。
- **子标签**：`Defaults`、`Blocks`、`Points`、`Calculated`。
- **属性**：

| 属性 | 含义 | 取值 | 默认 | 必填 |
|---|---|---|---|---|
| `id` | 点表标识 | 字符串 | — | ✅ |

### PointSet/Defaults（点位默认值）

- **是什么**：本点表点位没显式写时从这里继承。**不承载 `intervalMs`/`mode`**（写了报错）。
- **子标签**：无。
- **属性**：

| 属性 | 含义 | 取值 | 默认 | 必填 |
|---|---|---|---|---|
| `area` | 默认数据区 | `coil`/`discrete`/`input`/`holding`（含义见通用约定·area 全解） | — | 否 |
| `dataType` | 默认类型 | `bool`/`int16`/`uint16`/`int32`/`uint32`/`int64`/`uint64`/`float32`/`float64`/`string`/`bcd`/`datetime`/`raw`（含义见通用约定·dataType 全解） | — | 否 |
| `swap` | 默认字序 | `none`/`byte`/`word`/`word_byte`（含义见通用约定·swap 全解） | — | 否 |
| `unitId` | 默认从站号 | 0..255 | — | 否 |
| `access` | 默认读写权限 | `read`（只读）/ `write`（只写）/ `readwrite`（可读写） | — | 否 |

### PointSet/Blocks → Block（显式块）

- **是什么**：一段连续地址窗口，一次请求读完，块内点位共享结果；块用自己 `intervalMs` 控制节奏。**块内点位不得写 area/unitId/intervalMs/mode**；点位地址须落在 `[start, start+count)` 内；块内点位地址连续（空洞 ≤ ignoreGap 容差）。
- **子标签**：`Point`（点位，写法与散点一致但受块约束）。
- **属性**：

| 属性 | 含义 | 取值 | 默认 | 必填 | 备注 |
|---|---|---|---|---|---|
| `id` | 块标识 | 字符串 | — | ✅ | 供 `TriggerBlockReadAsync` 用 |
| `start` | 起始地址（0 基） | ≥0 | — | ✅ | |
| `count` | 寄存器/位数量 | ≥1，≤ 地址组上限 | — | ✅ | |
| `area` | 数据区 | `coil`/`discrete`/`input`/`holding`（含义见通用约定·area 全解） | holding | 否 | |
| `intervalMs` | 轮询间隔（毫秒） | ≥50 | 继承全局 | 否 | |
| `mode` | 采集模式 | `auto`（周期读）/ `onDemand`（手动触发）/ `once`（启动读一次直到成功） | auto | 否 | |
| `unitId` | 从站号 | 0..255 | 设备 unitId | 否 | |
| `swap` | 字序 | `none`/`byte`/`word`/`word_byte`（含义见通用约定·swap 全解） | 继承 | 否 | |
| `enabled` | 是否启用 | `true`/`false` | true | 否 | |

### PointSet/Points → Point（点位：读什么、怎么解）

- **是什么**：一个具体点位。所有采集语义（地址、类型、字序、缩放、报警、写策略）都在这层配。
- **子标签**：`Scale`、`Format`、`Write`、`String`、`Bcd`、`DateTime`、`Script`、`Alarm`、`Bits`、`Slices`、`DependsOn`、`Expression`（各见下节）。
- **属性**：

| 属性 | 含义 | 取值 | 默认 | 必填 | 备注 |
|---|---|---|---|---|---|
| `id` | 点位标识（门面寻址 `pointId`） | 字符串 | — | ✅ | 点表内唯一 |
| `name` / `desc` / `unit` | 名称/说明/单位（i18n 可替换） | 字符串 | — | 否 | 展示用 |
| `area` | 数据区 | `coil`/`discrete`/`input`/`holding` | holding | 否 | |
| `address` | 协议地址（0 基） | ≥0 整数 | — | ✅ | `addrFormat="plc"` 时按通用约定换算 |
| `length` | 有效字长 | 0（按类型推导）或正整数 | 0 | 否 | 写 0 报错；与类型推导不一致报错 |
| `dataType` | 类型 | `bool`/`int16`/`uint16`/`int32`/`uint32`/`int64`/`uint64`/`float32`/`float64`/`string`/`bcd`/`datetime`/`raw`（含义见通用约定·dataType 全解） | uint16 | 否 | |
| `swap` | 字序 | `none`/`byte`/`word`/`word_byte`（含义见通用约定·swap 全解） | 继承链 | 否 | 多字值才受影响 |
| `bit` | 取哪位 | 0..15 | — | 否 | bool/整字点取位 |
| `bitRange` | 取位段 | `"from-to"`，0≤from≤to≤15 | — | 否 | 如 `"4-7"` |
| `intervalMs` | 轮询间隔（毫秒） | ≥50 | 继承全局 | 否 | `once` 模式不得写 |
| `mode` | 采集模式 | `auto`（按 `intervalMs` 周期读，默认）/ `onDemand`（不进轮询计划，只由 `TriggerOnDemandRead` 手动触发）/ `once`（启动后读一次，失败按退避重试直到成功一次） | auto | 否 | `once` 不得同时写 `intervalMs` |
| `access` | 读写权限 | `read`（只读）/ `write`（只写）/ `readwrite`（可读写） | read | 否 | 只读点不能带 `<Write>` |
| `unitId` | 覆盖从站号 | 0..255 | 设备 unitId | 否 | |
| `enabled` | 是否启用 | `true`/`false` | true | 否 | |
| `template` | 引用 PointTemplates | 模板 id | — | 否 | |
| `readonlyBy` / `range` | 提示/范围 | 字符串 | — | 否 | 写了进告警（宿主展示层） |

#### Point/Scale（缩放）

- **是什么**：原始寄存器 → 工程值的换算（写路径自动逆换算）。`×0.1` 的温度就用 `factor="0.1"`。
- **子标签**：`Clamp`。
- **属性**：

| 属性 | 含义 | 取值 | 默认 | 必填 | 备注 |
|---|---|---|---|---|---|
| `factor` | 线性系数：工程值 = raw×factor+offset | 有限数 | 1.0 | 否 | |
| `offset` | 偏移 | 有限数 | 0.0 | 否 | |
| `rawLow`/`rawHigh`/`scaledLow`/`scaledHigh` | 双点线性映射（四值齐备时优先于 factor/offset） | 有限数 | — | 否 | 如 `0..10000 → -50.0..200.0` |
| `mode` | 缩放模式 | `linear`（当前只支持它） | linear | 否 | 写 `twopoint` 等只告警 |

`Clamp`（钳制）：`mode` = `none`（不钳）/ `low`（只钳下限，低于 `low` 就压到 `low`）/ `high`（只钳上限，高于 `high` 就压到 `high`）/ `both`（上下都钳）；`low`/`high`（别名 `min`/`max`）为钳制边界。

#### Point/Format（显示格式）

- **是什么**：`GetValue` 显示串的格式（小数位、千分位、前后缀、值→文本映射、时间格式）。
- **子标签**：`Map` → `Item`。
- **属性**：

| 属性 | 含义 | 取值 | 默认 | 必填 |
|---|---|---|---|---|
| `decimals` | 小数位 | 0..15 | 0 | 否 |
| `prefix` / `suffix` | 前后缀 | 字符串 | — | 否 |
| `thousands` | 千分位 | `true`/`false` | false | 否 |
| `mapOn` | `Map` 值→文本映射按哪个值来查 | `engineering`（按缩放后的工程值查）/ `raw`（按原始寄存器值查） | engineering | 否 |
| `pattern` | 时间格式（仅 DateTime 点） | 格式串 | — | 否 |

`Map/Item`：`key`（值）+ 文本（如报警码 `0→无`、`1→油温过高`）；布尔点键为 `true`/`false`。

#### Point/Write（写策略）

- **是什么**：写操作的校验与行为。**只读点（access≠write/readwrite）不得带它**。
- **子标签**：无。
- **属性**：

| 属性 | 含义 | 取值 | 默认 | 必填 | 备注 |
|---|---|---|---|---|---|
| `min` / `max` | 工程值范围（越界写 → `Rejected`，未发通讯） | 有限数，min≤max | — | 否 | |
| `verify` | 写后回读校验 | `true`/`false` | false | 否 | 回读不一致打 `VerifyMismatch` 标记而非报失败 |
| `pulseMs` | 点动脉冲：写 true 后延时自动写回 false（毫秒） | ≥0 | 0 | 否 | |
| `step` / `permission` / `confirm` | 步进/权限/二次确认 | — | — | 否 | **写了进告警**（未消费；角色授权属宿主） |

#### Point/String（字符串解码）

- **是什么**：`string` 类型点位的编码与裁剪。
- **子标签**：无。
- **属性**：

| 属性 | 含义 | 取值 | 默认 | 必填 |
|---|---|---|---|---|
| `encoding` | 编码 | `ascii` / `utf-8` 等（以支持列表为准） | ascii | 否 |
| `trimNull` | 裁掉 `0x00` 补齐 | `true`/`false` | true | 否 |
| `padding` | 填充字节（写时补） | `0x00`..`0xFF`（可写 `0x20` 或 `32`） | 0 | 否 |
| `left` | 靠左还是靠右补齐 | `true`（左对齐，裁尾部）/ `false`（右对齐，裁头部） | true | 否 |

`perByte`/`byteAligned`：不支持，写了报错。

#### Point/Bcd（BCD 解码）

- **是什么**：BCD 编码的位数。
- **子标签**：无。
- **属性**：

| 属性 | 含义 | 取值 | 默认 | 必填 |
|---|---|---|---|---|
| `digits` | BCD 有效位数 | 1..64 | 4 | 否 |

#### Point/DateTime（时间解码）

- **是什么**：日期时间类型点的格式。
- **子标签**：无。
- **属性**：

| 属性 | 含义 | 取值（字长） | 默认 | 必填 |
|---|---|---|---|---|
| `format` | 时间格式 | `plc6`（6 字，年/月/日/时/分/秒 各 1 字，常见 PLC 格式）/ `plc4`（4 字，年/月/日/时）/ `unixsec`（2 字，Unix 秒）/ `unixms`（4 字，Unix 毫秒） | plc6 | 否 |

#### Point/Script（脚本解码）

- **是什么**：用 JS（Jint ES5.1）把原始寄存器算成工程值。有脚本时**不再叠加 Scale**。输入：`raw`（寄存器数组）、`rawValue`（预缩放原始值）、`P('id')`（只认好值）、`T('key')`、`timestamp`；返回值即工程值。
- **子标签**：无（正文 = 脚本源码，写在标签内文本）。
- **属性**：

| 属性 | 含义 | 取值 | 默认 | 必填 | 备注 |
|---|---|---|---|---|---|
| `language` | 脚本语言 | `js` | js | 否 | 只支持 js |
| `timeoutMs` | 超时（毫秒） | >0 | 取 Global/Script | 否 | 超时置 Bad |
| `onError` | 脚本失败处置 | `markBad`（失败时点位质量置 Bad） | 取 Global/Script | 否 | 当前只支持 markBad |

#### Point/Alarm（报警）

- **是什么**：点位越限/数字量报警规则。触发→延时→激活→确认→清除三态事件（`docs/教程.md` 第 7 章）。
- **子标签**：无。
- **属性**：

| 属性 | 含义 | 取值 | 默认 | 必填 | 备注 |
|---|---|---|---|---|---|
| `id` | 报警标识（确认键，缺省 = `pointId#type`） | 字符串 | — | 否 | |
| `type` | 报警类型 | `high`/`highHigh`/`low`/`lowLow`/`digital` | high | 否 | 限值型需 `limit`；digital 在值为 true（或非零）时触发 |
| `limit` | 限值 | 有限数 | — | 限值型 ✅ | |
| `delayMs` | 越限持续多久才激活（毫秒） | ≥0 | 0 | 否 | |
| `deadband` | 回差（数值） | ≥0 | 0 | 否 | high 回落到 limit−deadband 之下才清 |
| `priority` | 等级 | 引用 AlarmClass@id | — | 否 | |
| `message` | 报警消息（i18n 可替换） | 字符串 | — | 否 | |
| `latch` | 锁存：激活后需确认才回 Normal | `true`/`false` | false | 否 | |
| `ackRequired` | 需确认 | `true`/`false` | false | 否 | |

限值关系：`lowLow < low < high < highHigh`（配错加载期报错）。

#### Point/Bits（位映射展开）

- **是什么**：把一个整字点展开成多个子点位（`整字点id.位名`），适合状态字/标志位。子点位**不挂报警、不做业务判断**。只挂单字整数点（bool/int16/uint16），与 bit/bitRange/Slices 互斥。
- **子标签**：`Bit`（`index` 0..15 + `name` + `text`）、`Field`（`from`/`to` + `name` + `text`，可带 `Map`）。
- **属性**：无（`<Bits>` 本身无属性）。

#### Point/Slices（非连续片段）

- **是什么**：点位由多个不连续的寄存器片段组成（如第 1 个和第 5 个字）。与 Bits/bit/bitRange 互斥。
- **子标签**：`Slice`。
- **`Slice` 属性**：`address`（片段起始地址，必填）、`length`（片段字长，>0）。

#### Point/DependsOn（显式依赖）

- **是什么**：计算点显式声明依赖（与表达式 `P('id')` 一起参与成环判定）。
- **子标签**：`PointRef`。
- **`PointRef` 属性**：`id`（被依赖点位 id）。

#### Point/Expression（计算点表达式）

- **是什么**：计算点（在 `Calculated` 下）的表达式文本，如 `P('a') * 2`。与 `<Script>` 二选一。
- **子标签**：无。

### PointSet/Calculated（计算点）

- **是什么**：计算点位集合——不占地址，由表达式或脚本从其它点位算出来。
- **约束**：每个计算点**恰有** `<Expression>` 或 `<Script>` 之一；不得挂 `<Bits>`/`<Alarm>`；依赖成环（含脚本 `P()`、`DependsOn`）加载期报错。

---

## 未实现段（写了按 unsupportedPolicy 告警，默认 warn）

以下段落当前**未实现**——配置里写了不会报错（默认策略），但没有对应行为，别期待功能：

- `Commands`（命令执行引擎）、`Storage`（历史存储）、`Ui` / `Users`（界面/角色——角色授权属宿主）、`History`（点位历史）、`Trace`（诊断跟踪）、`Point/History`、`Point/Tags`、`Device@Simulate`。

`Write@permission`、`Global/Retry@backoff` 等"写了进告警"的字段同理——框架只记账/忽略，**角色授权、命令执行等属宿主职责**。

---

## 校验规则（加载期一次报全，行为层总结）

配置加载分两阶段：解析 + 全量校验（**所有矛盾一次收集**）→ 零错误才产出配置。违反任何一条，加载直接失败并列出全部错误（每条带定位）。

| 类别 | 规则一句话 |
|---|---|
| 结构 | 未知元素 = 加载错误；未知属性按 `unsupportedPolicy`；`schemaVersion` 必须 3.x |
| 枚举/数值 | 所有枚举属性穷举校验（非法值报错不静默回落）；`bit` 0..15、`length`≥1、`port` 1..65535、`decimals` 0..15、`digits` 1..64、`intervalMs`≥50、超时≥1 等边界全查 |
| 数据区 | 只读区（input/discrete）不得声明可写；位区不得声明多字类型（32/64 位、string、bcd、datetime、length>1） |
| 块 | 块内点位不得覆盖 area/unitId/intervalMs/mode；地址须落在块窗口内且连续（空洞 ≤ ignoreGap） |
| 点位 | 同址同位重复声明报错；地址+有效长度不得越区容量；`once` 模式不得带 intervalMs |
| 计算点 | 恰有 Expression 或 Script 之一；依赖成环（表达式 + 脚本 P() + DependsOn，跨点表）报错 |
| 报警 | 类型穷举（high/highHigh/low/lowLow/digital）；限值型必须给有限 limit；限值关系 lowLow<low<high<highHigh；deadband/delayMs≥0 |
| 脚本 | language 只认 js；timeoutMs>0；onError 只认 markBad；正文非空；未知属性报错 |
| i18n | 配置里的 `${KEY}` 必须在默认语言资源中存在 |

---

## 附录 A：最小完整配置样例（schemaVersion 3.0）

```xml
<?xml version="1.0" encoding="utf-8"?>
<HostConfig schemaVersion="3.0" unsupportedPolicy="warn">
  <Global language="zh_CN" fallbackLanguage="en_US" nullText="--" swap="none">
    <Polling defaultIntervalMs="500" requestTimeoutMs="1000"/>
    <Reconnect enabled="true" delays="300,1000,3000,10000,30000" manualRetry="true" offlineQuality="offline"/>
    <Quality onCommError="bad" onCommErrorValue="null" staleAfterMs="5000"/>
  </Global>

  <AlarmClasses>
    <AlarmClass id="High" name="高" color="#FF9800"/>
  </AlarmClasses>

  <Transports>
    <!-- TCP；换成串口只改这一节：variant="rtu" portName="COM3" baudRate="9600" parity="none" stopBits="one" -->
    <Transport id="net" variant="tcp" host="127.0.0.1" port="1502"/>
  </Transports>

  <Devices>
    <Device id="IM-01" name="1号注塑机" transport="net" unitId="1" pointSet="IM_Points"/>
  </Devices>

  <PointSets>
    <PointSet id="IM_Points">
      <Defaults area="input" dataType="uint16" swap="none"/>
      <Points>
        <Point id="alarm" area="discrete" address="3" dataType="bool"/>
        <Point id="alarm.code" address="1"/>
        <Point id="barrel.temp" address="30" dataType="int16" intervalMs="500">
          <Scale factor="0.1"/>
          <Format decimals="1" suffix=" ℃"/>
        </Point>
        <Point id="set.temp" area="holding" address="2" dataType="int16" access="readwrite">
          <Scale factor="0.1"/>
          <Write verify="true"/>
        </Point>
      </Points>
      <Calculated>
        <Point id="temp.dev" dataType="float64">
          <Expression>P('barrel.temp') - P('set.temp')</Expression>
        </Point>
      </Calculated>
    </PointSet>
  </PointSets>
</HostConfig>
```

> 完整、可运行的示例从站与逐步教程见 [`docs/教程.md`](教程.md)（含 TCP⇄RTU 对照）。

---

## 附录 B：术语对照表

| 中文 | 英文 | 说明 |
|---|---|---|
| 配置 | HostConfig | 根元素；一份 XML 描述链路/设备/点位/报警 |
| 链路 | Transport | 一条连接（TCP 或串口） |
| 设备 | Device | 链路上的一个从站（unitId 区分） |
| 点表 | PointSet | 点位集合 |
| 点位 | Point | 一个采集项：地址+类型+解码 |
| 数据区 | area | coil / discrete / input / holding |
| 字序 | swap | none / byte / word / word_byte |
| 缩放 | Scale | 原始值→工程值换算 |
| 质量 | Quality | Good / Uncertain / Bad / Offline |
| 写管道 | Write Pipeline | 写操作全流程（校验→下发→回读） |
| 两级退避 | two-level backoff | 设备级 / 链路级退避 |
| 报警 | Alarm | 越限/数字量报警规则 |
