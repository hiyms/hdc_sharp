using Xunit;

namespace HdcSharp.Tests.RealDevice;

/// <summary>
/// 真机 Unity 命令用例（只读）。
/// 依据 docs/verification/2026-09-11-real-device-connect.md §10：
/// hilog 行流（CMD_UNITY_HILOG 1005）与 bugreport 分块流（BUGREPORT_INIT/DATA 1011/1012）。
/// 明确不覆盖会改变设备状态的命令——reboot(1003)、remount(1002)、rootrun(1007)、runmode(1004)：
/// 它们会重启 daemon 或改写系统参数，违反「测试不得改变设备状态」约束，故仅由单元测试（FakeDaemon）覆盖。
/// </summary>
[Collection(RealDeviceCollectionDefinition.Name)]
[Trait("RealDevice", "true")]
public class RealDeviceUnityTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromMinutes(2);

    [RealDeviceFact]
    public async Task StreamHilogAsync_ReadsLinesThenConsumerExitLeavesSessionUsable()
    {
        await using var session = await RealDeviceSession.OpenAsync(Budget);
        var lines = new List<string>();
        using (var hilogCts = CancellationTokenSource.CreateLinkedTokenSource(session.Token))
        {
            hilogCts.CancelAfter(TimeSpan.FromSeconds(30));

            // hilog 是长命命令：取够若干行即主动退出，库应发 CHANNEL_CLOSE[0] 清理且不报错
            await foreach (string line in session.Device.StreamHilogAsync(hilogCts.Token))
            {
                lines.Add(line);
                if (lines.Count >= 5)
                {
                    break;
                }
            }
        }

        Assert.Equal(5, lines.Count);
        Assert.All(lines, line => Assert.False(string.IsNullOrWhiteSpace(line)));
        // 提前退出后的清理不得破坏会话
        Assert.Equal("alive", (await session.Device.ExecuteShellAsync("echo alive", session.Token)).Trim());
    }

    [RealDeviceFact]
    public async Task StreamBugReportAsync_ProducesChunksFromDeviceHidumper()
    {
        await using var session = await RealDeviceSession.OpenAsync(Budget);
        int chunks = 0;
        long bytes = 0;
        using (var bugreportCts = CancellationTokenSource.CreateLinkedTokenSource(session.Token))
        {
            bugreportCts.CancelAfter(TimeSpan.FromSeconds(60));

            // 采到若干字节即停，避免长时间占用设备（真机实测 300KB 输出为 hidumper 原文）
            await foreach (byte[] chunk in session.Device.StreamBugReportAsync(bugreportCts.Token))
            {
                chunks++;
                bytes += chunk.Length;
                if (bytes >= 20_000)
                {
                    break;
                }
            }
        }

        Assert.True(chunks > 0, "bugreport 未产出任何分块");
        Assert.True(bytes > 0, "bugreport 未产出任何字节");

        // 设备侧 hidumper 自身可能失败（实测其输出恰为 14 字节的 request error，shell 直跑同值），
        // 故不以固定字节数断言；改为与 shell 直跑 hidumper 的输出规模交叉校验，既确定又不受设备状态影响
        string direct = (await session.Device.ExecuteShellAsync("hidumper 2>&1 | wc -c", session.Token)).Trim();
        Assert.True(int.TryParse(direct, out int directBytes), $"无法解析 hidumper 输出规模：{direct}");
        if (directBytes < 1024)
        {
            Assert.InRange(bytes, 1, Math.Max(1, directBytes));
        }
        else
        {
            Assert.True(bytes >= 1024, $"hidumper 直跑 {directBytes} 字节，但 bugreport 仅采集到 {bytes} 字节");
        }

        Assert.Equal("alive", (await session.Device.ExecuteShellAsync("echo alive", session.Token)).Trim());
    }
}
