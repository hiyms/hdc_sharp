using Xunit;

namespace HdcSharp.Tests.RealDevice;

/// <summary>
/// 真机集成测试集合定义：设备是共享物理资源（同一 daemon、同一 hdcd 会话与设备端临时空间），
/// 故标记禁止并行，xunit 会把全部真机用例安排在非并行阶段逐个执行，避免并行连接与临时路径互相干扰。
/// </summary>
[CollectionDefinition(RealDeviceCollectionDefinition.Name, DisableParallelization = true)]
public sealed class RealDeviceCollectionDefinition
{
    /// <summary>集合名；全部真机测试类均以 <c>[Collection(RealDeviceCollectionDefinition.Name)]</c> 加入。</summary>
    public const string Name = "RealDevice";
}
