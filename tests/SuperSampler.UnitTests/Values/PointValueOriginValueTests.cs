using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Linq;
using SuperSampler.Abstractions.Values;
using SuperSampler.Core.Config;
using SuperSampler.Core.Runtime;
using SuperSampler.Drivers.Modbus;
using SuperSampler.Drivers.Modbus.Wire;
using SuperSampler.UnitTests.Support;
using Xunit;

namespace SuperSampler.UnitTests.Values;

/// <summary>
/// `PointValue.OriginValue`（协议侧缩放前原始值）：
/// 逐类型断言 `PointCodec.Decode` 产出的 OriginValue 形态；脚本点给 rawValue、计算点 null；
/// 门面 `GetValueDetail().OriginValue` 可见；相等性纳入 OriginValue。
/// </summary>
public sealed class PointValueOriginValueTests
{
    private static readonly DateTimeOffset T = new(2026, 9, 16, 10, 0, 0, TimeSpan.FromHours(8));

    private static RuntimePoint Pt(Action<PointConfig>? configure = null)
    {
        var point = new PointConfig
        {
            Id = "p",
            Area = RuntimeArea.HoldingRegister,
            DataType = RuntimeDataType.UInt16,
            Swap = SwapMode.None,
            HasSwapDeclared = true,
            Address = 0,
        };
        configure?.Invoke(point);
        var device = new DeviceConfig { Id = "dev1", UnitId = 1, Transport = "tcp1", PointSetId = "ps1" };
        return new RuntimePoint(point, device);
    }

    private static PointValue Decode(RuntimePoint point, params ushort[] registers)
        => PointCodec.Decode(point, registers, T, out _);

    // ═══════════════ 逐类型 OriginValue ═══════════════

    [Fact]
    public void Uint16_origin_is_the_raw_ushort()
    {
        var v = Decode(Pt(p => { p.DataType = RuntimeDataType.UInt16; }), 0x1234);
        Assert.Equal((ushort)0x1234, v.OriginValue);
        Assert.Equal((ushort)0x1234, v.Value);   // 无 Scale：工程值 == 原始值
    }

    [Fact]
    public void Int16_origin_is_the_raw_short()
    {
        var v = Decode(Pt(p => { p.DataType = RuntimeDataType.Int16; }), 0xFFFF);
        Assert.Equal((short)-1, v.OriginValue);
    }

    [Fact]
    public void Uint32_origin_is_the_assembled_raw_value()
    {
        // 大端：0x0001 0x86A0 → 0x000186A0
        var v = Decode(Pt(p => { p.DataType = RuntimeDataType.UInt32; }), 0x0001, 0x86A0);
        Assert.Equal((uint)0x000186A0, v.OriginValue);
    }

    [Fact]
    public void Float32_origin_is_the_raw_float()
    {
        var v = Decode(Pt(p => { p.DataType = RuntimeDataType.Float32; }), 0x4049, 0x0FDB); // 3.14159f
        Assert.Equal(3.14159f, Assert.IsType<float>(v.OriginValue), 5);
    }

    [Fact]
    public void Scaled_point_origin_is_pre_scale_raw()
    {
        // 寄存器 2200 + Scale ×0.1 → 工程值 220.0，OriginValue 仍是 2200
        var v = Decode(Pt(p =>
        {
            p.DataType = RuntimeDataType.UInt16;
            p.Scale = new ScaleConfig { Factor = 0.1 };
        }), 2200);
        Assert.Equal((ushort)2200, v.OriginValue);
        Assert.Equal(220.0, Assert.IsType<double>(v.Value), 3);
    }

    [Fact]
    public void Bit_point_origin_is_the_raw_bit_as_bool()
    {
        var v = Decode(Pt(p => { p.DataType = RuntimeDataType.UInt16; p.Bit = 3; }), 0x0008);
        Assert.True(Assert.IsType<bool>(v.OriginValue));
        Assert.True(Assert.IsType<bool>(v.Value));
    }

    [Fact]
    public void Bit_range_origin_is_the_raw_field_as_ushort()
    {
        // 位段 4-7：0x00F0 的 field = 0x000F
        var v = Decode(Pt(p => { p.DataType = RuntimeDataType.UInt16; p.BitRange = "4-7"; }), 0x00F0);
        Assert.Equal((ushort)0x000F, v.OriginValue);
    }

    [Fact]
    public void String_origin_is_the_decoded_string()
    {
        // "Hi" ASCII 大端两字
        var v = Decode(Pt(p =>
        {
            p.DataType = RuntimeDataType.String;
            p.Length = 2;
            p.StringEncoding = "ascii";
        }), 0x4869, 0x0000);
        Assert.Equal("Hi", v.OriginValue);
        Assert.Equal("Hi", v.Value);
    }

    [Fact]
    public void Bcd_origin_is_the_raw_bcd_number()
    {
        var v = Decode(Pt(p => { p.DataType = RuntimeDataType.Bcd; }), 0x1234);
        Assert.Equal(1234L, v.OriginValue);
    }

    [Fact]
    public void Raw_point_origin_is_the_register_array()
    {
        var v = Decode(Pt(p => { p.DataType = RuntimeDataType.Raw; p.Length = 2; }), 0x1111, 0x2222);
        var origin = Assert.IsType<ushort[]>(v.OriginValue);
        Assert.Equal(new ushort[] { 0x1111, 0x2222 }, origin);
    }

    [Fact]
    public void Offline_and_bad_have_null_origin()
    {
        Assert.Null(PointValue.Offline(T).OriginValue);
        Assert.Null(PointValue.Bad("ss.reason.shortFrame", T).OriginValue);
    }

    // ═══════════════ 相等性纳入 OriginValue ═══════════════

    [Fact]
    public void Equals_false_when_only_origin_differs()
    {
        var a = PointValue.Good((ushort)2200, T, (ushort)2200);
        var b = PointValue.Good((ushort)2200, T, (ushort)2199);
        Assert.NotEqual(a, b);
        Assert.NotEqual(a.GetHashCode(), b.GetHashCode());
    }

    // ═══════════════ 引擎链路：脚本给 rawValue、计算点 null、门面可见 ═══════════════

    [Fact]
    public void Engine_get_value_detail_exposes_origin_for_polled_script_and_calculated()
    {
        var link = new FakeModbusLink();
        link.DefaultReadData = new ushort[32];
        // 3x/30 料筒温度 raw=2200（脚本点），3x/1 计算点依赖
        link.SetReadData(1, DataArea.InputRegister, 30, 2200);
        link.SetReadData(1, DataArea.InputRegister, 1, 100);

        const string xml =
            "<HostConfig schemaVersion=\"3.0\">"
            + "<Global language=\"zh_CN\" fallbackLanguage=\"en_US\" swap=\"none\">"
            + "<Polling defaultIntervalMs=\"200\" requestTimeoutMs=\"500\" />"
            + "<Quality onCommError=\"bad\" onCommErrorValue=\"null\" />"
            + "</Global>"
            + "<Transports><Transport id=\"tcp1\" variant=\"tcp\" host=\"127.0.0.1\" port=\"1502\" /></Transports>"
            + "<Devices><Device id=\"d1\" transport=\"tcp1\" unitId=\"1\" pointSet=\"ps\" /></Devices>"
            + "<PointSets><PointSet id=\"ps\">"
            + "<Points>"
            + "<Point id=\"base\" area=\"input\" address=\"1\" dataType=\"uint16\" />"
            + "<Point id=\"scaled\" area=\"input\" address=\"30\" dataType=\"uint16\"><Scale factor=\"0.1\" /></Point>"
            + "<Point id=\"scripted\" area=\"input\" address=\"30\" dataType=\"uint16\"><Script>rawValue * 2</Script></Point>"
            + "</Points>"
            + "<Calculated><Point id=\"calc\" dataType=\"float64\"><Expression>P('base') * 3</Expression></Point></Calculated>"
            + "</PointSet></PointSets>"
            + "</HostConfig>";

        var config = SamplerConfigLoader.Load(XDocument.Parse(xml), Directory.GetCurrentDirectory());
        using var engine = new SamplerEngine(config, new Dictionary<string, IModbusLink> { ["tcp1"] = link });
        engine.Start();

        WaitUntil(() =>
        {
            var s = engine.GetValueDetail("d1", "scaled");
            var sc = engine.GetValueDetail("d1", "scripted");
            var c = engine.GetValueDetail("d1", "calc");
            return s.IsGood && s.Value != null && sc.IsGood && sc.Value != null
                   && c.IsGood && c.Value != null;
        }, 5000);

        // 普通采集点（带 Scale）：Value=工程值、OriginValue=缩放前 raw
        var scaled = engine.GetValueDetail("d1", "scaled");
        Assert.Equal(220.0, Assert.IsType<double>(scaled.Value), 3);
        Assert.Equal((ushort)2200, scaled.OriginValue);

        // 脚本点：Value=脚本输出（rawValue*2=4400），OriginValue=rawValue（2200）
        var scripted = engine.GetValueDetail("d1", "scripted");
        Assert.Equal(4400.0, Assert.IsType<double>(scripted.Value), 3);
        Assert.Equal((ushort)2200, scripted.OriginValue);

        // 计算点：无协议侧原始值 → null
        var calc = engine.GetValueDetail("d1", "calc");
        Assert.Equal(300.0, Assert.IsType<double>(calc.Value), 3);
        Assert.Null(calc.OriginValue);

        engine.Stop();
    }

    private static void WaitUntil(Func<bool> condition, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            System.Threading.Thread.Sleep(20);
        }

        Assert.True(condition(), "等待条件超时");
    }
}
