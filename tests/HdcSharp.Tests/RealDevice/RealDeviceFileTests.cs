using Xunit;

namespace HdcSharp.Tests.RealDevice;

/// <summary>
/// 真机文件传输用例：覆盖 2026-09-11 实测过的丢尾边界尺寸（块整数倍与「整数倍+1」），双向以 sha256 比对。
/// 依据 docs/verification/2026-09-11-real-device-connect.md §6（含丢尾缺陷与修复后的 0 失败基线）。
/// 发送方向校验设备侧 sha256；接收方向校验本地 sha256。
/// </summary>
[Collection(RealDeviceCollectionDefinition.Name)]
[Trait("RealDevice", "true")]
public class RealDeviceFileTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromMinutes(3);

    /// <summary>数据块为 48KiB(49152)：含 0/1 字节、块的整数倍与整数倍+1、多块尾部。</summary>
    public static TheoryData<int> FileSizes => new()
    {
        0,
        1,
        49152,
        49153,
        98304,
        98305,
        147457,
        500000,
    };

    [RealDeviceTheory]
    [MemberData(nameof(FileSizes))]
    public async Task SendFile_EverySizeBoundary_DeviceSha256MatchesLocal(int size)
    {
        await using var session = await RealDeviceSession.OpenAsync(Budget);
        string localDir = RealDeviceHarness.NewLocalTempDir($"send{size}");
        string remotePath = RealDeviceHarness.NewRemotePath($"send{size}");
        try
        {
            string localPath = Path.Combine(localDir, $"payload-{size}.bin");
            byte[] expected = RealDeviceHarness.WriteRandomFile(localPath, size);

            await session.Device.SendFileAsync(localPath, remotePath, ct: session.Token);

            string? deviceSha = await RealDeviceHarness.TryDeviceSha256Async(session.Device, remotePath, session.Token);
            Assert.NotNull(deviceSha);
            Assert.Equal(RealDeviceHarness.Sha256Hex(expected), deviceSha);
        }
        finally
        {
            await RealDeviceHarness.TryCleanupRemoteAsync(session.Device, remotePath);
            RealDeviceHarness.TryDeleteLocalDir(localDir);
        }
    }

    [RealDeviceTheory]
    [MemberData(nameof(FileSizes))]
    public async Task ReceiveFile_EverySizeBoundary_LocalSha256MatchesSource(int size)
    {
        await using var session = await RealDeviceSession.OpenAsync(Budget);
        string localDir = RealDeviceHarness.NewLocalTempDir($"recv{size}");
        string remotePath = RealDeviceHarness.NewRemotePath($"recv{size}");
        try
        {
            // 先在设备侧造出源文件（发送方向已由上一个用例单独校验），再接收回来闭环比对
            string sourcePath = Path.Combine(localDir, "source.bin");
            byte[] expected = RealDeviceHarness.WriteRandomFile(sourcePath, size);
            await session.Device.SendFileAsync(sourcePath, remotePath, ct: session.Token);

            string receivedPath = Path.Combine(localDir, "received.bin");
            await session.Device.ReceiveFileAsync(remotePath, receivedPath, ct: session.Token);

            Assert.True(File.Exists(receivedPath), "接收完成后本地文件应存在");
            Assert.Equal(RealDeviceHarness.Sha256Hex(expected), RealDeviceHarness.Sha256HexOfFile(receivedPath));
        }
        finally
        {
            await RealDeviceHarness.TryCleanupRemoteAsync(session.Device, remotePath);
            RealDeviceHarness.TryDeleteLocalDir(localDir);
        }
    }
}
