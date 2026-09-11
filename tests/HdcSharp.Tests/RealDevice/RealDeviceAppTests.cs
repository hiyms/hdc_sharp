using HdcSharp.Protocol;
using Xunit;

namespace HdcSharp.Tests.RealDevice;

/// <summary>
/// 真机应用安装/卸载用例：卸载走设备端 bm 并提取真实错误；安装以「本地缺失」与「垃圾 hap」两条路径
/// 证明 APP 帧链路已到达设备端 bm 安装器。
/// 依据 docs/verification/2026-09-11-real-device-connect.md §8。
/// 真实应用包的安装成功路径由 <see cref="RealDeviceHapFactAttribute"/> 门控（需 HDC_TEST_HAP 指向本地签名包）。
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

/// <summary>
/// 需真实签名 .hap 包的真机应用生命周期用例：卸载（确认从设备消失）→ 安装（确认出现在设备）
/// → 幂等复装，逐步骤以设备端 bm 查询交叉验证，而非仅依赖返回文本。
/// 依据 docs/verification/2026-09-11-real-device-connect.md §11。
/// </summary>
[Collection(RealDeviceCollectionDefinition.Name)]
[Trait("RealDevice", "true")]
public class RealDeviceAppLifecycleTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromMinutes(3);

    [RealDeviceHapFact]
    public async Task InstallAndUninstall_RealSignedHap_DeviceStateReflectsEachStep()
    {
        string hap = Environment.GetEnvironmentVariable(RealDeviceGate.HapEnvironmentVariable)!;
        string bundle = ReadBundleName(hap);
        await using var session = await RealDeviceSession.OpenAsync(Budget);

        // bm dump -n 的产出行数：包存在时是完整清单（数百行），不存在时仅一行错误文本
        static async Task<bool> IsInstalledAsync(HdcDevice device, string bundleName, CancellationToken ct)
        {
            string dump = await device.ExecuteShellAsync($"bm dump -n {bundleName} 2>&1 | wc -l", ct);
            return int.TryParse(dump.Trim(), out int lines) && lines > 10;
        }

        // 归零设备状态：先卸载（若本来没装则忽略该错误），确保后续安装是可观察的状态变化
        try
        {
            await session.Device.UninstallAsync(bundle, ct: session.Token);
        }
        catch (HdcException)
        {
        }

        Assert.False(await IsInstalledAsync(session.Device, bundle, session.Token), $"{bundle} 应已从设备移除");

        string installOutput = await session.Device.InstallAsync(hap, new InstallOptions { Replace = true }, session.Token);
        Assert.Contains("success", installOutput, StringComparison.OrdinalIgnoreCase);
        Assert.True(await IsInstalledAsync(session.Device, bundle, session.Token), $"{bundle} 应出现在设备上");

        // 幂等复装：-r 覆盖安装同样成功，且设备状态保持
        string reinstallOutput = await session.Device.InstallAsync(hap, new InstallOptions { Replace = true }, session.Token);
        Assert.Contains("success", reinstallOutput, StringComparison.OrdinalIgnoreCase);
        Assert.True(await IsInstalledAsync(session.Device, bundle, session.Token), $"{bundle} 复装后仍应在设备上");

        // 清理：卸载回初始状态
        string uninstallOutput = await session.Device.UninstallAsync(bundle, ct: session.Token);
        Assert.Contains("success", uninstallOutput, StringComparison.OrdinalIgnoreCase);
        Assert.False(await IsInstalledAsync(session.Device, bundle, session.Token), $"{bundle} 应已从设备移除");
    }

    /// <summary>从 .hap（zip）内 module.json 读出 bundleName，避免在测试里硬编码包名。</summary>
    private static string ReadBundleName(string hapPath)
    {
        using var archive = System.IO.Compression.ZipFile.OpenRead(hapPath);
        System.IO.Compression.ZipArchiveEntry entry = archive.GetEntry("module.json")
            ?? throw new InvalidOperationException($"{hapPath} 内缺少 module.json");
        using var reader = new StreamReader(entry.Open());
        using var document = System.Text.Json.JsonDocument.Parse(reader.ReadToEnd());
        return document.RootElement.GetProperty("app").GetProperty("bundleName").GetString()
            ?? throw new InvalidOperationException($"{hapPath} 的 module.json 未声明 bundleName");
    }
}
