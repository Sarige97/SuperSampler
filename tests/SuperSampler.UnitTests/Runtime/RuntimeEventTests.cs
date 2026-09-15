using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Xml.Linq;
using SuperSampler.Abstractions.Events;
using SuperSampler.Abstractions.Values;
using SuperSampler.Core.Config;
using SuperSampler.Core.Events;
using SuperSampler.Core.Runtime;
using Xunit;

namespace SuperSampler.UnitTests.Runtime;

/// <summary>第二轮事件测试：值变化仅变化时才发；写审计事件无论成败必发。</summary>
public class RuntimeEventTests
{
    private static SamplerConfiguration Load() => SamplerConfigLoader.Load(XDocument.Parse(CFG), Directory.GetCurrentDirectory());

    [Fact]
    public void Value_change_event_emitted_only_when_value_or_quality_changes()
    {
        var cfg = Load();
        var registry = new PointRegistry(cfg);
        var cache = new ValueCache();
        var bus = new InProcessEventBus();
        var changes = new List<PointValueChangedEvent>();
        bus.Subscribe<PointValueChangedEvent>(e => changes.Add(e.Body), DeliveryMode.Inline);

        var scheduler = new Scheduler(bus, registry, cache, cfg);
        var point = registry.GetPoint("d1", "p1");
        var method = typeof(Scheduler)
            .GetMethod("PublishIfChanged", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("PublishIfChanged 私有方法缺失");

        var ts = DateTimeOffset.UtcNow;
        method.Invoke(scheduler, new object[] { point, PointValue.Good(5.0, ts) }); // 首值：无缓存 → emit
        method.Invoke(scheduler, new object[] { point, PointValue.Good(5.0, ts) }); // 相同值 → 不 emit
        method.Invoke(scheduler, new object[] { point, PointValue.Good(5.0, ts.AddSeconds(1)) }); // 值同质量同 → 不 emit
        method.Invoke(scheduler, new object[] { point, PointValue.Good(6.0, ts) }); // 值变 → emit

        Assert.Equal(2, changes.Count);
        Assert.Equal(5.0, changes[0].Value.Value);
        Assert.Equal(6.0, changes[1].Value.Value);
    }

    [Fact]
    public void Value_change_event_emitted_on_quality_change()
    {
        var cfg = Load();
        var registry = new PointRegistry(cfg);
        var cache = new ValueCache();
        var bus = new InProcessEventBus();
        var changes = new List<PointValueChangedEvent>();
        bus.Subscribe<PointValueChangedEvent>(e => changes.Add(e.Body), DeliveryMode.Inline);

        var scheduler = new Scheduler(bus, registry, cache, cfg);
        var point = registry.GetPoint("d1", "p1");
        var method = typeof(Scheduler)
            .GetMethod("PublishIfChanged", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("PublishIfChanged 私有方法缺失");

        var ts = DateTimeOffset.UtcNow;
        method.Invoke(scheduler, new object[] { point, PointValue.Good(5.0, ts) });
        method.Invoke(scheduler, new object[] { point, PointValue.Bad("ss.reason.comm", ts) });

        Assert.Equal(2, changes.Count);
    }

    [Fact]
    public async Task Write_audit_event_always_emitted_even_on_reject()
    {
        using (var engine = new SamplerEngine(Load()))
        {
            var written = new List<PointWrittenEvent>();
            engine.Bus.Subscribe<PointWrittenEvent>(e => written.Add(e.Body), DeliveryMode.Inline);

            // 只读点直接拒绝——即使失败也必须有写审计事件
            var result = await engine.SetValueAsync("d1", "p1", 5);

            Assert.StartsWith("Rejected", result.Outcome.ToString());
            var ev = Assert.Single(written);
            Assert.Equal("p1", ev.PointId);
            Assert.Equal("d1", ev.DeviceId);
            Assert.Equal("Local", ev.User);
            Assert.Equal("Rejected", ev.Outcome);
            Assert.Equal("ss.reason.notWritable", ev.Message);
        }
    }

    private const string CFG = """
        <SamplerConfig schemaVersion="3.0">
          <ScanGroups>
          <ScanGroup id="normal" />
          </ScanGroups>
          <Transports><Transport id="tcp1" host="127.0.0.1" /></Transports>
          <Devices><Device id="d1" transport="tcp1" pointSet="ps1" /></Devices>
          <PointSets>
            <PointSet id="ps1">
              <Points>
                <Point id="p1" address="0" />
              </Points>
            </PointSet>
          </PointSets>
        </SamplerConfig>
        """;
}