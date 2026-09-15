using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SuperSampler.Abstractions.Events;
using SuperSampler.Core.Config;
using SuperSampler.Core.Events;
using Xunit;

namespace SuperSampler.UnitTests.Events;

/// <summary>
/// 事件总线补测（docs/07 测试计划 §3.2 G-R 族）：I18n 代入口径、Wait 溢出阻塞语义、
/// 批量订阅 maxBatchDelay 到期投递与总数守恒。
/// </summary>
public class EventBusGapTests
{
    private sealed class TickEvent : IEvent
    {
        public TickEvent(int n) => N = n;
        public int N { get; }
        public EventCategory Category => EventCategory.Performance;
        public EventLevel Level => EventLevel.Debug;
    }

    // ─────────────── G-R-3：I18n Substitute 口径 ───────────────

    [Fact]
    public void Gr3_i18n_substitute_replaces_known_keeps_unknown()
    {
        var catalog = new I18nCatalog();
        catalog.Add("PT_NAME", "主温度");
        catalog.Add("UNIT_C", "℃");

        Assert.Equal("主温度 [℃]", catalog.Substitute("${PT_NAME} [${UNIT_C}]"));
        // 未命中的 key 原样保留（不删除、不抛）
        Assert.Equal("${MISSING} 主温度", catalog.Substitute("${MISSING} ${PT_NAME}"));
        // 无 ${ 的文本原样返回（快路径）
        Assert.Equal("plain text", catalog.Substitute("plain text"));
        // 空串安全
        Assert.Equal(string.Empty, catalog.Substitute(string.Empty));
    }

    // ─────────────── G-R-4：Wait 溢出策略——队列满时 Emit 阻塞，不丢不崩 ───────────────

    [Fact]
    public async Task Gr4_wait_overflow_blocks_emitter_when_queue_full()
    {
        using var bus = new InProcessEventBus();
        using var gate = new ManualResetEventSlim(false);

        // 容量 1 的 Wait 订阅者，handler 挂住 → 泵占住第一条，队列进第二条，第三条 Emit 阻塞
        var delivered = new List<int>();
        bus.Subscribe<TickEvent>(
            e => { gate.Wait(); lock (delivered) delivered.Add(e.Body.N); },
            DeliveryMode.Queued, OverflowPolicy.Wait, queueCapacity: 1);

        bus.Emit(new TickEvent(1));
        bus.Emit(new TickEvent(2)); // 入队（容量 1 满）

        var third = Task.Run(() => bus.Emit(new TickEvent(3)));
        await Task.Delay(300);
        Assert.False(third.IsCompleted, "队列满时 Emit 应阻塞，不应立即返回");

        gate.Set(); // 放行第一条 → 泵腾出空间 → 第三条入队 → Emit 解除阻塞
        var finished = await Task.WhenAny(third, Task.Delay(5_000));
        Assert.Same(third, finished);

        // Wait 语义：零丢弃
        var sub = bus.Metrics;
        Assert.Equal(0, sub.TotalDropped);
        Assert.True(SpinWait.SpinUntil(() => { lock (delivered) return delivered.Count == 3; }, 5_000));
        Assert.Equal(new[] { 1, 2, 3 }, delivered.OrderBy(n => n));
    }

    // ─────────────── G-R-5：批量订阅 maxBatchDelay 到期不满批也投递，总数守恒 ───────────────

    [Fact]
    public void Gr5_batch_subscriber_flushes_on_maxBatchDelay_and_conserves_total()
    {
        using var bus = new InProcessEventBus();
        var batches = new List<List<int>>();
        using var firstBatch = new ManualResetEventSlim(false);

        bus.SubscribeBatch<TickEvent>(
            batch => { lock (batches) { batches.Add(batch.Select(e => e.Body.N).ToList()); } firstBatch.Set(); },
            maxBatchSize: 10,
            maxBatchDelay: TimeSpan.FromMilliseconds(120));

        bus.Emit(new TickEvent(1));
        bus.Emit(new TickEvent(2)); // 只有 2 条 < maxBatchSize=10

        Assert.True(firstBatch.Wait(5_000), "maxBatchDelay 到期后不满批也应投递");
        Assert.True(SpinWait.SpinUntil(() => { lock (batches) return batches.Count == 1; }, 5_000));

        // 满批立即投递 + 总数守恒
        using var done = new ManualResetEventSlim(false);
        for (var i = 3; i <= 12; i++) bus.Emit(new TickEvent(i)); // 凑满 10 条
        SpinWait.SpinUntil(() => { lock (batches) return batches.Sum(b => b.Count) == 12; }, 5_000);

        lock (batches)
        {
            Assert.Equal(12, batches.Sum(b => b.Count));
            Assert.Contains(batches, b => b.Count == 10); // 满批立即发
        }
        Assert.Equal(0, bus.Metrics.TotalDropped);
        Assert.Equal(12, bus.Metrics.TotalEmitted);
    }
}
