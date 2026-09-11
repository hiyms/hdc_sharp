using Xunit;

namespace HdcSharp.Tests.RealDevice;

/// <summary>
/// 真机目录传输用例：多文件 + 3 层嵌套 + 空目录，发送后按相对路径逐个比对设备侧 sha256，再接收回来做往返闭环。
/// 依据 docs/verification/2026-09-11-real-device-connect.md §7（目录结构、相对路径与往返一致性）。
/// </summary>
[Collection(RealDeviceCollectionDefinition.Name)]
[Trait("RealDevice", "true")]
public class RealDeviceDirectoryTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromMinutes(3);

    [RealDeviceFact]
    public async Task SendDirectory_NestedTree_DeviceContentMatchesAndRoundTrips()
    {
        await using var session = await RealDeviceSession.OpenAsync(Budget);
        string localRoot = RealDeviceHarness.NewLocalTempDir("dir");
        string remoteRoot = RealDeviceHarness.NewRemotePath("dir");
        string remoteTree = $"{remoteRoot}/tree";
        string backRoot = RealDeviceHarness.NewLocalTempDir("dirback");
        try
        {
            Dictionary<string, string> expected = BuildLocalTree(localRoot);

            // 目标父目录须先存在：daemon 只按相对路径建子目录
            await session.Device.ExecuteShellAsync($"mkdir -p {remoteRoot}", session.Token);
            await session.Device.SendDirectoryAsync(localRoot, remoteTree, ct: session.Token);

            // 目标不存在时 daemon 把 remoteTree 当作重命名后的源目录（spec §4.7.3）
            foreach ((string relative, string sha) in expected)
            {
                string? deviceSha = await RealDeviceHarness.TryDeviceSha256Async(
                    session.Device, $"{remoteTree}/{relative}", session.Token);
                Assert.NotNull(deviceSha);
                Assert.Equal(sha, deviceSha);
            }

            // 空目录不产生任何条目（协议只承载文件，spec §4.7.3）
            Assert.False(await RealDeviceHarness.DevicePathExistsAsync(session.Device, $"{remoteTree}/emptydir", session.Token));

            await session.Device.ReceiveDirectoryAsync(remoteTree, backRoot, ct: session.Token);

            Dictionary<string, string> received = BuildManifest(backRoot);
            Assert.Equal(expected.Count, received.Count);
            foreach ((string relative, string sha) in expected)
            {
                Assert.True(
                    received.Any(kv => kv.Key.EndsWith(relative, StringComparison.Ordinal) && kv.Value == sha),
                    $"设备回收的文件 {relative} 未按相对路径与内容匹配，实际收到：{string.Join(", ", received.Keys)}");
            }
        }
        finally
        {
            await RealDeviceHarness.TryCleanupRemoteAsync(session.Device, remoteRoot);
            RealDeviceHarness.TryDeleteLocalDir(localRoot);
            RealDeviceHarness.TryDeleteLocalDir(backRoot);
        }
    }

    [RealDeviceFact]
    public async Task SendDirectory_EmptyDirectory_SucceedsWithoutTouchingDeviceState()
    {
        await using var session = await RealDeviceSession.OpenAsync(Budget);
        string localRoot = RealDeviceHarness.NewLocalTempDir("emptydir");
        string remoteTree = RealDeviceHarness.NewRemotePath("emptydir");
        try
        {
            Directory.CreateDirectory(Path.Combine(localRoot, "nested-empty"));

            // 空目录不发明线上帧，也不报错（上游 CLI 在同场景报错，本库选择直接成功）
            await session.Device.SendDirectoryAsync(localRoot, remoteTree, ct: session.Token);

            Assert.False(await RealDeviceHarness.DevicePathExistsAsync(session.Device, remoteTree, session.Token));
        }
        finally
        {
            await RealDeviceHarness.TryCleanupRemoteAsync(session.Device, remoteTree);
            RealDeviceHarness.TryDeleteLocalDir(localRoot);
        }
    }

    /// <summary>构造「根文件 + 3 层嵌套 + 空目录」的本地树，返回相对路径（'/' 分隔）到 sha256 的清单。</summary>
    private static Dictionary<string, string> BuildLocalTree(string localRoot)
    {
        File.WriteAllText(Path.Combine(localRoot, "root.txt"), "root-file");
        Directory.CreateDirectory(Path.Combine(localRoot, "sub", "deep", "deeper"));
        RealDeviceHarness.WriteRandomFile(Path.Combine(localRoot, "sub", "a.bin"), 120_000);
        File.WriteAllText(Path.Combine(localRoot, "sub", "deep", "b.txt"), "nested-b");
        RealDeviceHarness.WriteRandomFile(Path.Combine(localRoot, "sub", "deep", "deeper", "c.bin"), 5_000);
        Directory.CreateDirectory(Path.Combine(localRoot, "emptydir"));
        return BuildManifest(localRoot);
    }

    /// <summary>递归收集目录下全部文件，键为相对目录根的路径（'/' 分隔）。</summary>
    private static Dictionary<string, string> BuildManifest(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(
                path => Path.GetRelativePath(root, path).Replace('\\', '/'),
                RealDeviceHarness.Sha256HexOfFile,
                StringComparer.Ordinal);
}
