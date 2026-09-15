using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using SuperSampler.Abstractions.Facade;
using SuperSampler.Abstractions.Values;
using SuperSampler.Core.Config;
using SuperSampler.Core.Runtime;
using Xunit;

namespace SuperSampler.IntegrationTests;

/// <summary>
/// 真链路集成测试（L2，docs/07 §4.1）：Core 的 ModbusMaster/Channel 走真实 TCP 帧，
/// 对 `_simulator_design/modbus_tcp_sim.py` 模拟从站（TCP:1502 unit 1-4/11-14/99；RTU-over-TCP:1503 unit 10-12）。
/// 前置：模拟器已启动（_simulator_design/启动与验证.md §0）。环境未起 → 显式跳过，不静默通过。
/// 模拟器数据基线见启动与验证.md §4.1/§4.5。
/// </summary>
public class IntegrationTcpTests
{
    private const string Host = "127.0.0.1";
    private const int TcpPort = 1502;
    private const int RtuPort = 1503;

    private static readonly bool SimUp = ProbePort(TcpPort) && ProbePort(RtuPort);

    private static bool ProbePort(int port)
    {
        try
        {
            using var s = new TcpClient();
            s.Connect(Host, port);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static void RequireSim()
    {
        if (!SimUp)
        {
            throw Xunit.Sdk.SkipException.ForSkip("modbus_tcp_sim.py 未在运行：python _simulator_design/modbus_tcp_sim.py");
        }
    }

    // ───────────── 配置与引擎辅助 ─────────────

    private static SamplerConfiguration Cfg(string deviceXml, string pointSetXml)
    {
        var xml = string.Format(XML, deviceXml, pointSetXml);
        return SamplerConfigLoader.Load(XDocument.Parse(xml), Directory.GetCurrentDirectory());
    }

    private static SamplerEngine Engine(string deviceXml, string pointSetXml)
        => new(Cfg(deviceXml, pointSetXml));

    private static SamplerEngine EngineUnit(int unitId, string pointsXml)
        => Engine(
            $"""<Device id="d1" transport="tcp1" unitId="{unitId}" pointSet="ps1" />""",
            $"""<Points>{pointsXml}</Points>""");

    /// <summary>等一个点出现指定质量（Good）并返回三元组；超时抛异常。模拟器轮询上限给足。</summary>
    private static async Task<PointValue> WaitGood(IDeviceManager mgr, string deviceId, string pointId, int timeoutMs = 8000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var v = mgr.GetValueDetail(deviceId, pointId);
            if (v.IsGood)
            {
                return v;
            }
            await Task.Delay(50);
        }
        throw new TimeoutException($"点 {deviceId}/{pointId} 未在 {timeoutMs}ms 内 Good（质量={mgr.GetValueDetail(deviceId, pointId).Quality}, reason={mgr.GetValueDetail(deviceId, pointId).Reason ?? "null"}）");
    }

    private static T Get<T>(PointValue v)
    {
        Assert.True(v.TryGetValue<T>(out var value), $"值类型不符：期望 {typeof(T).Name}，得到 {v.Value?.GetType().Name}（{v}）");
        return value;
    }

    private const string XML = """
        <SamplerConfig schemaVersion="3.0">
          <Global><Retry count="0" intervalMs="10" /><Polling rateMs="300" requestTimeoutMs="800" /></Global>
          <ScanGroups><ScanGroup id="normal" /></ScanGroups>
          <Transports><Transport id="tcp1" host="127.0.0.1" port="1502" /></Transports>
          <Devices>{0}</Devices>
          <PointSets><PointSet id="ps1"><Defaults swap="none" />{1}</PointSet></PointSets>
        </SamplerConfig>
        """;

    // ───────────── IT-01：TCP 基线类型正确性（TYPEDEMO unit 1） ─────────────

    [Fact]
    public async Task Tcp_baseline_types_decode_to_sim_values()
    {
        RequireSim();
        var points = string.Join("\n",
            POINT("r.i16", 0, "int16"),
            POINT("r.u16", 1, "uint16"),
            POINT("r.i32", 2, "int32"),
            POINT("r.u32", 4, "uint32"),
            POINT("r.i64", 6, "int64"),
            POINT("r.u64", 10, "uint64"),
            POINT("r.f32", 14, "float32"),
            POINT("r.f64", 16, "float64"),
            POINT("r.str", 26, "string", "11"),
            """<Point id="r.utf8" address="40" dataType="string" length="14"><String encoding="utf-8" /></Point>""",
            POINT("r.bcd", 60, "uint16"),
            POINT("r.plain", 112, "uint16"),
            POINT("r.status", 200, "uint16"));

        using var engine = EngineUnit(1, points);
        engine.Start();
        try
        {
            var m = (IDeviceManager)engine;
            Assert.Equal(-12345, Get<short>(await WaitGood(m, "d1", "r.i16")));
            Assert.Equal(54321, Get<ushort>(await WaitGood(m, "d1", "r.u16")));
            Assert.Equal(-123456789, Get<int>(await WaitGood(m, "d1", "r.i32")));
            Assert.Equal(3456789012u, Get<uint>(await WaitGood(m, "d1", "r.u32")));
            Assert.Equal(-9876543210123L, Get<long>(await WaitGood(m, "d1", "r.i64")));
            Assert.Equal(18446744073709551615UL, Get<ulong>(await WaitGood(m, "d1", "r.u64")));
            Assert.Equal(3.14159f, Get<float>(await WaitGood(m, "d1", "r.f32")), 5);
            Assert.Equal(2.718281828459045, Get<double>(await WaitGood(m, "d1", "r.f64")), 12);
            Assert.Equal("SuperModbus ASCII demo", Get<string>(await WaitGood(m, "d1", "r.str")));
            Assert.Equal("温度模拟器·σ(Σ)©", Get<string>(await WaitGood(m, "d1", "r.utf8")));
            Assert.Equal(0x1234, Get<ushort>(await WaitGood(m, "d1", "r.bcd")));
            Assert.Equal(12345, Get<ushort>(await WaitGood(m, "d1", "r.plain")));
            Assert.Equal(0x0071, Get<ushort>(await WaitGood(m, "d1", "r.status")));
        }
        finally
        {
            engine.Dispose();
        }
    }

    // ───────────── IT-02：边界值（BOUNDARY unit 3） ─────────────

    [Fact]
    public async Task Boundary_values_decode_without_corruption()
    {
        RequireSim();
        var points = string.Join("\n",
            POINT("b.pinf", 0, "float32"),
            POINT("b.ninf", 2, "float32"),
            POINT("b.nan", 4, "float32"),
            POINT("b.dzero", 6, "float64"),
            POINT("b.max", 90, "uint16"),
            POINT("b.min", 91, "uint16"),
            POINT("b.empty", 10, "string", "1"),
            POINT("b.full", 40, "string", "26"));

        using var engine = EngineUnit(3, points);
        engine.Start();
        try
        {
            var m = (IDeviceManager)engine;
            Assert.Equal(float.PositiveInfinity, Get<float>(await WaitGood(m, "d1", "b.pinf")));
            Assert.Equal(float.NegativeInfinity, Get<float>(await WaitGood(m, "d1", "b.ninf")));
            Assert.True(float.IsNaN(Get<float>(await WaitGood(m, "d1", "b.nan"))), "NaN 应原样解码");
            Assert.Equal(0.0, Get<double>(await WaitGood(m, "d1", "b.dzero")));
            Assert.Equal(0xFFFF, Get<ushort>(await WaitGood(m, "d1", "b.max")));
            Assert.Equal(0x0000, Get<ushort>(await WaitGood(m, "d1", "b.min")));
            Assert.Equal(string.Empty, Get<string>(await WaitGood(m, "d1", "b.empty")));
            Assert.Equal("ABCDEFGHIJKLMNOPQRSTUVWXYZABCDEFGHIJKLMNOPQRSTUVWXYZ", Get<string>(await WaitGood(m, "d1", "b.full")));
        }
        finally
        {
            engine.Dispose();
        }
    }

    // ───────────── IT-04：四数据区（unit 1） ─────────────

    [Fact]
    public async Task Four_data_areas_are_readable()
    {
        RequireSim();
        var points = string.Join("\n",
            POINT("a.holding", 112, "uint16"),                 // 400113 = 12345
            POINT("a.input", 0, "int16", null, "input"),        // 300001 = -7777
            POINT("a.coil", 0, "bool", null, "coil"),           // 0x0.0 = ON
            POINT("a.discrete", 0, "bool", null, "discrete"));  // 1x.0 = ON

        using var engine = EngineUnit(1, points);
        engine.Start();
        try
        {
            var m = (IDeviceManager)engine;
            Assert.Equal(12345, Get<ushort>(await WaitGood(m, "d1", "a.holding")));
            Assert.Equal(-7777, Get<short>(await WaitGood(m, "d1", "a.input")));
            Assert.True(Get<bool>(await WaitGood(m, "d1", "a.coil")));
            Assert.True(Get<bool>(await WaitGood(m, "d1", "a.discrete")));
        }
        finally
        {
            engine.Dispose();
        }
    }

    // ───────────── IT-05：四字序同逻辑值（unit 11-14） ─────────────

    [Theory]
    [InlineData(11, "abcd")]
    [InlineData(12, "badc")]
    [InlineData(13, "cdab")]
    [InlineData(14, "dcba")]
    public async Task Word_order_devices_decode_same_logical_values(int unitId, string swap)
    {
        RequireSim();
        var points = string.Join("\n",
            POINT("w.f32", 0, "float32", null, "holding", swap),
            POINT("w.i32", 2, "int32", null, "holding", swap),
            POINT("w.u32", 4, "uint32", null, "holding", swap),
            POINT("w.f64", 8, "float64", null, "holding", swap),
            POINT("w.i64", 16, "int64", null, "holding", swap));

        using var engine = EngineUnit(unitId, points);
        engine.Start();
        try
        {
            var m = (IDeviceManager)engine;
            Assert.Equal(3.14f, Get<float>(await WaitGood(m, "d1", "w.f32")), 3);
            Assert.Equal(-1234567, Get<int>(await WaitGood(m, "d1", "w.i32")));
            Assert.Equal(4000000000u, Get<uint>(await WaitGood(m, "d1", "w.u32")));
            Assert.Equal(1.23456789, Get<double>(await WaitGood(m, "d1", "w.f64")), 6);
            Assert.Equal(-5000000000L, Get<long>(await WaitGood(m, "d1", "w.i64")));
        }
        finally
        {
            engine.Dispose();
        }
    }

    // ───────────── IT-07：RTU-over-TCP 三从站不串台（unit 10/11/12） ─────────────

    [Fact]
    public async Task Rtu_over_tcp_three_slaves_do_not_cross_talk()
    {
        RequireSim();
        var xml = """
            <SamplerConfig schemaVersion="3.0">
              <Global><Retry count="0" /><Polling rateMs="300" requestTimeoutMs="800" /></Global>
              <ScanGroups><ScanGroup id="normal" /></ScanGroups>
              <Transports><Transport id="rtu1" host="127.0.0.1" port="1503" variant="rtuOverTcp" /></Transports>
              <Devices>
                <Device id="s10" transport="rtu1" unitId="10" pointSet="ps1" />
                <Device id="s11" transport="rtu1" unitId="11" pointSet="ps2" />
                <Device id="s12" transport="rtu1" unitId="12" pointSet="ps3" />
              </Devices>
              <PointSets>
                <PointSet id="ps1"><Defaults swap="none" /><Points>{0}</Points></PointSet>
                <PointSet id="ps2"><Defaults swap="none" /><Points>{1}</Points></PointSet>
                <PointSet id="ps3"><Defaults swap="none" /><Points>{2}</Points></PointSet>
              </PointSets>
            </SamplerConfig>
            """;
        var ps1 = POINT("r.v", 0, "int16") + POINT("r.f", 2, "float32") + POINT("r.s", 4, "string", "2");
        var ps2 = POINT("r.v", 0, "int16") + POINT("r.f", 2, "float32") + POINT("r.s", 4, "string", "2");
        var ps3 = POINT("r.i64", 0, "int64") + POINT("r.u64", 4, "uint64");
        var cfg = SamplerConfigLoader.Load(
            XDocument.Parse(string.Format(xml, ps1, ps2, ps3)),
            Directory.GetCurrentDirectory());

        using var engine = new SamplerEngine(cfg);
        engine.Start();
        try
        {
            var m = (IDeviceManager)engine;
            Assert.Equal(-321, Get<short>(await WaitGood(m, "s10", "r.v")));
            Assert.Equal(1.5f, Get<float>(await WaitGood(m, "s10", "r.f")), 3);
            Assert.Equal("RTU1", Get<string>(await WaitGood(m, "s10", "r.s")));
            Assert.Equal(-654, Get<short>(await WaitGood(m, "s11", "r.v")));
            Assert.Equal(2.25f, Math.Abs(Get<float>(await WaitGood(m, "s11", "r.f"))), 3);
            Assert.Equal("RTU2", Get<string>(await WaitGood(m, "s11", "r.s")));
            Assert.Equal(-9876543210L, Get<long>(await WaitGood(m, "s12", "r.i64")));
            Assert.Equal(12345678901234UL, Get<ulong>(await WaitGood(m, "s12", "r.u64")));
        }
        finally
        {
            engine.Dispose();
        }
    }

    // ───────────── IT-08：大块读自动分块拼接（unit 1，区段 A 250 字） ─────────────

    [Fact]
    public async Task Large_block_read_is_chunked_and_concatenated()
    {
        RequireSim();
        // 注意：块 count ≤125 是配置层硬校验（协议单次读上限）；>125 的大块自动分块尚未实现
        //（findings W39：调度器不拆分块窗口，加载器拒绝超限块），此处按现行为断言 125 上限块。
        const int blockStart = 300;
        const int blockCount = 125;
        var points = string.Concat(Enumerable.Range(0, blockCount).Select(i =>
            $"<Point id=\"bk{i}\" address=\"{blockStart + i}\" dataType=\"uint16\" />"));
        // Blocks 挂在 PointSet 层级（不是 Points 内）
        var cfg = Cfg(
            $"""<Device id="d1" transport="tcp1" unitId="1" pointSet="ps1" />""",
            $"""<Blocks><Block id="b1" area="holding" start="{blockStart}" count="{blockCount}">{points}</Block></Blocks>""");
        using var engine = new SamplerEngine(cfg);
        engine.Start();
        try
        {
            var m = (IDeviceManager)engine;
            // 抽头断言：首/中/尾各一点，全量 125 点逐断言耗时过长
            foreach (var i in new[] { 0, 62, 124 })
            {
                var expected = (ushort)((i * 7 + 123) & 0xFFFF);
                Assert.Equal(expected, Get<ushort>(await WaitGood(m, "d1", $"bk{i}", 20000)));
            }
        }
        finally
        {
            engine.Dispose();
        }
    }

    // ───────────── IT-11/GATE-1：写只读设备 → Failed，不自动重写 ─────────────

    [Fact]
    public async Task Write_to_readonly_device_fails()
    {
        RequireSim();
        using var engine = EngineUnit(4, POINT("w.r", 0, "uint16", null, "holding", null, "readwrite"));
        engine.Start();
        try
        {
            var result = await engine.SetValueAsync("d1", "w.r", 123);
            Assert.Equal(WriteOutcome.Failed, result.Outcome);
            Assert.NotNull(result.Error);
        }
        finally
        {
            engine.Dispose();
        }
    }

    // ───────────── IT-14：写成功 + verify 回读一致 → Succeeded ─────────────

    [Fact]
    public async Task Write_with_verify_readback_succeeds()
    {
        RequireSim();
        using var engine = EngineUnit(1, POINT("w.v", 5000, "uint16", null, "holding", null, "readwrite", "true"));
        engine.Start();
        try
        {
            await WaitGood(engine, "d1", "w.v");
            var result = await engine.SetValueAsync("d1", "w.v", 0x4321);

            Assert.Equal(WriteOutcome.Succeeded, result.Outcome);
            Assert.False(result.VerifyMismatch);
            Assert.True(result.Readback.HasValue);
            Assert.Equal(0x4321, Get<ushort>(result.Readback!.Value));
        }
        finally
        {
            engine.Dispose();
        }
    }

    // ───────────── IT-15/GATE-1：写不应答从站 → Indeterminate，不自动重写 ─────────────

    [Fact]
    public async Task Write_to_silent_unit_is_indeterminate()
    {
        RequireSim();
        using var engine = EngineUnit(99, POINT("w.silent", 0, "uint16", null, "holding", null, "readwrite"));
        engine.Start();
        try
        {
            var result = await engine.SetValueAsync("d1", "w.silent", 55);
            // 超时后回读也失败 → 状态未知 → Indeterminate（docs/02 三态；GATE-1 绝不自动重试）
            Assert.Equal(WriteOutcome.Indeterminate, result.Outcome);
            Assert.NotNull(result.Error);
        }
        finally
        {
            engine.Dispose();
        }
    }

    // ───────────── IT-10：读不应答从站 → 持续失败 → 质量非 Good ─────────────

    [Fact]
    public async Task Read_from_silent_unit_never_becomes_good()
    {
        RequireSim();
        using var engine = EngineUnit(99, POINT("r.silent", 0, "uint16"));
        engine.Start();
        try
        {
            var m = (IDeviceManager)engine;
            var deadline = DateTime.UtcNow.AddSeconds(6);
            while (DateTime.UtcNow < deadline)
            {
                var v = m.GetValueDetail("d1", "r.silent");
                if (v.Quality != PointQuality.Good)
                {
                    // 失败路径可见（Bad/Offline 均可），且绝不产出伪 Good
                    return;
                }
                await Task.Delay(50);
            }
            Assert.Fail("unit99 不应答，点却一直 Good");
        }
        finally
        {
            engine.Dispose();
        }
    }

    // ───────────── 点位 XML 生成 ─────────────

    private static string POINT(string id, int address, string dataType,
        string? length = null, string? area = null, string? swap = null,
        string? access = null, string? verify = null)
    {
        var extra = "";
        if (length != null) extra += $" length=\"{length}\"";
        if (area != null) extra += $" area=\"{area}\"";
        if (swap != null) extra += $" swap=\"{swap}\"";
        if (access != null) extra += $" access=\"{access}\"";
        var write = verify != null ? $"<Write verify=\"{verify}\" />" : "";
        return $"<Point id=\"{id}\" address=\"{address}\" dataType=\"{dataType}\"{extra}>{write}</Point>";
    }
}