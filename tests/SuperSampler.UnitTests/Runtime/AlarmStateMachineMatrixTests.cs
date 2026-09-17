using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using SuperSampler.Abstractions.Events;
using SuperSampler.Abstractions.Values;
using SuperSampler.Core.Config;
using SuperSampler.Core.Events;
using SuperSampler.Core.Runtime;
using SuperSampler.UnitTests.Support;
using Xunit;

namespace SuperSampler.UnitTests.Runtime;

/// <summary>
/// 报警状态机矩阵（本轮重测：五种类型 / delayMs / deadband / latch+确认键 / 质量交互 / 事件字段 / 多报警并存 / 风暴）。
/// 全部为**纯单测**：直接喂 <see cref="PointValue"/> 给 <see cref="AlarmEngine.Evaluate"/>，不起端口、不起轮询线程。
/// 与既有 <c>AlarmEngineTests</c>（12 例主分支）与 <c>AlarmEngineGapTests</c>（G-A 族 6 例）不重复：
/// 本类补的是逐型双向穷举、边界与非法值形态、延时/死区方向与计时器行为、确认键冲突面、
/// 质量挂起口径、事件字段自洽性、跨点位独立性、报警风暴与并发确认。
/// 覆盖条目对应 <c>_testplan/模块重测-报警事件-方案.md</c> §一–§七。
/// </summary>
public class AlarmStateMachineMatrixTests
{
    // ═══════════════ 装配 ═══════════════

    private sealed class Harness
    {
        public Harness()
        {
            Bus.Subscribe<AlarmRaisedEvent>(e => Raised.Add(e.Body), DeliveryMode.Inline);
            Bus.Subscribe<AlarmClearedEvent>(e => Cleared.Add(e.Body), DeliveryMode.Inline);
            Bus.Subscribe<AlarmAcknowledgedEvent>(e => Acknowledged.Add(e.Body), DeliveryMode.Inline);
            Engine = new AlarmEngine(Bus);
        }

        public InProcessEventBus Bus { get; } = new();

        public AlarmEngine Engine { get; }

        public List<AlarmRaisedEvent> Raised { get; } = new();

        public List<AlarmClearedEvent> Cleared { get; } = new();

        public List<AlarmAcknowledgedEvent> Acknowledged { get; } = new();

        /// <summary>本点位状态机跳变次数（激活 + 清除）。</summary>
        public int Transitions => Raised.Count + Cleared.Count;
    }

    private static PointValue Good(double value) => PointValue.Good(value, DateTimeOffset.UtcNow);

    private static PointValue GoodBool(bool value) => PointValue.Good(value, DateTimeOffset.UtcNow);

    private static RuntimePoint Point(string pointId, params AlarmConfig[] alarms)
    {
        var point = RuntimePointFactory.Point(c =>
        {
            c.Id = pointId;
            foreach (var alarm in alarms) c.Alarms.Add(alarm);
        });
        return point;
    }

    private static AlarmConfig High(double limit, string id = "", double deadband = 0, int delayMs = 0,
        bool latch = false, bool ackRequired = false, string? priority = null, string? message = null)
        => new()
        {
            Id = id, Type = "high", Limit = limit, Deadband = deadband, DelayMs = delayMs,
            Latch = latch, AckRequired = ackRequired, Priority = priority, Message = message,
        };

    // ═══════════════ §一 五种类型：激活与清除双向 ═══════════════

    [Fact]
    public void High_and_highhigh_raise_above_the_limit_and_clear_below_it()
    {
        var h = new Harness();
        var point = Point("t1", High(10, "h10"), new AlarmConfig { Id = "h20", Type = "highhigh", Limit = 20 });

        h.Engine.Evaluate(point, Good(25));
        Assert.Equal(2, h.Raised.Count);
        Assert.Equal(10.0, h.Raised.Single(r => r.AlarmId == "h10").Limit!.Value);
        Assert.Equal(20.0, h.Raised.Single(r => r.AlarmId == "h20").Limit!.Value);
        Assert.Empty(h.Cleared);

        h.Engine.Evaluate(point, Good(15));   // ≤ 20 → highHigh 清；> 10 → high 保持
        Assert.Equal("h20", Assert.Single(h.Cleared).AlarmId);

        h.Engine.Evaluate(point, Good(5));    // ≤ 10 → high 清
        Assert.Equal(2, h.Cleared.Count);
    }

    [Fact]
    public void Low_and_lowlow_raise_below_the_limit_and_clear_above_it()
    {
        var h = new Harness();
        var point = Point("t2", new AlarmConfig { Id = "l10", Type = "low", Limit = 10 },
            new AlarmConfig { Id = "l0", Type = "lowlow", Limit = 0 });

        h.Engine.Evaluate(point, Good(-5));
        Assert.Equal(2, h.Raised.Count);
        Assert.Equal(10.0, h.Raised.Single(r => r.AlarmId == "l10").Limit!.Value);
        Assert.Equal(0.0, h.Raised.Single(r => r.AlarmId == "l0").Limit!.Value);

        h.Engine.Evaluate(point, Good(5));    // ≥ 0 → lowLow 清；< 10 → low 保持
        Assert.Equal("l0", Assert.Single(h.Cleared).AlarmId);

        h.Engine.Evaluate(point, Good(11));   // ≥ 10 → low 清
        Assert.Equal(2, h.Cleared.Count);
    }

    [Fact]
    public void Values_exactly_on_the_limit_are_not_crossed()
    {
        var h = new Harness();
        var high = Point("t3", High(10, "h"));
        var low = Point("t4", new AlarmConfig { Id = "l", Type = "low", Limit = 10 });

        h.Engine.Evaluate(high, Good(10));    // > 才越限
        h.Engine.Evaluate(low, Good(10));     // < 才越限
        Assert.Empty(h.Raised);

        h.Engine.Evaluate(high, Good(10.0001));
        h.Engine.Evaluate(low, Good(9.9999));
        Assert.Equal(2, h.Raised.Count);
    }

    [Fact]
    public void Digital_alarm_raises_on_true_and_clears_on_false_including_numeric_bits()
    {
        var h = new Harness();
        var boolean = Point("t5", new AlarmConfig { Id = "b", Type = "digital" });
        var numeric = Point("t6", new AlarmConfig { Id = "n", Type = "digital" });

        // 布尔形态：真 = 报警（不取反）
        h.Engine.Evaluate(boolean, GoodBool(true));
        Assert.Equal("b", Assert.Single(h.Raised).AlarmId);
        h.Engine.Evaluate(boolean, GoodBool(false));
        Assert.Equal("b", Assert.Single(h.Cleared).AlarmId);

        // 数值形态（0/1 位）：非 0 = 报警
        h.Engine.Evaluate(numeric, PointValue.Good((ushort)1, DateTimeOffset.UtcNow));
        Assert.Equal(2, h.Raised.Count);
        h.Engine.Evaluate(numeric, PointValue.Good((ushort)0, DateTimeOffset.UtcNow));
        Assert.Equal(2, h.Cleared.Count);
    }

    [Fact]
    public void Digital_alarm_with_limit_zero_inverts_the_bit_meaning()
    {
        // 口径（代码 `b == (Limit != 0)`）：digital 报警带 limit 时按「与 limit 的比较结果」取含义，
        // limit="0" ⇒ 值为**假**才算越限（取反），limit 非 0（如 true/1）⇒ 值为真算越限。
        var h = new Harness();
        var inverted = Point("t7", new AlarmConfig { Id = "inv", Type = "digital", Limit = 0 });
        var normal = Point("t8", new AlarmConfig { Id = "norm", Type = "digital", Limit = 1 });

        h.Engine.Evaluate(inverted, GoodBool(true));
        Assert.Empty(h.Raised);
        h.Engine.Evaluate(inverted, GoodBool(false));
        Assert.Equal("inv", Assert.Single(h.Raised).AlarmId);

        h.Engine.Evaluate(normal, GoodBool(false));         // limit 非 0 ⇒ 值为假不算越限
        Assert.Single(h.Raised);
        h.Engine.Evaluate(normal, GoodBool(true));          // 值为真 ⇒ 越限
        Assert.Equal(2, h.Raised.Count);
        Assert.Equal("norm", h.Raised[1].AlarmId);
    }

    [Fact]
    public void Non_numeric_values_never_cross_any_alarm_type_and_never_throw()
    {
        var h = new Harness();
        var high = Point("t9", High(10, "h"));
        var digital = Point("t10", new AlarmConfig { Id = "d", Type = "digital" });
        var text = PointValue.Good("abc", DateTimeOffset.UtcNow);

        h.Engine.Evaluate(high, text);
        h.Engine.Evaluate(digital, text);
        h.Engine.Evaluate(high, PointValue.Good(new[] { 1, 2 }, DateTimeOffset.UtcNow));

        Assert.Empty(h.Raised);
        Assert.Empty(h.Cleared);
    }

    [Fact]
    public void Every_numeric_value_form_is_comparable_for_threshold_alarms()
    {
        var h = new Harness();
        var ts = DateTimeOffset.UtcNow;
        // 全部取「限值之上」的正数：本用例只验「每种数值形态都能比较」，
        // 有符号负值走 low/lowLow 用例（见 Low_and_lowlow_*）。
        var forms = new object[]
        {
            (ushort)20, (short)20, 20, 20u, 20L, 20UL, 20f, 20d, 20m, (byte)20, (sbyte)20,
        };

        var index = 0;
        foreach (var form in forms)
        {
            var point = Point("tf" + index++, High(10, "h"));
            h.Engine.Evaluate(point, PointValue.Good(form, ts));
        }

        Assert.Equal(forms.Length, h.Raised.Count);
    }

    // ═══════════════ §二 delayMs 延时 ═══════════════

    [Fact]
    public void Delayed_alarm_raises_exactly_once_after_the_delay_elapses()
    {
        var h = new Harness();
        var point = Point("d1", High(10, "dl", delayMs: 300));

        h.Engine.Evaluate(point, Good(50));
        h.Engine.Evaluate(point, Good(50));
        Assert.Empty(h.Raised);                       // 延时未到：只挂起，不激活

        Thread.Sleep(400);
        h.Engine.Evaluate(point, Good(50));           // 到点 → 激活恰好一次
        h.Engine.Evaluate(point, Good(50));           // 状态已是 Active → 不重复发
        h.Engine.Evaluate(point, Good(60));

        Assert.Single(h.Raised);
    }

    [Fact]
    public void Delay_timer_resets_when_the_value_returns_to_normal_mid_window()
    {
        var h = new Harness();
        var point = Point("d2", High(10, "dl", delayMs: 400));

        h.Engine.Evaluate(point, Good(50));           // 起计时
        Thread.Sleep(200);
        h.Engine.Evaluate(point, Good(5));            // 半程回正常 → 计时器复位
        h.Engine.Evaluate(point, Good(50));           // 重新计时的起点

        Thread.Sleep(250);                            // 距重新计时 250ms < 400
        h.Engine.Evaluate(point, Good(50));
        Assert.Empty(h.Raised);                       // 若复位失效（沿用旧起点），这里就会已经激活 → 反证点

        Thread.Sleep(250);                            // 累计 500ms > 400
        h.Engine.Evaluate(point, Good(50));
        Assert.Single(h.Raised);
    }

    [Fact]
    public void Delay_accumulator_is_not_cleared_by_bad_values()
    {
        var h = new Harness();
        var point = Point("d3", High(10, "dl", delayMs: 300));

        h.Engine.Evaluate(point, Good(50));            // 起计时
        Thread.Sleep(400);                             // 延时已走完
        h.Engine.Evaluate(point, PointValue.Bad("ss.reason.comm", DateTimeOffset.UtcNow));
        h.Engine.Evaluate(point, PointValue.Offline(DateTimeOffset.UtcNow));
        h.Engine.Evaluate(point, PointValue.Uncertain(50, "ss.reason.nan", DateTimeOffset.UtcNow));
        Assert.Empty(h.Raised);                        // 坏值/不确定期间不评估、也不激活

        h.Engine.Evaluate(point, Good(50));            // 恢复好值：累加器未清零 → 立即激活
        Assert.Single(h.Raised);
    }

    [Fact]
    public void Clearing_is_immediate_and_not_gated_by_delay()
    {
        var h = new Harness();
        var point = Point("d4", High(10, "dl", delayMs: 300));

        h.Engine.Evaluate(point, Good(50));
        Thread.Sleep(400);
        h.Engine.Evaluate(point, Good(50));
        Assert.Single(h.Raised);

        h.Engine.Evaluate(point, Good(5));             // 回落 → 立即清除（delayMs 只作用于激活侧）
        Assert.Single(h.Cleared);
    }

    // ═══════════════ §三 deadband 死区（D4 方向）═══════════════

    [Fact]
    public void High_deadband_requires_a_drop_below_limit_minus_deadband()
    {
        var h = new Harness();
        var point = Point("b1", High(50, "h", deadband: 10));

        h.Engine.Evaluate(point, Good(60));
        Assert.Single(h.Raised);

        foreach (var inBand in new[] { 59.0, 55.0, 50.0, 45.0, 41.0, 40.1 })
        {
            h.Engine.Evaluate(point, Good(inBand));    // 死区带内（含限值与 限值−死区 边界）
        }

        Assert.Empty(h.Cleared);
        Assert.Single(h.Raised);

        h.Engine.Evaluate(point, Good(39.9));          // 低于 限值−死区 → 清
        Assert.Single(h.Cleared);
    }

    [Fact]
    public void Low_deadband_requires_a_rise_above_limit_plus_deadband()
    {
        var h = new Harness();
        var point = Point("b2", new AlarmConfig { Id = "l", Type = "low", Limit = 50, Deadband = 10 });

        h.Engine.Evaluate(point, Good(40));
        Assert.Single(h.Raised);

        foreach (var inBand in new[] { 41.0, 45.0, 50.0, 55.0, 59.0, 59.9 })
        {
            h.Engine.Evaluate(point, Good(inBand));
        }

        Assert.Empty(h.Cleared);

        h.Engine.Evaluate(point, Good(60.1));
        Assert.Single(h.Cleared);
    }

    [Fact]
    public void Chattering_inside_the_deadband_produces_zero_state_transitions()
    {
        var h = new Harness();
        var point = Point("b3", High(50, "h", deadband: 10));

        // 值在「限值附近」来回抖动但始终没进报警侧（49 ≤ 50）→ 状态机全程不动
        foreach (var v in new[] { 49.0, 50.0, 41.0, 49.0, 50.0, 41.0, 49.0 })
        {
            h.Engine.Evaluate(point, Good(v));
        }

        Assert.Equal(0, h.Transitions);

        h.Engine.Evaluate(point, Good(60));            // 真正越限 → 激活一次
        Assert.Single(h.Raised);

        // 激活后在死区带内抖动：不清、不重发
        foreach (var v in new[] { 55.0, 51.0, 49.0, 55.0, 45.0 })
        {
            h.Engine.Evaluate(point, Good(v));
        }

        Assert.Single(h.Raised);
        Assert.Empty(h.Cleared);
    }

    [Fact]
    public void Zero_deadband_clears_at_the_limit_boundary()
    {
        var h = new Harness();
        var point = Point("b4", High(50, "h"));

        h.Engine.Evaluate(point, Good(51));
        Assert.Single(h.Raised);

        h.Engine.Evaluate(point, Good(50.0001));       // 仍在限值上 → 不清
        Assert.Empty(h.Cleared);

        h.Engine.Evaluate(point, Good(50));            // 恰好等于限值（≤ limit−0）→ 清
        Assert.Single(h.Cleared);
    }

    // ═══════════════ §四 latch / 确认 / 确认键 ═══════════════

    [Fact]
    public void Latched_alarm_does_not_retrigger_until_acknowledged_then_rearms()
    {
        var h = new Harness();
        var point = Point("l1", High(50, "A1", latch: true));

        h.Engine.Evaluate(point, Good(60));
        Assert.Single(h.Raised);

        h.Engine.Evaluate(point, Good(40));            // 清除但仍锁存
        Assert.Single(h.Cleared);

        h.Engine.Evaluate(point, Good(60));            // 未确认 → 不重触发（D6）
        h.Engine.Evaluate(point, Good(70));
        Assert.Single(h.Raised);

        Assert.True(h.Engine.TryAcknowledge("dev1", "l1", "A1", "op-1"));
        Assert.Equal("op-1", Assert.Single(h.Acknowledged).User);

        h.Engine.Evaluate(point, Good(60));            // 确认后重新武装
        Assert.Equal(2, h.Raised.Count);
    }

    [Fact]
    public void Ack_required_without_latch_gates_retriggering_the_same_way()
    {
        var h = new Harness();
        var point = Point("l2", High(50, "A2", ackRequired: true));

        h.Engine.Evaluate(point, Good(60));
        h.Engine.Evaluate(point, Good(40));
        h.Engine.Evaluate(point, Good(60));
        Assert.Single(h.Raised);                       // AckPending = Latch || AckRequired

        Assert.True(h.Engine.TryAcknowledge("dev1", "l2", "A2", "op"));
        h.Engine.Evaluate(point, Good(60));
        Assert.Equal(2, h.Raised.Count);
    }

    [Fact]
    public void Acknowledging_while_the_alarm_is_still_active_rearms_it()
    {
        var h = new Harness();
        var point = Point("l3", High(50, "A3", latch: true));

        h.Engine.Evaluate(point, Good(60));            // 激活且仍越限
        Assert.True(h.Engine.TryAcknowledge("dev1", "l3", "A3", "op"));   // 越限期间确认

        h.Engine.Evaluate(point, Good(40));            // 清除
        Assert.Single(h.Cleared);
        h.Engine.Evaluate(point, Good(60));            // 已确认 → 可再次激活
        Assert.Equal(2, h.Raised.Count);
    }

    [Fact]
    public void Default_alarm_id_is_point_and_type_so_acknowledge_works_without_an_id_attribute()
    {
        var h = new Harness();
        var point = Point("l4", High(10, string.Empty, latch: true));

        h.Engine.Evaluate(point, Good(50));
        Assert.Equal("l4#high", Assert.Single(h.Raised).AlarmId);

        h.Engine.Evaluate(point, Good(5));
        h.Engine.Evaluate(point, Good(50));
        Assert.Single(h.Raised);

        Assert.True(h.Engine.TryAcknowledge("dev1", "l4", "l4#high", "op"));   // D9：同一套键
        h.Engine.Evaluate(point, Good(50));
        Assert.Equal(2, h.Raised.Count);
    }

    [Fact]
    public void Different_alarm_types_on_one_point_keep_independent_keys()
    {
        var h = new Harness();
        var point = Point("l5", High(10, string.Empty), new AlarmConfig { Type = "low", Limit = -10 });

        h.Engine.Evaluate(point, Good(50));
        Assert.Equal("l5#high", Assert.Single(h.Raised).AlarmId);

        h.Engine.Evaluate(point, Good(-50));
        Assert.Equal(2, h.Raised.Count);
        Assert.Contains(h.Raised, r => r.AlarmId == "l5#low");

        // 两个键各自独立确认（互不影响）
        Assert.True(h.Engine.TryAcknowledge("dev1", "l5", "l5#high", "op"));
        Assert.True(h.Engine.TryAcknowledge("dev1", "l5", "l5#low", "op"));
        Assert.Equal(2, h.Acknowledged.Count);
    }

    [Fact]
    public void Two_threshold_alarms_of_the_same_type_without_ids_are_rejected_at_load()
    {
        // findings D68 / CGV-36：两条同类型（limit 10 / 20）都不写 Alarm@id ⇒ 运行期状态键都是 pointId#high
        // （ADR D9 的缺省键口径）：第二条永远发不出自己的激活事件；值从 25 回落到 15（**仍在 10 之上**）
        // 会被另一条的清除判定当成已恢复 → 假清除。加载期直接拒绝这种配置。
        // （口径边界：写了两条不同 id 就合法且各自独立，见 Same_type_alarms_with_distinct_ids_stay_independent。）
        var ex = Assert.Throws<ConfigValidationException>(() => SamplerConfigLoader.Load(
            XDocument.Parse("""
                <SamplerConfig schemaVersion="3.0">
                  <Transports><Transport id="tcp1" host="127.0.0.1" /></Transports>
                  <Devices><Device id="d1" transport="tcp1" pointSet="ps1" /></Devices>
                  <PointSets><PointSet id="ps1"><Points>
                    <Point id="l6" address="0">
                      <Alarm type="high" limit="10" />
                      <Alarm type="high" limit="20" />
                    </Point>
                  </Points></PointSet></PointSets>
                </SamplerConfig>
                """), Directory.GetCurrentDirectory()));

        Assert.Contains(ex.Errors, e => e.Contains("l6") && e.Contains("报警键冲突") && e.Contains("l6#high"));
    }


    [Fact]
    public void Same_type_alarms_with_distinct_ids_stay_independent()
    {
        // 与上条对照：只要写了两条不同的 Alarm@id，撞键问题消失（口径边界）
        var h = new Harness();
        var point = Point("l7", High(10, "h10"), High(20, "h20"));

        h.Engine.Evaluate(point, Good(25));
        Assert.Equal(2, h.Raised.Count);
        Assert.Equal(new[] { "h10", "h20" }, h.Raised.Select(r => r.AlarmId).OrderBy(x => x).ToArray());

        h.Engine.Evaluate(point, Good(15));
        Assert.Equal("h20", Assert.Single(h.Cleared).AlarmId);

        h.Engine.Evaluate(point, Good(5));
        Assert.Equal(2, h.Cleared.Count);
    }

    [Fact]
    public void Acknowledging_an_alarm_that_never_raised_reports_no_pending_and_emits_nothing()
    {
        // docs/03：未配置/从未触发的 alarmId 返回 NotPending 且不发事件。
        // 状态记录在首次评估时就建立，所以「值一直在正常区」也必须走 NotPending——
        // 否则正常态点位能被"确认"出一条审计事件（findings D74，本轮修复）。
        var h = new Harness();
        var point = Point("l8", High(10, "A8", latch: true));

        h.Engine.Evaluate(point, Good(5));              // 评估过、但从未越限
        h.Engine.Evaluate(point, Good(9.9));

        Assert.False(h.Engine.TryAcknowledge("dev1", "l8", "A8", "op"));
        Assert.Empty(h.Acknowledged);

        // 真正触发过之后，确认才有意义
        h.Engine.Evaluate(point, Good(50));
        Assert.True(h.Engine.TryAcknowledge("dev1", "l8", "A8", "op"));
        Assert.Single(h.Acknowledged);
    }

    [Fact]
    public void Acknowledge_is_silent_for_unknown_alarm_ids_and_never_evaluated_points()
    {
        var h = new Harness();
        var point = Point("l9", High(10, "A9", latch: true));

        Assert.False(h.Engine.TryAcknowledge("dev1", "l9", "A9", "op"));          // 从未评估过
        h.Engine.Evaluate(point, Good(50));
        Assert.False(h.Engine.TryAcknowledge("dev1", "l9", "不存在", "op"));       // 配置里没有的 id
        Assert.False(h.Engine.TryAcknowledge("dev1", "别的点位", "A9", "op"));
        Assert.False(h.Engine.TryAcknowledge("别的设备", "l9", "A9", "op"));
        Assert.Empty(h.Acknowledged);
    }

    [Fact]
    public void Concurrent_acknowledges_of_the_same_alarm_are_safe_and_consistent()
    {
        var h = new Harness();
        var point = Point("l10", High(10, "A10", latch: true));

        h.Engine.Evaluate(point, Good(50));
        h.Engine.Evaluate(point, Good(5));
        h.Engine.Evaluate(point, Good(50));            // 未确认 → 不重触发
        Assert.Single(h.Raised);

        var succeeded = 0;
        var threads = new List<Thread>();
        for (var i = 0; i < 8; i++)
        {
            var th = new Thread(() => { if (h.Engine.TryAcknowledge("dev1", "l10", "A10", "op")) Interlocked.Increment(ref succeeded); });
            threads.Add(th);
        }

        foreach (var th in threads) th.Start();
        Assert.True(threads.All(th => th.Join(TimeSpan.FromSeconds(5))), "并发确认不应死锁");
        Assert.Equal(8, Volatile.Read(ref succeeded));   // 已触发过：重复确认按既有口径都算成功
        Assert.Equal(8, h.Acknowledged.Count);

        h.Engine.Evaluate(point, Good(60));              // 状态自洽：已重新武装
        Assert.Equal(2, h.Raised.Count);
    }

    // ═══════════════ §五 质量与报警交互 ═══════════════

    [Fact]
    public void Bad_and_offline_values_suspend_evaluation_without_clearing_or_repeating()
    {
        var h = new Harness();
        var point = Point("q1", High(10, "q"));

        h.Engine.Evaluate(point, Good(50));
        Assert.Single(h.Raised);

        h.Engine.Evaluate(point, PointValue.Bad("ss.reason.comm", DateTimeOffset.UtcNow));
        h.Engine.Evaluate(point, PointValue.Offline(DateTimeOffset.UtcNow));
        h.Engine.Evaluate(point, PointValue.Uncertain(5, "ss.reason.comm", DateTimeOffset.UtcNow)); // 坏值里带着"正常值"也不清
        Assert.Empty(h.Cleared);
        Assert.Single(h.Raised);

        h.Engine.Evaluate(point, Good(5));               // 好值回落 → 才清
        Assert.Single(h.Cleared);
    }

    [Fact]
    public void Uncertain_nan_never_crosses_and_never_clears()
    {
        // ADR D32：NaN 降级 Uncertain；Uncertain 不参与报警判定（挂起），既不越限也不清除。
        var h = new Harness();
        var point = Point("q2", High(10, "q"));
        var nan = PointValue.Uncertain(double.NaN, "ss.reason.nan", DateTimeOffset.UtcNow);

        h.Engine.Evaluate(point, nan);
        Assert.Empty(h.Raised);                          // NaN > limit 为 false，但这里根本不评估

        h.Engine.Evaluate(point, Good(50));
        Assert.Single(h.Raised);
        h.Engine.Evaluate(point, nan);                   // 已激活：NaN 不清除
        Assert.Empty(h.Cleared);
        Assert.Single(h.Raised);
    }

    [Fact]
    public void Recovery_after_bad_quality_rejudges_the_current_value_without_spurious_events()
    {
        var h = new Harness();
        var point = Point("q3", High(10, "q", delayMs: 250));

        // 坏值期间值早已越限（框架不知道）；恢复后第一次好值就要重新判：先挂起、再按延时激活
        h.Engine.Evaluate(point, PointValue.Bad("ss.reason.comm", DateTimeOffset.UtcNow));
        h.Engine.Evaluate(point, Good(50));
        Assert.Empty(h.Raised);                          // 不凭空报：延时从恢复后的第一次好值起算

        Thread.Sleep(350);
        h.Engine.Evaluate(point, Good(50));
        Assert.Single(h.Raised);                         // 也不漏报
    }

    [Fact]
    public void Pending_timer_is_not_cleared_by_uncertain_quality()
    {
        var h = new Harness();
        var point = Point("q4", High(10, "q", delayMs: 300));

        h.Engine.Evaluate(point, Good(50));              // 起计时
        Thread.Sleep(400);
        h.Engine.Evaluate(point, PointValue.Uncertain(double.NaN, "ss.reason.nan", DateTimeOffset.UtcNow));
        h.Engine.Evaluate(point, Good(50));              // 累加器保留 → 立即激活
        Assert.Single(h.Raised);
    }

    // ═══════════════ §六 事件字段与不重复 ═══════════════

    [Fact]
    public void Raised_event_carries_every_configured_field_with_alarm_category_and_warn_level()
    {
        var h = new Harness();
        var point = Point("e1", High(10, "E1", priority: "P1", message: "料筒温度超限"));

        h.Engine.Evaluate(point, Good(12.5));

        var ev = Assert.Single(h.Raised);
        Assert.Equal("E1", ev.AlarmId);
        Assert.Equal("dev1", ev.DeviceId);
        Assert.Equal("e1", ev.PointId);
        Assert.Equal("high", ev.AlarmType);
        Assert.Equal(10.0, ev.Limit!.Value);
        Assert.Equal(12.5, ev.Value);
        Assert.Equal("P1", ev.Priority);
        Assert.Equal("料筒温度超限", ev.Message);
        Assert.Equal(EventCategory.Alarm, ev.Category);
        Assert.Equal(EventLevel.Warn, ev.Level);
    }

    [Fact]
    public void Cleared_and_acknowledged_events_carry_their_identity_fields_and_levels()
    {
        var h = new Harness();
        var point = Point("e2", High(10, "E2", latch: true));

        h.Engine.Evaluate(point, Good(50));
        h.Engine.Evaluate(point, Good(5));
        h.Engine.TryAcknowledge("dev1", "e2", "E2", "op-x");

        var cleared = Assert.Single(h.Cleared);
        Assert.Equal("E2", cleared.AlarmId);
        Assert.Equal("dev1", cleared.DeviceId);
        Assert.Equal("e2", cleared.PointId);
        Assert.Equal("high", cleared.AlarmType);
        Assert.Equal(EventCategory.Alarm, cleared.Category);
        Assert.Equal(EventLevel.Info, cleared.Level);

        var ack = Assert.Single(h.Acknowledged);
        Assert.Equal("E2", ack.AlarmId);
        Assert.Equal("dev1", ack.DeviceId);
        Assert.Equal("e2", ack.PointId);
        Assert.Equal("op-x", ack.User);
        Assert.Equal(EventCategory.Alarm, ack.Category);
        Assert.Equal(EventLevel.Info, ack.Level);
    }

    [Fact]
    public void Envelope_sequence_is_monotonic_and_timestamps_are_assigned()
    {
        var h = new Harness();
        var seqs = new List<long>();
        var times = new List<DateTimeOffset>();
        h.Bus.Subscribe<AlarmRaisedEvent>(e => { seqs.Add(e.Seq); times.Add(e.Time); }, DeliveryMode.Inline);

        var point = Point("e3", High(10, "E3"));
        for (var i = 0; i < 5; i++)
        {
            h.Engine.Evaluate(point, Good(50));           // raise
            h.Engine.Evaluate(point, Good(0));            // clear
        }

        Assert.Equal(5, seqs.Count);
        for (var i = 1; i < seqs.Count; i++) Assert.True(seqs[i] > seqs[i - 1], "Seq 必须严格单调");
        Assert.All(times, t => Assert.NotEqual(default, t));
    }

    [Fact]
    public void Repeated_identical_values_do_not_emit_duplicate_alarm_events()
    {
        var h = new Harness();
        var point = Point("e4", High(10, "E4"));

        for (var i = 0; i < 20; i++) h.Engine.Evaluate(point, Good(50));
        Assert.Single(h.Raised);
        Assert.Empty(h.Cleared);

        for (var i = 0; i < 20; i++) h.Engine.Evaluate(point, Good(0));
        Assert.Single(h.Cleared);
        Assert.Single(h.Raised);
    }

    [Fact]
    public void Alarm_storm_of_ten_thousand_transitions_keeps_exact_counts_and_final_state()
    {
        // docs/07 STAB-3：高频越限/清除交替不冲垮锁存语义、不泄漏事件。
        var h = new Harness();
        var point = Point("e5", High(50, "E5", deadband: 1, latch: true));

        const int rounds = 10_000;
        for (var i = 0; i < rounds; i++)
        {
            h.Engine.Evaluate(point, Good(80));           // 越限（首次激活；未确认前不再触发）
            h.Engine.Evaluate(point, Good(0));            // 回落清除（首次清除；之后已 AckPending 不重触发）
        }

        var raisedOnce = Assert.Single(h.Raised);         // 锁存语义不被冲垮
        Assert.Equal("E5", raisedOnce.AlarmId);
        Assert.Single(h.Cleared);

        Assert.True(h.Engine.TryAcknowledge("dev1", "e5", "E5", "op"));
        h.Engine.Evaluate(point, Good(80));
        Assert.Equal(2, h.Raised.Count);                  // 确认后仍可触发 → 状态机活着

        // 无泄漏：一万次评估只产出这几条事件，没有额外/丢失的产物
        Assert.Single(h.Cleared);
        Assert.Single(h.Acknowledged);
    }

    // ═══════════════ §七 多报警并存、跨点位/跨设备 ═══════════════

    [Fact]
    public void Cross_point_and_cross_device_alarms_never_bleed_into_each_other()
    {
        var h = new Harness();
        var ts = DateTimeOffset.UtcNow;

        var points = new List<RuntimePoint>();
        for (var i = 0; i < 10; i++)
        {
            points.Add(RuntimePointFactory.Point(c =>
            {
                c.Id = "x" + i;
                c.Alarms.Add(High(10, "X" + i));
            }, i % 2 == 0 ? "devA" : "devB"));
        }

        for (var round = 0; round < 100; round++)
        {
            foreach (var point in points)
            {
                h.Engine.Evaluate(point, PointValue.Good(round % 2 == 0 ? 50 : 5, ts));
            }
        }

        // 100 轮 × 10 点：每点每轮各一次激活 + 一次清除，且每个 id 只出现自己的 50 次
        Assert.Equal(500, h.Raised.Count);
        Assert.Equal(500, h.Cleared.Count);
        Assert.Equal(10, h.Raised.Select(r => r.AlarmId).Distinct().Count());
        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(50, h.Raised.Count(r => r.AlarmId == "X" + i));
            Assert.Equal(50, h.Raised.Count(r => r.PointId == "x" + i && r.DeviceId == (i % 2 == 0 ? "devA" : "devB")));
        }

        Assert.All(h.Raised, r => Assert.Equal(50.0, Convert.ToDouble(r.Value)));   // 无串味
    }

    [Fact]
    public void Alarms_on_points_with_no_alarm_configuration_are_no_ops()
    {
        var h = new Harness();
        var point = RuntimePointFactory.Point(c => c.Id = "none");

        h.Engine.Evaluate(point, Good(999));
        h.Engine.Evaluate(point, PointValue.Bad("ss.reason.comm", DateTimeOffset.UtcNow));

        Assert.Equal(0, h.Transitions);
        Assert.Empty(h.Acknowledged);
    }

    [Fact]
    public void Acknowledge_after_the_bus_stopped_does_not_crash_and_emits_nothing()
    {
        // 生命周期口径（承接 W54）：总线停止后再确认不崩、不抛；事件被已停止的总线静默丢弃。
        var h = new Harness();
        var point = Point("z1", High(10, "Z1", latch: true));
        h.Engine.Evaluate(point, Good(50));
        Assert.True(h.Engine.TryAcknowledge("dev1", "z1", "Z1", "op"));

        h.Bus.Dispose();                                    // 停止总线
        Assert.True(h.Engine.TryAcknowledge("dev1", "z1", "Z1", "op"));   // 状态仍在 → 返回 true
        Assert.Single(h.Acknowledged);                      // 只收到停止前那一条
    }
}
