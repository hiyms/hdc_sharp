using HdcSharp.Protocol;
using Xunit;

namespace HdcSharp.Tests.Protocol;

public class HdcConstantsTests
{
    [Theory]
    [InlineData(HdcCommand.KernelHandshake, 1u)]
    [InlineData(HdcCommand.KernelChannelClose, 2u)]
    [InlineData(HdcCommand.KernelEcho, 9u)]
    [InlineData(HdcCommand.KernelEchoRaw, 10u)]
    [InlineData(HdcCommand.KernelWakeupSlavetask, 12u)]
    [InlineData(HdcCommand.UnityExecute, 1001u)]
    [InlineData(HdcCommand.UnityRemount, 1002u)]
    [InlineData(HdcCommand.UnityReboot, 1003u)]
    [InlineData(HdcCommand.UnityRunmode, 1004u)]
    [InlineData(HdcCommand.UnityHilog, 1005u)]
    [InlineData(HdcCommand.UnityRootrun, 1007u)]
    [InlineData(HdcCommand.UnityBugreportInit, 1011u)]
    [InlineData(HdcCommand.UnityBugreportData, 1012u)]
    [InlineData(HdcCommand.ShellInit, 2000u)]
    [InlineData(HdcCommand.ShellData, 2001u)]
    [InlineData(HdcCommand.ForwardInit, 2500u)]
    [InlineData(HdcCommand.ForwardCheck, 2501u)]
    [InlineData(HdcCommand.ForwardCheckResult, 2502u)]
    [InlineData(HdcCommand.ForwardActiveSlave, 2503u)]
    [InlineData(HdcCommand.ForwardActiveMaster, 2504u)]
    [InlineData(HdcCommand.ForwardData, 2505u)]
    [InlineData(HdcCommand.ForwardFreeContext, 2506u)]
    [InlineData(HdcCommand.ForwardList, 2507u)]
    [InlineData(HdcCommand.ForwardRemove, 2508u)]
    [InlineData(HdcCommand.ForwardSuccess, 2509u)]
    [InlineData(HdcCommand.FileInit, 3000u)]
    [InlineData(HdcCommand.FileCheck, 3001u)]
    [InlineData(HdcCommand.FileBegin, 3002u)]
    [InlineData(HdcCommand.FileData, 3003u)]
    [InlineData(HdcCommand.FileFinish, 3004u)]
    [InlineData(HdcCommand.FileMode, 3006u)]
    [InlineData(HdcCommand.DirMode, 3007u)]
    [InlineData(HdcCommand.AppInit, 3500u)]
    [InlineData(HdcCommand.AppCheck, 3501u)]
    [InlineData(HdcCommand.AppBegin, 3502u)]
    [InlineData(HdcCommand.AppData, 3503u)]
    [InlineData(HdcCommand.AppFinish, 3504u)]
    [InlineData(HdcCommand.AppUninstall, 3505u)]
    [InlineData(HdcCommand.HeartbeatMsg, 5000u)]
    public void CommandValues_MatchWireProtocol(HdcCommand cmd, uint expected) =>
        Assert.Equal(expected, (uint)cmd);

    [Fact]
    public void Constants_MatchSpecValues()
    {
        Assert.Equal("HW", HdcConstants.PacketFlag);
        Assert.Equal(0x01, HdcConstants.ProtocolVer);
        Assert.Equal(0x09, HdcConstants.PayloadVCode);
        Assert.Equal("OHOS HDC", HdcConstants.HandshakeMessage);
        Assert.Equal("HS FAILED", HdcConstants.HandshakeFailed);
        Assert.Equal("Ver: 3.2.0f", HdcConstants.HostVersion);
        Assert.Equal("Ver: 3.0.0b", HdcConstants.MinDaemonVersion);
        Assert.Equal(0x0C, HdcConstants.HostDaemonBufSeparator);
        Assert.Equal(11, HdcConstants.PayloadHeadSize);
        Assert.Equal(64, HdcConstants.TransferSlotSize);
        Assert.Equal(49152, HdcConstants.MaxFileChunkSize);
        Assert.Equal(5, HdcConstants.HeartbeatIntervalSeconds);
    }
}
