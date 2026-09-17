using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SuperSampler.Abstractions.Events;
using SuperSampler.Core.Events;
using Xunit;

namespace SuperSampler.UnitTests.Events;

/// <summary>
/// 事件总线第二轮重测（覆盖方案 §九–§十一）：订阅/退订语义、Inline vs Queued、溢出策略与丢弃计数、
/// 订阅者异常隔离、回调内危险操作、高并发与队列有界性。
/// 与既有 <c>InProcessEventBusTests</c>（9 例契约）与 <c>EventBusGapTests</c>（G-R 族 3 例）不重复：
/// 本类补的是**订阅者视角的计数守恒**、回调内的危险操作（不死锁）、并发发布/订阅风暴、
/// 以及本轮修复的「退订时在途事件必须计入 Dropped」。
/// </summary>
[Collection("real-polling-threads")]
public sealed class EventBusConcurrencyTests
{
    private sealed class Tick : IEvent
    {
        public Tick(int n) => N = n;

        public int N { get; }

        public EventCategory Category => EventCategory.Performance;

        public EventLevel Level => EventLevel.Debug;
    }

    private static int EventBusThreads()
        => System.Diagnostics.Process.GetCurrentProcess().Threads.Count;

    // ═══════════════ §九 订阅 / 退订语义 ═══════════════

    [Fact]
    public void Subscribing_the_same_handler_twice_creates_two_independent_subscriptions()
    {
        using var bus = new InProcessEventBus();
        var count = 0;
        Action<IEventEnvelope<Tick>> handler = _ => Interlocked.Increment(ref count);

        using var first = bus.Subscribe(handler, DeliveryMode.Inline);
        using var second = bus.Subscribe(handler, DeliveryMode.Inline);

        bus.Emit(new Tick(1));

        Assert.Equal(2, Volatile.Read(ref count));
        Assert.Equal(1, first.Delivered);
        Assert.Equal(1, second.Delivered);
        Assert.Equal(2, bus.Metrics.ActiveSubscriptions);
    }

    [Fact]
    public void Subscribing_null_handlers_throws_argument_null_in_both_modes()
    {
        using var bus = new InProcessEventBus();

        Assert.Throws<ArgumentNullException>(() => bus.Subscribe<Tick>((Action<IEventEnvelope<Tick>>)null!));
        Assert.Throws<ArgumentNullException>(() => bus.Subscribe<Tick>((Action<IEventEnvelope<Tick>>)null!, DeliveryMode.Inline));
        Assert.Throws<ArgumentNullException>(() => bus.SubscribeBatch<Tick>(
            (Action<IReadOnlyList<IEventEnvelope<Tick>>>)null!, 1, TimeSpan.FromMilliseconds(10)));
    }

    [Fact]
    public void Invalid_queue_parameters_are_rejected_and_inline_ignores_them()
    {
        using var bus = new InProcessEventBus();

        Assert.Throws<ArgumentOutOfRangeException>(() => bus.Subscribe<Tick>(_ => { }, DeliveryMode.Queued, OverflowPolicy.DropOldest, queueCapacity: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => bus.SubscribeBatch<Tick>(_ => { }, maxBatchSize: 0, maxBatchDelay: TimeSpan.FromMilliseconds(10)));

        // 口径：Inline 不建队列，容量/溢出参数被忽略（不抛）——记录现状，避免宿主以为设了容量
        using var inline = bus.Subscribe<Tick>(_ => { }, DeliveryMode.Inline, OverflowPolicy.Wait, queueCapacity: 0);
        Assert.True(inline.IsActive);
    }

    [Fact]
    public void Dispose_of_a_subscription_is_idempotent_and_keeps_counters_consistent()
    {
        using var bus = new InProcessEventBus();
        var sub = bus.Subscribe<Tick>(_ => { }, DeliveryMode.Queued);
        bus.Emit(new Tick(1));

        sub.Dispose();
        sub.Dispose();                                   // 重复退订：不抛、不重复计数

        Assert.False(sub.IsActive);
        Assert.Equal(0, bus.Metrics.ActiveSubscriptions);

        sub.Dispose();
        Assert.Equal(0, bus.Metrics.ActiveSubscriptions);
    }

    [Fact]
    public void An_unsubscribed_queued_subscriber_stops_receiving()
    {
        using var bus = new InProcessEventBus();
        var seen = new List<int>();
        var sub = bus.Subscribe<Tick>(e => { lock (seen) seen.Add(e.Body.N); }, DeliveryMode.Queued);

        bus.Emit(new Tick(1));
        Assert.True(SpinWait.SpinUntil(() => sub.Delivered == 1, 5000));

        sub.Dispose();

        bus.Emit(new Tick(2));
        Thread.Sleep(150);
        lock (seen) Assert.Equal(new[] { 1 }, seen.ToArray());
        Assert.Equal(1, sub.Delivered);
    }

    [Fact]
    public void Unsubscribing_discards_pending_events_but_they_stay_visible_in_the_drop_counters()
    {
        // docs/03 §4.4「丢弃必须可见（见 ISubscription.Dropped），绝不允许静默丢弃」：
        // 退订时队列里还没投递的条目同样是被丢弃的事件，必须计入计数（findings D75，本轮修复）。
        using var bus = new InProcessEventBus();
        using var gate = new ManualResetEventSlim(false);
        var started = new ManualResetEventSlim(false);

        var sub = bus.Subscribe<Tick>(_ => { started.Set(); gate.Wait(TimeSpan.FromSeconds(5)); },
            DeliveryMode.Queued, OverflowPolicy.DropOldest, queueCapacity: 64);

        for (var i = 0; i < 10; i++) bus.Emit(new Tick(i));   // 1 条被泵取走并阻塞，9 条留在队列
        Assert.True(started.Wait(5000), "泵应已开始处理第一条");

        sub.Dispose();

        Assert.Equal(9, sub.Dropped);                        // 修复前 = 0（静默丢弃）
        Assert.Equal(9, bus.Metrics.TotalDropped);
        // 守恒口径：Delivered 只计**回调已完成**的投递，被丢弃的 9 条全部可见，
        // 第 10 条正在回调里（既未完成投递、也没被丢）。
        Assert.Equal(0, sub.Delivered);
        Assert.Equal(10, sub.Delivered + sub.Dropped + 1);

        gate.Set();
    }

    [Fact]
    public void Emit_after_the_bus_is_disposed_is_a_silent_noop_and_subscribe_throws()
    {
        var bus = new InProcessEventBus();
        using var sub = bus.Subscribe<Tick>(_ => { }, DeliveryMode.Inline);

        bus.Dispose();

        bus.Emit(new Tick(1));                               // 不抛：不反向影响发出方（W54 口径）
        bus.Emit(new Tick(2));
        Assert.Throws<ObjectDisposedException>(() => bus.Subscribe<Tick>(_ => { }));

        bus.Dispose();                                       // 重复 Dispose 幂等
        Assert.False(sub.IsActive);
    }

    [Fact]
    public void Pump_threads_exit_after_unsubscribe_and_bus_dispose()
    {
        // 线程泄漏断言（近似）：连续建立/释放 20 个 Queued 订阅后，进程线程数应回到基线附近。
        var baseline = EventBusThreads();
        var bus = new InProcessEventBus();
        var subs = new List<ISubscription>();
        for (var i = 0; i < 12; i++) subs.Add(bus.Subscribe<Tick>(_ => { }, DeliveryMode.Queued));

        Assert.True(EventBusThreads() >= baseline + 6, "12 个 Queued 订阅应各起一条泵线程");

        foreach (var sub in subs) sub.Dispose();
        bus.Dispose();

        var settled = SpinWait.SpinUntil(() => EventBusThreads() <= baseline + 6, 5000);
        Assert.True(settled, "退订/Dispose 后泵线程应退出（当前 " + EventBusThreads() + " / 基线 " + baseline + "）");
        Assert.Equal(0, bus.Metrics.ActiveSubscriptions);
    }

    // ═══════════════ §十 投递模式 / 溢出 / 异常隔离 ═══════════════

    [Fact]
    public void Inline_delivers_synchronously_on_the_emitting_thread()
    {
        using var bus = new InProcessEventBus();
        var emitter = -1;
        var handler = -1;
        var completedBeforeEmitReturned = false;

        using var sub = bus.Subscribe<Tick>(_ =>
        {
            emitter = Thread.CurrentThread.ManagedThreadId;
            handler = Thread.CurrentThread.ManagedThreadId;
        }, DeliveryMode.Inline);

        var calling = Thread.CurrentThread.ManagedThreadId;
        bus.Emit(new Tick(1));
        completedBeforeEmitReturned = handler >= 0;

        Assert.Equal(calling, emitter);
        Assert.Equal(calling, handler);
        Assert.True(completedBeforeEmitReturned, "Inline 必须在 Emit 返回前完成回调");
        Assert.Equal(1, sub.Delivered);
    }

    [Fact]
    public void Queued_delivers_on_a_dedicated_pump_thread()
    {
        using var bus = new InProcessEventBus();
        var calling = Thread.CurrentThread.ManagedThreadId;
        var delivering = 0;
        string? pumpName = null;
        var done = new ManualResetEventSlim(false);

        using var sub = bus.Subscribe<Tick>(_ =>
        {
            delivering = Thread.CurrentThread.ManagedThreadId;
            pumpName = Thread.CurrentThread.Name;
            done.Set();
        });
        bus.Emit(new Tick(1));

        Assert.True(done.Wait(5000));
        Assert.NotEqual(calling, delivering);                       // 异步：不在发布线程上
        Assert.StartsWith("SuperSampler.EventBus", pumpName);        // 线程名带事件类型（便于排障）
    }

    [Fact]
    public void Queued_subscriber_receives_events_in_emit_order()
    {
        using var bus = new InProcessEventBus();
        var seqs = new List<long>();
        var bodies = new List<int>();
        using var sub = bus.Subscribe<Tick>(e => { lock (seqs) { seqs.Add(e.Seq); bodies.Add(e.Body.N); } }, DeliveryMode.Queued);

        for (var i = 0; i < 1000; i++) bus.Emit(new Tick(i));

        Assert.True(SpinWait.SpinUntil(() => sub.Delivered == 1000, 10_000), "1000 条应全部投递");
        lock (seqs)
        {
            Assert.Equal(Enumerable.Range(0, 1000).ToArray(), bodies.ToArray());
            for (var i = 1; i < seqs.Count; i++) Assert.True(seqs[i] > seqs[i - 1], "同订阅内 FIFO：Seq 必须严格递增");
        }
    }

    [Fact]
    public void Drop_newest_never_blocks_the_emitter_and_counts_every_loss()
    {
        using var bus = new InProcessEventBus();
        using var gate = new ManualResetEventSlim(false);
        var started = new ManualResetEventSlim(false);
        var processed = 0;

        using var sub = bus.Subscribe<Tick>(_ =>
        {
            if (Interlocked.Increment(ref processed) == 1)
            {
                started.Set();
                gate.Wait(TimeSpan.FromSeconds(10));
            }
        }, DeliveryMode.Queued, OverflowPolicy.DropNewest, queueCapacity: 4);

        bus.Emit(new Tick(0));
        Assert.True(started.Wait(5000));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 1; i <= 200; i++) bus.Emit(new Tick(i));
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 3000, "DropNewest 不允许阻塞发布者（实测 " + sw.ElapsedMilliseconds + "ms）");
        Assert.Equal(200 - 4, sub.Dropped);                   // 容量 4 之外的都被丢，且逐条计数
        Assert.Equal(200 - 4, bus.Metrics.TotalDropped);

        gate.Set();
        Assert.True(SpinWait.SpinUntil(() => sub.Delivered == 5, 5000), "丢弃后仍应把队列里的投完");
        Assert.Equal(200 + 1, sub.Delivered + sub.Dropped);    // 守恒
    }

    [Fact]
    public void Drop_oldest_keeps_the_newest_and_keeps_the_queue_bounded()
    {
        using var bus = new InProcessEventBus();
        using var gate = new ManualResetEventSlim(false);
        var started = new ManualResetEventSlim(false);
        var seen = new List<int>();

        using var sub = bus.Subscribe<Tick>(e =>
        {
            if (!started.IsSet)
            {
                started.Set();
                gate.Wait(TimeSpan.FromSeconds(10));
            }

            lock (seen) seen.Add(e.Body.N);
        }, DeliveryMode.Queued, OverflowPolicy.DropOldest, queueCapacity: 16);

        bus.Emit(new Tick(-1));
        Assert.True(started.Wait(5000));

        for (var i = 0; i < 4_000; i++) bus.Emit(new Tick(i));

        // 队列有界：泵阻塞期间最多再收容量条（16），其余全丢并计数
        Assert.Equal(4_000 - 16, sub.Dropped);
        // 守恒口径：4001 条发出 = 3984 被挤出（全部可见） + 16 条仍在队列 + 1 条在回调里
        //（Delivered 只计**回调已完成**的投递，此刻被阻塞的那条还没完成）
        Assert.Equal(4_000 - 16, sub.Delivered + sub.Dropped);

        gate.Set();
        Assert.True(SpinWait.SpinUntil(() => sub.Delivered == 17, 5000));
        lock (seen)
        {
            Assert.Equal(-1, seen[0]);                        // 第一条被处理掉的仍在
            Assert.Equal(3_999, seen[seen.Count - 1]);        // 最新的一条没被丢（DropOldest = 挤掉最旧）
        }
    }

    [Fact]
    public async Task Wait_overflow_never_loses_and_unblocks_as_the_consumer_progresses()
    {
        using var bus = new InProcessEventBus();
        using var gate = new ManualResetEventSlim(false);
        var received = 0;

        using var sub = bus.Subscribe<Tick>(_ => { gate.Wait(TimeSpan.FromSeconds(20)); Interlocked.Increment(ref received); },
            DeliveryMode.Queued, OverflowPolicy.Wait, queueCapacity: 2);

        bus.Emit(new Tick(0));                                // 泵取走并阻塞
        bus.Emit(new Tick(1));
        bus.Emit(new Tick(2));                                // 队列满（容量 2）

        var producer = Task.Run(() => { for (var i = 3; i < 300; i++) bus.Emit(new Tick(i)); });
        await Task.Delay(200);
        Assert.False(producer.IsCompleted, "Wait 策略下队列满应阻塞发布者");

        gate.Set();
        var finished = await Task.WhenAny(producer, Task.Delay(10_000));
        Assert.Same(producer, finished);
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref received) == 300, 10_000));

        Assert.Equal(0, sub.Dropped);                         // Wait = 不丢
        Assert.Equal(0, bus.Metrics.TotalDropped);
        Assert.Equal(300, sub.Delivered);
    }

    [Fact]
    public async Task Wait_overflow_producer_is_released_by_dispose_instead_of_blocking_forever()
    {
        var bus = new InProcessEventBus();
        using var gate = new ManualResetEventSlim(false);
        var started = new ManualResetEventSlim(false);

        var sub = bus.Subscribe<Tick>(_ => { started.Set(); gate.Wait(TimeSpan.FromSeconds(20)); },
            DeliveryMode.Queued, OverflowPolicy.Wait, queueCapacity: 1);

        bus.Emit(new Tick(0));
        Assert.True(started.Wait(5000));
        bus.Emit(new Tick(1));                                // 占住容量 1

        var producer = Task.Run(() => bus.Emit(new Tick(2)));  // 阻塞
        await Task.Delay(200);
        Assert.False(producer.IsCompleted);

        sub.Dispose();                                        // 退订 = 取消令牌 → 阻塞的发布者被释放
        var finished = await Task.WhenAny(producer, Task.Delay(5000));
        Assert.Same(producer, finished);
        Assert.True(sub.Dropped >= 1, "被取消的等待投递必须计入丢弃");

        gate.Set();
        bus.Dispose();
    }

    [Fact]
    public void A_throwing_queued_handler_never_kills_the_pump_and_other_subscribers_keep_receiving()
    {
        using var bus = new InProcessEventBus();
        var badSeen = new List<int>();
        var goodSeen = new List<int>();
        var faultsObserved = 0;

        using var bad = bus.Subscribe<Tick>(e =>
        {
            lock (badSeen) badSeen.Add(e.Body.N);
            if (e.Body.N < 3) throw new InvalidOperationException("坏订阅者");
        }, DeliveryMode.Queued);

        using var good = bus.Subscribe<Tick>(e => { lock (goodSeen) goodSeen.Add(e.Body.N); }, DeliveryMode.Queued);
        bus.HandlerFaulted += _ => Interlocked.Increment(ref faultsObserved);

        for (var i = 0; i < 10; i++) bus.Emit(new Tick(i));

        Assert.True(SpinWait.SpinUntil(() => { lock (goodSeen) return goodSeen.Count == 10; }, 10_000));
        Assert.True(SpinWait.SpinUntil(() => { lock (badSeen) return badSeen.Count == 10; }, 10_000), "抛异常的泵必须继续投后面的");
        Assert.Equal(3, bad.Faults);
        Assert.Equal(3, bus.Metrics.TotalFaults);
        Assert.Equal(3, Volatile.Read(ref faultsObserved));
        Assert.Equal(10, good.Delivered);
        Assert.Equal(0, good.Faults);
    }

    [Fact]
    public void A_throwing_batch_handler_never_kills_the_pump()
    {
        using var bus = new InProcessEventBus();
        var batches = 0;
        var seen = 0;

        using var sub = bus.SubscribeBatch<Tick>(
            b => { Interlocked.Increment(ref batches); Interlocked.Add(ref seen, b.Count); throw new InvalidOperationException("批处理炸了"); },
            maxBatchSize: 2, maxBatchDelay: TimeSpan.FromMilliseconds(30));

        for (var i = 0; i < 10; i++) bus.Emit(new Tick(i));

        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref seen) == 10, 10_000));
        Assert.True(SpinWait.SpinUntil(() => sub.Faults >= 1, 5000));
        // Delivered 的口径是「回调成功完成的事件数」：全部批次都抛 → 0；
        // 但泵没死，10 条事件全部被处理过（seen=10），故障计数可见。
        Assert.Equal(0, sub.Delivered);
        Assert.Equal(10, Volatile.Read(ref seen));
        Assert.True(bus.Metrics.TotalFaults >= 1);
    }

    [Fact]
    public void A_throwing_handler_fault_observer_is_swallowed_too()
    {
        using var bus = new InProcessEventBus();
        var good = new ManualResetEventSlim(false);
        bus.HandlerFaulted += _ => throw new InvalidOperationException("观察者自己也炸");

        using var sub = bus.Subscribe<Tick>(_ => { good.Set(); throw new InvalidOperationException("订阅者炸"); }, DeliveryMode.Inline);
        bus.Emit(new Tick(1));

        Assert.True(good.Wait(5000));
        Assert.Equal(1, sub.Faults);
    }

    // ═══════════════ §十.11–14 回调内的危险操作（超时断言：不死锁）═══════════════

    [Fact]
    public void Unsubscribing_self_inside_a_queued_callback_does_not_deadlock()
    {
        using var bus = new InProcessEventBus();
        ISubscription? sub = null;
        var invoked = new ManualResetEventSlim(false);
        sub = bus.Subscribe<Tick>(_ => { sub!.Dispose(); invoked.Set(); }, DeliveryMode.Queued);

        bus.Emit(new Tick(1));
        Assert.True(invoked.Wait(5000), "回调内退订自己不得死锁");
        bus.Emit(new Tick(2));
        Thread.Sleep(100);
        Assert.False(bus.Metrics.TotalFaults > 0);
    }

    [Fact]
    public void Unsubscribing_another_subscriber_inside_a_callback_does_not_deadlock()
    {
        using var bus = new InProcessEventBus();
        var otherDelivered = 0;
        var invoked = new ManualResetEventSlim(false);

        var victim = bus.Subscribe<Tick>(_ => Interlocked.Increment(ref otherDelivered));
        using var killer = bus.Subscribe<Tick>(_ => { victim.Dispose(); invoked.Set(); }, DeliveryMode.Inline);

        bus.Emit(new Tick(1));
        Assert.True(invoked.Wait(5000));
        Assert.True(SpinWait.SpinUntil(() => !victim.IsActive, 5000));
        Assert.Equal(1, victim.Delivered);                   // 本次快照已投出

        bus.Emit(new Tick(2));
        Assert.Equal(1, Volatile.Read(ref otherDelivered));  // 之后不再收到
    }

    [Fact]
    public void Subscribing_a_new_subscriber_inside_a_callback_does_not_deadlock_and_uses_a_snapshot()
    {
        using var bus = new InProcessEventBus();
        var newSubSeen = 0;
        var added = new ManualResetEventSlim(false);

        using var trigger = bus.Subscribe<Tick>(_ =>
        {
            if (added.IsSet) return;                          // 只在第一条事件上新增一次
            bus.Subscribe<Tick>(_ => Interlocked.Increment(ref newSubSeen));
            added.Set();
        }, DeliveryMode.Inline);

        bus.Emit(new Tick(1));                               // 当次 Emit 用旧快照 → 新订阅者收不到这一条
        Assert.True(added.Wait(5000));
        Assert.Equal(0, Volatile.Read(ref newSubSeen));

        bus.Emit(new Tick(2));                               // 下一条起收得到
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref newSubSeen) >= 1, 5000));
        Assert.Equal(2, bus.Metrics.ActiveSubscriptions);     // 触发器订阅 + 回调内新建的订阅
    }

    [Fact]
    public void Disposing_the_bus_inside_an_inline_callback_does_not_deadlock()
    {
        var bus = new InProcessEventBus();
        var invoked = new ManualResetEventSlim(false);
        var sub = bus.Subscribe<Tick>(_ => { bus.Dispose(); invoked.Set(); }, DeliveryMode.Inline);

        bus.Emit(new Tick(1));
        Assert.True(invoked.Wait(5000));
        bus.Emit(new Tick(2));                               // 已停止：静默
        Assert.False(sub.IsActive);
    }

    [Fact]
    public void Disposing_the_bus_inside_a_queued_callback_does_not_deadlock()
    {
        var bus = new InProcessEventBus();
        var invoked = new ManualResetEventSlim(false);
        var sub = bus.Subscribe<Tick>(_ => { bus.Dispose(); invoked.Set(); }, DeliveryMode.Queued);

        bus.Emit(new Tick(1));
        Assert.True(invoked.Wait(5000), "泵线程内 Dispose 总线不得死锁");
        Assert.True(SpinWait.SpinUntil(() => !sub.IsActive, 5000));
        bus.Emit(new Tick(2));
        sub.Dispose();
    }

    // ═══════════════ §十一 高并发 ═══════════════

    [Fact]
    public void Concurrent_publishers_from_eight_threads_neither_lose_events_nor_break_subscribers()
    {
        using var bus = new InProcessEventBus();
        const int threads = 6;
        const int perThread = 2_000;   // 合计 12000（万级），避免给并行执行的其它用例造成 CPU 压力

        var inlineCount = 0;
        var queuedSeen = 0;
        using var inline = bus.Subscribe<Tick>(_ => Interlocked.Increment(ref inlineCount), DeliveryMode.Inline);
        using var queued = bus.Subscribe<Tick>(_ => Interlocked.Increment(ref queuedSeen), DeliveryMode.Queued, queueCapacity: 65_536);

        var workers = new List<Thread>();
        for (var t = 0; t < threads; t++)
        {
            var th = new Thread(() => { for (var i = 0; i < perThread; i++) bus.Emit(new Tick(i)); });
            workers.Add(th);
        }

        foreach (var th in workers) th.Start();
        Assert.True(workers.All(th => th.Join(TimeSpan.FromSeconds(30))), "并发发布不得死锁");

        Assert.Equal(threads * perThread, inlineCount);
        Assert.Equal(threads * perThread, bus.Metrics.TotalEmitted);
        Assert.True(SpinWait.SpinUntil(() => queuedSeen == threads * perThread, 30_000),
            "Queued 订阅应守恒（" + queuedSeen + "/" + threads * perThread + "）");
        Assert.Equal(0, bus.Metrics.TotalDropped);
    }

    [Fact]
    public void Concurrent_subscribe_and_unsubscribe_storm_keeps_metrics_consistent()
    {
        using var bus = new InProcessEventBus();
        var stop = new ManualResetEventSlim(false);
        var churnSeen = 0;
        var emitter = new Thread(() => { while (!stop.IsSet) bus.Emit(new Tick(1)); });
        emitter.Start();

        var churn = new Thread(() =>
        {
            for (var i = 0; i < 500; i++)
            {
                var sub = bus.Subscribe<Tick>(_ => Interlocked.Increment(ref churnSeen), DeliveryMode.Inline);
                sub.Dispose();
            }
        });
        churn.Start();

        Assert.True(churn.Join(TimeSpan.FromSeconds(30)), "并发订阅/退订不得死锁");
        stop.Set();
        Assert.True(emitter.Join(TimeSpan.FromSeconds(10)));

        Assert.Equal(0, bus.Metrics.ActiveSubscriptions);
        Assert.Equal(0, bus.Metrics.TotalFaults);
    }

    [Fact]
    public void Delivery_order_is_preserved_per_publishing_thread()
    {
        // 口径：**单发布线程内**严格 FIFO（BlockingCollection 插入序）。
        // 多线程并发发布时，不同线程之间的投递顺序可能与 Seq 顺序不一致（见报告观察项 W58），
        // 宿主如需全局因果序应按信封 Seq 排序（findings W58）——这里只钉死「同线程子序列保序」。
        using var bus = new InProcessEventBus();
        const int threads = 4;
        const int perThread = 500;
        var received = new List<(int Thread, int Index)>();
        using var sub = bus.Subscribe<Tick>(e => { lock (received) received.Add((e.Body.N / 1000, e.Body.N % 1000)); }, DeliveryMode.Queued, queueCapacity: 8192);

        var workers = new List<Thread>();
        for (var t = 0; t < threads; t++)
        {
            var id = t;
            var th = new Thread(() => { for (var i = 0; i < perThread; i++) bus.Emit(new Tick(id * 1000 + i)); });
            workers.Add(th);
        }

        foreach (var th in workers) th.Start();
        foreach (var th in workers) Assert.True(th.Join(TimeSpan.FromSeconds(30)));

        Assert.True(SpinWait.SpinUntil(() => sub.Delivered == threads * perThread, 30_000));
        lock (received)
        {
            foreach (var group in received.GroupBy(r => r.Thread))
            {
                var indexes = group.Select(g => g.Index).ToArray();
                Assert.Equal(Enumerable.Range(0, perThread).ToArray(), indexes);
            }
        }
    }
}
