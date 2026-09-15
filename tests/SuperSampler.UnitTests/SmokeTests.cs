using Xunit;

namespace SuperSampler.UnitTests
{
    /// <summary>占位冒烟测试：验证「编译 + 测试运行器 + net462 引用 net46」整条流水线。</summary>
    public class SmokeTests
    {
        [Fact]
        public void TestPipeline_Runs()
        {
            Assert.True(true);
        }
    }
}
