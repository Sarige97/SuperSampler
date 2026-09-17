using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Xml.Linq;
using SuperSampler.Abstractions.Events;
using SuperSampler.Abstractions.Facade;
using SuperSampler.Core.Config;
using SuperSampler.Core.Runtime;
using SuperSampler.Drivers.Modbus;
using SuperSampler.Drivers.Modbus.Wire;
using SuperSampler.UnitTests.Support;
using Xunit;

namespace SuperSampler.UnitTests.Runtime;

/// <summary>
/// 第五~七步的**运行期**行为（注入假链路，走真实调度线程）：
/// <list type="bullet">
/// <item>位映射展开的子点位与整字点位同址同组、一次请求读回；子点位可读、可格式化（Field 的 Map 生效）；</item>
/// <item>子点位可写（继承整字点位），写入走位读改写：只翻目标位/位域，其余位原样保留；</item>
/// <item>子点位**不产生报警**（明确断言：报警只可能来自整字点位）；</item>
/// <item>Slices 点位在**轮询路径**取回（旧限制已删除）：片段连中间空洞合成一次请求读回后拼值，
/// 与同节拍的相邻点位合并读取；</item>
/// <item>字符串 padding/left 四种组合的解码与"只裁配置的补齐字节"。</item>
/// </list>
/// </summary>
public class BitMapSliceRuntimeTests
{
    /// <summary>测试夹具：一台设备（d1/unit1）+ 一个点表 + 假链路 + 报警事件收集。</summary>
    private sealed class Rig : IDisposable
    {
        private readonly List<AlarmRaisedEvent> _alarms = new();

        public Rig(string pointsXml, Action<FakeModbusLink>? setup = null)
        {
            Config = SamplerConfigLoader.Load(XDocument.Parse(Xml(pointsXml)), Directory.GetCurrentDirectory());
            Engine = new SamplerEngine(Config, new Dictionary<string, IModbusLink> { ["tcp1"] = Link });
            Engine.Bus.Subscribe<AlarmRaisedEvent>(e => { lock (_alarms) _alarms.Add(e.Body); }, DeliveryMode.Inline);
            setup?.Invoke(Link);
        }

        public FakeModbusLink Link { get; } = new();

        public SamplerEngine Engine { get; }

        public SamplerConfiguration Config { get; }

        public IReadOnlyList<AlarmRaisedEvent> Alarms
        {
            get { lock (_alarms) return _alarms.ToArray(); }
        }

        public IReadOnlyList<FakeLinkCall> Reads => Link.Calls.Where(c => c.IsRead).ToArray();

        public void Start() => Engine.Start();

        public void Dispose() => Engine.Dispose();

        private static string Xml(string pointsXml)
            => "<SamplerConfig schemaVersion=\"3.0\">"
               + "<Global><Polling defaultIntervalMs=\"100\" requestTimeoutMs=\"400\" />"
               + "<Quality onCommError=\"bad\" onCommErrorValue=\"null\" /></Global>"
               + "<Transports><Transport id=\"tcp1\" host=\"127.0.0.1\" /></Transports>"
               + "<Devices><Device id=\"d1\" transport=\"tcp1\" unitId=\"1\" pointSet=\"ps1\" /></Devices>"
               + "<PointSets><PointSet id=\"ps1\"><Defaults swap=\"none\" /><Points>" + pointsXml
               + "</Points></PointSet></PointSets></SamplerConfig>";
    }

    private static void WaitGood(IDeviceManager manager, string pointId, int timeoutMs = 5000)
        => Assert.True(SpinWait.SpinUntil(() => manager.GetValueDetail("d1", pointId).IsGood, timeoutMs),
            $"点位 {pointId} 未在 {timeoutMs}ms 内变 Good（实际 {manager.GetValueDetail("d1", pointId)}）");

    private const string StateWordWithBits =
        "<Point id=\"s\" address=\"3\" dataType=\"uint16\"><Bits>"
        + "<Bit index=\"0\" name=\"running\" text=\"运行\"/>"
        + "<Field from=\"4\" to=\"7\" name=\"alarmCode\" text=\"报警码\">"
        + "<Map><Item key=\"0\">无报警</Item><Item key=\"1\">油温过高</Item></Map>"
        + "</Field></Bits></Point>";

    // ═══════════════ A. 位映射展开：取值与合并读取 ═══════════════

    [Fact]
    public void Expanded_bit_children_are_polled_decoded_and_formatted()
    {
        using var rig = new Rig(StateWordWithBits, link =>
            link.SetReadData(1, DataArea.HoldingRegister, 3, 0x0011));   // bit0=1（运行）、bit4-7=1（油温过高）
        rig.Start();

        IDeviceManager manager = rig.Engine;
        WaitGood(manager, "s");
        WaitGood(manager, "s.running");
        WaitGood(manager, "s.alarmCode");

        Assert.Equal((ushort)0x0011, Assert.IsType<ushort>(manager.GetValueDetail("d1", "s").Value));
        Assert.True(Assert.IsType<bool>(manager.GetValueDetail("d1", "s.running").Value));
        Assert.Equal((ushort)1, Assert.IsType<ushort>(manager.GetValueDetail("d1", "s.alarmCode").Value));

        // Field 的 Map 生效（子点位同样走 Format 管道 → 报表/调试输出与普通点位一致）
        Assert.Equal("油温过高", manager.GetValue("d1", "s.alarmCode"));

        // 整字点位与两个子点位同址 → 同一个地址组、一次请求读回（不是每个点位各发一次请求）
        Assert.NotEmpty(rig.Reads);
        Assert.All(rig.Reads, call =>
        {
            Assert.Equal(3, call.Address);
            Assert.Equal(1, call.Count);
        });
    }

    [Fact]
    public void Expanded_bit_children_expire_into_the_same_group_as_the_integer_point()
    {
        // 同一寄存器上再放一个散点（地址 4），验证子点位不会把分组打散
        using var rig = new Rig(StateWordWithBits + "<Point id=\"n\" address=\"4\" dataType=\"uint16\" />",
            link => link.SetReadData(1, DataArea.HoldingRegister, 3, 0x0011, 0x0007));
        rig.Start();

        IDeviceManager manager = rig.Engine;
        WaitGood(manager, "s.running");
        WaitGood(manager, "n");

        // 地址 3 与 4 连续 → 一次请求读 2 个字覆盖全部四个点位
        Assert.Contains(rig.Reads, call => call.Address == 3 && call.Count == 2);
        Assert.Equal((ushort)7, Assert.IsType<ushort>(manager.GetValueDetail("d1", "n").Value));
    }

    [Fact]
    public void Expanded_bit_children_publish_value_change_events()
    {
        // 子点位与普通点位一样可订阅：值变化必须发 PointValueChangedEvent（报表/订阅方靠它驱动）
        using var rig = new Rig(StateWordWithBits, link =>
            link.SetReadData(1, DataArea.HoldingRegister, 3, 0x0011));

        var events = new List<PointValueChangedEvent>();
        rig.Engine.Bus.Subscribe<PointValueChangedEvent>(e => { lock (events) events.Add(e.Body); }, DeliveryMode.Inline);

        rig.Start();
        IDeviceManager manager = rig.Engine;
        WaitGood(manager, "s.running");
        WaitGood(manager, "s.alarmCode");

        Assert.True(SpinWait.SpinUntil(
            () => { lock (events) return events.Any(e => e.PointId == "s.running" && e.Value.Value is true); }, 5000),
            "子点位 s.running 的值变化必须发事件");
        Assert.True(SpinWait.SpinUntil(
            () => { lock (events) return events.Any(e => e.PointId == "s.alarmCode" && e.Value.Value is ushort and 1); }, 5000),
            "子点位 s.alarmCode 的值变化必须发事件");
    }

    // ═══════════════ B. 子点位可写：位读改写只翻目标位 ═══════════════

    [Fact]
    public async System.Threading.Tasks.Task Writable_child_flips_only_its_own_bit()
    {
        // 整字点位可写 → 子点位可写；写入必须读-改-写，其余位原样保留
        using var rig = new Rig(
            StateWordWithBits.Replace("dataType=\"uint16\"", "dataType=\"uint16\" access=\"readwrite\""),
            link =>
            {
                link.SetReadData(1, DataArea.HoldingRegister, 3, 0x0F05);   // bit0=1，bit2=1（bit4-7=0，bit8-11=1）
                link.WriteSingleReplies.Enqueue(ModbusReply.Ok(Array.Empty<ushort>(), 1));
                link.WriteSingleReplies.Enqueue(ModbusReply.Ok(Array.Empty<ushort>(), 1));
            });

        var off = await rig.Engine.SetValueAsync("d1", "s.running", false);
        Assert.Equal(WriteOutcome.Succeeded, off.Outcome);
        Assert.Equal(new ushort[] { 0x0F04 }, rig.Link.WriteSingleValues);   // 只清 bit0

        var field = await rig.Engine.SetValueAsync("d1", "s.alarmCode", 2);
        Assert.Equal(WriteOutcome.Succeeded, field.Outcome);
        Assert.Equal(0x0F25, Assert.Single(rig.Link.WriteSingleValues.Skip(1).ToArray()));  // bit4-7 置 2，其余位不变
    }

    [Fact]
    public async System.Threading.Tasks.Task Read_only_integer_point_keeps_children_read_only()
    {
        using var rig = new Rig(StateWordWithBits);   // access 默认 read

        var result = await rig.Engine.SetValueAsync("d1", "s.running", true);

        Assert.Equal(WriteOutcome.Rejected, result.Outcome);
        Assert.Empty(rig.Link.WriteSingleValues);
    }

    // ═══════════════ C. 子点位不产生报警 ═══════════════

    [Fact]
    public void Expanded_bit_children_never_raise_alarms()
    {
        // 整字点位挂一条高限报警（limit=0 → 任何非 0 值立刻报警）：报警只可能来自整字点位
        using var rig = new Rig(
            StateWordWithBits.Replace("dataType=\"uint16\"", "dataType=\"uint16\"")
                .Replace("</Bits></Point>", "</Bits><Alarm type=\"high\" limit=\"0\" /></Point>"),
            link => link.SetReadData(1, DataArea.HoldingRegister, 3, 0x0011));

        // 模型层：子点位没有任何报警定义
        Assert.Empty(rig.Config.PointSets[0].Points.Single(p => p.Id == "s.running").Alarms);
        Assert.Empty(rig.Config.PointSets[0].Points.Single(p => p.Id == "s.alarmCode").Alarms);

        rig.Start();

        Assert.True(SpinWait.SpinUntil(() => rig.Alarms.Count >= 1, 5000),
            "整字点位的报警必须照常触发（用作对照）");

        // 再跑一会儿：轮询多轮后仍然只有整字点位的报警，子点位一条都没有
        Thread.Sleep(300);
        Assert.All(rig.Alarms, alarm => Assert.Equal("s", alarm.PointId));
    }

    // ═══════════════ D. Slices 在轮询路径取回并拼值 ═══════════════

    [Fact]
    public void Slices_point_is_polled_in_one_request_and_sibling_points_join_the_window()
    {
        // 累计电能：低字 200、备用字 201、高字 202；再放一个相邻散点 203 → 应合成一次 4 字请求
        using var rig = new Rig(
            "<Point id=\"e\" address=\"200\" dataType=\"uint32\">"
            + "<Slices><Slice address=\"200\" length=\"1\"/><Slice address=\"202\" length=\"1\"/></Slices></Point>"
            + "<Point id=\"t\" address=\"203\" dataType=\"uint16\" />",
            link => link.SetReadData(1, DataArea.HoldingRegister, 200, 0x0001, 0x0002, 0x0003, 0x0004));

        rig.Start();

        IDeviceManager manager = rig.Engine;
        WaitGood(manager, "e");
        WaitGood(manager, "t");

        // 拼值：[200] 与 [202] → 0x00010003（uint32、swap=none）
        Assert.Equal(65539u, Assert.IsType<uint>(manager.GetValueDetail("d1", "e").Value));
        Assert.Equal((ushort)4, Assert.IsType<ushort>(manager.GetValueDetail("d1", "t").Value));

        // 一次请求读回（含中间空洞 201），没有为片段单独发请求
        Assert.NotEmpty(rig.Reads);
        Assert.All(rig.Reads, call =>
        {
            Assert.Equal(200, call.Address);
            Assert.Equal(4, call.Count);
        });
    }

    [Fact]
    public async System.Threading.Tasks.Task Slices_point_works_through_manual_trigger_read_too()
    {
        using var rig = new Rig(
            "<Point id=\"e\" address=\"200\" dataType=\"uint32\">"
            + "<Slices><Slice address=\"200\" length=\"1\"/><Slice address=\"202\" length=\"1\"/></Slices></Point>",
            link => link.SetReadData(1, DataArea.HoldingRegister, 200, 0x0009, 0, 0x0005));

        var value = await rig.Engine.TriggerReadAsync("d1", "e");

        Assert.Equal(0x00090005u, Assert.IsType<uint>(value.Value));
        Assert.Single(rig.Reads);                       // 手动触发同样只发一次请求
        Assert.Equal(200, rig.Reads[0].Address);
        Assert.Equal(3, rig.Reads[0].Count);
    }

    // ═══════════════ E. 字符串 padding + left ═══════════════

    private static RuntimePoint StringPoint(int padding, bool left, int length)
        => RuntimePointFactory.Point(c =>
        {
            c.DataType = RuntimeDataType.String;
            c.Length = length;
            c.StringPadding = padding;
            c.StringPadLeft = left;
        });

    private static string Decode(RuntimePoint point, params ushort[] registers)
    {
        var value = PointCodec.Decode(point, registers, DateTimeOffset.UtcNow);
        Assert.True(value.IsGood, value.ToString());
        return Assert.IsType<string>(value.Value);
    }

    [Fact]
    public void String_padding_space_left_aligned_trims_the_tail()
    {
        // "ABC "（空格补齐、靠左）→ 裁尾部补齐
        Assert.Equal("ABC", Decode(StringPoint(0x20, true, 2), 0x4142, 0x4320));
    }

    [Fact]
    public void String_padding_space_right_aligned_trims_the_head()
    {
        // "  AB"（空格补齐、靠右）→ 裁头部补齐
        Assert.Equal("AB", Decode(StringPoint(0x20, false, 2), 0x2020, 0x4142));
    }

    [Fact]
    public void String_padding_zero_left_aligned_keeps_real_spaces()
    {
        // 0x00 补齐的老口径：真实的尾部空格必须保留（旧实现会 TrimEnd(' ') 把尾巴吃掉）
        Assert.Equal("ABC ", Decode(StringPoint(0x00, true, 2), 0x4142, 0x4320));
    }

    [Fact]
    public void String_padding_zero_right_aligned_trims_leading_zeros()
    {
        Assert.Equal("AB", Decode(StringPoint(0x00, false, 2), 0x0000, 0x4142));
    }

    [Fact]
    public void String_only_the_configured_pad_byte_is_trimmed()
    {
        // padding=0x20 时尾部的 0x00 不是补齐字节，必须原样保留（只裁配置的字节）
        Assert.Equal("ABC\0", Decode(StringPoint(0x20, true, 2), 0x4142, 0x4300));
    }

    [Fact]
    public void String_encode_pads_with_the_configured_byte_and_honours_alignment()
    {
        var left = PointCodec.Encode(StringPoint(0x20, true, 3), "ABC");
        Assert.Equal(new ushort[] { 0x4142, 0x4320, 0x2020 }, left);

        var right = PointCodec.Encode(StringPoint(0x20, false, 3), "ABC");
        Assert.Equal(new ushort[] { 0x2020, 0x2041, 0x4243 }, right);
    }
}
