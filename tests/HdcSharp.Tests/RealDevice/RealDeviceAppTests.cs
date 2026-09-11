using HdcSharp.Protocol;
using Xunit;

namespace HdcSharp.Tests.RealDevice;

/// <summary>
/// 真机应用安装/卸载用例：卸载走设备端 bm 并提取真实错误；安装以「本地缺失」与「垃圾 hap」两条路径
/// 证明 APP 帧链路已到达设备端 bm 安装器。
/// 依据 docs/verification/2026-09-11-real-device-connect.md §8。
/// 真实应用包的安装成功路径需可用的 .hap 包方能覆盖，故不在本套件内（见 README 未覆盖项）。
/// </summary>
[Collection(RealDeviceCollectionDefinition.Name)]
[Trait("RealDevice", "true")]
public class RealDeviceAppTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromMinutes(2);

    [RealDeviceFact]
    public async Task Uninstall_MissingPackage_ReportsDeviceBmError()
    {
        await using var session = await RealDeviceSession.OpenAsync(Budget);

        HdcException error = await Assert.ThrowsAsync<HdcException>(
            () => session.Device.UninstallAsync("com.hdcsharp.nonexistent.pkg", ct: session.Token));

        // APP_FINISH 载荷中的 bm 原文被完整提取（帧路径到达设备端 bm install 服务的反证）
        Assert.Contains("failed to uninstall bundle", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("missing installed bundle", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [RealDeviceFact]
    public async Task Install_MissingLocalFile_ThrowsBeforeSendingAnyFrame()
    {
        await using var session = await RealDeviceSession.OpenAsync(Budget);
        string missing = Path.Combine(Path.GetTempPath(), $"hdcsharp-missing-{Guid.NewGuid():N}.hap");

        await Assert.ThrowsAsync<FileNotFoundException>(() => session.Device.InstallAsync(missing, ct: session.Token));

        // 会话未被破坏
        Assert.Equal("alive", (await session.Device.ExecuteShellAsync("echo alive", session.Token)).Trim());
    }

    [RealDeviceFact]
    public async Task Install_GarbageHap_ReportsBmSignatureRejection()
    {
        await using var session = await RealDeviceSession.OpenAsync(Budget);
        string localDir = RealDeviceHarness.NewLocalTempDir("garbagehap");
        try
        {
            // 1KB 垃圾内容：APP 检查/传输/设备端 bm 安装全程打通，最终由 bm 以「无签名」拒绝
            string packagePath = Path.Combine(localDir, "garbage.hap");
            File.WriteAllBytes(packagePath, new byte[1024]);

            HdcException error = await Assert.ThrowsAsync<HdcException>(
                () => session.Device.InstallAsync(packagePath, ct: session.Token));

            Assert.Contains("no signature", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            RealDeviceHarness.TryDeleteLocalDir(localDir);
        }
    }
}
