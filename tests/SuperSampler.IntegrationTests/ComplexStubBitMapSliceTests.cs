using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SuperSampler.Abstractions.Facade;
using SuperSampler.Abstractions.Values;
using SuperSampler.Core.Config;
using SuperSampler.Core.Runtime;
using Xunit;

namespace SuperSampler.IntegrationTests;

/// <summary>
/// 第五~七步的真链路用例（复杂桩镜像 complex_sim.py 直连内部口）：
/// <list type="bullet">
/// <item><b>位映射展开</b>：IM-01（P1:16002 unit1）3x@0「运行状态字」用 &lt;Bits&gt; 声明
/// bit0 电源 / bit1-7 阶段（Field+Map）/ bit8 故障，整字点位同时保留；
/// 断言子点位与整字点位**逐位一致**（同一次请求里的同一个寄存器），且子点位不带报警。</item>
/// <item><b>Slices 轮询拼值</b>：EM-01（P3:16004 unit5）3x@0「累计电能」拆成两个片段，
/// 与同址的整字点位读数逐值相等（证明片段拼出来的值就是设备上的值）。</item>
/// <item><b>字符串 padding/left</b>：ENV-01（P3:16004 unit6）3x@19「区域名称」UTF-8 多字节串，
/// 0x00 补齐靠左解码正确。</item>
/// </list>
/// 环境不可用 → 显式跳过（ComplexStubFixture.RequireStub）。
/// </summary>
[Collection(ComplexStubCollection.Name)]
public sealed class ComplexStubBitMapSliceTests
{
    private readonly ComplexStubFixture _stub;

    public ComplexStubBitMapSliceTests(ComplexStubFixture stub) => _stub = stub;

    private const string GlobalFast = """
        <Global>
          <Polling defaultIntervalMs="200" requestTimeoutMs="3000" />
          <Quality onCommError="bad" onCommErrorValue="null" />
        </Global>
        """;

    // ─────────────────────── 位映射展开（真链路） ───────────────────────

    [SkippableFact]
    public async Task C5_BitMapExpansion_ChildrenMatchTheIntegerPointAndCarryNoAlarms()
    {
        _stub.RequireStub();

        // IM-01：3x@0 运行状态字（bit0 电源 bit1..7 阶段 bit8 故障）、3x@1 报警码（枚举）
        var points =
            ComplexStubFixture.Point("status", 0, "uint16", "area=\"input\" intervalMs=\"200\"", "<Bits>"
                + "<Bit index=\"0\" name=\"power\" text=\"电源\"/>"
                + "<Field from=\"1\" to=\"7\" name=\"stage\" text=\"注塑阶段\">"
                + "<Map><Item key=\"0\">合模</Item><Item key=\"1\">注射</Item><Item key=\"2\">保压</Item>"
                + "<Item key=\"3\">熔胶</Item><Item key=\"4\">冷却</Item><Item key=\"5\">开模</Item>"
                + "<Item key=\"6\">顶出</Item></Map>"
                + "</Field>"
                + "<Bit index=\"8\" name=\"fault\" text=\"故障\"/>"
                + "</Bits>")
            + ComplexStubFixture.Point("alarmCode", 1, "uint16", "area=\"input\" intervalMs=\"200\"", "<Bits>"
                + "<Field from=\"0\" to=\"3\" name=\"code\" text=\"报警码\">"
                + "<Map><Item key=\"0\">无</Item><Item key=\"1\">油温过高</Item>"
                + "<Item key=\"2\">模具超温</Item><Item key=\"4\">射胶异常</Item></Map>"
                + "</Field></Bits>");

        var xml = "<SamplerConfig schemaVersion=\"3.0\">" + GlobalFast +
                  "<Transports>" + ComplexStubFixture.Transport("p1", _stub.SimPortP1) + "</Transports>" +
                  "<Devices>" + ComplexStubFixture.Device("im", "p1", 1, "psIM") + "</Devices>" +
                  "<PointSets><PointSet id=\"psIM\"><Defaults swap=\"abcd\" /><Points>" + points +
                  "</Points></PointSet></PointSets></SamplerConfig>";

        // 加载期：展开出的子点位进模型，整字点位保留，子点位不挂报警
        // （展开出的子点位追加在点表末尾，不与整字点位交错——顺序即上面声明的顺序 + 展开结果）
        var config = _stub.LoadConfig(xml);
        var expanded = config.PointSets[0].Points;
        Assert.Equal(new[] { "status", "alarmCode", "status.power", "status.stage", "status.fault", "alarmCode.code" },
            expanded.Select(p => p.Id).ToArray());
        Assert.All(expanded.Where(p => p.ParentPointId != null), p => Assert.Empty(p.Alarms));

        using var engine = new SamplerEngine(config);
        engine.Start();
        try
        {
            IDeviceManager manager = engine;

            for (var i = 0; i < 20; i++)
            {
                var word = ComplexStubFixture.GetAs<ushort>(await ComplexStubFixture.WaitGoodAsync(manager, "im", "status"));
                var power = ComplexStubFixture.GetAs<bool>(await ComplexStubFixture.WaitGoodAsync(manager, "im", "status.power"));
                var stage = ComplexStubFixture.GetAs<ushort>(await ComplexStubFixture.WaitGoodAsync(manager, "im", "status.stage"));
                var fault = ComplexStubFixture.GetAs<bool>(await ComplexStubFixture.WaitGoodAsync(manager, "im", "status.fault"));

                // 子点位 = 整字点位上的对应位（同一次请求里的同一个寄存器）
                Assert.Equal((word & 0x1) != 0, power);
                Assert.Equal((word >> 1) & 0x7F, stage);
                Assert.Equal((word & 0x100) != 0, fault);

                // Field 的 Map 生效（阶段值落在 0..6 时显示中文）
                var stageText = manager.GetValue("im", "status.stage");
                Assert.Equal(stage <= 6 ? new[] { "合模", "注射", "保压", "熔胶", "冷却", "开模", "顶出" }[stage] : stage.ToString(), stageText);

                var code = ComplexStubFixture.GetAs<ushort>(await ComplexStubFixture.WaitGoodAsync(manager, "im", "alarmCode.code"));
                Assert.Contains(code, new ushort[] { 0, 1, 2, 4 });   // 镜像只写这四个枚举值
                if (code == 1) Assert.Equal("油温过高", manager.GetValue("im", "alarmCode.code"));

                await Task.Delay(150);
            }
        }
        finally
        {
            engine.Stop();
        }
    }

    // ─────────────────────── Slices 轮询拼值（真链路） ───────────────────────

    [SkippableFact]
    public async Task C6_SlicesPointPollsAndAssemblesTheSameValueAsTheWholePoint()
    {
        _stub.RequireStub();

        // EM-01：3x@0 累计电能（uint32，占 0..1）——同一个值读两种写法：
        //   total      = 整字 uint32
        //   totalSplit = <Slices> 两个片段（低字 0、高字 1）拼出来的 uint32
        // 两者必须逐值相等；Slices 点位此前在轮询路径被跳过（旧限制），本用例即其回归证据。
        var points =
            ComplexStubFixture.Point("total", 0, "uint32", "area=\"input\" intervalMs=\"200\"")
            + ComplexStubFixture.Point("totalSplit", 0, "uint32", "area=\"input\" intervalMs=\"200\"",
                "<Slices><Slice address=\"0\" length=\"1\"/><Slice address=\"1\" length=\"1\"/></Slices>");

        var xml = "<SamplerConfig schemaVersion=\"3.0\">" + GlobalFast +
                  "<Transports>" + ComplexStubFixture.Transport("p3", _stub.SimPortP3) + "</Transports>" +
                  "<Devices>" + ComplexStubFixture.Device("em", "p3", 5, "psEM") + "</Devices>" +
                  "<PointSets><PointSet id=\"psEM\"><Defaults swap=\"abcd\" /><Points>" + points +
                  "</Points></PointSet></PointSets></SamplerConfig>";

        using var engine = new SamplerEngine(_stub.LoadConfig(xml));
        engine.Start();
        try
        {
            IDeviceManager manager = engine;

            // 轮询路径必须把两个点都刷成 Good（旧限制下 totalSplit 永远是坏值/未采集）
            await ComplexStubFixture.WaitGoodAsync(manager, "em", "totalSplit");

            for (var i = 0; i < 10; i++)
            {
                var whole = ComplexStubFixture.GetAs<uint>(await ComplexStubFixture.WaitGoodAsync(manager, "em", "total"));
                var split = ComplexStubFixture.GetAs<uint>(await ComplexStubFixture.WaitGoodAsync(manager, "em", "totalSplit"));
                Assert.Equal(whole, split);
                await Task.Delay(200);
            }
        }
        finally
        {
            engine.Stop();
        }
    }

    // ─────────────────────── 字符串 padding/left（真链路） ───────────────────────

    [SkippableFact]
    public async Task C7_StringPaddingDecodesMultiByteTextOnTheRealDevice()
    {
        _stub.RequireStub();

        // ENV-01：3x@19 区域名称（string(20B)，UTF-8 中文，不足补 0x00）
        var points = ComplexStubFixture.Point("zone", 19, "string", "area=\"input\" length=\"10\" intervalMs=\"200\"",
            "<String encoding=\"utf-8\" padding=\"0x00\" trimNull=\"true\" left=\"true\"/>");

        var xml = "<SamplerConfig schemaVersion=\"3.0\">" + GlobalFast +
                  "<Transports>" + ComplexStubFixture.Transport("p3", _stub.SimPortP3) + "</Transports>" +
                  "<Devices>" + ComplexStubFixture.Device("env", "p3", 6, "psENV") + "</Devices>" +
                  "<PointSets><PointSet id=\"psENV\"><Defaults swap=\"abcd\" /><Points>" + points +
                  "</Points></PointSet></PointSets></SamplerConfig>";

        using var engine = new SamplerEngine(_stub.LoadConfig(xml));
        engine.Start();
        try
        {
            IDeviceManager manager = engine;
            var text = ComplexStubFixture.GetAs<string>(await ComplexStubFixture.WaitGoodAsync(manager, "env", "zone"));

            Assert.StartsWith("注塑车间A区", text, StringComparison.Ordinal);
            Assert.DoesNotContain('\0', text);   // 0x00 补齐已按 padding 裁掉
        }
        finally
        {
            engine.Stop();
        }
    }
}
