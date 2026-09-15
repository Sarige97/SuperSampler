using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace SuperSampler.IntegrationTests;

/// <summary>
/// C0 环境自检：把夹具探测到的桩/断路器状态显式暴露出来。
/// 不可用时跳过（与其它用例一致），并把可操作提示写进消息，便于排查（trx 与控制台都能看到）。
/// </summary>
[Collection(ComplexStubCollection.Name)]
public sealed class ComplexStubEnvironmentTests
{
    private readonly ComplexStubFixture _stub;

    public ComplexStubEnvironmentTests(ComplexStubFixture stub) => _stub = stub;

    [SkippableFact]
    public void C0_ComplexStubMirrorIsAvailable()
    {
        Skip.If(!_stub.StubAvailable,
            "复杂桩镜像不可用：" + _stub.StubUnavailableReason +
            "。请先执行：cd " + _stub.ToolsDir + " && python complex_sim.py");
    }

    [SkippableFact]
    public void C0_ProgrammableBreakerIsAvailable()
    {
        Skip.If(!_stub.BreakerAvailable,
            "可编程断路器不可用：" + _stub.BreakerUnavailableReason +
            "。请先确认公共口 " + ComplexStubFixture.PublicBasePort + "-" + (ComplexStubFixture.PublicBasePort + 3) +
            " 空闲，然后重跑；夹具会自动执行：" +
            "python breaker.py --map <由 complex_ports.json 派生、public_port 改到 2502-2505 的映射> --control <临时文件>");
    }

    [SkippableFact]
    public void C0_ToolChainFilesArePresent()
    {
        _stub.RequireStub();
        Assert.True(File.Exists(Path.Combine(_stub.ToolsDir, "complex_sim.py")), "缺少 complex_sim.py");
        Assert.True(File.Exists(Path.Combine(_stub.ToolsDir, "complex_ports.json")), "缺少 complex_ports.json");
        Assert.True(File.Exists(Path.Combine(_stub.ToolsDir, "complex_points.json")), "缺少 complex_points.json");
        Skip.If(!_stub.SimLogAvailable,
            "复用的是外部已启动的镜像（未带 --log）→ 无法用请求日志做「零通讯 / 每周期请求数」断言。" +
            "请停掉外部实例让夹具自起，或手动：python complex_sim.py --log <path>");
    }

    [SkippableFact]
    public async Task C0_RtuPortIsAvailableForBadCrc()
    {
        _stub.RequireRtu();
        await Task.CompletedTask;
    }
}
