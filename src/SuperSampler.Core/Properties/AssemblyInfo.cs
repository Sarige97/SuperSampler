using System.Runtime.CompilerServices;

// 单元测试程序集可访问 Core 的 internal 成员。用途仅限两处（PROD-8 的时钟缝与退避状态机的确定性单测）：
//   · RuntimeClock.IRuntimeClock / SystemRuntimeClock / RuntimeClockMath（注入假时钟，模拟系统时间跳变，
//     绝不改真实系统时钟——长稳实验与同机其它进程都在跑）；
//   · SamplerConfiguration.Clock（把假时钟挂到配置上，per-instance，不用全局可变状态）。
// 不构成对宿主的公开契约：宿主引用 Core 时看不到这些成员。
[assembly: InternalsVisibleTo("SuperSampler.UnitTests")]
