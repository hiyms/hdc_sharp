using System.Text;
using System.Threading.Channels;
using HdcSharp.Protocol;
using HdcSharp.Transport;
using Xunit;

namespace HdcSharp.Tests.RealDevice;

/// <summary>
/// 真机 shell 用例：一次性执行、流式分块、交互式 PTY 与 C++ 沙箱 1200/Tlv32。
/// 依据 docs/verification/2026-09-11-real-device-connect.md §5（真机 Shell 验证）。
/// 设备要求：toybox shell（无 grep/tr/expr），stdout/stderr 合流、退出码不上线。
/// </summary>
[Collection(RealDeviceCollectionDefinition.Name)]
[Trait("RealDevice", "true")]
public class RealDeviceShellTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromMinutes(2);

    [RealDeviceFact]
    public async Task ExecuteShellAsync_Echo_ReturnsCommandOutput()
    {
        await using var session = await RealDeviceSession.OpenAsync(Budget);

        string output = await session.Device.ExecuteShellAsync("echo hello-hdcsharp", session.Token);

        Assert.Equal("hello-hdcsharp", output.Trim());
    }

    [RealDeviceFact]
    public async Task ExecuteShellAsync_MultibyteUtf8_RoundTripsWithoutMojibake()
    {
        await using var session = await RealDeviceSession.OpenAsync(Budget);

        string output = await session.Device.ExecuteShellAsync("echo 你好，HDC", session.Token);

        Assert.Equal("你好，HDC", output.Trim());
    }

    [RealDeviceFact]
    public async Task ExecuteShellAsync_Stderr_IsMergedIntoOutput()
    {
        await using var session = await RealDeviceSession.OpenAsync(Budget);

        string output = await session.Device.ExecuteShellAsync("ls /nonexistent-path-xyz", session.Token);

        Assert.Contains("nonexistent-path-xyz", output, StringComparison.Ordinal);
        Assert.Contains("No such file or directory", output, StringComparison.Ordinal);
    }

    [RealDeviceFact]
    public async Task ExecuteShellAsync_NonZeroExitCode_IsNotOnTheWire()
    {
        await using var session = await RealDeviceSession.OpenAsync(Budget);

        // 退出码不上线（spec §4.11）：命令确实执行了，但通道关闭即完成，输出为空
        string output = await session.Device.ExecuteShellAsync("sh -c 'exit 42'", session.Token);

        Assert.Equal("", output.Trim());
    }

    [RealDeviceFact]
    public async Task StreamShellOutputAsync_MultipleLines_PreservesChunkOrder()
    {
        await using var session = await RealDeviceSession.OpenAsync(Budget);
        var chunks = new List<byte[]>();

        await foreach (byte[] chunk in session.Device.StreamShellOutputAsync(
            "for i in 1 2 3; do echo line-$i; sleep 0.2; done", session.Token))
        {
            chunks.Add(chunk);
        }

        Assert.Equal("line-1\nline-2\nline-3\n", string.Concat(chunks.Select(Encoding.UTF8.GetString)));
        // 逐行 flush：三行不会挤在单个分块里（真机实测 3 块），否则流式读的语义就退化了
        Assert.True(chunks.Count >= 2, $"期望至少 2 个分块，实际 {chunks.Count} 个");
        Assert.All(chunks, chunk => Assert.NotEmpty(chunk));
    }

    [RealDeviceFact]
    public async Task OpenInteractiveShellAsync_EvaluatesArithmeticAndRejectsWriteAfterDispose()
    {
        await using var session = await RealDeviceSession.OpenAsync(Budget);
        await using IInteractiveShell shell = await session.Device.OpenInteractiveShellAsync(session.Token);
        var received = new StringBuilder();
        var resultSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(session.Token);

        Task pump = Task.Run(async () =>
        {
            try
            {
                await foreach (byte[] chunk in shell.Output.ReadAllAsync(pumpCts.Token))
                {
                    received.Append(Encoding.UTF8.GetString(chunk));
                    if (received.ToString().Contains("ARITH=42", StringComparison.Ordinal))
                    {
                        resultSeen.TrySetResult();
                        return;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // 会话预算耗尽：由下方等待超时统一判定失败
            }
        });

        try
        {
            await Task.Delay(300, session.Token);
            await shell.Input.WriteAsync(Encoding.UTF8.GetBytes("echo ARITH=$((6*7))\n"), session.Token);
            await shell.Input.FlushAsync(session.Token);

            // $((6*7)) 必须由设备端 shell 求值得到 42，才能证明这是真实 PTY 而非命令回显
            Task finished = await Task.WhenAny(resultSeen.Task, Task.Delay(TimeSpan.FromSeconds(20), session.Token));
            Assert.Same(resultSeen.Task, finished);
            Assert.Contains("ARITH=42", received.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            await shell.DisposeAsync();
        }

        await Assert.ThrowsAsync<ObjectDisposedException>(
            async () => await shell.Input.WriteAsync(new byte[] { 0x0A }, CancellationToken.None));

        pumpCts.Cancel();
        await pump;
    }

    [RealDeviceFact]
    public async Task ExecuteUnityAsync_UnknownBundle_ReportsDaemonRejection()
    {
        await using var session = await RealDeviceSession.OpenAsync(Budget);
        Assert.Equal(DaemonGeneration.Cpp, session.Device.Generation);
        const string BundleName = "com.hdcsharp.nonexistent.bundle";

        // 1200 强制要求命令 + 包名两个 Tlv32 标签；包名在设备上不存在 → daemon 回 [E003001]
        HdcException error = await Assert.ThrowsAsync<HdcException>(
            () => session.Device.ExecuteUnityAsync("echo unity-1200", new ShellOptions { BundleName = BundleName }, session.Token));

        Assert.Contains(BundleName, error.Message, StringComparison.Ordinal);
        Assert.Contains("E003", error.Message, StringComparison.Ordinal);
    }
}
