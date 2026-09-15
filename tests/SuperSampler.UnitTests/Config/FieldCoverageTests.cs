using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Linq;
using SuperSampler.Core.Config;
using Xunit;

namespace SuperSampler.UnitTests.Config;

/// <summary>
/// 全字段覆盖测试（解析 / 缺省 / 继承）。数据驱动，每行一个字段。
/// 权威清单：Config/配置字段说明.md 第 1–15 节。矩阵见 _testplan/field-coverage.md。
/// </summary>
public class FieldCoverageTests
{
    // ─────────────── 构造器 ───────────────

    private const string DefaultScanGroups =
        "<ScanGroups>"
        + "<ScanGroup id=\"normal\" />"
        + "<ScanGroup id=\"fast\" rateMs=\"500\" />"
        + "<ScanGroup id=\"ondemand\" mode=\"onDemand\" />"
        + "</ScanGroups>";

    private static SamplerConfiguration Cfg(string points, string defaults = "", string blocks = "", string top = "", string calc = "", string? scan = null)
    {
        var xml =
            "<HostConfig schemaVersion=\"3.0\">"
            + (scan ?? DefaultScanGroups)
            + "<Transports><Transport id=\"tcp1\" host=\"127.0.0.1\" /></Transports>"
            + "<Devices><Device id=\"d1\" transport=\"tcp1\" pointSet=\"ps1\" unitId=\"1\" /></Devices>"
            + top
            + "<PointSets><PointSet id=\"ps1\">" + defaults + blocks
            + "<Points>" + points + "</Points>" + calc + "</PointSet></PointSets>"
            + "</HostConfig>";
        return SamplerConfigLoader.Load(XDocument.Parse(xml), Directory.GetCurrentDirectory());
    }

    private static PointConfig P0(SamplerConfiguration c) => c.PointSets[0].Points[0];
    private static BlockConfig B0(SamplerConfiguration c) => c.PointSets[0].Blocks[0];
    private static PointConfig BP0(SamplerConfiguration c) => c.PointSets[0].Blocks[0].Points[0];
    private static DeviceConfig D0(SamplerConfiguration c) => c.Devices[0];
    private static TransportConfig T0(SamplerConfiguration c) => c.Transports[0];
    private static ScanGroupConfig S(SamplerConfiguration c, int i) => c.ScanGroups[i];
    private static GlobalOptions G(SamplerConfiguration c) => c.Global;

    /// <summary>点位快捷构造：默认 id=p、address=0。</summary>
    private static string Pt(string attrs, string inner = "")
        => inner.Length == 0
            ? "<Point id=\"p\" address=\"0\" " + attrs + " />"
            : "<Point id=\"p\" address=\"0\" " + attrs + ">" + inner + "</Point>";

    private static FieldCase C(string name, SamplerConfiguration cfg, Action<SamplerConfiguration> check)
        => new() { Name = name, Config = cfg, Check = check };

    private static IEnumerable<object[]> Cases(Func<IEnumerable<FieldCase>> factory)
    {
        foreach (var c in factory()) yield return new object[] { c };
    }

    public sealed class FieldCase
    {
        public string Name = string.Empty;
        public SamplerConfiguration Config = null!;
        public Action<SamplerConfiguration> Check = _ => { };
        public override string ToString() => Name;
    }

    // ─────────────── ① 解析：写了字段 → 模型值正确 ───────────────

    [Theory]
    [MemberData(nameof(Parsed))]
    public void Parses_field(FieldCase c) => c.Check(c.Config);

    public static IEnumerable<object[]> Parsed => Cases(ParsedCases);

    private static IEnumerable<FieldCase> ParsedCases()
    {
        // Global（nullText / swap 已接线，见 ADR D38；正向断言见 FieldCoverageGapsTests.GlobalNullTextAndSwapAreWired）
        yield return C("Global/Polling@rateMs", Cfg(Pt(""), top: "<Global><Polling rateMs=\"250\" /></Global>"), c => Assert.Equal(250, G(c).DefaultRateMs));
        yield return C("Global/Polling@requestTimeoutMs", Cfg(Pt(""), top: "<Global><Polling requestTimeoutMs=\"750\" /></Global>"), c => Assert.Equal(750, G(c).RequestTimeoutMs));
        yield return C("Global/Quality@onCommErrorValue", Cfg(Pt(""), top: "<Global><Quality onCommErrorValue=\"null\" /></Global>"), c => Assert.Equal("null", G(c).OnCommErrorValue));
        yield return C("Diagnostics@allowRawAccess", Cfg(Pt(""), top: "<Global /><Diagnostics allowRawAccess=\"true\" />"), c => Assert.True(G(c).AllowRawAccess));

        // Global 新增接线（findings W5/W7/W8/W9）
        yield return C("Global@language", Cfg(Pt(""), top: "<Global language=\"en-US\" />"), c => Assert.Equal("en-US", G(c).Language));
        yield return C("Global@fallbackLanguage", Cfg(Pt(""), top: "<Global fallbackLanguage=\"en-US\" />"), c => Assert.Equal("en-US", G(c).FallbackLanguage));
        yield return C("Global@timeZone", Cfg(Pt(""), top: "<Global timeZone=\"China Standard Time\" />"), c => Assert.Equal("China Standard Time", G(c).TimeZone));
        yield return C("Global/Retry@count", Cfg(Pt(""), top: "<Global><Retry count=\"4\" /></Global>"), c => Assert.Equal(4, G(c).RetryCount));
        yield return C("Global/Retry@intervalMs", Cfg(Pt(""), top: "<Global><Retry intervalMs=\"300\" /></Global>"), c => Assert.Equal(300, G(c).RetryIntervalMs));
        yield return C("Global/Retry@backoff", Cfg(Pt(""), top: "<Global><Retry backoff=\"fixed\" /></Global>"), c => Assert.Equal("fixed", G(c).RetryBackoff));
        yield return C("Global/Retry@escalateAfter", Cfg(Pt(""), top: "<Global><Retry escalateAfter=\"7\" /></Global>"), c => Assert.Equal(7, G(c).EscalateAfter));
        yield return C("Global/Retry@budgetMs", Cfg(Pt(""), top: "<Global><Retry budgetMs=\"1200\" /></Global>"), c => Assert.Equal(1200, G(c).BudgetMs));
        yield return C("Global/Scheduler@maxRegistersPerRead", Cfg(Pt(""), top: "<Global><Scheduler maxRegistersPerRead=\"60\" /></Global>"), c => Assert.Equal(60, G(c).MaxRegistersPerRead));
        yield return C("Global/Scheduler@maxBitsPerRead", Cfg(Pt(""), top: "<Global><Scheduler maxBitsPerRead=\"900\" /></Global>"), c => Assert.Equal(900, G(c).MaxBitsPerRead));
        yield return C("Global/Scheduler@mergeGap", Cfg(Pt(""), top: "<Global><Scheduler mergeGap=\"3\" /></Global>"), c => Assert.Equal(3, G(c).MergeGap));
        yield return C("Global/Script@timeoutMs", Cfg(Pt(""), top: "<Global><Script timeoutMs=\"120\" /></Global>"), c => Assert.Equal(120, G(c).ScriptTimeoutMs));
        yield return C("Global/Script@onError", Cfg(Pt(""), top: "<Global><Script onError=\"keepLast\" /></Global>"), c => Assert.Equal("keepLast", G(c).ScriptOnError));
        yield return C("Global/Quality@onCommError", Cfg(Pt(""), top: "<Global><Quality onCommError=\"uncertain\" /></Global>"), c => Assert.Equal("uncertain", G(c).OnCommError));
        yield return C("Global/Quality@staleAfterMs", Cfg(Pt(""), top: "<Global><Quality staleAfterMs=\"9000\" /></Global>"), c => Assert.Equal(9000, G(c).StaleAfterMs));

        // Meta（findings W2：此前整段未读）
        yield return C("Meta/ProjectName", Cfg(Pt(""), top: "<Meta><ProjectName>泵站</ProjectName></Meta>"), c => Assert.Equal("泵站", c.Meta.ProjectName));
        yield return C("Meta/Comment", Cfg(Pt(""), top: "<Meta><Comment>演示</Comment></Meta>"), c => Assert.Equal("演示", c.Meta.Comment));
        yield return C("Meta/Author", Cfg(Pt(""), top: "<Meta><Author>alice</Author></Meta>"), c => Assert.Equal("alice", c.Meta.Author));
        yield return C("Meta/CreatedAt", Cfg(Pt(""), top: "<Meta><CreatedAt>2026-01-02</CreatedAt></Meta>"), c => Assert.Equal("2026-01-02", c.Meta.CreatedAt));
        yield return C("Meta/Revision", Cfg(Pt(""), top: "<Meta><Revision>7</Revision></Meta>"), c => Assert.Equal(7, c.Meta.Revision));
        yield return C("Meta/Tags/Tag", Cfg(Pt(""), top: "<Meta><Tags><Tag>a</Tag><Tag>b</Tag></Tags></Meta>"),
            c => { Assert.Equal(2, c.Meta.Tags.Count); Assert.Equal("b", c.Meta.Tags[1]); });

        // Transport 串口新增接线（findings W13）
        yield return C("Transport@handshake", Cfg(Pt(""), top: "<Transports><Transport id=\"t2\" portName=\"COM3\" handshake=\"rtscts\" /></Transports>"), c => Assert.Equal("rtscts", c.Transports[1].Handshake));
        yield return C("Transport@dtr", Cfg(Pt(""), top: "<Transports><Transport id=\"t2\" portName=\"COM3\" dtr=\"false\" /></Transports>"), c => Assert.False(c.Transports[1].DtrEnable));
        yield return C("Transport@rts", Cfg(Pt(""), top: "<Transports><Transport id=\"t2\" portName=\"COM3\" rts=\"false\" /></Transports>"), c => Assert.False(c.Transports[1].RtsEnable));
        yield return C("Transport@readTimeoutMs", Cfg(Pt(""), top: "<Transports><Transport id=\"t2\" portName=\"COM3\" readTimeoutMs=\"800\" /></Transports>"), c => Assert.Equal(800, c.Transports[1].ReadTimeoutMs));
        yield return C("Transport@writeTimeoutMs", Cfg(Pt(""), top: "<Transports><Transport id=\"t2\" portName=\"COM3\" writeTimeoutMs=\"900\" /></Transports>"), c => Assert.Equal(900, c.Transports[1].WriteTimeoutMs));
        yield return C("Transport/Retry@count", Cfg(Pt(""), top: "<Transports><Transport id=\"t2\"><Retry count=\"6\" /></Transport></Transports>"), c => Assert.Equal(6, c.Transports[1].RetryCount));
        yield return C("Transport/Retry@intervalMs", Cfg(Pt(""), top: "<Transports><Transport id=\"t2\"><Retry intervalMs=\"400\" /></Transport></Transports>"), c => Assert.Equal(400, c.Transports[1].RetryIntervalMs));

        // Block@enabled / Point@enabled、desc、range、readonlyBy、Write@confirm/@pulseMs（findings W16/W17/W22）
        yield return C("Block@enabled", Cfg("", blocks: "<Blocks><Block id=\"b1\" start=\"0\" count=\"4\" enabled=\"false\" /></Blocks>"), c => Assert.False(B0(c).Enabled));
        yield return C("Point@enabled", Cfg(Pt("enabled=\"false\"")), c => Assert.False(P0(c).Enabled));
        yield return C("Point@desc", Cfg(Pt("desc=\"进水压力\"")), c => Assert.Equal("进水压力", P0(c).Desc));
        yield return C("Point@readonlyBy", Cfg(Pt("readonlyBy=\"op\"")), c => Assert.Equal("op", P0(c).ReadonlyBy));
        yield return C("Point@range", Cfg(Pt("range=\"0..300\"")), c => { Assert.Equal(0d, P0(c).RangeLow); Assert.Equal(300d, P0(c).RangeHigh); });
        yield return C("Point@range (缺省无高低限)", Cfg(Pt("")), c => { Assert.Null(P0(c).RangeLow); Assert.Null(P0(c).RangeHigh); });
        yield return C("Write@confirm", Cfg(Pt("access=\"write\"", "<Write confirm=\"true\" />")), c => Assert.True(P0(c).Write!.Confirm));
        yield return C("Write@pulseMs", Cfg(Pt("access=\"write\"", "<Write pulseMs=\"500\" />")), c => Assert.Equal(500, P0(c).Write!.PulseMs));

        // ScanGroups
        yield return C("ScanGroup@id", Cfg(Pt("")), c => Assert.Equal("normal", S(c, 0).Id));
        yield return C("ScanGroup@mode", Cfg(Pt("")), c => Assert.Equal("ondemand", S(c, 2).Mode, ignoreCase: true));
        yield return C("ScanGroup@rateMs", Cfg(Pt("")), c => Assert.Equal(500, S(c, 1).RateMs));
        yield return C("ScanGroup@jitterMs", Cfg(Pt(""), scan: "<ScanGroups><ScanGroup id=\"normal\" jitterMs=\"30\" /></ScanGroups>"), c => Assert.Equal(30, S(c, 0).JitterMs));

        // Transports
        yield return C("Transport@id", Cfg(Pt("")), c => Assert.Equal("tcp1", T0(c).Id));
        yield return C("Transport@variant", Cfg(Pt(""), top: "<Transports><Transport id=\"t2\" variant=\"rtuovertcp\" /></Transports>"), c => Assert.Equal("rtuovertcp", c.Transports[1].Variant));
        yield return C("Transport@enabled", Cfg(Pt(""), top: "<Transports><Transport id=\"t2\" enabled=\"false\" /></Transports>"), c => Assert.False(c.Transports[1].Enabled));
        yield return C("Transport@host", Cfg(Pt("")), c => Assert.Equal("127.0.0.1", T0(c).Host));
        yield return C("Transport@port", Cfg(Pt(""), top: "<Transports><Transport id=\"t2\" port=\"1502\" /></Transports>"), c => Assert.Equal(1502, c.Transports[1].Port));
        yield return C("Transport@connectTimeoutMs", Cfg(Pt(""), top: "<Transports><Transport id=\"t2\" connectTimeoutMs=\"4000\" /></Transports>"), c => Assert.Equal(4000, c.Transports[1].ConnectTimeoutMs));
        yield return C("Transport@requestTimeoutMs", Cfg(Pt(""), top: "<Transports><Transport id=\"t2\" requestTimeoutMs=\"1234\" /></Transports>"), c => Assert.Equal(1234, c.Transports[1].RequestTimeoutMs));
        yield return C("Transport@gapMs", Cfg(Pt(""), top: "<Transports><Transport id=\"t2\" gapMs=\"20\" /></Transports>"), c => Assert.Equal(20, c.Transports[1].GapMs));
        yield return C("Transport@portName", Cfg(Pt(""), top: "<Transports><Transport id=\"t2\" portName=\"COM3\" /></Transports>"), c => Assert.Equal("COM3", c.Transports[1].PortName));
        yield return C("Transport@baudRate", Cfg(Pt(""), top: "<Transports><Transport id=\"t2\" baudRate=\"19200\" /></Transports>"), c => Assert.Equal(19200, c.Transports[1].BaudRate));
        yield return C("Transport@dataBits", Cfg(Pt(""), top: "<Transports><Transport id=\"t2\" dataBits=\"7\" /></Transports>"), c => Assert.Equal(7, c.Transports[1].DataBits));
        yield return C("Transport@parity", Cfg(Pt(""), top: "<Transports><Transport id=\"t2\" parity=\"even\" /></Transports>"), c => Assert.Equal("even", c.Transports[1].Parity));
        yield return C("Transport@stopBits", Cfg(Pt(""), top: "<Transports><Transport id=\"t2\" stopBits=\"two\" /></Transports>"), c => Assert.Equal("two", c.Transports[1].StopBits));

        // Devices
        yield return C("Device@name", Cfg(Pt(""), top: "<Devices><Device id=\"d2\" unitId=\"2\" name=\"炉温\" transport=\"tcp1\" pointSet=\"ps1\" /></Devices>"), c => Assert.Equal("炉温", c.Devices[1].Name));
        yield return C("Device@enabled", Cfg(Pt(""), top: "<Devices><Device id=\"d2\" enabled=\"false\" transport=\"tcp1\" pointSet=\"ps1\" unitId=\"2\" /></Devices>"), c => Assert.False(c.Devices[1].Enabled));
        yield return C("Device@transport", Cfg(Pt("")), c => Assert.Equal("tcp1", D0(c).Transport));
        yield return C("Device@unitId", Cfg(Pt(""), top: "<Devices><Device id=\"d2\" transport=\"tcp1\" pointSet=\"ps1\" unitId=\"9\" /></Devices>"), c => Assert.Equal(9, c.Devices[1].UnitId));
        yield return C("Device@pointSet", Cfg(Pt("")), c => Assert.Equal("ps1", D0(c).PointSetId));
        yield return C("Device@scanGroup", Cfg(Pt(""), top: "<Devices><Device id=\"d2\" unitId=\"2\" transport=\"tcp1\" pointSet=\"ps1\" scanGroup=\"fast\" /></Devices>"), c => Assert.Equal("fast", c.Devices[1].ScanGroup));
        yield return C("Device@swap", Cfg(Pt(""), top: "<Devices><Device id=\"d2\" unitId=\"2\" transport=\"tcp1\" pointSet=\"ps1\" swap=\"none\" /></Devices>"), c => Assert.Equal(SwapMode.None, c.Devices[1].Swap));
        yield return C("Device@requestTimeoutMs", Cfg(Pt(""), top: "<Devices><Device id=\"d2\" unitId=\"2\" transport=\"tcp1\" pointSet=\"ps1\" requestTimeoutMs=\"888\" /></Devices>"), c => Assert.Equal(888, c.Devices[1].RequestTimeoutMs));
        yield return C("Device/Retry@count", Cfg(Pt(""), top: "<Devices><Device id=\"d2\" unitId=\"2\" transport=\"tcp1\" pointSet=\"ps1\"><Retry count=\"5\" /></Device></Devices>"), c => Assert.Equal(5, c.Devices[1].RetryCount));
        yield return C("Device/Retry@intervalMs", Cfg(Pt(""), top: "<Devices><Device id=\"d2\" unitId=\"2\" transport=\"tcp1\" pointSet=\"ps1\"><Retry intervalMs=\"250\" /></Device></Devices>"), c => Assert.Equal(250, c.Devices[1].RetryIntervalMs));
        yield return C("Device/Pause@maintenance", Cfg(Pt(""), top: "<Devices><Device id=\"d2\" unitId=\"2\" transport=\"tcp1\" pointSet=\"ps1\"><Pause maintenance=\"true\" /></Device></Devices>"), c => Assert.True(c.Devices[1].Paused));

        // PointSet Defaults
        yield return C("Defaults@area", Cfg(Pt(""), defaults: "<Defaults area=\"coil\" />"), c => Assert.Equal(RuntimeArea.Coil, P0(c).Area));
        yield return C("Defaults@dataType", Cfg(Pt(""), defaults: "<Defaults dataType=\"float32\" />"), c => Assert.Equal(RuntimeDataType.Float32, P0(c).DataType));
        yield return C("Defaults@swap", Cfg(Pt(""), defaults: "<Defaults swap=\"byte\" />"), c => Assert.Equal(SwapMode.Byte, P0(c).Swap));
        yield return C("Defaults@scanGroup", Cfg(Pt(""), defaults: "<Defaults scanGroup=\"fast\" />"), c => Assert.Equal("fast", P0(c).ScanGroup));
        yield return C("Defaults@unitId", Cfg(Pt(""), defaults: "<Defaults unitId=\"7\" />"), c => Assert.Equal(7, P0(c).UnitIdOverride));
        yield return C("Defaults@access", Cfg(Pt(""), defaults: "<Defaults access=\"readwrite\" />"), c => Assert.True(P0(c).IsWritable));

        // Block
        yield return C("Block@id", Cfg("", blocks: "<Blocks><Block id=\"b1\" start=\"0\" count=\"4\" /></Blocks>"), c => Assert.Equal("b1", B0(c).Id));
        yield return C("Block@area", Cfg("", blocks: "<Blocks><Block id=\"b1\" area=\"coil\" start=\"0\" count=\"4\" /></Blocks>"), c => Assert.Equal(RuntimeArea.Coil, B0(c).Area));
        yield return C("Block@start", Cfg("", blocks: "<Blocks><Block id=\"b1\" start=\"5\" count=\"4\" /></Blocks>"), c => Assert.Equal(5, B0(c).Start));
        yield return C("Block@count", Cfg("", blocks: "<Blocks><Block id=\"b1\" start=\"0\" count=\"4\" /></Blocks>"), c => Assert.Equal(4, B0(c).Count));
        yield return C("Block@scanGroup", Cfg("", blocks: "<Blocks><Block id=\"b1\" start=\"0\" count=\"4\" scanGroup=\"fast\" /></Blocks>"), c => Assert.Equal("fast", B0(c).ScanGroup));
        yield return C("Block@unitId", Cfg("", blocks: "<Blocks><Block id=\"b1\" start=\"0\" count=\"4\" unitId=\"6\" /></Blocks>"), c => Assert.Equal(6, B0(c).UnitId));
        yield return C("Block@swap", Cfg("", blocks: "<Blocks><Block id=\"b1\" start=\"0\" count=\"4\" swap=\"byte\" /></Blocks>"), c => Assert.Equal(SwapMode.Byte, B0(c).Swap));

        // Point attributes
        yield return C("Point@id", Cfg(Pt("")), c => Assert.Equal("p", P0(c).Id));
        yield return C("Point@name", Cfg(Pt("name=\"温度\"")), c => Assert.Equal("温度", P0(c).Name));
        yield return C("Point@area", Cfg(Pt("area=\"input\"")), c => Assert.Equal(RuntimeArea.InputRegister, P0(c).Area));
        yield return C("Point@address", Cfg("<Point id=\"p\" address=\"17\" />"), c => Assert.Equal(17, P0(c).Address));
        yield return C("Point@addrFormat+plc", Cfg("<Point id=\"p\" address=\"30001\" addrFormat=\"plc\" area=\"input\" />"), c => Assert.Equal(0, P0(c).Address));
        yield return C("Point@length", Cfg(Pt("dataType=\"string\" length=\"8\"")), c => Assert.Equal(8, P0(c).Length));
        yield return C("Point@dataType", Cfg(Pt("dataType=\"int32\"")), c => Assert.Equal(RuntimeDataType.Int32, P0(c).DataType));
        yield return C("Point@swap", Cfg(Pt("swap=\"word_byte\"")), c => Assert.Equal(SwapMode.WordByte, P0(c).Swap));
        yield return C("Point@bit", Cfg(Pt("bit=\"3\"")), c => Assert.Equal(3, P0(c).Bit));
        yield return C("Point@bitRange", Cfg(Pt("bitRange=\"4-7\"")), c => Assert.Equal("4-7", P0(c).BitRange));
        yield return C("Point@unit", Cfg(Pt("unit=\"C\"")), c => Assert.Equal("C", P0(c).Unit));
        yield return C("Point@scanGroup", Cfg(Pt("scanGroup=\"fast\"")), c => Assert.Equal("fast", P0(c).ScanGroup));
        yield return C("Point@unitId", Cfg(Pt("unitId=\"12\"")), c => Assert.Equal(12, P0(c).UnitIdOverride));
        yield return C("Point@access=write", Cfg(Pt("access=\"write\"")), c => Assert.True(P0(c).IsWritable));

        // Point/Scale
        yield return C("Scale@factor", Cfg(Pt("", "<Scale factor=\"0.1\" />")), c => Assert.Equal(0.1, P0(c).Scale!.Factor));
        yield return C("Scale@offset", Cfg(Pt("", "<Scale offset=\"5\" />")), c => Assert.Equal(5.0, P0(c).Scale!.Offset));
        yield return C("Scale@rawLow/rawHigh/scaledLow/scaledHigh", Cfg(Pt("", "<Scale rawLow=\"0\" rawHigh=\"100\" scaledLow=\"0\" scaledHigh=\"10\" />")),
            c => { Assert.Equal(0.0, P0(c).Scale!.RawLow); Assert.Equal(100.0, P0(c).Scale!.RawHigh); Assert.Equal(10.0, P0(c).Scale!.ScaledHigh); });
        yield return C("Scale/Clamp@mode+low+high", Cfg(Pt("", "<Scale><Clamp mode=\"both\" low=\"1\" high=\"9\" /></Scale>")),
            c => { Assert.Equal(ClampMode.Both, P0(c).Scale!.Clamp); Assert.Equal(1.0, P0(c).Scale!.ClampLow); Assert.Equal(9.0, P0(c).Scale!.ClampHigh); });

        // Point/Format
        yield return C("Format@decimals", Cfg(Pt("", "<Format decimals=\"2\" />")), c => Assert.Equal(2, P0(c).Format!.Decimals));
        yield return C("Format@prefix", Cfg(Pt("", "<Format prefix=\"T=\" />")), c => Assert.Equal("T=", P0(c).Format!.Prefix));
        yield return C("Format@suffix", Cfg(Pt("", "<Format suffix=\"C\" />")), c => Assert.Equal("C", P0(c).Format!.Suffix));
        yield return C("Format@thousands", Cfg(Pt("", "<Format thousands=\"true\" />")), c => Assert.True(P0(c).Format!.Thousands));
        yield return C("Format@mapOn", Cfg(Pt("", "<Format mapOn=\"raw\" />")), c => Assert.Equal("raw", P0(c).Format!.MapOn));
        yield return C("Format@pattern", Cfg(Pt("", "<Format pattern=\"yyyy-MM-dd\" />")), c => Assert.Equal("yyyy-MM-dd", P0(c).Format!.Pattern));
        yield return C("Format/Map/Item@key", Cfg(Pt("", "<Format><Map><Item key=\"0\">关</Item><Item key=\"1\">开</Item></Map></Format>")),
            c => { Assert.Equal("关", P0(c).Format!.Map["0"]); Assert.Equal("开", P0(c).Format!.Map["1"]); });

        // Point/String,Bcd,DateTime
        yield return C("String@encoding", Cfg(Pt("dataType=\"string\"", "<String encoding=\"utf8\" />")), c => Assert.Equal("utf8", P0(c).StringEncoding));
        yield return C("String@trimNull", Cfg(Pt("dataType=\"string\"", "<String trimNull=\"false\" />")), c => Assert.False(P0(c).StringTrimNull));
        yield return C("Bcd@digits", Cfg(Pt("dataType=\"bcd\"", "<Bcd digits=\"6\" />")), c => Assert.Equal(6, P0(c).BcdDigits));
        yield return C("DateTime@format", Cfg(Pt("dataType=\"datetime\"", "<DateTime format=\"unixSec\" />")), c => Assert.Equal("unixsec", P0(c).DateTimeFormat));

        // Point/Alarm
        yield return C("Alarm@id", Cfg(Pt("", "<Alarm id=\"a1\" type=\"high\" limit=\"10\" />")), c => Assert.Equal("a1", P0(c).Alarms[0].Id));
        yield return C("Alarm@type", Cfg(Pt("", "<Alarm type=\"lowlow\" limit=\"1\" />")), c => Assert.Equal("lowlow", P0(c).Alarms[0].Type));
        yield return C("Alarm@limit", Cfg(Pt("", "<Alarm type=\"high\" limit=\"42\" />")), c => Assert.Equal(42.0, P0(c).Alarms[0].Limit));
        yield return C("Alarm@delayMs", Cfg(Pt("", "<Alarm type=\"high\" limit=\"1\" delayMs=\"500\" />")), c => Assert.Equal(500, P0(c).Alarms[0].DelayMs));
        yield return C("Alarm@deadband", Cfg(Pt("", "<Alarm type=\"high\" limit=\"1\" deadband=\"2\" />")), c => Assert.Equal(2.0, P0(c).Alarms[0].Deadband));
        yield return C("Alarm@message", Cfg(Pt("", "<Alarm type=\"high\" limit=\"1\" message=\"超温\" />")), c => Assert.Equal("超温", P0(c).Alarms[0].Message));
        yield return C("Alarm@latch", Cfg(Pt("", "<Alarm type=\"high\" limit=\"1\" latch=\"true\" />")), c => Assert.True(P0(c).Alarms[0].Latch));
        yield return C("Alarm@ackRequired", Cfg(Pt("", "<Alarm type=\"high\" limit=\"1\" ackRequired=\"true\" />")), c => Assert.True(P0(c).Alarms[0].AckRequired));

        // Point/Write
        yield return C("Write@min", Cfg(Pt("access=\"write\"", "<Write min=\"1\" />")), c => Assert.Equal(1.0, P0(c).Write!.Min));
        yield return C("Write@max", Cfg(Pt("access=\"write\"", "<Write max=\"9\" />")), c => Assert.Equal(9.0, P0(c).Write!.Max));
        yield return C("Write@step", Cfg(Pt("access=\"write\"", "<Write step=\"0.5\" />")), c => Assert.Equal(0.5, P0(c).Write!.Step));
        yield return C("Write@permission", Cfg(Pt("access=\"write\"", "<Write permission=\"op\" />")), c => Assert.Equal("op", P0(c).Write!.Permission));
        yield return C("Write@verify", Cfg(Pt("access=\"write\"", "<Write verify=\"true\" />")), c => Assert.True(P0(c).Write!.Verify));

        // Slices
        yield return C("Slices/Slice@address+length", Cfg(Pt("", "<Slices><Slice address=\"10\" length=\"2\" /><Slice address=\"20\" length=\"1\" /></Slices>")),
            c => { Assert.Equal(10, P0(c).Slices![0].Address); Assert.Equal(2, P0(c).Slices![0].Length); Assert.Equal(20, P0(c).Slices![1].Address); });

        // Calculated
        yield return C("Calculated/Expression", Cfg("", calc: "<Calculated><Point id=\"c1\"><Expression>P('p') + 1</Expression></Point></Calculated>"),
            c => { Assert.Single(c.PointSets[0].Calculated); Assert.True(c.PointSets[0].Calculated[0].IsCalculated); Assert.Equal("P('p') + 1", c.PointSets[0].Calculated[0].Expression); });
    }

    // ─────────────── ② 缺省：字段未写 → 字典默认值 ───────────────

    [Theory]
    [MemberData(nameof(Defaults))]
    public void Applies_default(FieldCase c) => c.Check(c.Config);

    public static IEnumerable<object[]> Defaults => Cases(DefaultCases);

    private static IEnumerable<FieldCase> DefaultCases()
    {
        // Global（不写 Global 段）
        yield return C("Global@nullText default", Cfg(Pt("")), c => Assert.Equal("--", G(c).NullText));
        yield return C("Global@swap default word", Cfg(Pt("")), c => Assert.Equal(SwapMode.Word, G(c).DefaultSwap));
        yield return C("Global/Polling@rateMs default", Cfg(Pt("")), c => Assert.Equal(1000, G(c).DefaultRateMs));
        yield return C("Global/Polling@requestTimeoutMs default", Cfg(Pt("")), c => Assert.Equal(1000, G(c).RequestTimeoutMs));
        yield return C("Global/Quality@onCommErrorValue default", Cfg(Pt("")), c => Assert.Equal("keepLast", G(c).OnCommErrorValue));
        yield return C("Global/Quality@onCommError default", Cfg(Pt("")), c => Assert.Equal("bad", G(c).OnCommError));
        yield return C("Global/Quality@staleAfterMs default", Cfg(Pt("")), c => Assert.Equal(5000, G(c).StaleAfterMs));
        yield return C("Global/Retry@count default", Cfg(Pt("")), c => Assert.Equal(2, G(c).RetryCount));
        yield return C("Global/Retry@intervalMs default", Cfg(Pt("")), c => Assert.Equal(100, G(c).RetryIntervalMs));
        yield return C("Global/Retry@backoff default", Cfg(Pt("")), c => Assert.Equal("exponential", G(c).RetryBackoff));
        yield return C("Global/Retry@escalateAfter default", Cfg(Pt("")), c => Assert.Equal(3, G(c).EscalateAfter));
        yield return C("Global/Retry@budgetMs default", Cfg(Pt("")), c => Assert.Equal(3000, G(c).BudgetMs));
        yield return C("Global/Scheduler@maxRegistersPerRead default", Cfg(Pt("")), c => Assert.Equal(125, G(c).MaxRegistersPerRead));
        yield return C("Global/Scheduler@maxBitsPerRead default", Cfg(Pt("")), c => Assert.Equal(2000, G(c).MaxBitsPerRead));
        yield return C("Global/Scheduler@mergeGap default", Cfg(Pt("")), c => Assert.Equal(0, G(c).MergeGap));
        yield return C("Global/Script@timeoutMs default", Cfg(Pt("")), c => Assert.Equal(50, G(c).ScriptTimeoutMs));
        yield return C("Global/Script@onError default", Cfg(Pt("")), c => Assert.Equal("markBad", G(c).ScriptOnError));
        yield return C("HostConfig@unsupportedPolicy default warn", Cfg(Pt("")), c => Assert.Equal("warn", c.UnsupportedPolicy));
        yield return C("Meta default empty", Cfg(Pt("")), c => { Assert.Null(c.Meta.ProjectName); Assert.Equal(0, c.Meta.Revision); Assert.Empty(c.Meta.Tags); });
        yield return C("Diagnostics@allowRawAccess default false", Cfg(Pt("")), c => Assert.False(G(c).AllowRawAccess));

        // ScanGroup
        yield return C("ScanGroup@mode default poll", Cfg(Pt("")), c => Assert.Equal("poll", S(c, 0).Mode));
        yield return C("ScanGroup@rateMs default 1000", Cfg(Pt("")), c => Assert.Equal(1000, S(c, 0).RateMs));
        yield return C("ScanGroup@jitterMs default 0", Cfg(Pt("")), c => Assert.Equal(0, S(c, 0).JitterMs));

        // Transport
        yield return C("Transport@variant default tcp", Cfg(Pt("")), c => Assert.Equal("tcp", T0(c).Variant));
        yield return C("Transport@enabled default true", Cfg(Pt("")), c => Assert.True(T0(c).Enabled));
        yield return C("Transport@port default 502", Cfg(Pt("")), c => Assert.Equal(502, T0(c).Port));
        yield return C("Transport@connectTimeoutMs default 3000", Cfg(Pt("")), c => Assert.Equal(3000, T0(c).ConnectTimeoutMs));
        yield return C("Transport@requestTimeoutMs default 1000", Cfg(Pt("")), c => Assert.Equal(1000, T0(c).RequestTimeoutMs));
        yield return C("Transport@gapMs default 0", Cfg(Pt("")), c => Assert.Equal(0, T0(c).GapMs));
        yield return C("Transport@baudRate default 9600", Cfg(Pt("")), c => Assert.Equal(9600, T0(c).BaudRate));
        yield return C("Transport@dataBits default 8", Cfg(Pt("")), c => Assert.Equal(8, T0(c).DataBits));
        yield return C("Transport@parity default none", Cfg(Pt("")), c => Assert.Equal("none", T0(c).Parity));
        yield return C("Transport@stopBits default one", Cfg(Pt("")), c => Assert.Equal("one", T0(c).StopBits));
        yield return C("Transport@handshake default none", Cfg(Pt("")), c => Assert.Equal("none", T0(c).Handshake));
        yield return C("Transport@dtr default false", Cfg(Pt("")), c => Assert.False(T0(c).DtrEnable));
        yield return C("Transport@rts default false", Cfg(Pt("")), c => Assert.False(T0(c).RtsEnable));
        yield return C("Transport@readTimeoutMs default 500", Cfg(Pt("")), c => Assert.Equal(500, T0(c).ReadTimeoutMs));
        yield return C("Transport@writeTimeoutMs default 500", Cfg(Pt("")), c => Assert.Equal(500, T0(c).WriteTimeoutMs));
        yield return C("Transport/Retry 缺省无覆盖", Cfg(Pt("")), c => Assert.False(T0(c).HasRetryOverride));

        // Device
        yield return C("Device@enabled default true", Cfg(Pt("")), c => Assert.True(D0(c).Enabled));
        yield return C("Device@unitId default 1", Cfg(Pt("")), c => Assert.Equal(1, D0(c).UnitId));
        yield return C("Device@scanGroup default normal", Cfg(Pt("")), c => Assert.Equal("normal", D0(c).ScanGroup));
        yield return C("Device/Retry@count default 2", Cfg(Pt("")), c => Assert.Equal(2, D0(c).RetryCount));
        yield return C("Device/Retry@intervalMs default 100", Cfg(Pt("")), c => Assert.Equal(100, D0(c).RetryIntervalMs));
        yield return C("Device/Pause default false", Cfg(Pt("")), c => Assert.False(D0(c).Paused));

        // Point
        yield return C("Point@area default holding", Cfg(Pt("")), c => Assert.Equal(RuntimeArea.HoldingRegister, P0(c).Area));
        yield return C("Point@dataType default uint16", Cfg(Pt("")), c => Assert.Equal(RuntimeDataType.UInt16, P0(c).DataType));
        yield return C("Point@swap default word", Cfg(Pt("")), c => Assert.Equal(SwapMode.Word, P0(c).Swap));
        yield return C("Point@scanGroup default normal", Cfg(Pt("")), c => Assert.Equal("normal", P0(c).ScanGroup));
        yield return C("Point@access default read", Cfg(Pt("")), c => Assert.False(P0(c).IsWritable));
        yield return C("Point@length default 0", Cfg(Pt("")), c => Assert.Equal(0, P0(c).Length));
        yield return C("Point@bit default null", Cfg(Pt("")), c => Assert.Null(P0(c).Bit));
        yield return C("Point@enabled default true", Cfg(Pt("")), c => Assert.True(P0(c).Enabled));
        yield return C("Point@desc default null", Cfg(Pt("")), c => Assert.Null(P0(c).Desc));
        yield return C("Point@readonlyBy default null", Cfg(Pt("")), c => Assert.Null(P0(c).ReadonlyBy));
        yield return C("Block@enabled default true", Cfg("", blocks: "<Blocks><Block id=\"b1\" start=\"0\" count=\"4\" /></Blocks>"), c => Assert.True(B0(c).Enabled));
        yield return C("String@encoding default ascii", Cfg(Pt("")), c => Assert.Equal("ascii", P0(c).StringEncoding));
        yield return C("String@trimNull default true", Cfg(Pt("")), c => Assert.True(P0(c).StringTrimNull));
        yield return C("Bcd@digits default 4", Cfg(Pt("")), c => Assert.Equal(4, P0(c).BcdDigits));
        yield return C("DateTime@format default plc6", Cfg(Pt("")), c => Assert.Equal("plc6", P0(c).DateTimeFormat));

        // Scale / Format
        yield return C("Scale@factor default 1", Cfg(Pt("", "<Scale />")), c => Assert.Equal(1.0, P0(c).Scale!.Factor));
        yield return C("Scale@offset default 0", Cfg(Pt("", "<Scale />")), c => Assert.Equal(0.0, P0(c).Scale!.Offset));
        yield return C("Scale/Clamp@mode default none", Cfg(Pt("", "<Scale />")), c => Assert.Equal(ClampMode.None, P0(c).Scale!.Clamp));
        yield return C("Format@decimals default 0", Cfg(Pt("", "<Format />")), c => Assert.Equal(0, P0(c).Format!.Decimals));
        yield return C("Format@thousands default false", Cfg(Pt("", "<Format />")), c => Assert.False(P0(c).Format!.Thousands));
        yield return C("Format@mapOn default engineering", Cfg(Pt("", "<Format />")), c => Assert.Equal("engineering", P0(c).Format!.MapOn));

        // Alarm
        yield return C("Alarm@type default high", Cfg(Pt("", "<Alarm limit=\"1\" />")), c => Assert.Equal("high", P0(c).Alarms[0].Type));
        yield return C("Alarm@delayMs default 0", Cfg(Pt("", "<Alarm limit=\"1\" />")), c => Assert.Equal(0, P0(c).Alarms[0].DelayMs));
        yield return C("Alarm@deadband default 0", Cfg(Pt("", "<Alarm limit=\"1\" />")), c => Assert.Equal(0.0, P0(c).Alarms[0].Deadband));
        yield return C("Alarm@latch default false", Cfg(Pt("", "<Alarm limit=\"1\" />")), c => Assert.False(P0(c).Alarms[0].Latch));
        yield return C("Alarm@ackRequired default false", Cfg(Pt("", "<Alarm limit=\"1\" />")), c => Assert.False(P0(c).Alarms[0].AckRequired));
    }

    // ─────────────── ③ 继承：就近覆盖，不叠加 ───────────────

    [Theory]
    [MemberData(nameof(Inheritance))]
    public void Inherits_field(FieldCase c) => c.Check(c.Config);

    public static IEnumerable<object[]> Inheritance => Cases(InheritanceCases);

    private static IEnumerable<FieldCase> InheritanceCases()
    {
        // Defaults → Point（Point 未写时取 Defaults）
        yield return C("Defaults.area → Point", Cfg(Pt(""), defaults: "<Defaults area=\"input\" />"), c => Assert.Equal(RuntimeArea.InputRegister, P0(c).Area));
        yield return C("Defaults.dataType → Point", Cfg(Pt(""), defaults: "<Defaults dataType=\"int32\" />"), c => Assert.Equal(RuntimeDataType.Int32, P0(c).DataType));
        yield return C("Defaults.scanGroup → Point", Cfg(Pt(""), defaults: "<Defaults scanGroup=\"fast\" />"), c => Assert.Equal("fast", P0(c).ScanGroup));
        yield return C("Defaults.unitId → Point.UnitIdOverride", Cfg(Pt(""), defaults: "<Defaults unitId=\"7\" />"), c => Assert.Equal(7, P0(c).UnitIdOverride));
        yield return C("Defaults.access → Point", Cfg(Pt(""), defaults: "<Defaults access=\"readwrite\" />"), c => Assert.True(P0(c).IsWritable));

        // Point 覆盖 Defaults（就近优先）
        yield return C("Point.area overrides Defaults", Cfg(Pt("area=\"coil\""), defaults: "<Defaults area=\"input\" />"), c => Assert.Equal(RuntimeArea.Coil, P0(c).Area));
        yield return C("Point.scanGroup overrides Defaults", Cfg(Pt("scanGroup=\"fast\""), defaults: "<Defaults scanGroup=\"normal\" />"), c => Assert.Equal("fast", P0(c).ScanGroup));

        // Point.swap 覆盖 Defaults（链的点位侧；设备级/全局级兜底在 RuntimePoint 解析，见 ADR D38）
        yield return C("Point.swap overrides Defaults.swap", Cfg(Pt("swap=\"word_byte\""), defaults: "<Defaults swap=\"byte\" />"), c => Assert.Equal(SwapMode.WordByte, P0(c).Swap));

        // Block → Block 内点位（块内点位未写时继承块）
        yield return C("Block.area → block point", Cfg("", blocks: "<Blocks><Block id=\"b1\" start=\"0\" count=\"4\" area=\"coil\"><Point id=\"bp\" address=\"1\" /></Block></Blocks>"),
            c => Assert.Equal(RuntimeArea.Coil, BP0(c).Area));
        yield return C("Block.swap → block point", Cfg("", blocks: "<Blocks><Block id=\"b1\" start=\"0\" count=\"4\" swap=\"byte\"><Point id=\"bp\" address=\"1\" /></Block></Blocks>"),
            c => Assert.Equal(SwapMode.Byte, BP0(c).Swap));
        yield return C("Block.scanGroup → block point", Cfg("", blocks: "<Blocks><Block id=\"b1\" start=\"0\" count=\"4\" scanGroup=\"fast\"><Point id=\"bp\" address=\"1\" /></Block></Blocks>"),
            c => Assert.Equal("fast", BP0(c).ScanGroup));
        yield return C("Block.unitId → block point", Cfg("", blocks: "<Blocks><Block id=\"b1\" start=\"0\" count=\"4\" unitId=\"6\"><Point id=\"bp\" address=\"1\" /></Block></Blocks>"),
            c => Assert.Equal(6, BP0(c).UnitIdOverride));
        yield return C("Block point.swap overrides Block", Cfg("", blocks: "<Blocks><Block id=\"b1\" start=\"0\" count=\"4\" swap=\"byte\"><Point id=\"bp\" address=\"1\" swap=\"word_byte\" /></Block></Blocks>"),
            c => Assert.Equal(SwapMode.WordByte, BP0(c).Swap));

        // 设备模板：模板属性垫入实例（未被实例覆盖时生效）
        yield return C("DeviceTemplate.unitId → Device", Cfg(Pt(""),
            top: "<DeviceTemplates><Device id=\"dt\" unitId=\"11\" scanGroup=\"fast\" /></DeviceTemplates>"
                + "<Devices><Device id=\"d2\" template=\"dt\" transport=\"tcp1\" pointSet=\"ps1\" /></Devices>"),
            c => { Assert.Equal(11, c.Devices[1].UnitId); Assert.Equal("fast", c.Devices[1].ScanGroup); });
        yield return C("Device.unitId overrides template", Cfg(Pt(""),
            top: "<DeviceTemplates><Device id=\"dt\" unitId=\"11\" /></DeviceTemplates>"
                + "<Devices><Device id=\"d2\" template=\"dt\" unitId=\"3\" transport=\"tcp1\" pointSet=\"ps1\" /></Devices>"),
            c => Assert.Equal(3, c.Devices[1].UnitId));

        // 点位模板：模板属性垫入实例
        yield return C("PointTemplate.dataType → Point",
            Cfg("<Point id=\"p\" address=\"0\" template=\"pt\" />",
                top: "<PointTemplates><Point id=\"pt\" dataType=\"int32\" /></PointTemplates>"),
            c => Assert.Equal(RuntimeDataType.Int32, P0(c).DataType));
    }
}
