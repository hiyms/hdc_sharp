using Xunit;

namespace HdcSharp.Tests;

/// <summary>
/// 冒烟测试：验证测试工程与主工程的引用链路可正常编译与执行。
/// </summary>
public class SmokeTests
{
    /// <summary>
    /// 验证 xUnit 运行器与项目引用链路畅通。
    /// </summary>
    [Fact]
    public void Smoke_True_Passes()
    {
        Assert.True(true);
    }
}
