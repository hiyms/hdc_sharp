using Xunit;

namespace HdcSharp.Tests.RealDevice;

/// <summary>
/// 真机集成测试门控特性：未设置环境变量 <c>HDC_TEST_TARGET</c>（形如 <c>ip:port</c>）时整用例标记为 Skip，
/// 使默认 <c>dotnet test</c> 与 CI 无真机依赖、零副作用。使用本特性的测试类须同时标注
/// <c>[Trait("RealDevice", "true")]</c>，以便用 <c>--filter "RealDevice=true"</c> 只跑真机用例。
/// </summary>
public sealed class RealDeviceFactAttribute : FactAttribute
{
    /// <summary>创建特性：环境变量缺失时写入跳过原因。</summary>
    public RealDeviceFactAttribute()
    {
        Skip = RealDeviceGate.SkipReason;
    }
}

/// <summary>
/// 与 <see cref="RealDeviceFactAttribute"/> 同门控的 <see cref="TheoryAttribute"/> 变体：
/// 数据驱动的真机用例（如各边界尺寸）同样在未设置 <c>HDC_TEST_TARGET</c> 时整体跳过。
/// </summary>
public sealed class RealDeviceTheoryAttribute : TheoryAttribute
{
    /// <summary>创建特性：环境变量缺失时写入跳过原因。</summary>
    public RealDeviceTheoryAttribute()
    {
        Skip = RealDeviceGate.SkipReason;
    }
}

/// <summary>真机集成测试的共同门控：环境变量名与跳过原因。</summary>
internal static class RealDeviceGate
{
    /// <summary>启用真机集成测试的环境变量名，取值为 daemon 端点（<c>ip:port</c>）。</summary>
    internal const string TargetEnvironmentVariable = "HDC_TEST_TARGET";

    /// <summary>未配置真机端点时的跳过原因；已配置时为 null（不跳过）。</summary>
    internal static string? SkipReason =>
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(TargetEnvironmentVariable))
            ? $"未设置 {TargetEnvironmentVariable}=<ip:port>，真机集成测试默认跳过"
            : null;
}
