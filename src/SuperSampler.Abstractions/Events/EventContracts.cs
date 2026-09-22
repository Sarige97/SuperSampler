using System;
using System.Collections.Generic;

namespace SuperSampler.Abstractions.Events;

/// <summary>事件类别。宿主按类别订阅；每个类别下是一组类型化事件。</summary>
public enum EventCategory
{
    /// <summary>启动、停止、配置加载、热重载。</summary>
    Lifecycle = 0,

    /// <summary>链路打开、关闭、重连成功、重连失败。</summary>
    Connection = 1,

    /// <summary>设备上线、离线、超时、统计快照。</summary>
    Device = 2,

    /// <summary>值变化、质量变化。高频，默认关闭。</summary>
    Value = 3,

    /// <summary>写入前、写入后、被拒、校验失败。</summary>
    Write = 4,

    /// <summary>命令开始、完成、失败、回滚。</summary>
    Command = 5,

    /// <summary>报警激活、清除、确认、升级。不可丢。</summary>
    Alarm = 6,

    /// <summary>审计：登录、登出、配置变更。不可丢。</summary>
    Audit = 7,

    /// <summary>原始收发帧。高频，默认关闭，抓包时开。</summary>
    Comm = 8,

    /// <summary>时延、队列深度、失败率快照。</summary>
    Performance = 9,
}

/// <summary>事件级别。</summary>
public enum EventLevel
{
    Trace = 0,
    Debug = 1,
    Info = 2,
    Warn = 3,
    Error = 4,
}

/// <summary>
/// 投递模式。Inline 在发出方线程同步执行（契约：订阅者必须极快，如计数）；
/// Queued 进入该订阅者专属的有界队列，由泵线程异步投递，慢消费者不拖累采集线程。
/// <para>
/// Inline **没有队列**：<c>Subscribe</c> 的 <c>queueCapacity</c>/<c>overflow</c> 对它无效（被忽略），
/// 其 <see cref="ISubscription.Dropped"/> 恒为 0——同步直投没有「丢掉」这一状态（订阅者抛异常走 Faults）。
/// </para>
/// </summary>
public enum DeliveryMode
{
    Inline = 0,
    Queued = 1,
}

/// <summary>
/// 队列满时的溢出策略。丢弃必须可见（见 ISubscription.Dropped），绝不允许静默。
/// Wait 会阻塞发出方，只允许配给审计、报警这类低频且不可丢的类别。
/// </summary>
public enum OverflowPolicy
{
    /// <summary>挤掉队列里最旧的一条，放入新事件。</summary>
    DropOldest = 0,

    /// <summary>丢弃正在入队的新事件。</summary>
    DropNewest = 1,

    /// <summary>阻塞发出方直到有空位（永不丢弃）。仅限低频不可丢类别。</summary>
    Wait = 2,
}

/// <summary>
/// 所有事件的公共标记：类别与级别。
/// 序列号与时间戳不放在事件本体上：事件保持不可变，由总线在信封上统一分配（见 IEventEnvelope）。
/// </summary>
public interface IEvent
{
    /// <summary>事件类别。</summary>
    EventCategory Category { get; }

    /// <summary>事件级别。</summary>
    EventLevel Level { get; }
}

/// <summary>
/// 事件信封：总线在投递时把全局单调序号与时间戳挂在信封上，事件本体保持不可变。
/// TEvent 用 out 修饰以支持协变：EventEnvelope&lt;TimeoutError&gt; 可当作 IEventEnvelope&lt;IErrorEvent&gt;
/// 传递，这正是「按基接口订阅」得以实现的基础。
/// </summary>
public interface IEventEnvelope<out TEvent> where TEvent : IEvent
{
    /// <summary>全局单调递增序号。宿主据此检测事件丢失。</summary>
    long Seq { get; }

    /// <summary>总线分配的投递时间（UTC）。</summary>
    DateTimeOffset Time { get; }

    /// <summary>事件本体。</summary>
    TEvent Body { get; }
}

/// <summary><see cref="IEventEnvelope{TEvent}"/> 的默认实现。</summary>
public sealed class EventEnvelope<TEvent> : IEventEnvelope<TEvent> where TEvent : IEvent
{
    public EventEnvelope(long seq, DateTimeOffset time, TEvent body)
    {
        Seq = seq;
        Time = time;
        Body = body;
    }

    /// <summary>全局单调递增序号。</summary>
    public long Seq { get; }

    /// <summary>总线分配的投递时间（UTC）。</summary>
    public DateTimeOffset Time { get; }

    /// <summary>事件本体。</summary>
    public TEvent Body { get; }

    public override string ToString() => $"#{Seq} {Time:HH:mm:ss.fff} {typeof(TEvent).Name} {Body}";
}

/// <summary>一次订阅的句柄。Dispose 即退订；同时暴露该订阅的投递、丢弃与故障计数。</summary>
public interface ISubscription : IDisposable
{
    /// <summary>已成功投递给本订阅的事件数。</summary>
    long Delivered { get; }

    /// <summary>
    /// 丢弃计数：**两类都计入**（口径见 <c>InProcessEventBus</c> 类注释与 docs/03 第 4.4 节）——
    /// ① 队列满时按溢出策略丢弃的事件；
    /// ② **退订（<see cref="IDisposable.Dispose"/>）或总线停止时，队列里尚未投递的条目**。
    /// 丢弃必须可见，绝不能静默：静默丢弃会让宿主的审计/报警漏记而不自知。
    /// <see cref="DeliveryMode.Inline"/> 订阅没有队列，本项恒为 0。
    /// </summary>
    long Dropped { get; }

    /// <summary>订阅者处理时抛异常的次数。订阅者的异常不会影响框架。</summary>
    long Faults { get; }

    /// <summary>是否仍在接收事件。</summary>
    bool IsActive { get; }
}

/// <summary>事件总线整体计数。宿主据此发现「事件丢失」与「订阅者异常」。</summary>
public interface IEventBusMetrics
{
    /// <summary>累计发出的事件数。</summary>
    long TotalEmitted { get; }

    /// <summary>累计丢弃的事件数（各订阅合计）：队列满丢弃 + 退订/停止时未投递的条目，口径同 <see cref="ISubscription.Dropped"/>。</summary>
    long TotalDropped { get; }

    /// <summary>累计订阅者处理异常次数。</summary>
    long TotalFaults { get; }

    /// <summary>当前活跃订阅数。</summary>
    int ActiveSubscriptions { get; }
}

/// <summary>
/// 进程内事件总线。框架只 Emit；宿主 Subscribe 自己关心的类型或基接口。
/// 关键契约（docs/03 第 4 节）：
/// 1) 订阅者的异常被总线吞掉并计数，绝不影响框架；
/// 2) 事件按发出顺序进入每个订阅者的 FIFO 队列，单订阅内严格保序；
/// 3) 队列有界，溢出策略显式，丢弃计数可见；
/// 4) Seq 全局单调，宿主据此检测事件丢失。
/// </summary>
public interface IEventBus
{
    /// <summary>未显式指定容量时使用的默认有界队列容量。</summary>
    int DefaultQueueCapacity { get; }

    /// <summary>
    /// 订阅单个事件（同步处理器）。TEvent 可以是具体类型，也可以是基接口（如 IErrorEvent）。
    /// <paramref name="queueCapacity"/> 与 <paramref name="overflow"/> 只对 <see cref="DeliveryMode.Queued"/> 生效：
    /// <see cref="DeliveryMode.Inline"/> 无队列，两者被**忽略**（也不校验容量，传 0 不抛）。
    /// </summary>
    ISubscription Subscribe<TEvent>(
        Action<IEventEnvelope<TEvent>> handler,
        DeliveryMode mode = DeliveryMode.Queued,
        OverflowPolicy overflow = OverflowPolicy.DropOldest,
        int? queueCapacity = null) where TEvent : IEvent;

    /// <summary>
    /// 批量订阅：攒满 maxBatchSize 或距批首事件超过 maxBatchDelay 就回调一次。
    /// 高频类别（Value / Comm）用这个，避免每事件一次回调。
    /// 攒批依赖队列，故 <see cref="DeliveryMode.Inline"/> 无意义：传 Inline 一律按 Queued 处理。
    /// 交给 handler 的列表是**每批一份的独立快照**（不是内部复用缓冲），
    /// 可安全跨回调持有 / 延迟消费（如先存下、再切到 UI 线程枚举）。
    /// </summary>
    ISubscription SubscribeBatch<TEvent>(
        Action<IReadOnlyList<IEventEnvelope<TEvent>>> handler,
        int maxBatchSize,
        TimeSpan maxBatchDelay,
        DeliveryMode mode = DeliveryMode.Queued,
        OverflowPolicy overflow = OverflowPolicy.DropOldest,
        int? queueCapacity = null) where TEvent : IEvent;

    /// <summary>
    /// 发出事件。Seq 与时间戳由总线统一分配。
    /// 本方法不会抛出订阅者的异常；除非显式选择 Wait 溢出策略，否则不因订阅者缓慢而阻塞。
    /// </summary>
    void Emit<TEvent>(TEvent body) where TEvent : IEvent;

    /// <summary>总线整体计数。</summary>
    IEventBusMetrics Metrics { get; }
}
