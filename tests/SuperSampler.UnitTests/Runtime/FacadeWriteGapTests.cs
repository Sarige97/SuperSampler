using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using SuperSampler.Abstractions.Events;
using SuperSampler.Abstractions.Facade;
using SuperSampler.Abstractions.Values;
using SuperSampler.Core.Config;
using SuperSampler.Core.Runtime;
using SuperSampler.Drivers.Modbus;
using SuperSampler.Drivers.Modbus.Wire;
using SuperSampler.UnitTests.Support;
using Xunit;

namespace SuperSampler.UnitTests.Runtime;

/// <summary>
/// 写管道与门面补测（docs/07 测试计划 §3.2 G-W/G-F 族）：
/// verify 回读不一致、单字/多字写分流、编码失败拒绝、未知点位；
/// GetValue 格式化/nullText/未知 id、缓存直读零通讯、RawRead 开关、TriggerRead/TriggerBlockRead。
/// </summary>
public class FacadeWriteGapTests
{
    private static SamplerConfiguration Cfg(string pointXml, bool allowRaw = false)
        => SamplerConfigLoader.Load(XDocument.Parse(string.Format(Xml(allowRaw), pointXml)), Directory.GetCurrentDirectory());

    private static string Xml(bool allowRaw)
    {
        var diagnostics = allowRaw ? "\n  <Diagnostics allowRawAccess=\"true\" />" : string.Empty;
        return """
            <SamplerConfig schemaVersion="3.0">{DIAG}
              <Global nullText="--" />
              <ScanGroups>
                <ScanGroup id="normal" />
                <ScanGroup id="onDemand" mode="onDemand" />
              </ScanGroups>
              <Transports><Transport id="tcp1" host="127.0.0.1" /></Transports>
              <Devices><Device id="d1" transport="tcp1" pointSet="ps1" unitId="7" /></Devices>
              <PointSets><PointSet id="ps1">
                <Blocks>
                  <Block id="b1" start="0" count="5" scanGroup="onDemand">
                    <Point id="bp" address="0" />
                  </Block>
                </Blocks>
                <Points>
                  <Point id="t" address="10" dataType="float32" length="2" swap="none">
                    <Format decimals="1" suffix="℃" />
                  </Point>
                  {0}
                </Points>
              </PointSet></PointSets>
            </SamplerConfig>
            """.Replace("{DIAG}", diagnostics);
    }

    private static SamplerEngine Engine(FakeModbusLink link, string pointXml, bool allowRaw = false)
        => new(Cfg(pointXml, allowRaw), new Dictionary<string, IModbusLink> { ["tcp1"] = link });

    private const string WR_POINT = """<Point id="wr" address="20" access="write" />""";

    private const string WR_VERIFY =
        """<Point id="wr" address="20" access="write"><Write verify="true" /></Point>""";

    private const string WR_FLOAT =
        """<Point id="fw" address="30" access="write" dataType="float32" length="2" swap="none" />""";

    // ═══════════════ G-W 族 ═══════════════

    // G-W-1：verify=true 写成功后回读值附带在结果上。锁定实际口径：
    // 成功路径 verify 只回读不判定一致性（结果恒 Succeeded，findings D10 待产品决策）；
    // 「回读不一致判 Failed」仅存在于超时路径（D7，见 WritePipelineTests）。
    [Fact]
    public async Task Gw1_verify_write_success_attaches_readback_without_verdict()
    {
        var link = new FakeModbusLink();
        link.WriteSingleReplies.Enqueue(ModbusReply.Ok(Array.Empty<ushort>(), 1));
        link.ReadReplies.Enqueue(ModbusReply.Ok(new ushort[] { 999 }, 3)); // 回读 ≠ 写入的 5
        using var engine = Engine(link, WR_VERIFY);

        var result = await engine.SetValueAsync("d1", "wr", 5);

        Assert.Equal(WriteOutcome.Succeeded, result.Outcome);
        Assert.Equal(1, link.ReadCalls);
        var readback = result.Readback.GetValueOrDefault();
        Assert.True(readback.IsGood);
        Assert.Equal((ushort)999, Assert.IsType<ushort>(readback.Value)); // 回读值如实携带，供宿主自行比对
    }

    // G-W-2：多字写走 WriteMulti、单字写走 WriteSingle
    [Fact]
    public async Task Gw2_multi_word_write_uses_writemulti_single_uses_writesingle()
    {
        var link = new FakeModbusLink();
        link.WriteMultiReplies.Enqueue(ModbusReply.Ok(Array.Empty<ushort>(), 1));
        link.WriteSingleReplies.Enqueue(ModbusReply.Ok(Array.Empty<ushort>(), 1));
        using var engine = Engine(link, WR_FLOAT + WR_POINT);

        await engine.SetValueAsync("d1", "fw", 1.5); // float32 → 2 字
        Assert.Equal(1, link.WriteMultiCallCount);
        Assert.Equal(0, link.WriteSingleCallCount);
        Assert.Equal(new ushort[] { 0x3FC0, 0x0000 }, link.WriteMultiValues[0]);

        await engine.SetValueAsync("d1", "wr", 5); // uint16 → 1 字（wr 点在同一配置）
        Assert.Equal(1, link.WriteMultiCallCount);
        Assert.Equal(1, link.WriteSingleCallCount);
        Assert.Equal(5, Assert.Single(link.WriteSingleValues));
    }

    // G-W-3：编码阶段异常 → Rejected 且链路零调用
    [Fact]
    public async Task Gw3_encode_failure_is_rejected_without_link_call()
    {
        var link = new FakeModbusLink();
        using var engine = Engine(link, WR_POINT);

        var result = await engine.SetValueAsync("d1", "wr", "abc"); // 字符串喂 uint16 点

        Assert.Equal(WriteOutcome.Rejected, result.Outcome);
        Assert.Equal(0, link.WriteCallCount);
        Assert.Equal(0, link.ReadCalls);
    }

    // G-W-4：未知点位 SetValueAsync → KeyNotFoundException（编程错误，D25）
    [Fact]
    public async Task Gw4_set_value_on_unknown_point_throws_key_not_found()
    {
        var link = new FakeModbusLink();
        using var engine = Engine(link, WR_POINT);

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => engine.SetValueAsync("d1", "不存在", 1));
    }

    // ═══════════════ G-F 族 ═══════════════

    // G-F-1：GetValue 返回 Format 后文本（decimals + suffix）
    [Fact]
    public async Task Gf1_get_value_returns_formatted_text()
    {
        var link = new FakeModbusLink();
        link.SetReadData(7, DataArea.HoldingRegister, 10, 0x4049, 0x0FDB); // 3.14159f
        using var engine = Engine(link, string.Empty);

        await engine.TriggerReadAsync("d1", "t");

        Assert.Equal("3.1℃", engine.GetValue("d1", "t"));
    }

    // G-F-2：非 Good 质量 → nullText
    [Fact]
    public void Gf2_get_value_non_good_returns_null_text()
    {
        var link = new FakeModbusLink();
        using var engine = Engine(link, string.Empty); // 缓存为空 → Bad

        Assert.Equal("--", engine.GetValue("d1", "t"));
    }

    // G-F-3：未知 device/point → KeyNotFoundException
    [Fact]
    public void Gf3_get_value_unknown_id_throws_key_not_found()
    {
        var link = new FakeModbusLink();
        using var engine = Engine(link, string.Empty);

        Assert.Throws<KeyNotFoundException>(() => engine.GetValue("d1", "不存在"));
        Assert.Throws<KeyNotFoundException>(() => engine.GetValue("不存在的设备", "t"));
        Assert.Throws<KeyNotFoundException>(() => engine.GetValueDetail("d1", "不存在"));
    }

    // G-F-4：GetValueDetail 直读缓存，不发通讯
    [Fact]
    public async Task Gf4_get_value_detail_reads_cache_without_link_call()
    {
        var link = new FakeModbusLink();
        link.SetReadData(7, DataArea.HoldingRegister, 10, 0x4049, 0x0FDB);
        using var engine = Engine(link, string.Empty);
        await engine.TriggerReadAsync("d1", "t");
        link.ClearCalls();

        var detail = engine.GetValueDetail("d1", "t");

        Assert.True(detail.IsGood);
        Assert.Empty(link.Calls.Where(c => c.IsRead)); // 零通讯
    }

    // G-F-5：allowRawAccess=false（默认）→ RawRead/RawWrite 双拒绝
    [Fact]
    public async Task Gf5_raw_access_disabled_by_default_rejects_both()
    {
        var link = new FakeModbusLink();
        using var engine = Engine(link, string.Empty, allowRaw: false);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => engine.RawReadAsync("d1", ModbusArea.HoldingRegister, 0, 2));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => engine.RawWriteAsync("d1", ModbusArea.HoldingRegister, 0, new ushort[] { 1 }));
        Assert.Equal(0, link.ReadCalls); // 拒绝发生在链路之前
    }

    // G-F-6：allowRawAccess=true → RawRead 放行并返回寄存器
    [Fact]
    public async Task Gf6_raw_read_allowed_when_enabled()
    {
        var link = new FakeModbusLink();
        link.SetReadData(7, DataArea.HoldingRegister, 10, 0xAAAA, 0xBBBB);
        using var engine = Engine(link, string.Empty, allowRaw: true);

        var result = await engine.RawReadAsync("d1", ModbusArea.HoldingRegister, 10, 2);

        Assert.True(result.Success);
        Assert.Equal(new ushort[] { 0xAAAA, 0xBBBB }, result.Registers);
        Assert.Null(result.Error);
    }

    // G-F-7：TriggerRead 走完整解析管道并写缓存（注意：不直接发值事件，事件由调度侧变化检测发出——锁定实际口径）
    [Fact]
    public async Task Gf7_trigger_read_populates_cache_through_full_pipeline()
    {
        var link = new FakeModbusLink();
        link.SetReadData(7, DataArea.HoldingRegister, 10, 0x4049, 0x0FDB);
        using var engine = Engine(link, string.Empty);

        var value = await engine.TriggerReadAsync("d1", "t");

        Assert.True(value.IsGood);
        Assert.True(Math.Abs(Convert.ToDouble(value.Value) - 3.14159) < 0.0001);
        Assert.Equal("3.1℃", engine.GetValue("d1", "t")); // 门面读到刚触发的值
        Assert.Equal(1, link.Calls.Count(c => c.IsRead));
    }

    // G-F-8：TriggerBlockRead 返回块寄存器数；onDemand 块可触达
    [Fact]
    public async Task Gf8_trigger_block_read_returns_block_register_count()
    {
        var link = new FakeModbusLink();
        link.SetReadData(7, DataArea.HoldingRegister, 0, 1, 2, 3, 4, 5);
        using var engine = Engine(link, string.Empty);

        var count = await engine.TriggerBlockReadAsync("b1");

        Assert.Equal(5, count);
        Assert.Equal(1, link.ReadCalls);
        Assert.Empty(link.ReadReplies);
    }
}
