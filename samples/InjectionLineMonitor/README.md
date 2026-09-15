# InjectionLineMonitor —— 注塑产线监视线（SuperSampler 宿主样例）

用 SuperSampler 框架写的**真实感 Console 上位机**：一台注塑机 + 一条网关产线（模温机/干燥机/机械手）
+ 总电表/车间环境 + 一台写保护从站，全部数据来自复杂工业桩（`_simulator_design/tools/complex_sim.py`），
链路中间串了可编程断路器（`breaker.py`），可以随时注入断电/黑洞/丢包/延迟。

它的目的是把「宿主该干的活」跑一遍：配置加载与摘要、事件订阅与落盘、轮询显示、操作脚本、
断线韧性、退出汇总与进程资源——**框架负责采集与解析，宿主负责显示、审计、落盘、操作时序**。

- 目标框架：`net46`（`dotnet build -c Debug`，0 警告 0 错误）
- 唯一项目引用：`..\..\src\SuperSampler.Core\SuperSampler.Core.csproj`
- 点表真源：`_simulator_design/tools/complex_points.json`；设备语义见 `_simulator_design/tools/复杂桩说明.md`

---

## 1. 文件清单

| 文件 | 作用 |
|---|---|
| `InjectionLineMonitor.csproj` | 宿主工程（Exe / net46） |
| `line.xml` | 宿主配置：3 条链路 / 5 台设备 / 145 点位 / 7 个块 / 2 条报警 / 2 个计算点 |
| `breaker_map.json` | 断路器端口映射（= `complex_ports.json` 的公共口改到 2502-2505，避开其它测试用的 1502/1503） |
| `ops.txt` | 操作脚本示例（非脚本模式时宿主会自造一条默认序列） |
| `i18n/ui_zh_CN.i18n`、`i18n/ui_en_US.i18n` | 文案与单位（`line.xml` 用 `${KEY}` 引用） |
| `Program.cs` | 启动/主循环/优雅退出 |
| `HostOptions.cs` | 命令行解析 |
| `LineSummary.cs` | 配置解析摘要（设备/点位/扫描组/Warnings） |
| `EventJournal.cs` | 事件订阅、计数、CSV 落盘、质量跃迁记录 |
| `OpsRunner.cs` | 操作脚本解析与执行（write/pulse/ack/show/note） |
| `Dashboard.cs` | 每 N 秒一屏关键点位表（值+质量+坏值原因+数据年龄） |
| `RunReport.cs` | 退出汇总 + 每 30s 进程资源采样 |
| `CsvSink.cs` | CSV 落盘器（6 个文件） |
| `ConsoleOut.cs` | 控制台输出闸门（多线程不打架） |
| `run/` | 实跑留档（host.log、csv/、断路器控制文件与回执），可整目录删除 |

配置规模（宿主启动摘要实测）：链路 3 / 设备 5 / 点表 5 / 点位 145（可写 35 / 计算点 2 / 报警 2）/ 块 7。
点位类型覆盖 int16、uint16、uint32、int32、float32、float64、string、bcd、datetime、bool、`bit`、`bitRange`、
双点映射（raw→工程）与钳位；网关下多从站用**点位级 `unitId`** 表达（见 `LineB_Points`）。

## 2. 前置：起桩与断路器

```bash
# ① 复杂桩镜像（内部口 16002-16005）
cd D:/IT/SuperModbus/_simulator_design/tools
python complex_sim.py                                   # 或加 --log xxx 记录请求

# ② 断路器（公共口 2502-2505 → 内部口）：控制文件是故障注入入口
python breaker.py --map D:/IT/SuperModbus/SuperSampler/samples/InjectionLineMonitor/breaker_map.json \
                  --control D:/IT/SuperModbus/SuperSampler/samples/InjectionLineMonitor/run/ctl.json \
                  --log     D:/IT/SuperModbus/SuperSampler/samples/InjectionLineMonitor/run/breaker.log \
                  --no-console
```

> `breaker.py` 没有 `--public-base` 参数：公共口来自映射文件，所以本目录带了 `breaker_map.json`。
> 不想起断路器时，把 `line.xml` 里三条 `Transport@port` 改成 16002/16003/16004 即可直连镜像。

## 3. 跑宿主

```bash
cd D:/IT/SuperModbus/SuperSampler/samples/InjectionLineMonitor
./bin/Debug/net46/InjectionLineMonitor.exe \
    --config line.xml --script ops.txt --duration 150s --every 2 --csv run/run1/csv
```

| 参数 | 缺省 | 说明 |
|---|---|---|
| `--config <path>` | `line.xml` | 宿主配置；i18n 相对路径按配置文件所在目录解析 |
| `--duration 90s\|5m` | `60s` | 运行时长（支持 `s`/`m`/`h`，纯数字按秒）；到点或 Ctrl+C 优雅退出 |
| `--script <file>` | 无（内置默认序列） | 操作脚本；缺省序列 = 找一个带 `pulseMs` 的点做点动、找一个带 min/max 的可写点写中值、找一个带报警的点做确认 |
| `--csv <dir>` | `csv` | 事件落盘目录（不存在会创建） |
| `--every <秒>` | `2` | 点位表刷新周期 |
| `--user <名:角色>` | `op01:Operator` | 写值与报警确认带的 `ActingUser` |

## 4. 控制台输出与落盘文件

**控制台**：启动横幅 → 配置解析摘要（设备/点位/扫描组/Warnings）→ 每 `--every` 秒一屏关键点位表
（`!!` = 质量非 Good，`~` = 质量 Good 但超过 `staleAfterMs` 未刷新）→ 操作脚本行/报警三态/写审计/错误行
→ 退出汇总（事件计数、写四态直方图、质量分布、质量跃迁、报警流水、进程资源）。

**CSV**（框架不落盘，这是宿主职责）：

| 文件 | 内容 | 来源 |
|---|---|---|
| `values.csv` | 每个点位每次值/质量变化的快照 | `PointValueChangedEvent` |
| `alarms.csv` | 报警三态（Raised/Cleared/Acknowledged） | `AlarmRaised/Cleared/AcknowledgedEvent` |
| `writes.csv` | 写审计（四态、操作者） | `PointWrittenEvent` |
| `errors.csv` | 错误族（分类/错误码/结构化上下文） | `IErrorEvent`（基接口订阅） |
| `ops.csv` | 宿主动作结果（含 `WriteResult` 的 `VerifyMismatch` 与 `Readback`） | 门面返回值（事件里没有） |
| `resources.csv` | 每 30s 的线程数/句柄数/GC 后托管堆 | `Process` + `GC.GetTotalMemory(true)` |

## 5. 操作脚本语法

```
<时间> <命令> <参数...>          # 时间从宿主启动算，s=秒（缺省）、m=分；# 开头是注释

write <设备>/<点位> <值>         # 走完整写管道：可写性 → 范围 → 审计 → 下发 →（verify）回读
write <点位> <值>                # 点位 id 全库唯一时可省设备前缀（脚本里更短）
pulse <设备>/<点位>              # 点动：写 true，框架按 pulseMs 自动写回 false
ack   <设备>/<点位> [报警id]      # 报警确认；省略报警 id 则确认该点位配置的全部报警
show  <设备>/<点位>              # 立即打印一次值/质量/原因/时间戳
note  <文本>                     # 日志分隔说明
```

写值按点位 `dataType` 转换（整型点允许写 `28.0` 这种小数文本，缩放由框架的 `Scale` 处理）。
值、`Write min/max`、报警 `limit` **一律是工程量**：`env01.acSetTemp` 是 `int16 + factor 0.1`，
要写 28.0 ℃ 就写 `28.0`，写 `280` 会被范围校验拒掉（本样例第一版就写错了，见 §8）。

## 6. 样例剧情（`ops.txt` + 桩的真实行为）

| 时间 | 动作 | 期望看点 |
|---|---|---|
| 10s | 写 `im01.cavitySetTemp=235` | `Succeeded` + `verify` 回读 235 ℃ |
| 15s | `pulse im01.moldCloseJog` | 写 true，804ms 后框架自动写回 false |
| 20s / 25s | 越上限写设定值 / 写只读点 | 两次 `Rejected`（未发通讯，`aboveMax` / `notWritable`） |
| 30s | 空调设定 26→28 ℃ | 车间温度爬升 |
| ~40s | （报警）`env.highTemp` | high 报警：limit 27.0 / delay 2s / deadband 0.3 / latch / ackRequired |
| 45s | `ack env01.roomTemp env.highTemp` | `AlarmAcknowledgedEvent` |
| 55s / 60s | 写保护从站单字写 / 双字写 | `Succeeded`（FC06）/ `Failed`（设备回 0x02，`MODBUS.EXCEPTION.02`） |
| 70s / 80s / 85s | 急停置位 / 复位 / 确认 | digital 报警的 RAISED → CLEARED → ACKED 三态 |
| 90s | 空调设定回 24 ℃ | ~93s 高温报警 CLEARED（回落出死区） |
| 110s | 写原料批次号（ASCII 字符串点） | 字符串写回读 |

## 7. 实跑留档（`run/`）

| 目录 | 场景 | 结论摘要 |
|---|---|---|
| `run/run1` | `ops.txt`，160s，无故障 | 值事件 6213 全 Good；报警 2 激活/2 清除/2 确认；写 13 次 = Succeeded 10 / **Failed 1** / Rejected 2；错误事件 0；线程 17→16、托管堆 +0.12 MB |
| `run/run2` | 断线演练：`refuse 3s` + `silent 5s`（2502 整口） | 质量 Good→Bad→Good 跃迁 58 条；错误事件 3（2 超时 + 1 链路）；宿主不崩不卡，恢复后全部 Good |
| `run/run3` | `silent 10s` 期间写设定值 | `WriteResult = Indeterminate`（7.1s 后返回，框架不猜、不重写）；恢复后 Good |
| `run/run4` | ①`refuse 8s` 期间写 ②停桩/起桩 | ①写最终 `Succeeded`（4.6s，跨重连重试）②写 `Indeterminate`，停桩期间质量滞后变 Bad，起桩后 2s 内回 Good |

`run/run1/breaker_events.log` 是断路器侧事件提取（listen/offline/online/fault_set/conn_reject，共 80 条），
用来与宿主侧时间线对齐；断路器原始 JSONL 逐帧日志很大（8.9MB/160s），已删除。

## 8. 开发过程中遇到的框架摩擦点（本次交付重点）

### 8.1 配置与文档/实现不一致（最费时间的一处）

**`Block@swap` 缺省 `word`，并覆盖 `PointSet/Defaults@swap`。**
`Config/配置字段说明.md`（§「swap 兜底链」）写的是 `Point > PointSet/Defaults > Block（块内） > Device > Global`，
但 `SamplerConfigLoader.Points.cs` 在块内点位未显式写 `swap` 时直接 `point.Swap = block.Swap`（`Block@swap` 缺省 `word`）
并标记为「已显式声明」，Defaults 被跳过。实测后果：同一份配置里块内 `uint32/float32/float64` 全按 CDAB 解错
（累计电能读成 15198453.76 kWh、float64 读成 `-7.1e-197`），而块外同类型点位（int32/float32/float64/datetime）全部正确——
因为块内 `datetime(plc6)` 恰好逐字段解码、不受 swap 影响，掩盖了问题。**规避**：本样例给每个 `<Block>` 显式写 `swap="none"`。
建议：或在加载器里让 `Defaults` 优先于 `Block`，或把 `Block@swap` 的缺省改成「继承」而不是 `word`，并同步文档。

### 8.2 必须宿主自己补的（框架有意不做，或门面没给）

1. **点位元数据没有门面**：门面只有 `GetValue/GetValueDetail/SetValueAsync/AcknowledgeAlarmAsync`。
   「枚举设备下所有点、看类型/区/地址/扫描组/单位/报警、按点名反查设备」全都得直接引用
   `SuperSampler.Core.Runtime.PointRegistry`、`SamplerConfiguration` 这些 Core 内部类型（本样例的 `PointCatalog`）。
   建议加一个只读元数据接口（如 `IPointCatalog`），否则每个宿主都会自己拼一套。
2. **没有「设备/链路在线」状态**：断线时质量是**逐窗口**变 Bad 的，串行重试下滞后明显——
   实测停桩后第一台设备的第一个块 21s 就报 `LinkError`，而同一设备的温度块 14s 后才变 Bad，其它设备更晚。
   宿主想做「整机离线」横幅只能订阅 `IErrorEvent` 自己推断。建议门面或事件补设备级在线状态/事件。
3. **写审计事件不带回读**：`PointWrittenEvent` 只有 `value/user/outcome/message`，
   `VerifyMismatch`、`Readback`、错误码都在 `SetValueAsync` 的返回值里，且事件在返回前就已发出——
   宿主想把「回读值」留在审计里只能自己再写一份（本样例落在 `ops.csv`）。
4. **报警没有查询接口**：`AcknowledgeAlarmAsync` 只回 `Acknowledged/NotPending`，
   既看不到 `AckPending`，也拿不到「当前激活报警」清单。报警列表、确认状态、「已恢复未确认」视图
   都得宿主自己按事件维护（本样例 `EventJournal.ActiveAlarms` 就是这么来的）。
5. **`latch` 的语义与直觉不同**：实现里条件回落照样发 `AlarmCleared`、`Active` 照样清，
   `latch/ackRequired` 只是「未确认前不得重触发」的门控。实测：急停复位后 1s 内报警位再次置位，
   因未确认而没有新的 `RAISED`；确认后 0.1s 立刻又 `RAISED`。按「锁存=值还在但界面锁住」去做界面会对不上。
6. **陈旧判定要自己做**：`Quality@staleAfterMs` 只被加载器读取，框架不做陈旧标记（`GetValueDetail` 只给时间戳）。
   本样例的 `Dashboard` 自己按时间戳算，并给 Good 但过期的点标 `~`。
7. **没有「格式化任意 PointValue」的公开 API**：`WriteResult.Readback` 是工程值对象，
   宿主想显示成和 `GetValue` 一样的字符串只能自己拼（本样例打印 `<值> <单位>[质量]`）。
   顺带一个坑：**不能用 `GetValue` 显示回读值**——它读的是轮询缓存，慢扫描组上可能是几秒前的旧值
   （本样例第一版就把写入 235 显示成缓存的 220.0），必须用 `Readback` 本身。
8. **计算点不发事件**：计算点只在 `GetValue/GetValueDetail` 被调用时求值并回写缓存，不进轮询、不发
   `PointValueChangedEvent`（实测 `values.csv` 里没有 `im01.moldTempDev` / `env01.plantLoad` 的行）。
   要显示/归档只能宿主定时读——本样例靠 2s 一屏的 `GetValueDetail` 顺带完成。

### 8.3 接口不顺手 / 行为需要显式确认

1. **`pulseMs` 是写管道里的同步 `Thread.Sleep`**：实测一次点动写调用耗时 804ms，期间该设备的写通道被占住；
   自动写回 false 的那次写入没有审计事件、失败也不上报。宿主若在 UI 线程同步等待会卡界面。
2. **写的重发取决于 `Retry@count`**：写路径把设备级重试用在写请求上。实测 `count=1` 时，
   8 秒连接拒绝窗口内发出的写最终仍 `Succeeded`（耗时 4.6s，跨重连重试）；框架自己的
   `C2_WriteTimeout_IsIndeterminate_AndWriteIsSentExactlyOnce` 用的是 `count=0` 才拿到 `Indeterminate`。
   对「重复执行有副作用」的写命令（点动、置位、下发配方），宿主必须显式把重试设 0，
   否则「写超时绝不自动重写」这条承诺在配置层就被削弱了。建议点位级给一个「此写不得重发」的开关。
3. **BCD/datetime 只能读不能写**：`PointCodec.Encode` 没有这两类的分支——写 BCD 点会把数字当普通整数落字
   （读回来语义就变了），写 datetime 会走 `Convert.ToDouble(DateTime)` 抛异常变成 `Rejected`。
   本样例的 `dry01.batchBcd` 因此只读不写；建议要么补编码，要么在加载期对 `access=readwrite` 的 bcd/datetime 点位告警。
4. **`Scale@mode="linear"` 会进 Warnings**：声明唯一受支持的值也被告警为「尚未实现」，
   `AlarmClass@name/@color` 同理。Warnings 应被视为「提示」而非「缺陷」，宿主界面要能区分
   （本样例的启动摘要里把 Warnings 单独列一行）。建议加载器只在取值**不受支持**时才告警。
5. **门面 `GetValue` 对未知 id 抛 `KeyNotFoundException`**（设计如此，现场很好用），
   但宿主脚本里点位名写错时要注意把它转成可读提示——本样例在 `PointCatalog.Resolve` 里统一包成中文错误。

### 8.4 做得好的地方（下次照抄）

- **质量三元组（值/质量/时间戳/原因）**：`GetValueDetail` 只读缓存、可高频调用，显示层写起来很省事；
  非 Good 时 `GetValue` 返回 `nullText`、`GetValueDetail` 保留原因，两者分工清楚。
- **按基接口订阅错误**：`Subscribe<IErrorEvent>` 一条订阅就收齐所有错误族（实测 3 条全收到），
  新增错误类型不会漏统计。
- **`VerifyMismatch` 语义**：写成功但设备把值改了（钳位）时返回 `Succeeded + VerifyMismatch=true`，
  不误触发宿主自动重试，宿主只需提示——这个设计在真实设备上非常对。
- **写四态**：`Rejected` 明确表示「未发通讯」，实测越上限/只读点两次都 0ms 返回，没有多余报文。
- **事件总线**：每订阅者独立队列 + `Dropped/Faults` 可见，本样例 6 条订阅、6232 条事件、0 丢弃 0 异常。

---

## 9. 复现断线演练

```bash
S=/d/IT/SuperModbus/SuperSampler/samples/InjectionLineMonitor
T0=$(date +%s)
( cd "$S" && ./bin/Debug/net46/InjectionLineMonitor.exe --config line.xml \
    --script run/run2/drill_ops.txt --duration 90s --every 2 --csv run/run2/csv > run/run2/host.log 2>&1 ) &
bash "$S/run/run2/inject.sh" "$T0"      # 写断路器控制文件：T+20s refuse 3s、T+45s silent 5s
wait
```

`inject.sh` 只是把一行 JSON 写进 `run/ctl.json`（断路器每 0.2s 轮询该文件，回执写在 `run/ctl.ack.json`）：

```json
{"seq":101,"commands":[{"action":"offline","port":2502,"mode":"refuse","seconds":3,"reason":"drill-refuse-3s"}]}
```

可用的 `action`：`offline/refuse/silent/halfopen`（可带 `seconds` 自动恢复）、`online`、`clear`、
`drop`、`delay`、`badcrc`（仅 rtuOverTcp 口）。
