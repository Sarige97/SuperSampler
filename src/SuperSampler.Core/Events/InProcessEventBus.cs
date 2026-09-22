using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using SuperSampler.Abstractions.Events;

namespace SuperSampler.Core.Events;

/// <summary>
/// 单进程事件总线实现（net46，零外部依赖）。
/// 结构：每个 Queued 订阅者一条专属有界 FIFO 队列 + 一条专属后台泵线程。
/// 「一个订阅者一条队列」同时满足隔离（慢消费者不拖累别人）与单订阅内严格保序（FIFO），
/// 因此 docs/03 第 4.1 节的 A/B 双通道方案在此简化为统一实现。
/// 代价是每个 Queued 订阅一条后台线程；订阅数通常为个位数到两位数，可接受，
/// 将来订阅规模显著增大时再优化为共享泵。
/// <para>
/// 丢弃计数的口径（<see cref="ISubscription.Dropped"/> / <see cref="IEventBusMetrics.TotalDropped"/>）：
/// 除「队列满按溢出策略丢弃」外，**退订/停止时队列里未投递的条目同样计入**——
/// docs/03 第 4.4 条要求丢弃必须可见，静默丢弃会让宿主的审计漏记。
/// </para>
/// </summary>
public sealed class InProcessEventBus : IEventBus, IDisposable
{
    private const int DefaultCapacity = 4096;

    private readonly object _gate = new();
    private readonly List<SubscriptionBase> _subscriptions = new();
    private readonly Counters _counters = new();
    private SubscriptionBase[] _snapshot = Array.Empty<SubscriptionBase>();
    private long _seq;
    private int _disposedFlag;

    /// <summary>订阅者处理抛异常时触发（总线已吞掉异常并计数）。观察者自身的异常同样被吞掉。</summary>
    public event Action<Exception>? HandlerFaulted;

    /// <summary>默认队列容量。</summary>
    public int DefaultQueueCapacity => DefaultCapacity;

    /// <summary>总线整体计数。</summary>
    public IEventBusMetrics Metrics => _counters;

    /// <inheritdoc />
    public ISubscription Subscribe<TEvent>(
        Action<IEventEnvelope<TEvent>> handler,
        DeliveryMode mode = DeliveryMode.Queued,
        OverflowPolicy overflow = OverflowPolicy.DropOldest,
        int? queueCapacity = null) where TEvent : IEvent
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));

        SubscriptionBase sub = mode == DeliveryMode.Inline
            ? new InlineSubscription<TEvent>(typeof(TEvent), handler, this)
            : new SingleSubscription<TEvent>(typeof(TEvent), handler, ResolveCapacity(queueCapacity), overflow, this);

        Register(sub);
        return sub;
    }

    /// <inheritdoc />
    public ISubscription SubscribeBatch<TEvent>(
        Action<IReadOnlyList<IEventEnvelope<TEvent>>> handler,
        int maxBatchSize,
        TimeSpan maxBatchDelay,
        DeliveryMode mode = DeliveryMode.Queued,
        OverflowPolicy overflow = OverflowPolicy.DropOldest,
        int? queueCapacity = null) where TEvent : IEvent
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));
        if (maxBatchSize < 1) throw new ArgumentOutOfRangeException(nameof(maxBatchSize));

        // 批量依赖攒批队列，Inline 无意义：传 Inline 一律按 Queued 处理（见接口注释）。
        var sub = new BatchSubscription<TEvent>(typeof(TEvent), handler, maxBatchSize, maxBatchDelay,
            ResolveCapacity(queueCapacity), overflow, this);

        Register(sub);
        return sub;
    }

    /// <inheritdoc />
    public void Emit<TEvent>(TEvent body) where TEvent : IEvent
    {
        if (body == null) throw new ArgumentNullException(nameof(body));

        // _snapshot 是不可变数组，注册/退订时整体替换；引用读取原子，无需加锁。
        var snapshot = Volatile.Read(ref _snapshot);
        if (Volatile.Read(ref _disposedFlag) != 0) return; // 总线已停止：静默忽略，不反向影响发出方

        var seq = Interlocked.Increment(ref _seq);
        var time = DateTimeOffset.UtcNow;
        _counters.OnEmitted();

        for (var i = 0; i < snapshot.Length; i++)
        {
            var sub = snapshot[i];
            if (!sub.IsActive || !sub.Matches(body)) continue;
            sub.Deliver(body, seq, time);
        }
    }

    /// <summary>
    /// 停止总线：退订全部订阅并结束泵线程。
    /// 已入队未投递的事件被丢弃 —— 这是停止语义，不做补偿（审计类订阅应使用 Wait 溢出 + 同步落盘）。
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposedFlag, 1) != 0) return;

        SubscriptionBase[] snapshot;
        lock (_gate)
        {
            snapshot = _snapshot;
            _subscriptions.Clear();
            _snapshot = Array.Empty<SubscriptionBase>();
        }

        for (var i = 0; i < snapshot.Length; i++) snapshot[i].Dispose();
    }

    private void Register(SubscriptionBase sub)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            _subscriptions.Add(sub);
            _snapshot = _subscriptions.ToArray();
            _counters.OnSubscribed();
        }

        sub.Start(); // 在锁外启动泵线程
    }

    private void Remove(SubscriptionBase sub)
    {
        lock (_gate)
        {
            _subscriptions.Remove(sub);
            _snapshot = _subscriptions.ToArray();
        }
    }

    internal void RaiseHandlerFaulted(Exception ex)
    {
        var handler = HandlerFaulted;
        if (handler == null) return;

        try
        {
            handler(ex);
        }
        catch
        {
            // 观察者异常同样吞掉：任何订阅路径都不允许反向影响框架。
        }
    }

    private int ResolveCapacity(int? queueCapacity)
    {
        if (!queueCapacity.HasValue) return DefaultCapacity;
        if (queueCapacity.Value < 1) throw new ArgumentOutOfRangeException(nameof(queueCapacity), "队列容量必须不小于 1");
        return queueCapacity.Value;
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposedFlag) != 0) throw new ObjectDisposedException(nameof(InProcessEventBus));
    }

    // ───────────────────────── 计数器 ─────────────────────────

    /// <summary>Interlocked 计数器。各项独立累加，读数是近似快照，足够用于监控与告警。</summary>
    private sealed class Counters : IEventBusMetrics
    {
        private long _emitted;
        private long _dropped;
        private long _faults;
        private int _active;

        internal void OnEmitted() => Interlocked.Increment(ref _emitted);
        internal void OnDropped() => Interlocked.Increment(ref _dropped);
        internal void OnFault() => Interlocked.Increment(ref _faults);
        internal void OnSubscribed() => Interlocked.Increment(ref _active);
        internal void OnUnsubscribed() => Interlocked.Decrement(ref _active);

        public long TotalEmitted => Interlocked.Read(ref _emitted);
        public long TotalDropped => Interlocked.Read(ref _dropped);
        public long TotalFaults => Interlocked.Read(ref _faults);
        public int ActiveSubscriptions => Volatile.Read(ref _active);
    }

    // ───────────────────────── 订阅基类 ─────────────────────────

    private abstract class SubscriptionBase : ISubscription
    {
        private readonly InProcessEventBus _owner;
        private long _delivered;
        private long _dropped;
        private long _faults;
        private int _state; // 0=活跃 1=已退订

        protected SubscriptionBase(Type filter, InProcessEventBus owner)
        {
            Filter = filter;
            _owner = owner;
        }

        /// <summary>事件类型过滤器：本体是该类型或其派生类型才投递。基接口订阅由此实现。</summary>
        protected Type Filter { get; }

        public bool IsActive => Volatile.Read(ref _state) == 0;

        public long Delivered => Interlocked.Read(ref _delivered);

        public long Dropped => Interlocked.Read(ref _dropped);

        public long Faults => Interlocked.Read(ref _faults);

        public bool Matches(object body) => Filter.IsInstanceOfType(body);

        public abstract void Start();

        public abstract void Deliver(object body, long seq, DateTimeOffset time);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _state, 1) != 0) return;

            OnDisposing();
            _owner.Remove(this);
            _owner._counters.OnUnsubscribed();
        }

        /// <summary>退订时的资源清理（停队列、停泵）。</summary>
        protected virtual void OnDisposing() { }

        protected void CountDelivered() => Interlocked.Increment(ref _delivered);

        protected void CountDelivered(int count) => Interlocked.Add(ref _delivered, count);

        protected void CountDropped()
        {
            Interlocked.Increment(ref _dropped);
            _owner._counters.OnDropped();
        }

        /// <summary>订阅者异常：计数并通知观察者。绝不向上抛。</summary>
        protected void CountFault(Exception ex)
        {
            Interlocked.Increment(ref _faults);
            _owner._counters.OnFault();
            _owner.RaiseHandlerFaulted(ex);
        }
    }

    // ───────────────────────── 内联订阅 ─────────────────────────

    private sealed class InlineSubscription<TEvent> : SubscriptionBase where TEvent : IEvent
    {
        private readonly Action<IEventEnvelope<TEvent>> _handler;

        public InlineSubscription(Type filter, Action<IEventEnvelope<TEvent>> handler, InProcessEventBus owner)
            : base(filter, owner)
        {
            _handler = handler;
        }

        public override void Start() { }

        public override void Deliver(object body, long seq, DateTimeOffset time)
        {
            // Inline 在发出方线程上同步执行：契约要求订阅者必须极快（如计数、置标志）。
            try
            {
                _handler(new EventEnvelope<TEvent>(seq, time, (TEvent)body));
                CountDelivered();
            }
            catch (Exception ex)
            {
                CountFault(ex);
            }
        }
    }

    // ───────────────────────── 队列订阅（公共基类） ─────────────────────────

    private abstract class QueuedSubscriptionBase<TEvent> : SubscriptionBase where TEvent : IEvent
    {
        private readonly BlockingCollection<EventEnvelope<TEvent>> _queue;
        private readonly CancellationTokenSource _cts = new();
        private readonly Thread _pump;

        protected QueuedSubscriptionBase(Type filter, int capacity, OverflowPolicy overflow, InProcessEventBus owner)
            : base(filter, owner)
        {
            Overflow = overflow;
            _queue = new BlockingCollection<EventEnvelope<TEvent>>(capacity);
            _pump = new Thread(PumpLoop)
            {
                IsBackground = true,
                Name = "SuperSampler.EventBus[" + typeof(TEvent).Name + "]",
            };
        }

        protected OverflowPolicy Overflow { get; }

        public override void Start() => _pump.Start();

        /// <summary>有界入队，按溢出策略处理。DropOldest 带重试上限，保证终止。</summary>
        public override void Deliver(object body, long seq, DateTimeOffset time)
        {
            var envelope = new EventEnvelope<TEvent>(seq, time, (TEvent)body);

            for (var attempt = 0; ; attempt++)
            {
                bool added;
                try
                {
                    added = _queue.TryAdd(envelope);
                }
                catch (InvalidOperationException)
                {
                    CountDropped(); // 队列已标记完成或已释放：正在停止
                    return;
                }

                if (added) return;

                switch (Overflow)
                {
                    case OverflowPolicy.DropNewest:
                        CountDropped();
                        return;

                    case OverflowPolicy.DropOldest:
                        if (attempt >= 3)
                        {
                            CountDropped();
                            return;
                        }

                        // 挤掉一条就是丢弃一条：必须计数，否则丢弃统计会静默失真
                        if (_queue.TryTake(out _)) CountDropped();
                        break;

                    case OverflowPolicy.Wait:
                        try
                        {
                            _queue.Add(envelope, _cts.Token);
                            return;
                        }
                        catch (OperationCanceledException)
                        {
                            CountDropped(); // 总线停止，未投递
                            return;
                        }
                        catch (InvalidOperationException)
                        {
                            return; // 已停止接收或已释放
                        }

                    default:
                        CountDropped();
                        return;
                }
            }
        }

        protected sealed override void OnDisposing()
        {
            try
            {
                _queue.CompleteAdding();
            }
            catch (ObjectDisposedException)
            {
            }

            _cts.Cancel();

            // 泵线程自身触发退订时不能 Join 自己
            if (!ReferenceEquals(Thread.CurrentThread, _pump)) _pump.Join(1000);

            // 退订/停止时队列里仍未投递的事件同样是被丢弃的事件，必须计入计数：
            // docs/03 第 4.4 条「丢弃必须可见（见 ISubscription.Dropped），绝不允许静默丢弃」。
            // 此前这些条目在 Dropped / TotalDropped 里都查不到，宿主用丢弃计数做审计时会漏记。
            // 计数放在 Join 之后：此刻泵已退出（或已按 1 秒上界放弃），队列内容已定型；
            // 与泵并发取走的条目由泵正常投递，不会被这里误计。
            while (_queue.TryTake(out _)) CountDropped();

            _cts.Dispose();
            _queue.Dispose();
        }

        private void PumpLoop()
        {
            try
            {
                PumpCore();
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch (InvalidOperationException)
            {
            }
        }

        /// <summary>泵主循环，由派生类决定单条还是批量。</summary>
        protected abstract void PumpCore();

        /// <summary>带超时取一条；超时、取消或队列完成时返回 false。</summary>
        protected bool TryTake(out EventEnvelope<TEvent> item, int timeoutMs)
        {
            try
            {
                return _queue.TryTake(out item!, timeoutMs, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                item = null!;
                return false;
            }
            catch (InvalidOperationException) // 含 ObjectDisposedException
            {
                item = null!;
                return false;
            }
        }

        /// <summary>阻塞取一条；取消或队列完成时返回 null。</summary>
        protected EventEnvelope<TEvent>? TakeBlocking()
        {
            try
            {
                return _queue.Take(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (InvalidOperationException) // 含 ObjectDisposedException
            {
                return null;
            }
        }
    }

    // ───────────────────────── 单条队列订阅 ─────────────────────────

    private sealed class SingleSubscription<TEvent> : QueuedSubscriptionBase<TEvent> where TEvent : IEvent
    {
        private readonly Action<IEventEnvelope<TEvent>> _handler;

        public SingleSubscription(Type filter, Action<IEventEnvelope<TEvent>> handler,
            int capacity, OverflowPolicy overflow, InProcessEventBus owner)
            : base(filter, capacity, overflow, owner)
        {
            _handler = handler;
        }

        protected override void PumpCore()
        {
            while (true)
            {
                var envelope = TakeBlocking();
                if (envelope == null) return;

                try
                {
                    _handler(envelope);
                    CountDelivered();
                }
                catch (Exception ex)
                {
                    CountFault(ex);
                }
            }
        }
    }

    // ───────────────────────── 批量队列订阅 ─────────────────────────

    private sealed class BatchSubscription<TEvent> : QueuedSubscriptionBase<TEvent> where TEvent : IEvent
    {
        private readonly Action<IReadOnlyList<IEventEnvelope<TEvent>>> _handler;
        private readonly int _maxBatchSize;
        private readonly TimeSpan _maxBatchDelay;

        public BatchSubscription(Type filter, Action<IReadOnlyList<IEventEnvelope<TEvent>>> handler,
            int maxBatchSize, TimeSpan maxBatchDelay, int capacity, OverflowPolicy overflow, InProcessEventBus owner)
            : base(filter, capacity, overflow, owner)
        {
            _handler = handler;
            _maxBatchSize = maxBatchSize;
            _maxBatchDelay = maxBatchDelay;
        }

        protected override void PumpCore()
        {
            var batch = new List<EventEnvelope<TEvent>>(_maxBatchSize);

            while (true)
            {
                batch.Clear();
                var first = TakeBlocking();
                if (first == null) return;
                batch.Add(first);

                // 攒批：满 maxBatchSize 条，或距批首事件超过 maxBatchDelay 即发出
                var deadline = DateTimeOffset.UtcNow + _maxBatchDelay;
                while (batch.Count < _maxBatchSize)
                {
                    var remainMs = (int)Math.Min((deadline - DateTimeOffset.UtcNow).TotalMilliseconds, int.MaxValue);
                    if (remainMs <= 0) break;
                    if (!TryTake(out var next, remainMs)) break;
                    batch.Add(next);
                }

                try
                {
                    // 交给 handler 的是**每批一份的独立快照**，不是上面这个复用缓冲：
                    // batch 每轮 Clear/重填，若把同一实例交出去，宿主一旦延迟消费
                    // （如 Dispatcher.BeginInvoke 里再枚举）就会撞"集合已修改"——
                    // 接口声明的是 IReadOnlyList，宿主有权按只读快照理解并跨回调持有。
                    // 每批一次小分配（≤ maxBatchSize 个引用），相对攒批省下的逐事件回调可忽略。
                    _handler(new List<EventEnvelope<TEvent>>(batch));
                    CountDelivered(batch.Count);
                }
                catch (Exception ex)
                {
                    CountFault(ex);
                }
            }
        }
    }
}
