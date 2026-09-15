using System;
using System.Collections.Generic;
using SuperSampler.Abstractions.Events;
using SuperSampler.Abstractions.Values;
using SuperSampler.Core.Config;
using SuperSampler.Core.Events;
using SuperSampler.Core.Runtime;
using SuperSampler.UnitTests.Support;
using Xunit;

namespace SuperSampler.UnitTests.Runtime;

/// <summary>报警状态机测试：用假值直接喂 AlarmEngine.Evaluate，验证激活/延时/清除/数字量/确认事件。</summary>
public class AlarmEngineTests
{
    private readonly InProcessEventBus _bus = new();

    private readonly List<AlarmRaisedEvent> _raised = new();
    private readonly List<AlarmClearedEvent> _cleared = new();
    private readonly List<AlarmAcknowledgedEvent> _acked = new();

    /// <summary>订阅三类报警事件，Inline 模式同步捕获。</summary>
    private AlarmEngine NewEngine()
    {
        _bus.Subscribe<AlarmRaisedEvent>(e => _raised.Add(e.Body), DeliveryMode.Inline);
        _bus.Subscribe<AlarmClearedEvent>(e => _cleared.Add(e.Body), DeliveryMode.Inline);
        _bus.Subscribe<AlarmAcknowledgedEvent>(e => _acked.Add(e.Body), DeliveryMode.Inline);
        return new AlarmEngine(_bus);
    }

    private static RuntimePoint Point(Action<RuntimePointFactory.RuntimePointConfig>? configure = null)
        => RuntimePointFactory.Point(configure);

    private static PointValue Good(double value)
        => PointValue.Good(value, DateTimeOffset.UtcNow);

    [Fact]
    public void High_alarm_above_limit_raises_immediately()
    {
        var engine = NewEngine();
        var point = Point(c => c.Alarms.Add(new AlarmConfig { Type = "high", Limit = 10 }));

        engine.Evaluate(point, Good(11));

        var ev = Assert.Single(_raised);
        Assert.Equal("p#high", ev.AlarmId);
        Assert.Equal("high", ev.AlarmType);
        Assert.Equal(10.0, ev.Limit);
        Assert.Equal(11.0, ev.Value);
    }

    [Fact]
    public void High_alarm_below_limit_does_not_raise()
    {
        var engine = NewEngine();
        var point = Point(c => c.Alarms.Add(new AlarmConfig { Type = "high", Limit = 10 }));

        engine.Evaluate(point, Good(9));

        Assert.Empty(_raised);
    }

    [Fact]
    public void Delayed_alarm_first_feed_only_arms_pending()
    {
        // DelayMs 到位前不激活：第一次越限只是挂起（不触发事件）。
        var engine = NewEngine();
        var point = Point(c => c.Alarms.Add(new AlarmConfig { Type = "high", Limit = 10, DelayMs = 5000 }));

        engine.Evaluate(point, Good(99));

        Assert.Empty(_raised);
    }

    [Fact]
    public void Clearing_dropping_below_limit_emits_cleared()
    {
        var engine = NewEngine();
        var point = Point(c => c.Alarms.Add(new AlarmConfig { Type = "high", Limit = 10 }));

        engine.Evaluate(point, Good(11)); // raise
        engine.Evaluate(point, Good(9));  // drop below → clear

        Assert.Single(_raised);
        var ev = Assert.Single(_cleared);
        Assert.Equal("p#high", ev.AlarmId);
        Assert.Equal("high", ev.AlarmType);
    }

    [Fact]
    public void Latching_alarm_requires_acknowledge_flow()
    {
        var engine = NewEngine();
        var point = Point(c => c.Alarms.Add(new AlarmConfig
        {
            Id = "alarm1", Type = "high", Limit = 10, Latch = true,
        }));

        engine.Evaluate(point, Good(11));
        Assert.Single(_raised);
        Assert.Equal("alarm1", _raised[0].AlarmId);

        // 人工确认 → 发确认事件（携带操作者）
        engine.Acknowledge("dev1", "p", "alarm1", "operator");
        var ack = Assert.Single(_acked);
        Assert.Equal("alarm1", ack.AlarmId);
        Assert.Equal("operator", ack.User);
    }

    [Fact]
    public void Digital_alarm_raises_on_true_and_clears_on_false()
    {
        var engine = NewEngine();
        var point = Point(c => c.Alarms.Add(new AlarmConfig { Type = "digital" }));

        engine.Evaluate(point, PointValue.Good(true, DateTimeOffset.UtcNow));
        Assert.Single(_raised);

        engine.Evaluate(point, PointValue.Good(false, DateTimeOffset.UtcNow));
        Assert.Single(_cleared);
    }

    [Fact]
    public void Low_alarm_raises_below_limit()
    {
        var engine = NewEngine();
        var point = Point(c => c.Alarms.Add(new AlarmConfig { Type = "low", Limit = 0 }));

        engine.Evaluate(point, Good(-1));

        var ev = Assert.Single(_raised);
        Assert.Equal("low", ev.AlarmType);
    }

    [Fact]
    public void Bad_quality_value_is_ignored_and_preserves_state()
    {
        var engine = NewEngine();
        var point = Point(c => c.Alarms.Add(new AlarmConfig { Type = "high", Limit = 10 }));

        // 越限激活
        engine.Evaluate(point, Good(11));
        Assert.Single(_raised);

        // 坏值不参与评估：保持原状态，不产生新事件
        engine.Evaluate(point, PointValue.Bad("ss.reason.comm", DateTimeOffset.UtcNow));
        engine.Evaluate(point, PointValue.Uncertain(11, "ss.reason.comm", DateTimeOffset.UtcNow));

        // 仍未激活？No——已激活保持。断言不产生清除/重复激活事件
        engine.Evaluate(point, Good(11)); // 仍在限值上，未变化 → 不应再次 raise
        Assert.Single(_raised);
        Assert.Empty(_cleared);
    }

    [Fact]
    public void Non_numeric_value_on_high_alarm_is_skipped()
    {
        var engine = NewEngine();
        var point = Point(c => c.Alarms.Add(new AlarmConfig { Type = "high", Limit = 10 }));

        // 字符串值无法用高限判断 → IsCrossed 返回 null，静默跳过
        engine.Evaluate(point, PointValue.Good("abc", DateTimeOffset.UtcNow));
        Assert.Empty(_raised);
    }

    [Fact]
    public void Deadband_prevents_churn_near_limit()
    {
        // 高限 50、死区 10。激活后值仍高于限值（落在 50..60 死区带内）不应清除，
        // 须回落到 限值−死区=40 之下才清 —— 否则限值附近来回刷（findings D4）。
        var engine = NewEngine();
        var point = Point(c => c.Alarms.Add(new AlarmConfig { Type = "high", Limit = 50, Deadband = 10 }));

        engine.Evaluate(point, Good(60)); // 越限 → 激活
        Assert.Single(_raised);

        // 值回到 60 与 50 之间的死区带，但仍在限值之上 → 不抖动、不清除
        engine.Evaluate(point, Good(55));
        engine.Evaluate(point, Good(52));
        Assert.Empty(_cleared);
        Assert.Single(_raised);

        // 真正回落到 40 之下才清除
        engine.Evaluate(point, Good(38));
        Assert.Single(_cleared);
    }

    [Fact]
    public void Latched_alarm_does_not_retrigger_before_acknowledge()
    {
        // 锁存语义（findings D6）：清除后未确认前不允许重新触发。
        var engine = NewEngine();
        var point = Point(c => c.Alarms.Add(new AlarmConfig
        {
            Id = "alarm1", Type = "high", Limit = 50, Latch = true,
        }));

        engine.Evaluate(point, Good(60)); // 激活
        Assert.Single(_raised);

        engine.Evaluate(point, Good(40)); // 回落 → 清除（但仍锁存待确认）
        Assert.Single(_cleared);

        engine.Evaluate(point, Good(60)); // 条件再次满足——未确认前不得重触发
        Assert.Single(_raised);

        engine.Acknowledge("dev1", "p", "alarm1", "operator");
        Assert.Single(_acked);

        engine.Evaluate(point, Good(60)); // 确认后允许再次激活
        Assert.Equal(2, _raised.Count);
    }
}