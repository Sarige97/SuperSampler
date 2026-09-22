using System;
using System.Collections.Generic;
using System.Threading;
using SuperSampler.Abstractions.Errors;
using SuperSampler.Abstractions.Events;
using SuperSampler.Core.Events;
using Xunit;

namespace SuperSampler.UnitTests.Events;

/// <summary>事件总线契约测试，对应 docs/03 第 4 节的硬约束。</summary>
public class InProcessEventBusTests : IDisposable
{
    private readonly InProcessEventBus _bus = new();

    public void Dispose() => _bus.Dispose();

    private static ErrorInfo Info(
        EventCategory category = EventCategory.Device,
        EventLevel level = EventLevel.Warn)
        => new(
            "TEST.ERR",
            category,
            level,
            ErrorSource.Device,
            "ss.test.error",
            new ErrorContext(DeviceId: "Mold01", UnitId: 1, PointId: "temp", Address: 100));

    [Fact]
    public void Inline_delivers_in_emit_order_with_monotonic_seq()
    {
        var seqs = new List<long>();
        using var sub = _bus.Subscribe<TimeoutError>(e => seqs.Add(e.Seq), DeliveryMode.Inline);

        for (var i = 0; i < 5; i++) _bus.Emit(new TimeoutError(Info()));

        Assert.Equal(new long[] { 1, 2, 3, 4, 5 }, seqs);
        Assert.Equal(5, sub.Delivered);
        Assert.Equal(5, _bus.Metrics.TotalEmitted);
    }

    [Fact]
    public void Queued_delivers_event_with_intact_body()
    {
        var done = new ManualResetEventSlim(false);
        IEventEnvelope<TimeoutError>? got = null;
        using var sub = _bus.Subscribe<TimeoutError>(e => { got = e; done.Set(); });

        _bus.Emit(new TimeoutError(Info()));

        Assert.True(done.Wait(TimeSpan.FromSeconds(5)), "5 秒内未收到事件");
        Assert.NotNull(got);
        Assert.Equal(EventCategory.Device, got!.Body.Category);
        Assert.Equal("Mold01", got.Body.Info.Context.DeviceId);
    }

    [Fact]
    public void Subscriber_exception_is_swallowed_and_counted()
    {
        var badCalled = new ManualResetEventSlim(false);
        var goodCalled = new ManualResetEventSlim(false);
        var faultObserved = new ManualResetEventSlim(false);
        _bus.HandlerFaulted += _ => faultObserved.Set();

        using var bad = _bus.Subscribe<TimeoutError>(e =>
        {
            badCalled.Set();
            throw new InvalidOperationException("坏订阅者");
        }, DeliveryMode.Inline);
        using var good = _bus.Subscribe<TimeoutError>(_ => goodCalled.Set(), DeliveryMode.Inline);

        _bus.Emit(new TimeoutError(Info()));

        Assert.True(badCalled.Wait(TimeSpan.FromSeconds(5)));
        Assert.True(goodCalled.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, bad.Faults);
        Assert.Equal(1, _bus.Metrics.TotalFaults);
        Assert.True(faultObserved.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, good.Delivered);
    }

    [Fact]
    public void Queue_full_DropNewest_drops_incoming_and_counts()
    {
        var started = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        var seqs = new List<long>();
        var processed = 0;

        using var sub = _bus.Subscribe<TimeoutError>(e =>
        {
            lock (seqs)
            {
                seqs.Add(e.Seq);
                processed++;
            }

            if (processed == 1)
            {
                started.Set();                          // 告知泵已在处理第一条
                release.Wait(TimeSpan.FromSeconds(5)); // 锁外阻塞，制造队列堆积
            }
        }, DeliveryMode.Queued, OverflowPolicy.DropNewest, queueCapacity: 1);

        _bus.Emit(new TimeoutError(Info()));   // #1 被泵取出并阻塞在处理器里
        Assert.True(started.Wait(TimeSpan.FromSeconds(5)), "处理未开始");
        _bus.Emit(new TimeoutError(Info()));   // #2 占住容量 1 的队列
        _bus.Emit(new TimeoutError(Info()));   // #3 队列满 → 丢弃
        Assert.Equal(1, _bus.Metrics.TotalDropped);

        release.Set();

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (sub.Delivered < 2 && DateTime.UtcNow < deadline) Thread.Sleep(10);

        Assert.Equal(2, sub.Delivered);
        Assert.Equal(1, sub.Dropped);
        lock (seqs) Assert.Equal(new long[] { 1, 2 }, seqs.ToArray());
    }

    [Fact]
    public void Queue_full_DropOldest_keeps_newest()
    {
        var started = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        var seqs = new List<long>();
        var processed = 0;

        using var sub = _bus.Subscribe<TimeoutError>(e =>
        {
            lock (seqs)
            {
                seqs.Add(e.Seq);
                processed++;
            }

            if (processed == 1)
            {
                started.Set();
                release.Wait(TimeSpan.FromSeconds(5));
            }
        }, DeliveryMode.Queued, OverflowPolicy.DropOldest, queueCapacity: 1);

        _bus.Emit(new TimeoutError(Info()));   // #1 处理中
        Assert.True(started.Wait(TimeSpan.FromSeconds(5)), "处理未开始");
        _bus.Emit(new TimeoutError(Info()));   // #2 入队
        _bus.Emit(new TimeoutError(Info()));   // #3 队列满 → 挤掉 #2
        release.Set();

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (sub.Delivered < 2 && DateTime.UtcNow < deadline) Thread.Sleep(10);

        Assert.Equal(2, sub.Delivered);
        Assert.Equal(1, sub.Dropped);
        lock (seqs) Assert.Equal(new long[] { 1, 3 }, seqs.ToArray());
    }

    [Fact]
    public void Base_interface_subscription_receives_derived_events()
    {
        var done = new ManualResetEventSlim(false);
        IErrorEvent? body = null;
        using var sub = _bus.Subscribe<IErrorEvent>(e => { body = e.Body; done.Set(); });

        _bus.Emit(new TimeoutError(Info()));

        Assert.True(done.Wait(TimeSpan.FromSeconds(5)));
        Assert.IsType<TimeoutError>(body);
        Assert.Equal(ErrorClass.Transient, ErrorClasses.Classify(body!));
    }

    [Fact]
    public void Marker_interface_subscription_filters_by_error_class()
    {
        var transientSeen = new ManualResetEventSlim(false);
        var permanentSeen = new ManualResetEventSlim(false);
        IErrorEvent? permanentBody = null;

        using var t = _bus.Subscribe<ITransientError>(_ => transientSeen.Set());
        using var p = _bus.Subscribe<IPermanentError>(e => { permanentBody = e.Body; permanentSeen.Set(); });

        _bus.Emit(new TimeoutError(Info()));   // Transient：只应触发 t
        Assert.True(transientSeen.Wait(TimeSpan.FromSeconds(5)));
        Assert.False(permanentSeen.Wait(TimeSpan.FromMilliseconds(300)), "永久错误订阅不应收到瞬时事件");

        _bus.Emit(new DeviceExceptionError(Info(), 0x02));   // Permanent
        Assert.True(permanentSeen.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal(0x02, Assert.IsType<DeviceExceptionError>(permanentBody).ExceptionCode);
    }

    [Fact]
    public void Batch_subscription_conserves_total_count()
    {
        var sizes = new List<int>();
        var allDelivered = new ManualResetEventSlim(false);
        var sum = 0L;

        using var sub = _bus.SubscribeBatch<TimeoutError>(
            b =>
            {
                lock (sizes) sizes.Add(b.Count);
                if (Interlocked.Add(ref sum, b.Count) >= 5) allDelivered.Set();
            },
            maxBatchSize: 10,
            maxBatchDelay: TimeSpan.FromMilliseconds(50));

        for (var i = 0; i < 5; i++) _bus.Emit(new TimeoutError(Info()));

        Assert.True(allDelivered.Wait(TimeSpan.FromSeconds(5)), "未收齐 5 条");
        Thread.Sleep(150); // 等攒批窗口彻底结束

        Assert.Equal(5, sub.Delivered);
        Assert.Equal(5, Interlocked.Read(ref sum));
        lock (sizes) Assert.True(sizes.Count >= 1);
    }

    /// <summary>
    /// 回归：handler 拿到的 batch 是**每批独立快照**，可以只存引用、在回调返回之后再消费。
    /// 修复前它是泵线程复用的同一个 List（每轮 Clear/重填），宿主把引用存下来延迟枚举
    /// —— 典型如 WPF 里 `Dispatcher.BeginInvoke(() => foreach (batch))` —— 会读到被清空/换批的
    /// 内容，或直接抛「集合已修改；可能无法执行枚举操作」。
    /// </summary>
    [Fact]
    public void Batch_list_is_a_snapshot_safe_to_consume_after_callback_returns()
    {
        var captured = new List<IReadOnlyList<IEventEnvelope<TimeoutError>>>();
        var received = 0;
        var allDelivered = new ManualResetEventSlim(false);

        using var sub = _bus.SubscribeBatch<TimeoutError>(
            b =>
            {
                lock (captured)
                {
                    captured.Add(b);          // 只存引用：模拟宿主延迟消费
                    received += b.Count;
                    if (received >= 6) allDelivered.Set();
                }
            },
            maxBatchSize: 2,                  // 6 条事件至少攒 3 批，保证复用缓冲被推进多轮
            maxBatchDelay: TimeSpan.FromMilliseconds(30));

        for (var i = 0; i < 6; i++) _bus.Emit(new TimeoutError(Info()));

        Assert.True(allDelivered.Wait(TimeSpan.FromSeconds(5)), "未收齐 6 条");
        Thread.Sleep(200);   // 让泵线程再转一轮：修复前这一步会把旧引用指向的缓冲清空

        List<IReadOnlyList<IEventEnvelope<TimeoutError>>> batches;
        lock (captured) batches = new List<IReadOnlyList<IEventEnvelope<TimeoutError>>>(captured);
        Assert.True(batches.Count >= 2, "至少应攒出两批，否则覆盖不到复用缓冲场景");

        // 延迟消费：回调早已返回，此刻枚举之前拿到的每批
        var seqs = new List<long>();
        foreach (var batch in batches)
        {
            foreach (var env in batch) seqs.Add(env.Seq);
        }

        seqs.Sort();
        Assert.Equal(new long[] { 1, 2, 3, 4, 5, 6 }, seqs);   // 各批互不干扰：1..6 恰好各一次
    }

    [Fact]
    public void Unsubscribe_stops_delivery()
    {
        var count = 0L;
        var sub = _bus.Subscribe<TimeoutError>(_ => Interlocked.Increment(ref count), DeliveryMode.Inline);

        _bus.Emit(new TimeoutError(Info()));
        Assert.Equal(1, Interlocked.Read(ref count));

        sub.Dispose();
        Assert.False(sub.IsActive);
        Assert.Equal(0, _bus.Metrics.ActiveSubscriptions);

        _bus.Emit(new TimeoutError(Info()));
        Assert.Equal(1, Interlocked.Read(ref count));
    }
}
