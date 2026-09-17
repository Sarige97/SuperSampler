using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using SuperSampler.Abstractions.Facade;
using SuperSampler.Abstractions.Values;
using SuperSampler.Core.Runtime;
using Xunit;

namespace SuperSampler.IntegrationTests;

/// <summary>
/// 调度与采集链路的**真链路**演练（复杂桩镜像，内部口直连）：假链路证明不了的三件事——
/// <list type="number">
/// <item><b>同链路多从站并发不串台</b>：4 台从站（P1 unit 1-4）同时轮询，每台各自的点位值必须等于
/// 直接读该从站寄存器得到的值（unitId 串台会立刻对不上）；</item>
/// <item><b>超地址组上限自动切分</b>：P3 unit 7（224 个保持寄存器）声明 200 个严格相邻点 →
/// 一次节拍必须切成 2 次请求（125+75）。不切分就会被从站按非法数量拒绝（异常码 03）；</item>
/// <item><b>onDemand 不进周期计划</b>：只在门面整批触发时发请求，触发的请求数有界
/// （由镜像请求日志计数，不靠睡眠猜）。</item>
/// </list>
/// 环境不可用 → 显式跳过；退避/断路器形态已由 <c>ComplexStubBackoffTests</c>/<c>ComplexStubBreakerTests</c> 覆盖。
/// </summary>
[Collection(ComplexStubCollection.Name)]
public sealed class ComplexStubSchedulerTests
{
    private readonly ComplexStubFixture _stub;

    public ComplexStubSchedulerTests(ComplexStubFixture stub) => _stub = stub;

    /// <summary>请求超时给足（SLOW 从站单次 2s）；轮询间隔 200ms 让观察窗内有多拍。</summary>
    private const string GlobalFast = """
        <Global>
          <Retry count="0" intervalMs="10" />
          <Polling defaultIntervalMs="200" requestTimeoutMs="3000" />
          <Quality onCommError="bad" onCommErrorValue="null" />
        </Global>
        <Diagnostics allowRawAccess="true" />
        """;

    private SamplerEngine Build(string devices, string pointSets, int port, string variant = "tcp")
    {
        var xml = "<SamplerConfig schemaVersion=\"3.0\">" + GlobalFast +
                  "<Transports>" + ComplexStubFixture.Transport("t", port, variant) + "</Transports>" +
                  "<Devices>" + devices + "</Devices>" +
                  "<PointSets>" + pointSets + "</PointSets>" +
                  "</SamplerConfig>";

        var engine = new SamplerEngine(_stub.LoadConfig(xml));
        engine.Start();
        return engine;
    }

    // ─────────────────────── 1. 多从站并发：值不串台 ───────────────────────

    [SkippableFact]
    public async Task Four_slaves_polled_on_one_link_keep_every_value_on_its_own_slave()
    {
        _stub.RequireStub();

        // P1 的 4 台从站（IM/MTC/DRY/ROB）各一套点表：保持寄存器 0、输入寄存器 0、线圈 0
        var devices = new StringBuilder();
        var pointSets = new StringBuilder();
        var deviceIds = new List<string>();
        for (var unit = 1; unit <= 4; unit++)
        {
            var deviceId = "u" + unit;
            deviceIds.Add(deviceId);
            devices.Append(ComplexStubFixture.Device(deviceId, "t", unit, "ps" + unit));
            pointSets.Append("<PointSet id=\"ps").Append(unit).Append("\"><Defaults swap=\"abcd\" /><Points>")
                .Append(ComplexStubFixture.Point("holding", 0, "uint16", "area=\"holding\""))
                .Append(ComplexStubFixture.Point("input", 0, "uint16", "area=\"input\" access=\"read\""))
                .Append(ComplexStubFixture.Point("coil", 0, "bool", "area=\"coil\" access=\"read\""))
                .Append("</Points></PointSet>");
        }

        using var engine = Build(devices.ToString(), pointSets.ToString(), _stub.SimPortP1);
        try
        {
            foreach (var deviceId in deviceIds)
            {
                await ComplexStubFixture.WaitGoodAsync(engine, deviceId, "holding");
                await ComplexStubFixture.WaitGoodAsync(engine, deviceId, "input");
                await ComplexStubFixture.WaitGoodAsync(engine, deviceId, "coil");
            }

            // 3 轮：每轮把「调度缓存里的值」与「直接按该从站读回来的寄存器」逐台比对。
            // 串台（例如把 unit2 的应答解到 unit1 的点上）会立即表现为不一致。
            // 只比对**静态**寄存器：保持寄存器 0 与线圈 0（镜像侧无脚本改写）；
            // 输入寄存器 0 由各设备的工艺状态机每秒重写，做值比对有竞态，所以只要求 Good。
            for (var round = 0; round < 3; round++)
            {
                foreach (var deviceId in deviceIds)
                {
                    var rawHolding = await engine.RawReadAsync(deviceId, ModbusArea.HoldingRegister, 0, 1);
                    var rawCoil = await engine.RawReadAsync(deviceId, ModbusArea.Coil, 0, 1);
                    Assert.True(rawHolding.Success && rawCoil.Success, deviceId + " 的原始读必须成功");

                    Assert.Equal(rawHolding.Registers[0], Assert.IsType<ushort>(engine.GetValueDetail(deviceId, "holding").Value));
                    Assert.Equal(rawCoil.Registers[0] != 0, Assert.IsType<bool>(engine.GetValueDetail(deviceId, "coil").Value));
                }

                await Task.Delay(150);
            }
        }
        finally
        {
            engine.Dispose();
        }
    }

    // ─────────────────────── 2. 超地址组上限：真链路切分 ───────────────────────

    [SkippableFact]
    public async Task Auto_group_beyond_125_registers_splits_into_two_real_requests()
    {
        _stub.RequireStub();
        _stub.RequireSimLog();

        // unit 7（WO-ABCD 验证从站）有 224 个保持寄存器：声明 200 个严格相邻点（0..199）
        var points = new StringBuilder();
        for (var address = 0; address < 200; address++)
        {
            points.Append(ComplexStubFixture.Point("p" + address, address, "uint16", "area=\"holding\" intervalMs=\"3000\""));
        }

        var before = _stub.CountSimLogLines(_stub.SimPortP3, 7);

        using var engine = Build(
            ComplexStubFixture.Device("wo", "t", 7, "ps"),
            "<PointSet id=\"ps\"><Defaults swap=\"abcd\" /><Points>" + points + "</Points></PointSet>",
            _stub.SimPortP3);
        try
        {
            await ComplexStubFixture.WaitGoodAsync(engine, "wo", "p199", 10000);   // 切分后最后一个点位也必须刷新
            Assert.True(engine.GetValueDetail("wo", "p0").IsGood, "首个点位必须 Good");

            var frames = _stub.CountSimLogLines(_stub.SimPortP3, 7) - before;

            // 一轮节拍 = 125 + 75 两次请求（一次 200 字的请求会被从站按非法数量拒绝）
            Assert.True(frames is 2 or 4, $"200 个相邻点的一拍必须切成 2 次请求（实测 {frames} 条镜像日志）");
            Assert.Equal(0, frames % 2);
        }
        finally
        {
            engine.Dispose();
        }
    }

    // ─────────────────────── 3. onDemand：不进周期计划，整批触发请求数有界 ───────────────────────

    [SkippableFact]
    public async Task On_demand_points_send_nothing_until_the_batch_trigger()
    {
        _stub.RequireStub();
        _stub.RequireSimLog();

        // unit 6（ENV-01，P3 上的 4x:32）三个 onDemand 点：0/1 相邻（一组一次请求）+ 20 单独一组
        // → 触发时恰好 2 次请求
        var points = ComplexStubFixture.Point("od0", 0, "uint16", "area=\"holding\" mode=\"onDemand\"")
                     + ComplexStubFixture.Point("od1", 1, "uint16", "area=\"holding\" mode=\"onDemand\"")
                     + ComplexStubFixture.Point("od2", 20, "uint16", "area=\"holding\" mode=\"onDemand\"");

        var before = _stub.CountSimLogLines(_stub.SimPortP3, 6);

        using var engine = Build(
            ComplexStubFixture.Device("env", "t", 6, "ps"),
            "<PointSet id=\"ps\"><Defaults swap=\"abcd\" /><Points>" + points + "</Points></PointSet>",
            _stub.SimPortP3);
        try
        {
            await Task.Delay(600);   // 若干节拍：onDemand 点一个请求都不该有

            Assert.Equal(before, _stub.CountSimLogLines(_stub.SimPortP3, 6));

            var updated = await engine.TriggerOnDemandReadAsync("env");

            Assert.Equal(3, updated);
            Assert.True(engine.GetValueDetail("env", "od0").IsGood);
            Assert.True(engine.GetValueDetail("env", "od2").IsGood);
            Assert.Equal(before + 2, _stub.CountSimLogLines(_stub.SimPortP3, 6));   // 相邻两点合并 + 远点各一次

            await Task.Delay(600);
            Assert.Equal(before + 2, _stub.CountSimLogLines(_stub.SimPortP3, 6));   // 触发之后不再有周期请求
        }
        finally
        {
            engine.Dispose();
        }
    }
}
