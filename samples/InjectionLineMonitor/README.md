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
| `ops_long.txt` | 长稳脚本示例（30 分钟周期性操作，与 `ops.txt` 同语法） |
| `i18n/ui_zh_CN.i18n`、`i18n/ui_en_US.i18n` | 文案与单位（`line.xml` 用 `${KEY}` 引用） |
| `Program.cs` | 启动/主循环/优雅退出 |
| `HostOptions.cs` | 命令行解析 |
| `LineSummary.cs` | 配置解析摘要（设备/点位/块/间隔与模式/Warnings） |
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

**控制台**：启动横幅 → 配置解析摘要（设备/点位/块/间隔与模式/Warnings）→ 每 `--every` 秒一屏关键点位表
（`!!` = 质量非 Good，`~` = 质量 Good 但超过 `staleAfterMs` 未刷新）→ 操作脚本行/报警三态/写审计/错误行
→ 退出汇总（事件计数、写四态直方图、质量分布、质量跃迁、报警流水、进程资源）。

**CSV**（框架不落盘，这是宿主职责）：

| 文件 | 内容 | 来源 |
|---|---|---|
| `values.csv` | 每个点位每次值/质量变化的快照 | `PointValueChangedEvent` |
| `alarms.csv` | 报警三态（Raised/Cleared/Acknowledged） | `AlarmRaised/Cleared/AcknowledgedEvent` |
| `writes.csv` | 写审计（四态、操作者） | `PointWrittenEvent` |
| `errors.csv` | 错误族（分类/错误码/结构化上下文） | `IErrorEvent`（基接口订阅） |
| `ops.csv` | 宿主动作结果（含 `WriteResult` 的 `VerifyMismatch` 与 `Readback`） | 门面返回值（`PointWrittenEvent` 同样携带这些字段，此处记门面四态完整结果） |
| `resources.csv` | 每 30s 的线程数/句柄数/GC 后托管堆 | `Process` + `GC.GetTotalMemory(true)` |

## 5. 操作脚本语法

```
<时间> <命令> <参数...>          # 时间从宿主启动算，s=秒（缺省）、m=分；# 开头是注释

write <设备>/<点位> <值>         # 走完整写管道：可写性 → 数据区 → 范围 → 类型容量 → 编码 → 下发 →（verify）回读
write <点位> <值>                # 点位 id 全库唯一时可省设备前缀（脚本里更短）
pulse <设备>/<点位>              # 点动：写 true，框架按 pulseMs 自动写回 false
ack   <设备>/<点位> [报警id]      # 报警确认；省略报警 id 则确认该点位配置的全部报警
show  <设备>/<点位>              # 立即打印一次值/质量/原因/时间戳
note  <文本>                     # 日志分隔说明
```

写值按点位 `dataType` 转换（整型点允许写 `28.0` 这种小数文本，缩放由框架的 `Scale` 处理）。
**写请求恒不重试**：写超时即 `Indeterminate`（绝不自动重发，由框架保证），与 `Retry@count` 无关，宿主无需自设重试为 0。
值、`Write min/max`、报警 `limit` **一律是工程量**：`env01.acSetTemp` 是 `int16 + factor 0.1`，
要写 28.0 ℃ 就写 `28.0`，写 `280` 会被范围校验拒掉。

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
| `run/run1` | `ops.txt`，160s，无故障 | 值事件 6213 全 Good；报警 2 激活/2 清除/2 确认；写 13 次 = Succeeded 10 / **Failed 1** / Rejected 2；错误事件 0；线程 18→17、托管堆 +0.10 MB（以 `run/run1/csv/resources.csv` 为准） |
| `run/run2` | 断线演练：`refuse 3s` + `silent 5s`（2502 整口） | 质量 Good→Bad→Good 跃迁 58 条；错误事件 3（2 超时 + 1 链路）；宿主不崩不卡，恢复后全部 Good |
| `run/run3` | `silent 10s` 期间写设定值 | `WriteResult = Indeterminate`（7.1s 后返回，框架不猜、不重写）；恢复后 Good |
| `run/run4` | ①`refuse 8s` 期间写 ②停桩/起桩 | ①写超时后回读定论、确认已生效 → `Succeeded`（4.6s；写请求**恒不重发**）②写 `Indeterminate`，停桩期间质量滞后变 Bad，起桩后 2s 内回 Good |

`run/run1/breaker_events.log` 是断路器侧事件提取（listen/offline/online/fault_set/conn_reject，共 80 条），
用来与宿主侧时间线对齐；断路器原始 JSONL 逐帧日志很大（8.9MB/160s），已删除。

## 8. 已知限制（宿主须知）

框架在这些地方有意不做，宿主需要自己补或自己把关；以下按当前实现列出的真实边界：

1. **门面不提供点位元数据枚举**：没有「枚举设备下所有点、看类型/区/地址/间隔与模式/单位/报警、按点名反查设备」的只读接口。
   宿主可自行解析配置 `line.xml`，或直接引用 `SuperSampler.Core.Runtime.PointRegistry` 等 Core 类型
   ——这是框架缺口、样例被迫为之（本样例的 `PointCatalog` 就是这么做的）。
2. **无设备/链路在线状态查询门面**：断线时质量是逐窗口变 `Bad` 的（串行重试下滞后明显），没有「整机离线」的直接查询。
   宿主用 `GetValueAge`（距上次成功采集的时长）配合质量判断，或订阅 `IErrorEvent` 自行推断。
3. **`Write@permission` 未强制**：配置里可声明 `Write@permission`，但框架只解析不校验（角色授权未实现，
   `ActingUser` 只用于审计记账）；是否允许写入由宿主自行把关。
4. **无报警查询接口**：`AcknowledgeAlarmAsync` 只回 `Acknowledged/NotPending`，看不到 `AckPending`、
   也拿不到「当前激活报警」清单；报警列表/确认状态/「已恢复未确认」视图要靠订阅报警事件自行维护
   （本样例 `EventJournal.ActiveAlarms` 就是这么来的）。
5. **点动 `pulseMs` 是同步等待**：写 `true` 后框架在同一写调用里等待 `pulseMs` 再写回 `false`，期间该设备的
   写通道被占住；宿主若在 UI 线程同步等待会卡界面。
6. **`latch` 的语义与直觉不同**：条件回落照样发 `AlarmCleared`、`Active` 照样清，`latch/ackRequired` 只是
   「未确认前不得重触发」的门控；按「锁存=值还在但界面锁住」做界面会对不上。
7. **陈旧判定要自己做**：`Quality@staleAfterMs` 只被加载器读取，框架不做陈旧标记（`GetValueDetail` 只给时间戳）；
   本样例 `Dashboard` 自己按时间戳算，给 Good 但过期的点标 `~`。
8. **计算点不进轮询、不发 `PointValueChangedEvent`**：计算点只在 `GetValue/GetValueDetail` 被调用时求值并回写缓存；
   要显示/归档只能宿主定时读（本样例靠 2s 一屏的 `GetValueDetail` 顺带完成）。
9. **`.NET Core` / `.NET 6` 宿主未验证**：样例与框架按 `net46` 构建与实测，其它目标框架的宿主未验证。

一点使用注意：门面 `GetValue` 对未知 id 抛 `KeyNotFoundException`（设计如此，配置错误尽早暴露），
宿主脚本里点位名写错时要把异常转成可读提示；显示写回读值要用 `WriteResult.Readback` 本身，
不要用 `GetValue`（它读轮询缓存，长间隔的点上可能是几秒前的旧值）。

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
