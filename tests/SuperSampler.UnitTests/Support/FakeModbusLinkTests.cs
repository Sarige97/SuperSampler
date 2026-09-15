using System;
using System.Linq;
using System.Threading.Tasks;
using SuperSampler.Drivers.Modbus;
using SuperSampler.Drivers.Modbus.Wire;
using SuperSampler.UnitTests.Support;
using Xunit;

namespace SuperSampler.UnitTests.Support;

/// <summary>FakeModbusLink v2 扩展的冒烟测试：故障规则、调用记录、延迟注入、确定性概率。</summary>
public class FakeModbusLinkTests
{
    [Fact]
    public void Fml1_fault_rule_injects_timeout_on_matching_read()
    {
        var link = new FakeModbusLink();
        link.FaultRules.Add(new FakeFaultRule { UnitId = 1, Area = DataArea.HoldingRegister, Kind = ModbusFailureKind.Timeout });

        var bad = link.Read(DataArea.HoldingRegister, 0, 2, unitId: 1, 500, 0, 0);
        var good = link.Read(DataArea.HoldingRegister, 0, 2, unitId: 2, 500, 0, 0);

        Assert.False(bad.Success);
        Assert.Equal(ModbusFailureKind.Timeout, bad.Kind);
        // unitId 不命中规则时走默认（无数据注册 → LinkDown，但不属于注入超时）
        Assert.False(good.Success);
        Assert.Equal(ModbusFailureKind.LinkDown, good.Kind);
    }

    [Fact]
    public void Fml2_protocol_rule_carries_exception_code()
    {
        var link = new FakeModbusLink();
        link.SetReadData(4, DataArea.HoldingRegister, 0, 0x1111, 0x2222);
        link.FaultRules.Add(new FakeFaultRule { UnitId = 4, Kind = ModbusFailureKind.Protocol, ExceptionCode = 0x02, RemainingCalls = 1 });

        var first = link.Read(DataArea.HoldingRegister, 0, 2, 4, 500, 0, 0);
        var second = link.Read(DataArea.HoldingRegister, 0, 2, 4, 500, 0, 0);

        Assert.Equal(ModbusFailureKind.Protocol, first.Kind);
        Assert.Equal((byte)0x02, first.ExceptionCode);
        // 规则 RemainingCalls=1 用尽后恢复注册数据
        Assert.True(second.Success);
        Assert.Equal((ushort)0x1111, second.Registers[0]);
    }

    [Fact]
    public void Fml3_call_recorder_counts_reads_and_writes_thread_safely()
    {
        var link = new FakeModbusLink();
        link.SetReadData(1, DataArea.HoldingRegister, 0, 1);
        link.DefaultReadData = new ushort[] { 7 };

        link.Read(DataArea.HoldingRegister, 0, 1, 1, 500, 0, 0);
        Parallel.For(0, 20, _ =>
        {
            link.Read(DataArea.HoldingRegister, 0, 1, 1, 500, 0, 0);
            link.WriteSingle(DataArea.Coil, 10, 1, 1, 500, 0, 0);
            link.WriteMulti(DataArea.HoldingRegister, 20, new ushort[] { 1, 2, 3 }, 1, 500, 0, 0);
        });

        Assert.Equal(21, link.Calls.Count(c => c.IsRead));
        Assert.Equal(20, link.WriteSingleCallCount);
        Assert.Equal(20, link.WriteMultiCallCount);
        Assert.Contains(link.Calls, c => c.IsWrite && c.IsMulti && c.Address == 20 && c.Count == 3);
        link.ClearCalls();
        Assert.Empty(link.Calls);
    }

    [Fact]
    public void Fml4_delay_rule_sleeps_before_reply()
    {
        var link = new FakeModbusLink();
        link.SetReadData(1, DataArea.HoldingRegister, 0, 0x00FF);
        link.FaultRules.Add(new FakeFaultRule { UnitId = 1, DelayMs = 120, Kind = ModbusFailureKind.Timeout, RemainingCalls = 1 });

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var reply = link.Read(DataArea.HoldingRegister, 0, 1, 1, 500, 0, 0);
        sw.Stop();

        Assert.False(reply.Success);
        Assert.Equal(ModbusFailureKind.Timeout, reply.Kind);
        Assert.True(sw.ElapsedMilliseconds >= 100, $"expected delay >=100ms, got {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public void Fml5_probability_rule_is_deterministic_without_random()
    {
        var link = new FakeModbusLink();
        link.SetReadData(1, DataArea.HoldingRegister, 0, 1);
        // 每第 3 次命中一次超时（确定性口径）
        link.FaultRules.Add(new FakeFaultRule
        {
            UnitId = 1,
            Kind = ModbusFailureKind.Timeout,
            Probability = 1.0 / 3.0,
            RandomModulo = 3,
        });

        var timeouts = 0;
        for (var i = 0; i < 9; i++)
        {
            var reply = link.Read(DataArea.HoldingRegister, 0, 1, 1, 500, 0, 0);
            if (!reply.Success && reply.Kind == ModbusFailureKind.Timeout) timeouts++;
        }

        Assert.Equal(3, timeouts);
    }

    [Fact]
    public void Fml6_open_close_simulation_counts_transitions()
    {
        var link = new FakeModbusLink();
        Assert.True(link.IsOpen);

        link.SetOpen(false);
        link.SetOpen(false); // 重复无效
        link.SetOpen(true);

        Assert.False(link.IsOpen == false);
        Assert.True(link.IsOpen);
        Assert.Equal(1, link.CloseCount);
        Assert.Equal(1, link.OpenCount); // 构造时的初始 Open 不计为转换
    }
}
