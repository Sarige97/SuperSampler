using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using SuperSampler.Abstractions.Facade;
using SuperSampler.Abstractions.Values;
using SuperSampler.Core.Config;
using SuperSampler.Core.Runtime;
using Xunit;

namespace SuperSampler.IntegrationTests;

/// <summary>
/// C1 采集链路：复杂工业桩镜像（complex_sim.py，4 端口 / 25 从站 / 1029 点）直连内部口
/// （16002-16005），走完整 Core 链路（配置加载 → 注册表 → 调度 → 编解码 → 缓存/事件）。
///
/// 数据口径以 _simulator_design/tools/complex_points.json 为准；点位语义见「复杂桩说明.md」。
/// 环境不可用 → 全部显式跳过（ComplexStubFixture.RequireStub）。
/// </summary>
[Collection(ComplexStubCollection.Name)]
public sealed class ComplexStubAcquisitionTests
{
    private readonly ComplexStubFixture _stub;

    public ComplexStubAcquisitionTests(ComplexStubFixture stub) => _stub = stub;

    private const string GlobalFast = """
        <Global>
          <Retry count="0" intervalMs="10" />
          <Polling rateMs="200" requestTimeoutMs="3000" />
          <Quality onCommError="bad" onCommErrorValue="null" />
        </Global>
        <ScanGroups><ScanGroup id="normal" mode="poll" rateMs="200" /></ScanGroups>
        """;

    // ─────────────────────── C1-1：4 口 25 站、四区可读 ───────────────────────

    [SkippableFact]
    public async Task C1_AllFourPortsAndTwentyFiveSlaves_AllFourAreasReadable()
    {
        _stub.RequireStub();

        // 端口 → 从站清单（与 complex_ports.json 一致；P4 为 rtuOverTcp）
        var layout = new (string TransportPort, string TransportId, string Variant, int[] Units)[]
        {
            (_stub.SimPortP1.ToString(CultureInfo.InvariantCulture), "p1", "tcp", new[] { 1, 2, 3, 4 }),
            (_stub.SimPortP2.ToString(CultureInfo.InvariantCulture), "p2", "tcp", new[] { 1, 2, 3, 4 }),
            (_stub.SimPortP3.ToString(CultureInfo.InvariantCulture), "p3", "tcp", new[] { 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 }),
            (_stub.SimPortP4.ToString(CultureInfo.InvariantCulture), "p4", "rtuovertcp", new[] { 1, 2, 3, 4, 20, 21 }),
        };

        var transports = new StringBuilder();
        var devices = new StringBuilder();
        var pointSets = new StringBuilder();
        var points = new StringBuilder();

        foreach (var (port, transportId, variant, units) in layout)
        {
            transports.Append(ComplexStubFixture.Transport(transportId, int.Parse(port, CultureInfo.InvariantCulture), variant));
            foreach (var unit in units)
            {
                var deviceId = transportId + "u" + unit;
                var setId = "ps_" + deviceId;
                devices.Append(ComplexStubFixture.Device(deviceId, transportId, unit, setId));
                pointSets.Append("<PointSet id=\"").Append(setId).Append("\"><Points>")
                    .Append(ComplexStubFixture.Point("coil", 0, "bool", "area=\"coil\""))
                    .Append(ComplexStubFixture.Point("discrete", 0, "bool", "area=\"discrete\""))
                    .Append(ComplexStubFixture.Point("input", 0, "uint16", "area=\"input\""))
                    .Append(ComplexStubFixture.Point("holding", 0, "uint16", "area=\"holding\""))
                    .Append("</Points></PointSet>");
            }
        }

        var xml = "<SamplerConfig schemaVersion=\"3.0\">" + GlobalFast +
                  "<Transports>" + transports + "</Transports>" +
                  "<Devices>" + devices + "</Devices>" +
                  "<PointSets>" + pointSets + "</PointSets></SamplerConfig>";

        using var engine = new SamplerEngine(_stub.LoadConfig(xml));
        engine.Start();
        try
        {
            IDeviceManager manager = engine;
            var failures = new List<string>();
            var deviceIds = new List<string>();
            foreach (var (_, transportId, _, units) in layout)
            {
                deviceIds.AddRange(units.Select(unit => transportId + "u" + unit));
            }

            // 25 站 × 4 区：等全部变 Good（SLOW-2000 单次响应 2s，给足预算）
            var deadline = DateTime.UtcNow.AddSeconds(40);
            var pending = new List<string>(deviceIds.SelectMany(d => new[] { d + "/coil", d + "/discrete", d + "/input", d + "/holding" }));
            while (pending.Count > 0 && DateTime.UtcNow < deadline)
            {
                foreach (var key in pending.ToList())
                {
                    var parts = key.Split('/');
                    if (manager.GetValueDetail(parts[0], parts[1]).Quality == PointQuality.Good)
                    {
                        pending.Remove(key);
                    }
                }

                if (pending.Count > 0) await Task.Delay(100);
            }

            foreach (var key in pending)
            {
                var parts = key.Split('/');
                var value = manager.GetValueDetail(parts[0], parts[1]);
                failures.Add(key + "=" + value.Quality + (value.Reason == null ? string.Empty : "(" + value.Reason + ")"));
            }

            Assert.True(failures.Count == 0,
                "4 口 25 站四区共 100 点应全部 Good，未达标的 " + failures.Count + " 点：" +
                string.Join(", ", failures.Take(20)));

            Assert.Equal(25, deviceIds.Count);
            Assert.Equal(25 * 4, 100);
        }
        finally
        {
            engine.Dispose();
        }
    }

    // ─────────────────────── C1-2：全类型解码（含 UTF-8 / BCD / datetime / raw） ───────────────────────

    [SkippableFact]
    public async Task C1_AllDataTypesDecodeOnComplexMirror()
    {
        _stub.RequireStub();

        // unit 7 = WO-ABCD（四字序基准台，swap=ABCD）；unit 13 = BOUND（边界台）
        var ps7 = string.Concat(
            ComplexStubFixture.Point("coil", 0, "bool", "area=\"coil\""),
            ComplexStubFixture.Point("discrete", 0, "bool", "area=\"discrete\""),
            ComplexStubFixture.Point("input.u16", 1, "uint16", "area=\"input\""),          // 固件版本 260915 → 镜像按 u16 落字 = 0xFB33
            ComplexStubFixture.Point("input.i16", 2, "int16", "area=\"input\""),           // -12345
            ComplexStubFixture.Point("input.raw", 4, "raw", "area=\"input\" length=\"12\""),
            ComplexStubFixture.Point("holding.u16max", 7, "uint16", "area=\"holding\""),   // 0xFFFF
            ComplexStubFixture.Point("holding.f32", 0, "float32", "area=\"holding\""),     // 3.14
            ComplexStubFixture.Point("holding.i32", 2, "int32", "area=\"holding\""),       // -1234567
            ComplexStubFixture.Point("holding.u32", 4, "uint32", "area=\"holding\""),      // 4000000000
            ComplexStubFixture.Point("holding.f64", 8, "float64", "area=\"holding\""),     // 1.23456789
            ComplexStubFixture.Point("holding.i64", 12, "int64", "area=\"holding\""),      // -5000000000
            ComplexStubFixture.Point("holding.str", 40, "string", "area=\"holding\" length=\"26\""),
            ComplexStubFixture.Point("holding.empty", 66, "string", "area=\"holding\" length=\"10\""),
            // bcd(8)/datetime(plc6) 均跨 2/6 个寄存器，但 CGV-8 禁止给这两类写 length
            // （框架现状见 C1_MultiWordBcdAndDateTime_* 与 findings F4）：只能靠 Slices 手动指定连续片段，
            // 且 Slices 点位只在 TriggerRead 路径生效（ReadWindow 明确跳过 Slices 点位）。
            ComplexStubFixture.Point("holding.bcd", 76, "bcd", "area=\"holding\" length=\"2\"",
                "<Bcd digits=\"8\" /><Slices><Slice address=\"76\" length=\"2\" /></Slices>"),
            ComplexStubFixture.Point("holding.dt", 78, "datetime", "area=\"holding\" length=\"6\"",
                "<Slices><Slice address=\"78\" length=\"6\" /></Slices>"));

        var ps13 = string.Concat(
            ComplexStubFixture.Point("u64max", 20, "uint64", "area=\"holding\""),
            ComplexStubFixture.Point("i64min", 16, "int64", "area=\"holding\""),
            ComplexStubFixture.Point("utf8", 88, "string", "area=\"holding\" length=\"20\"",
                "<String encoding=\"utf-8\" />"),
            ComplexStubFixture.Point("bcd8", 108, "bcd", "area=\"holding\" length=\"2\"",
                "<Bcd digits=\"8\" /><Slices><Slice address=\"108\" length=\"2\" /></Slices>"),            ComplexStubFixture.Point("f32inf", 24, "float32", "area=\"holding\""),
            ComplexStubFixture.Point("f32nan", 28, "float32", "area=\"holding\""),
            ComplexStubFixture.Point("f32zero", 30, "float32", "area=\"holding\""));

        var xml = "<SamplerConfig schemaVersion=\"3.0\">" + GlobalFast +
                  "<Transports>" + ComplexStubFixture.Transport("p3", _stub.SimPortP3) + "</Transports>" +
                  "<Devices>" +
                  ComplexStubFixture.Device("wo", "p3", 7, "ps7") +
                  ComplexStubFixture.Device("bd", "p3", 13, "ps13") +
                  "</Devices>" +
                  "<PointSets>" +
                  "<PointSet id=\"ps7\"><Defaults swap=\"abcd\" /><Points>" + ps7 + "</Points></PointSet>" +
                  "<PointSet id=\"ps13\"><Defaults swap=\"abcd\" /><Points>" + ps13 + "</Points></PointSet>" +
                  "</PointSets></SamplerConfig>";

        using var engine = new SamplerEngine(_stub.LoadConfig(xml));
        engine.Start();
        try
        {
            IDeviceManager m = engine;

            Assert.True(F.GetAs<bool>(await ComplexStubFixture.WaitGoodAsync(m, "wo", "coil")));
            Assert.True(F.GetAs<bool>(await ComplexStubFixture.WaitGoodAsync(m, "wo", "discrete")));
            Assert.Equal(64307, F.GetAs<ushort>(await ComplexStubFixture.WaitGoodAsync(m, "wo", "input.u16")));
            Assert.Equal(-12345, F.GetAs<short>(await ComplexStubFixture.WaitGoodAsync(m, "wo", "input.i16")));

            // raw：不做字序置换，原样返回 12 个字（(i*5+1) & 0xFFFF）
            var raw = F.GetAs<ushort[]>(await ComplexStubFixture.WaitGoodAsync(m, "wo", "input.raw"));
            Assert.Equal(12, raw.Length);
            for (var i = 0; i < 12; i++)
            {
                Assert.Equal((ushort)((i * 5 + 1) & 0xFFFF), raw[i]);
            }

            Assert.Equal(0xFFFF, F.GetAs<ushort>(await ComplexStubFixture.WaitGoodAsync(m, "wo", "holding.u16max")));
            Assert.Equal(3.14f, F.GetAs<float>(await ComplexStubFixture.WaitGoodAsync(m, "wo", "holding.f32")), 5);
            Assert.Equal(-1234567, F.GetAs<int>(await ComplexStubFixture.WaitGoodAsync(m, "wo", "holding.i32")));
            Assert.Equal(4000000000u, F.GetAs<uint>(await ComplexStubFixture.WaitGoodAsync(m, "wo", "holding.u32")));
            Assert.Equal(1.23456789, F.GetAs<double>(await ComplexStubFixture.WaitGoodAsync(m, "wo", "holding.f64")), 9);
            Assert.Equal(-5000000000L, F.GetAs<long>(await ComplexStubFixture.WaitGoodAsync(m, "wo", "holding.i64")));
            Assert.Equal("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz",
                F.GetAs<string>(await ComplexStubFixture.WaitGoodAsync(m, "wo", "holding.str")));
            // 桩数据注意：complex_points.json 里「空串」点的 initial 是字面量 ''（两个单引号），
            // 镜像按字符串原样落字（rule 写的是「全 0x00」，属桩数据与规则不符）——
            // 真正的空串编码/解码覆盖放在 C2 的字符串写回读里。
            Assert.Equal("''", F.GetAs<string>(await ComplexStubFixture.WaitGoodAsync(m, "wo", "holding.empty")));

            // BCD(8) / datetime(plc6)：多寄存器，走 TriggerRead（Slices 点位只在按需路径生效）
            var debug = (IModbusDebugTool)engine;
            var bcd = await debug.TriggerReadAsync("wo", "holding.bcd");
            Assert.Equal(12345678L, F.GetAs<long>(bcd));

            var written = F.GetAs<DateTime>(await debug.TriggerReadAsync("wo", "holding.dt"));
            Assert.InRange(written.Year, 2000, 2100);
            Assert.InRange(written.Month, 1, 12);
            Assert.InRange(written.Day, 1, 31);

            Assert.Equal(18446744073709551615UL,
                F.GetAs<ulong>(await ComplexStubFixture.WaitGoodAsync(m, "bd", "u64max")));
            Assert.Equal(-9223372036854775808L,
                F.GetAs<long>(await ComplexStubFixture.WaitGoodAsync(m, "bd", "i64min")));

            var bcd8 = await debug.TriggerReadAsync("bd", "bcd8");
            Assert.Equal(99999999L, F.GetAs<long>(bcd8));

            // UTF-8 多字节：镜像按 40 字节字段截断（串为 41 字节 → 末字截断），前缀必须逐字节正确
            var utf8 = F.GetAs<string>(await ComplexStubFixture.WaitGoodAsync(m, "bd", "utf8"));
            Assert.StartsWith("温度模拟器·σ(Σ)© 中文边界", utf8, StringComparison.Ordinal);

            // 非有限浮点：值保留、质量 Uncertain（GATE-3 绝不产出伪 Good）
            var inf = await ComplexStubFixture.WaitQualityAsync(m, "bd", "f32inf", PointQuality.Uncertain);
            Assert.Equal(float.PositiveInfinity, F.GetAs<float>(inf));
            Assert.Equal("ss.reason.infinite", inf.Reason);

            var nan = await ComplexStubFixture.WaitQualityAsync(m, "bd", "f32nan", PointQuality.Uncertain);
            Assert.True(float.IsNaN(F.GetAs<float>(nan)));
            Assert.Equal("ss.reason.nan", nan.Reason);

            Assert.Equal(0.0f, F.GetAs<float>(await ComplexStubFixture.WaitGoodAsync(m, "bd", "f32zero")));
        }
        finally
        {
            engine.Dispose();
        }
    }

    // ─────────────────────── C1-3：四字序四台同逻辑值、原始字互异 ───────────────────────

    [SkippableTheory]
    [InlineData(7, "abcd", new[] { 0x4048, 0xF5C3 }, "4000000000", -1234567)]   // ABCD
    [InlineData(8, "badc", new[] { 0x4840, 0xC3F5 }, "4000000000", -1234567)]   // BADC
    [InlineData(9, "cdab", new[] { 0xF5C3, 0x4048 }, "4000000000", -1234567)]   // CDAB
    [InlineData(10, "dcba", new[] { 0xC3F5, 0x4840 }, "4000000000", -1234567)]  // DCBA
    public async Task C1_FourWordOrders_DecodeSameLogicalValue_WithDistinctRawWords(
        int unitId, string swap, int[] expectedRawWords, string expectedU32, int expectedI32)
    {
        _stub.RequireStub();

        var points = string.Concat(
            ComplexStubFixture.Point("f32", 0, "float32", "area=\"holding\""),
            ComplexStubFixture.Point("i32", 2, "int32", "area=\"holding\""),
            ComplexStubFixture.Point("u32", 4, "uint32", "area=\"holding\""),
            ComplexStubFixture.Point("f64", 8, "float64", "area=\"holding\""),
            ComplexStubFixture.Point("i64", 12, "int64", "area=\"holding\""),
            // raw 点不做字序置换：拿到线上原始字，用于证明四台原始字排布互异
            ComplexStubFixture.Point("rawf32", 0, "raw", "area=\"holding\" length=\"2\""));

        var xml = "<SamplerConfig schemaVersion=\"3.0\">" + GlobalFast +
                  "<Transports>" + ComplexStubFixture.Transport("p3", _stub.SimPortP3) + "</Transports>" +
                  "<Devices>" + ComplexStubFixture.Device("d", "p3", unitId, "ps") + "</Devices>" +
                  "<PointSets><PointSet id=\"ps\"><Defaults swap=\"" + swap + "\" /><Points>" + points +
                  "</Points></PointSet></PointSets></SamplerConfig>";

        using var engine = new SamplerEngine(_stub.LoadConfig(xml));
        engine.Start();
        try
        {
            IDeviceManager m = engine;

            Assert.Equal(3.14f, F.GetAs<float>(await ComplexStubFixture.WaitGoodAsync(m, "d", "f32")), 5);
            Assert.Equal(expectedI32, F.GetAs<int>(await ComplexStubFixture.WaitGoodAsync(m, "d", "i32")));
            Assert.Equal(uint.Parse(expectedU32, CultureInfo.InvariantCulture),
                F.GetAs<uint>(await ComplexStubFixture.WaitGoodAsync(m, "d", "u32")));
            Assert.Equal(1.23456789, F.GetAs<double>(await ComplexStubFixture.WaitGoodAsync(m, "d", "f64")), 9);
            Assert.Equal(-5000000000L, F.GetAs<long>(await ComplexStubFixture.WaitGoodAsync(m, "d", "i64")));

            // 线上原始字：3.14 的 IEEE754 大端为 0x4048F5C3，四台各按自己字序排布
            var raw = F.GetAs<ushort[]>(await ComplexStubFixture.WaitGoodAsync(m, "d", "rawf32"));
            Assert.Equal(new[] { (ushort)expectedRawWords[0], (ushort)expectedRawWords[1] }, raw);
        }
        finally
        {
            engine.Dispose();
        }
    }

    // ─────────────────────── C1-4：块读（120 字大块 + 静态块逐字断言） ───────────────────────

    [SkippableFact]
    public async Task C1_BlockRead_CoversLargeWindowAndExactValues()
    {
        _stub.RequireStub();
        _stub.RequireSimLog();

        // 静态块：VER-BOUND 4x@116..136（0xFFFF/0x0000 交替，脚本不刷新）
        var blockPoints = string.Concat(
            ComplexStubFixture.Point("blk.raw20", 116, "raw", "length=\"20\""),
            ComplexStubFixture.Point("blk.u16a", 130, "uint16"),
            ComplexStubFixture.Point("blk.u16b", 135, "uint16"));

        var xml = "<SamplerConfig schemaVersion=\"3.0\">" + GlobalFast +
                  "<Transports>" + ComplexStubFixture.Transport("p3", _stub.SimPortP3) + "</Transports>" +
                  "<Devices>" + ComplexStubFixture.Device("bd", "p3", 13, "ps") + "</Devices>" +
                  "<PointSets><PointSet id=\"ps\"><Defaults area=\"holding\" />" +
                  "<Blocks><Block id=\"static20\" area=\"holding\" start=\"116\" count=\"20\">" + blockPoints +
                  "</Block></Blocks>" +
                  "<Points>" + ComplexStubFixture.Point("bd.heartbeat", 136, "uint16", "area=\"holding\"") + "</Points>" +
                  "</PointSet></PointSets></SamplerConfig>";

        using var engine = new SamplerEngine(_stub.LoadConfig(xml));
        engine.Start();
        try
        {
            IDeviceManager m = engine;

            var block = F.GetAs<ushort[]>(await ComplexStubFixture.WaitGoodAsync(m, "bd", "blk.raw20"));
            Assert.Equal(20, block.Length);
            for (var i = 0; i < 20; i++)
            {
                Assert.Equal(i % 2 == 0 ? (ushort)0xFFFF : (ushort)0x0000, block[i]);
            }

            Assert.Equal(0xFFFF, F.GetAs<ushort>(await ComplexStubFixture.WaitGoodAsync(m, "bd", "blk.u16a")));
            Assert.Equal(0x0000, F.GetAs<ushort>(await ComplexStubFixture.WaitGoodAsync(m, "bd", "blk.u16b")));
            Assert.True(F.GetAs<ushort>(await ComplexStubFixture.WaitGoodAsync(m, "bd", "bd.heartbeat")) >= 0);

            // 一次块读 = 一次请求：TriggerBlockRead 前后统计镜像日志中该从站的请求数
            var before = _stub.CountSimLogLines(_stub.SimPortP3, 13);
            var read = await ((IModbusDebugTool)engine).TriggerBlockReadAsync("bd/static20");
            var after = _stub.CountSimLogLines(_stub.SimPortP3, 13);

            Assert.Equal(20, read);
            Assert.Equal(1, after - before);
        }
        finally
        {
            engine.Dispose();
        }
    }

    // ─────────────────────── C1-5：散点合并（mergeGap / maxRegistersPerRead） ───────────────────────

    [SkippableFact]
    public async Task C1_ScatterMerge_HonoursMergeGapAndMaxRegistersPerRead()
    {
        _stub.RequireStub();
        _stub.RequireSimLog();

        // WO-ABCD（unit 7）：4x@0..7 连续 8 字 + 4x@10 起的 float64（空 2 字空洞）
        var points = string.Concat(
            ComplexStubFixture.Point("h.f32", 0, "float32"),
            ComplexStubFixture.Point("h.i32", 2, "int32"),
            ComplexStubFixture.Point("h.u32", 4, "uint32"),
            ComplexStubFixture.Point("h.i16", 6, "int16"),
            ComplexStubFixture.Point("h.u16", 7, "uint16"));
        var tail = ComplexStubFixture.Point("h.tail", 10, "float64");

        // ① mergeGap=4：2 字空洞被吸收 → 每周期 1 次请求（14 字一窗）
        Assert.Equal(1, (await CountRequestsPerCycle(points, mergeGap: 4, maxRegistersPerRead: 125, extraPoints: tail)).PerCycle);

        // ② mergeGap=0：空洞不吸收 → 每周期 2 次请求
        Assert.Equal(2, (await CountRequestsPerCycle(points, mergeGap: 0, maxRegistersPerRead: 125, extraPoints: tail)).PerCycle);

        // ③ maxRegistersPerRead=4：14 字必须按上限拆窗 → 每周期 3 次请求
        Assert.Equal(3, (await CountRequestsPerCycle(points, mergeGap: 4, maxRegistersPerRead: 4, extraPoints: tail)).PerCycle);
    }

    private sealed class CycleStats
    {
        public int Requests;
        public int Cycles;
        public int PerCycle => Cycles == 0 ? 0 : (int)Math.Round((double)Requests / Cycles, MidpointRounding.AwayFromZero);

        public override string ToString() => "请求 " + Requests + " 次 / " + Cycles + " 周期 = 每周期 " + PerCycle + " 次";
    }

    /// <summary>用镜像请求日志统计「每周期请求数」，验证散点合并与单次读上限行为。</summary>
    private async Task<CycleStats> CountRequestsPerCycle(string points, int mergeGap, int maxRegistersPerRead,
        string extraPoints)
    {
        // 观察窗口取 2s @ 300ms 一拍 ≈ 6 拍；允许 ±1 拍边界误差（断言用 ±2 次请求的容差）
        const int rateMs = 300;
        const int windowMs = 2000;
        var xml = "<SamplerConfig schemaVersion=\"3.0\">" +
                  "<Global><Retry count=\"0\" /><Polling rateMs=\"" + rateMs + "\" requestTimeoutMs=\"3000\" />" +
                  "<Scheduler mergeGap=\"" + mergeGap + "\" maxRegistersPerRead=\"" + maxRegistersPerRead + "\" /></Global>" +
                  "<ScanGroups><ScanGroup id=\"normal\" mode=\"poll\" rateMs=\"" + rateMs + "\" /></ScanGroups>" +
                  "<Transports>" + ComplexStubFixture.Transport("p3", _stub.SimPortP3) + "</Transports>" +
                  "<Devices>" + ComplexStubFixture.Device("wo", "p3", 7, "ps") + "</Devices>" +
                  "<PointSets><PointSet id=\"ps\"><Points>" + points + extraPoints + "</Points></PointSet></PointSets>" +
                  "</SamplerConfig>";

        using var engine = new SamplerEngine(_stub.LoadConfig(xml));
        engine.Start();
        try
        {
            // 等首轮采集完成（4x@0 可读即代表首轮已发）
            await ComplexStubFixture.WaitGoodAsync((IDeviceManager)engine, "wo", "h.f32", 10000);

            var before = _stub.CountSimLogLines(_stub.SimPortP3, 7);
            await Task.Delay(windowMs);
            var after = _stub.CountSimLogLines(_stub.SimPortP3, 7);

            return new CycleStats { Requests = after - before, Cycles = windowMs / rateMs };
        }
        finally
        {
            engine.Dispose();
        }
    }

    // ─────────────────────── C1-6：unitId 覆盖（块级生效 / 点位级在轮询中被忽略） ───────────────────────

    [SkippableFact]
    public async Task C1_BlockUnitIdOverride_ReadsFromGatewayChildSlave()
    {
        _stub.RequireStub();
        _stub.RequireSimLog();

        // 设备声明为 unit 7，但块级 unitId=8 → 必须从 WO-BADC 读，且用 BADC 字序解码
        var blockPoints = ComplexStubFixture.Point("g.f32", 0, "float32");
        var xml = "<SamplerConfig schemaVersion=\"3.0\">" + GlobalFast +
                  "<Transports>" + ComplexStubFixture.Transport("p3", _stub.SimPortP3) + "</Transports>" +
                  "<Devices>" + ComplexStubFixture.Device("gw", "p3", 7, "ps") + "</Devices>" +
                  "<PointSets><PointSet id=\"ps\">" +
                  "<Blocks><Block id=\"b8\" area=\"holding\" start=\"0\" count=\"2\" unitId=\"8\" swap=\"badc\">" +
                  blockPoints + "</Block></Blocks>" +
                  "</PointSet></PointSets></SamplerConfig>";

        using var engine = new SamplerEngine(_stub.LoadConfig(xml));
        engine.Start();
        try
        {
            IDeviceManager m = engine;
            await ComplexStubFixture.WaitGoodAsync(m, "gw", "g.f32");
            Assert.Equal(3.14f, F.GetAs<float>(m.GetValueDetail("gw", "g.f32")), 5);

            // 证据：请求确实发给了 unit 8（WO-BADC），而不是设备声明的 unit 7
            Assert.True(_stub.CountSimLogLines(_stub.SimPortP3, 8) > 0, "块 unitId 覆盖未生效：unit 8 没有收到请求");
        }
        finally
        {
            engine.Dispose();
        }
    }

    [SkippableFact]
    public async Task C1_PointUnitIdOverride_AppliesToPollingAndOnDemand()
    {
        // ADR D33（findings D19）：点位级 unitId 决定该点位属于哪个从站，轮询与按需两条路径都必须遵守。
        // 设备声明 unit 7（WO-ABCD），两个点都覆盖 unitId="8"（WO-BADC）+ swap="badc"。
        // 修复前轮询固定用 device.UnitId → 从 unit 7 读、按 BADC 解码 → 得到 197391.83 的错值且质量仍是 Good。
        _stub.RequireStub();
        _stub.RequireSimLog();

        var xml = "<SamplerConfig schemaVersion=\"3.0\">" +
                  "<Global><Retry count=\"0\" /><Polling rateMs=\"200\" requestTimeoutMs=\"3000\" /></Global>" +
                  "<ScanGroups><ScanGroup id=\"normal\" mode=\"poll\" rateMs=\"200\" />" +
                  "<ScanGroup id=\"od\" mode=\"onDemand\" /></ScanGroups>" +
                  "<Transports>" + ComplexStubFixture.Transport("p3", _stub.SimPortP3) + "</Transports>" +
                  "<Devices>" + ComplexStubFixture.Device("wo", "p3", 7, "ps") + "</Devices>" +
                  "<PointSets><PointSet id=\"ps\"><Points>" +
                  ComplexStubFixture.Point("od.f32", 0, "float32",
                      "unitId=\"8\" swap=\"badc\" scanGroup=\"od\"") +
                  ComplexStubFixture.Point("np.f32", 0, "float32",
                      "unitId=\"8\" swap=\"badc\" scanGroup=\"normal\"") +
                  "</Points></PointSet></PointSets></SamplerConfig>";

        using var engine = new SamplerEngine(_stub.LoadConfig(xml));
        var unit7Before = _stub.CountSimLogLines(_stub.SimPortP3, 7);
        var unit8Before = _stub.CountSimLogLines(_stub.SimPortP3, 8);
        engine.Start();
        try
        {
            IDeviceManager m = engine;

            // 轮询点：按点位自身 unitId 发往 unit 8，解出真实值 3.14
            var polled = await ComplexStubFixture.WaitGoodAsync(m, "wo", "np.f32");
            Assert.Equal(3.14f, F.GetAs<float>(polled), 5);

            await Task.Delay(400);
            Assert.True(_stub.CountSimLogLines(_stub.SimPortP3, 8) > unit8Before, "轮询点应发往点位声明的 unit 8");
            Assert.Equal(unit7Before, _stub.CountSimLogLines(_stub.SimPortP3, 7)); // 不再误读设备声明的 unit 7

            // onDemand + TriggerRead：同一条点位级 unitId 覆盖
            var triggered = await ((IModbusDebugTool)engine).TriggerReadAsync("wo", "od.f32");
            Assert.Equal(3.14f, F.GetAs<float>(triggered), 5);
            Assert.True(_stub.CountSimLogLines(_stub.SimPortP3, 8) > unit8Before,
                "TriggerRead 应按点位级 unitId 发往 unit 8");
        }
        finally
        {
            engine.Dispose();
        }
    }

    // ─────────────────────── C1-6b：设备级 swap 生效（D18 / ADR D38） ───────────────────────

    [SkippableFact]
    public async Task C1_DeviceLevelSwap_AppliesToPointsWithoutExplicitSwap()
    {
        // 测试计划（_testplan/integration-tests.md 场景 5）的口径是「四台 device 各自 swap」：
        // 点位不写 swap，由所属设备的 swap 决定字序。
        // 修复前 Device@swap 被解析进 DeviceConfig.Swap 却从不参与点位兜底链（findings D18）：
        // 点位只能拿到 Global 默认 CDAB，于是 3.14 的 BADC 原始字被解成 -490.5645。
        _stub.RequireStub();

        // unit 8 = WO-BADC：原始字按 BADC 排布。设备声明 swap="badc"。
        var points = ComplexStubFixture.Point("f32", 0, "float32", "area=\"holding\"");
        var xml = "<SamplerConfig schemaVersion=\"3.0\">" + GlobalFast +
                  "<Transports>" + ComplexStubFixture.Transport("p3", _stub.SimPortP3) + "</Transports>" +
                  "<Devices>" + ComplexStubFixture.Device("wob", "p3", 8, "ps", "swap=\"badc\"") + "</Devices>" +
                  "<PointSets><PointSet id=\"ps\"><Points>" + points + "</Points></PointSet></PointSets>" +
                  "</SamplerConfig>";

        var config = _stub.LoadConfig(xml);
        Assert.Equal(SwapMode.Byte, config.Devices.Single().Swap);                            // 设备级属性已解析
        Assert.False(config.PointSets.Single().Points.Single().HasSwapDeclared);              // 点位未声明 → 待设备兜底

        using var engine = new SamplerEngine(config);
        engine.Start();
        try
        {
            var value = await ComplexStubFixture.WaitGoodAsync((IDeviceManager)engine, "wob", "f32");
            // 设备级 swap 生效：按 BADC 解码 → 3.14（而不是 Global 缺省 CDAB 解出的 -490.56）
            Assert.Equal(3.14f, F.GetAs<float>(value), 5);
        }
        finally
        {
            engine.Dispose();
        }
    }

    // ─────────────────────── C1-6c：多字 BCD / datetime 轮询解出完整值（D20 / ADR D34） ───────────────────────

    [SkippableFact]
    public async Task C1_MultiWordBcdAndDateTime_DecodeWholeValueWhilePolling()
    {
        // ADR D34：bcd 按 Bcd@digits、datetime 按 DateTime@format 推导有效字长，
        // 轮询路径据此切片，不再只读第 1 个字（findings D20：
        // 修复前 BCD 只解出 1234、datetime 月/日被兜底成 1，且质量仍是 Good）。
        _stub.RequireStub();

        // 与修复前同样的点位：不写 length（CGV-8 只放行 string/raw），也不靠 <Slices>
        var polled = string.Concat(
            ComplexStubFixture.Point("dt.poll", 78, "datetime", "area=\"holding\""),
            ComplexStubFixture.Point("bcd.poll", 76, "bcd", "area=\"holding\"", "<Bcd digits=\"8\" />"));

        var xml = "<SamplerConfig schemaVersion=\"3.0\">" + GlobalFast +
                  "<Transports>" + ComplexStubFixture.Transport("p3", _stub.SimPortP3) + "</Transports>" +
                  "<Devices>" + ComplexStubFixture.Device("wo", "p3", 7, "ps") + "</Devices>" +
                  "<PointSets><PointSet id=\"ps\"><Defaults swap=\"abcd\" /><Points>" + polled +
                  "</Points></PointSet></PointSets></SamplerConfig>";

        using var engine = new SamplerEngine(_stub.LoadConfig(xml));
        engine.Start();
        try
        {
            IDeviceManager m = engine;

            // BCD(8)：2 个寄存器 → 12345678（修复前只读首字得 1234）
            var bcd = await ComplexStubFixture.WaitGoodAsync(m, "wo", "bcd.poll");
            Assert.Equal(12345678L, F.GetAs<long>(bcd));

            // datetime(plc6)：6 个字 → 年月日时分秒齐全（桩每秒刷新为当前时间）
            var dt = F.GetAs<DateTime>(await ComplexStubFixture.WaitGoodAsync(m, "wo", "dt.poll"));
            // 断言「6 个字解成了自洽的日期时间」，而不是「等于今天」：
            // 桩提供的可能是"脚本启动时刻"（长稳跨午夜时就不是今天了），绑死当天日期会让用例假失败。
            var now = DateTime.Now;
            Assert.InRange(dt, now.AddDays(-2), now.AddDays(2));
            Assert.InRange(dt.Hour, 0, 23);
            Assert.InRange(dt.Minute, 0, 59);
        }
        finally
        {
            engine.Dispose();
        }
    }

    // ─────────────────────── C1-7：停用点位 / 停用块不轮询 ───────────────────────

    [SkippableFact]
    public async Task C1_DisabledPointsAndBlocks_AreNotPolled()
    {
        _stub.RequireStub();
        _stub.RequireSimLog();

        // d.offpoints（unit 9）：全部点位 enabled=false → 一条请求都不该发
        // d.offblock（unit 10）：唯一的块 enabled=false → 一条请求都不该发
        // d.ctrl（unit 7）：正常点位 → 必须持续有请求（证明日志口径有效）
        var ps9 = string.Concat(
            ComplexStubFixture.Point("x.f32", 0, "float32", "enabled=\"false\""),
            ComplexStubFixture.Point("x.u16", 6, "uint16", "enabled=\"false\""));

        var ps10 = ComplexStubFixture.Point("blk.u16", 0, "uint16");
        var ps7 = ComplexStubFixture.Point("ok.u16", 1, "uint16", "area=\"input\"");

        var xml = "<SamplerConfig schemaVersion=\"3.0\">" + GlobalFast +
                  "<Transports>" + ComplexStubFixture.Transport("p3", _stub.SimPortP3) + "</Transports>" +
                  "<Devices>" +
                  ComplexStubFixture.Device("offpoints", "p3", 9, "ps9") +
                  ComplexStubFixture.Device("offblock", "p3", 10, "ps10") +
                  ComplexStubFixture.Device("ctrl", "p3", 7, "ps7") +
                  "</Devices>" +
                  "<PointSets>" +
                  "<PointSet id=\"ps9\"><Points>" + ps9 + "</Points></PointSet>" +
                  "<PointSet id=\"ps10\"><Blocks><Block id=\"b\" area=\"holding\" start=\"0\" count=\"2\" enabled=\"false\">" +
                  ps10 + "</Block></Blocks></PointSet>" +
                  "<PointSet id=\"ps7\"><Points>" + ps7 + "</Points></PointSet>" +
                  "</PointSets></SamplerConfig>";

        using var engine = new SamplerEngine(_stub.LoadConfig(xml));
        engine.Start();
        try
        {
            await ComplexStubFixture.WaitGoodAsync((IDeviceManager)engine, "ctrl", "ok.u16");

            var disabled9 = _stub.CountSimLogLines(_stub.SimPortP3, 9);
            var disabled10 = _stub.CountSimLogLines(_stub.SimPortP3, 10);
            var ctrlBefore = _stub.CountSimLogLines(_stub.SimPortP3, 7);

            await Task.Delay(900);

            Assert.Equal(disabled9, _stub.CountSimLogLines(_stub.SimPortP3, 9));
            Assert.Equal(disabled10, _stub.CountSimLogLines(_stub.SimPortP3, 10));
            Assert.True(_stub.CountSimLogLines(_stub.SimPortP3, 7) > ctrlBefore, "对照组未继续轮询，日志口径不可信");
        }
        finally
        {
            engine.Dispose();
        }
    }

    // ─────────────────────── C1-8：扫描组 poll / once / onDemand ───────────────────────

    [SkippableFact]
    public async Task C1_ScanGroupModes_PollOnceOnDemand()
    {
        _stub.RequireStub();
        _stub.RequireSimLog();

        // unit 7 = poll（持续）、unit 8 = once（只跑一次）、unit 9 = onDemand（不轮询，手动触发）
        var xml = "<SamplerConfig schemaVersion=\"3.0\">" +
                  "<Global><Retry count=\"0\" /><Polling rateMs=\"200\" requestTimeoutMs=\"3000\" /></Global>" +
                  "<ScanGroups>" +
                  "<ScanGroup id=\"poll\" mode=\"poll\" rateMs=\"250\" />" +
                  "<ScanGroup id=\"once\" mode=\"once\" rateMs=\"250\" />" +
                  "<ScanGroup id=\"od\" mode=\"onDemand\" rateMs=\"250\" />" +
                  "</ScanGroups>" +
                  "<Transports>" + ComplexStubFixture.Transport("p3", _stub.SimPortP3) + "</Transports>" +
                  "<Devices>" +
                  ComplexStubFixture.Device("pollDev", "p3", 7, "psPoll") +
                  ComplexStubFixture.Device("onceDev", "p3", 8, "psOnce") +
                  ComplexStubFixture.Device("odDev", "p3", 9, "psOd") +
                  "</Devices>" +
                  "<PointSets>" +
                  "<PointSet id=\"psPoll\"><Points>" + ComplexStubFixture.Point("v", 1, "uint16", "area=\"input\" scanGroup=\"poll\"") + "</Points></PointSet>" +
                  "<PointSet id=\"psOnce\"><Points>" + ComplexStubFixture.Point("v", 1, "uint16", "area=\"input\" scanGroup=\"once\"") + "</Points></PointSet>" +
                  "<PointSet id=\"psOd\"><Points>" + ComplexStubFixture.Point("v", 1, "uint16", "area=\"input\" scanGroup=\"od\"") + "</Points></PointSet>" +
                  "</PointSets></SamplerConfig>";

        using var engine = new SamplerEngine(_stub.LoadConfig(xml));
        var pollBefore = _stub.CountSimLogLines(_stub.SimPortP3, 7);
        var onceBefore = _stub.CountSimLogLines(_stub.SimPortP3, 8);
        var odBefore = _stub.CountSimLogLines(_stub.SimPortP3, 9);
        engine.Start();
        try
        {
            await ComplexStubFixture.WaitGoodAsync((IDeviceManager)engine, "pollDev", "v");
            await Task.Delay(1000);

            var poll = _stub.CountSimLogLines(_stub.SimPortP3, 7) - pollBefore;
            var once = _stub.CountSimLogLines(_stub.SimPortP3, 8) - onceBefore;
            var od = _stub.CountSimLogLines(_stub.SimPortP3, 9) - odBefore;

            Assert.True(poll >= 3, "poll 组应持续轮询（实测 " + poll + " 次）");
            Assert.Equal(1, once);
            Assert.Equal(0, od);

            // onDemand：手动触发才有请求
            await ((IModbusDebugTool)engine).TriggerReadAsync("odDev", "v");
            Assert.Equal(1, _stub.CountSimLogLines(_stub.SimPortP3, 9) - odBefore);
        }
        finally
        {
            engine.Dispose();
        }
    }

    // ─────────────────────── C1-9：计算点 + i18n + 显示格式化 ───────────────────────

    [SkippableFact]
    public async Task C1_CalculatedPointAndI18nSubstitution()
    {
        _stub.RequireStub();

        var i18nDir = _stub.WorkSubDir("i18n");
        File.WriteAllText(Path.Combine(i18nDir, "zh.lang"),
            "gain = 2\n# 注释行\nunit.c = 温度标定\npoint.f32.name = 料筒温度\n", new UTF8Encoding(false));

        var points = string.Concat(
            ComplexStubFixture.Point("wo.f32", 0, "float32",
                "name=\"${point.f32.name}\" unit=\"${unit.c}\"",
                "<Format prefix=\"${unit.c} \" suffix=\" C\" decimals=\"1\" />"),
            // 加一层 Scale：产量为 double（Float32 原样解码是 float），表达式求值器只认 double/整型口径
            ComplexStubFixture.Point("wo.scaled", 0, "float32", "area=\"holding\"",
                "<Scale factor=\"1\" />"),
            ComplexStubFixture.Point("wo.count", 220, "uint16"));

        var xml = "<SamplerConfig schemaVersion=\"3.0\">" + GlobalFast +
                  "<I18n><Files><File path=\"" + Path.Combine(i18nDir, "zh.lang").Replace("\\", "/") + "\" /></Files></I18n>" +
                  "<Transports>" + ComplexStubFixture.Transport("p3", _stub.SimPortP3) + "</Transports>" +
                  "<Devices>" + ComplexStubFixture.Device("wo", "p3", 7, "ps") + "</Devices>" +
                  "<PointSets><PointSet id=\"ps\"><Defaults swap=\"abcd\" /><Points>" + points + "</Points>" +
                  "<Calculated>" +
                  "<Point id=\"calc.gain\"><Expression>P('wo.scaled') * T('gain') + 1</Expression></Point>" +
                  "<Point id=\"calc.f32direct\"><Expression>P('wo.f32') + 1</Expression></Point>" +
                  "</Calculated>" +
                  "</PointSet></PointSets></SamplerConfig>";

        var config = _stub.LoadConfig(xml);
        var set = config.PointSets.Single();
        var f32 = set.Points.Single(p => p.Id == "wo.f32");
        Assert.Equal("料筒温度", f32.Name);
        Assert.Equal("温度标定", f32.Unit);
        Assert.Equal("温度标定 ", f32.Format?.Prefix);

        using var engine = new SamplerEngine(config);
        engine.Start();
        try
        {
            IDeviceManager m = engine;
            await ComplexStubFixture.WaitGoodAsync(m, "wo", "wo.f32");
            await ComplexStubFixture.WaitGoodAsync(m, "wo", "wo.scaled");

            // 显示：i18n 前缀 + 1 位小数
            Assert.Equal("温度标定 3.1 C", m.GetValue("wo", "wo.f32"));

            // 计算点：P('wo.scaled') * T('gain') + 1 = (3.14 * 1) * 2 + 1 = 7.28
            var calc = await ComplexStubFixture.WaitValueAsync(m, "wo", "calc.gain", (double v) => Math.Abs(v - 7.28) < 0.0001);
            Assert.Equal(7.28, calc, 4);

            // D21（ADR D35）：P() 对数值类型统一转 double —— float32 点解出的 float 同样可用
            // （修复前 TryGetValue<double> 判类型不符 → NaN → Bad(ss.reason.calculate)）
            var direct = await ComplexStubFixture.WaitValueAsync(m, "wo", "calc.f32direct",
                (double v) => Math.Abs(v - 4.14) < 0.0001);
            Assert.Equal(4.14, direct, 4);
            Assert.Equal(PointQuality.Good, m.GetValueDetail("wo", "calc.f32direct").Quality);
        }
        finally
        {
            engine.Dispose();
        }
    }

    // ─────────────────────── C1-10：raw 门禁 + TriggerRead/TriggerBlockRead ───────────────────────

    [SkippableFact]
    public async Task C1_RawAccessGate_AndTriggerReads()
    {
        _stub.RequireStub();
        _stub.RequireSimLog();

        const string devices = "<Devices>" + "<Device id=\"wo\" transport=\"p3\" unitId=\"7\" pointSet=\"ps7\" />" +
                               "<Device id=\"wob\" transport=\"p3\" unitId=\"8\" pointSet=\"ps8\" />" +
                               "<Device id=\"bd\" transport=\"p3\" unitId=\"13\" pointSet=\"ps13\" />" + "</Devices>";
        const string pointSets = "<PointSets>" +
            "<PointSet id=\"ps7\"><Defaults swap=\"abcd\" /><Points>" + "<Point id=\"f32\" address=\"0\" dataType=\"float32\" area=\"holding\" />" + "</Points></PointSet>" +
            "<PointSet id=\"ps8\"><Defaults swap=\"badc\" /><Points>" + "<Point id=\"f32\" address=\"0\" dataType=\"float32\" area=\"holding\" />" + "</Points></PointSet>" +
            "<PointSet id=\"ps13\"><Defaults area=\"holding\" />" +
            "<Blocks><Block id=\"b16\" area=\"holding\" start=\"116\" count=\"16\">" +
            "<Point id=\"raw16\" address=\"116\" dataType=\"raw\" length=\"16\" /></Block></Blocks></PointSet>" +
            "</PointSets>";
        var transports = "<Transports>" + ComplexStubFixture.Transport("p3", _stub.SimPortP3) + "</Transports>";

        // ① 默认关闭：RawRead/RawWrite 必须抛 InvalidOperationException（门禁生效）
        var closed = new SamplerEngine(_stub.LoadConfig(
            "<SamplerConfig schemaVersion=\"3.0\">" + GlobalFast + transports + devices + pointSets + "</SamplerConfig>"));
        closed.Start();
        try
        {
            var debug = (IModbusDebugTool)closed;
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => debug.RawReadAsync("wo", ModbusArea.HoldingRegister, 0, 2));
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => debug.RawWriteAsync("wo", ModbusArea.HoldingRegister, 0, new ushort[] { 1 }));
        }
        finally
        {
            closed.Dispose();
        }

        // ② Diagnostics@allowRawAccess=true：原始读跨越字序（不做任何置换）
        var open = new SamplerEngine(_stub.LoadConfig(
            "<SamplerConfig schemaVersion=\"3.0\">" + GlobalFast +
            "<Diagnostics allowRawAccess=\"true\" />" + transports + devices + pointSets + "</SamplerConfig>"));
        open.Start();
        try
        {
            var debug = (IModbusDebugTool)open;
            IDeviceManager m = open;

            await ComplexStubFixture.WaitGoodAsync(m, "wo", "f32");

            var abcd = await debug.RawReadAsync("wo", ModbusArea.HoldingRegister, 0, 2);
            var badc = await debug.RawReadAsync("wob", ModbusArea.HoldingRegister, 0, 2);
            Assert.True(abcd.Success);
            Assert.True(badc.Success);
            Assert.Equal(new ushort[] { 0x4048, 0xF5C3 }, abcd.Registers);
            Assert.Equal(new ushort[] { 0x4840, 0xC3F5 }, badc.Registers);
            Assert.Equal(4, abcd.RawBytes.Length);

            // TriggerRead：走点位解析管道
            var triggered = await debug.TriggerReadAsync("wob", "f32");
            Assert.Equal(3.14f, F.GetAs<float>(triggered), 5);

            // TriggerBlockRead：返回块内寄存器数，逐点写缓存
            var before = _stub.CountSimLogLines(_stub.SimPortP3, 13);
            var count = await debug.TriggerBlockReadAsync("bd/b16");
            var after = _stub.CountSimLogLines(_stub.SimPortP3, 13);
            Assert.Equal(16, count);
            Assert.Equal(1, after - before);

            var raw16 = F.GetAs<ushort[]>(m.GetValueDetail("bd", "raw16"));
            Assert.Equal(16, raw16.Length);
            Assert.Equal(0xFFFF, raw16[0]);
            Assert.Equal(0x0000, raw16[1]);
        }
        finally
        {
            open.Dispose();
        }
    }
}
