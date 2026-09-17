using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Xml.Linq;
using SuperSampler.Abstractions.Values;
using SuperSampler.Core.Config;
using SuperSampler.Core.Runtime;
using SuperSampler.Drivers.Modbus;
using SuperSampler.Drivers.Modbus.Wire;
using SuperSampler.UnitTests.Support;
using Xunit;

namespace SuperSampler.UnitTests.Config;

/// <summary>
/// 「加载期必须报全矛盾」口径的回归用例（<c>_testplan/模块重测-编解码配置-报告.md</c> §2 与
/// <c>_testplan/findings.md</c> D46–D50）。
/// <para>
/// 前一轮重测时这五条**断言的是有缺口的现状**（D46 脚本正文不进成环判定、D47 请求超时 ≤ 0 放行、
/// D48 单点宽度超地址组上限放行、D49 空块 count=0 放行、D50 脚本坏依赖算成 Good）。
/// 缺口在本轮由配置/脚本模块修复后，用例已**改名并反转**为正向断言：
/// 每一条都要求加载期报错（或运行期给出正确质量），修回去就会立刻变红。
/// </para>
/// </summary>
[Collection("real-polling-threads")]
public sealed class ConfigGateFindingsTests : IDisposable
{
    private readonly FakeModbusLink _link = new();
    private SamplerEngine? _engine;

    public void Dispose() => _engine?.Dispose();

    private const string DevD1 = "<Device id=\"d1\" transport=\"tcp1\" pointSet=\"ps1\" unitId=\"1\" />";

    private static string Xml(string pointSets, string requestTimeoutMs = "500")
        => "<SamplerConfig schemaVersion=\"3.0\">"
           + "<Global>"
           + "<Polling defaultIntervalMs=\"2000\" requestTimeoutMs=\"" + requestTimeoutMs + "\" />"
           + "<Quality onCommError=\"bad\" onCommErrorValue=\"null\" />"
           + "</Global>"
           + "<Transports><Transport id=\"tcp1\" host=\"127.0.0.1\" /></Transports>"
           + "<Devices>" + DevD1 + "</Devices>"
           + "<PointSets>" + pointSets + "</PointSets>"
           + "</SamplerConfig>";

    private static SamplerConfiguration Load(string xml)
        => SamplerConfigLoader.Load(XDocument.Parse(xml), Directory.GetCurrentDirectory());

    private static ConfigValidationException LoadError(string xml)
        => Assert.Throws<ConfigValidationException>(() => Load(xml));

    private SamplerEngine Start(SamplerConfiguration config)
    {
        _engine = new SamplerEngine(config, new Dictionary<string, IModbusLink> { ["tcp1"] = _link });
        _engine.Start();
        return _engine;
    }

    // ────────────────────────────────────────────────────────────────────────
    // D47：Polling@requestTimeoutMs ≤ 0 —— 加载期必须报错（0 会让每个请求立即超时）
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Polling_request_timeout_below_one_is_rejected_at_load()
    {
        // 修复前：requestTimeoutMs="0"/"-1" 零错误通过，运行期 ModbusChannel.ReadOne 对 timeoutMs <= 0
        // 直接返回 false → 每个请求都判超时 → 全部点位离线 + 设备级退避，加载期一句提示都没有。
        const string points = "<PointSet id=\"ps1\"><Points><Point id=\"p\" address=\"0\" /></Points></PointSet>";

        var zero = LoadError(Xml(points, requestTimeoutMs: "0"));
        Assert.Contains(zero.Errors, e => e.Contains("requestTimeoutMs") && e.Contains("≥ 1"));

        var negative = LoadError(Xml(points, requestTimeoutMs: "-1"));
        Assert.Contains(negative.Errors, e => e.Contains("requestTimeoutMs") && e.Contains("≥ 1"));

        // 边界：1ms 是允许的最小值，照常加载
        var minimal = Load(Xml(points, requestTimeoutMs: "1"));
        Assert.True(minimal.IsValidated);
        Assert.Equal(1, minimal.Global.RequestTimeoutMs);
    }

    [Fact]
    public void Transport_request_timeout_below_one_is_rejected_at_load()
    {
        // 链路级超时同样直接进驱动（不与 Global 合并），0/负数同样是纯配置矛盾
        var ex = LoadError("<SamplerConfig schemaVersion=\"3.0\">"
            + "<Transports><Transport id=\"tcp1\" host=\"127.0.0.1\" requestTimeoutMs=\"0\" /></Transports>"
            + "<Devices>" + DevD1 + "</Devices>"
            + "<PointSets><PointSet id=\"ps1\"><Points><Point id=\"p\" address=\"0\" /></Points></PointSet></PointSets>"
            + "</SamplerConfig>");

        Assert.Contains(ex.Errors, e => e.Contains("Transport tcp1") && e.Contains("requestTimeoutMs"));
    }

    // ────────────────────────────────────────────────────────────────────────
    // D48：单点的一次请求宽度 > 地址组上限 —— 加载期必须报错
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Single_point_wider_than_the_group_limit_is_rejected_at_load()
    {
        // 修复前：string length=200（默认 groupLimitRegisters=125）零错误通过，运行期真的发出
        // count=200 的一次请求（超 Modbus 一次读 125 寄存器的上限）——真设备回异常码 03，该点永远坏值。
        // 自动分组只能切分「组」，单个点位不可切分，所以必须加载期拒绝。
        var over = LoadError(Xml("<PointSet id=\"ps1\"><Points>"
            + "<Point id=\"s\" address=\"0\" dataType=\"string\" length=\"200\" />"
            + "</Points></PointSet>"));
        Assert.Contains(over.Errors, e => e.Contains("点位 ps1/s") && e.Contains("地址组上限"));

        // 边界：刚好等于上限（125）通过
        var atLimit = Load(Xml("<PointSet id=\"ps1\"><Points>"
            + "<Point id=\"s\" address=\"0\" dataType=\"string\" length=\"125\" /></Points></PointSet>"));
        Assert.True(atLimit.IsValidated);

        // 位区：一个地址只有 1 位，多字点位在加载期由 CGV-35（findings D66）直接拒绝——
        // 位区不可能出现「单点宽度超 groupLimitBits」的可达配置，这一分支只剩纵深防御。
        var bitArea = LoadError(Xml("<PointSet id=\"ps1\"><Points>"
            + "<Point id=\"c\" area=\"coil\" address=\"0\" dataType=\"raw\" length=\"200\" /></Points></PointSet>"));
        Assert.Contains(bitArea.Errors, e => e.Contains("点位 ps1/c") && e.Contains("位区") && e.Contains("多字"));
    }

    // ────────────────────────────────────────────────────────────────────────
    // D49：Block@count < 1 —— 加载期必须报错（空块运行期静默无效）
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Block_count_zero_is_rejected_at_load()
    {
        // 修复前：count=0 且块内**无点位**时零错误通过，运行期该块一个请求都不发、也不报错（静默无效）。
        // 口径：块是「显式声明的一次请求」，count ≥ 1 无条件成立。
        var zero = LoadError(Xml("<PointSet id=\"ps1\">"
            + "<Blocks><Block id=\"b\" start=\"0\" count=\"0\" intervalMs=\"100\" /></Blocks>"
            + "<Points><Point id=\"p\" address=\"0\" intervalMs=\"100\" /></Points></PointSet>"));
        Assert.Contains(zero.Errors, e => e.Contains("Block ps1/b") && e.Contains("必须 ≥ 1"));

        // 负数同样报（此前靠「窗口不含点位」间接拒绝，现在直接报 count 非法）
        var negative = LoadError(Xml("<PointSet id=\"ps1\">"
            + "<Blocks><Block id=\"b\" start=\"0\" count=\"-1\"><Point id=\"p\" address=\"0\" /></Block></Blocks>"
            + "<Points /></PointSet>"));
        Assert.Contains(negative.Errors, e => e.Contains("Block ps1/b") && e.Contains("必须 ≥ 1"));

        // 超地址组上限仍是 CGV-11 的报错（这一条此前就对，保持不回归）
        Assert.Contains(LoadError(Xml("<PointSet id=\"ps1\">"
                + "<Blocks><Block id=\"b\" start=\"0\" count=\"200\"><Point id=\"p\" address=\"0\" /></Block></Blocks>"
                + "<Points /></PointSet>")).Errors, e => e.Contains("地址组上限"));
    }

    // ────────────────────────────────────────────────────────────────────────
    // D50：脚本型计算点的坏依赖必须传染（不再是伪正常数 1）
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Script_point_with_a_bad_dependency_is_bad_not_a_coerced_zero()
    {
        // 修复前：P('id') 对坏值返回 JS null，`null + 1` 在 Jint 里等于 1 → 输出 Good=1（伪正常数）。
        // 现在：P() 对坏值返回 undefined（算术自然得 NaN）并置 UsedBadDependency，
        // 脚本型与表达式型同口径 —— 依赖坏 → 本点 Bad(ss.reason.calculate)，门面返回 nullText。
        _link.DefaultReadData = new ushort[4096];

        var config = Load(Xml("<PointSet id=\"ps1\">"
            + "<Points><Point id=\"raw\" address=\"0\" mode=\"onDemand\" /></Points>"   // 从不采集 → 永远没有好值
            + "<Calculated>"
            + "<Point id=\"sc\" dataType=\"float64\"><Script>P('raw') + 1</Script></Point>"
            + "<Point id=\"ex\" dataType=\"float64\"><Expression>P('raw') + 1</Expression></Point>"
            + "<Point id=\"guarded\" dataType=\"float64\"><Script>if (P('raw') == null) { return null; } return 1;</Script></Point>"
            + "</Calculated></PointSet>"));

        var engine = Start(config);
        Thread.Sleep(200);

        var script = engine.GetValueDetail("d1", "sc");
        Assert.Equal(PointQuality.Bad, script.Quality);
        Assert.Equal("ss.reason.calculate", script.Reason);
        Assert.Equal("--", engine.GetValue("d1", "sc"));          // 门面按 nullText 显示，不再是 "1"

        // 表达式型仍是同一口径（对照面）
        var expression = engine.GetValueDetail("d1", "ex");
        Assert.Equal(PointQuality.Bad, expression.Quality);
        Assert.Equal("ss.reason.calculate", expression.Reason);

        // 显式判空后主动放弃（return null）仍是 Bad(ss.reason.scriptNull)：脚本作者的显式降级通路不变
        var guarded = engine.GetValueDetail("d1", "guarded");
        Assert.Equal(PointQuality.Bad, guarded.Quality);
        Assert.Equal("ss.reason.scriptNull", guarded.Reason);
    }

    // ────────────────────────────────────────────────────────────────────────
    // D46：脚本正文里的 P() 参与加载期成环判定（CGV-15）
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Script_bodies_participate_in_load_time_cycle_validation()
    {
        // 修复前：ReferencesOf 只扫 <Expression> 与 <DependsOn>，脚本型计算点互引用/自引用的环
        // 能零错误通过加载期，只在运行期由引擎防环兜底拦下。
        var mutual = LoadError(Xml("<PointSet id=\"ps1\"><Points />"
            + "<Calculated>"
            + "<Point id=\"x\"><Script>P('y') + 1</Script></Point>"
            + "<Point id=\"y\"><Script>P('x') + 1</Script></Point>"
            + "</Calculated></PointSet>"));
        Assert.Contains(mutual.Errors, e => e.Contains("循环依赖") && e.Contains("ps1/x") && e.Contains("ps1/y"));

        // 自引用同样报环
        Assert.Contains(LoadError(Xml("<PointSet id=\"ps1\"><Points />"
            + "<Calculated><Point id=\"self\"><Script>P('self') + 1</Script></Point></Calculated>"
            + "</PointSet>")).Errors, e => e.Contains("循环依赖"));

        // 对照：表达式成环（此前已正常拒绝，保持不回归）
        Assert.Contains(LoadError(Xml("<PointSet id=\"ps1\"><Points />"
                + "<Calculated>"
                + "<Point id=\"x\"><Expression>P('y') + 1</Expression></Point>"
                + "<Point id=\"y\"><Expression>P('x') + 1</Expression></Point>"
                + "</Calculated></PointSet>")).Errors, e => e.Contains("循环依赖"));

        // 不回归面：脚本引用「非计算点」（普通采集点）是合法用法，必须照常加载
        var ok = Load(Xml("<PointSet id=\"ps1\"><Points>"
            + "<Point id=\"p\" address=\"0\" /></Points>"
            + "<Calculated><Point id=\"c\"><Script>P('p') * 2</Script></Point></Calculated></PointSet>"));
        Assert.True(ok.IsValidated);
    }
}
