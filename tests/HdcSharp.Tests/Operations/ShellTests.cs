using System.Security.Cryptography;
using System.Text;
using HdcSharp.Protocol;
using HdcSharp.Security;
using HdcSharp.Tests.TestDoubles;
using HdcSharp.Transport;
using Xunit;

namespace HdcSharp.Tests.Operations;

/// <summary>
/// Task 14 shell 操作测试：一次性执行、流式输出、交互式 PTY 与 C++ 沙箱 1200 路径。
/// 剧本由 FakeDaemon 实现，输出帧统一为 KERNEL_ECHO_RAW(10)、终结帧为 CHANNEL_CLOSE[1]（上游 daemon 语义）。
/// </summary>
public class ShellTests
{
    private static readonly TimeSpan TestBudget = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task ExecuteShellAsync_SimpleCommand_ReturnsAggregatedOutput()
    {
        // 分块故意在多字节 UTF-8 字符中间切开（"你" = E4 BD A0），验证库按字节聚合后整体解码
        byte[] chinese = "你好"u8.ToArray();
        using var daemon = new FakeDaemon(new FakeDaemonOptions
        {
            ShellChunks = ["hello "u8.ToArray(), chinese[..3], chinese[3..], "world"u8.ToArray()],
        });
        await using var host = new HdcHost();
        HdcDevice device = await ConnectAsync(host, daemon);

        string output = await WithTimeoutAsync(device.ExecuteShellAsync("echo hello 你好world"), TestBudget);

        Assert.Equal("hello 你好world", output);
        Frame execute = Assert.Single(daemon.ReceivedFrames, f => f.Command == HdcCommand.UnityExecute);
        Assert.Equal("echo hello 你好world", Encoding.UTF8.GetString(execute.Payload));
    }

    [Fact]
    public async Task ExecuteShellAsync_CommandNotFound_ReturnsDaemonMessage()
    {
        // shell 命令不存在时 stderr 也走 ECHO_RAW，与 stdout 合并为同一输出流
        using var daemon = FakeDaemon.WithShellScript("sh: nosuchcmd: command not found\n");
        await using var host = new HdcHost();
        HdcDevice device = await ConnectAsync(host, daemon);

        string output = await WithTimeoutAsync(device.ExecuteShellAsync("nosuchcmd"), TestBudget);

        Assert.Equal("sh: nosuchcmd: command not found\n", output);
    }

    [Fact]
    public async Task ExecuteShellAsync_DaemonClosesChannel_CompletesWithoutHang()
    {
        using var daemon = FakeDaemon.WithShellScript();
        await using var host = new HdcHost();
        HdcDevice device = await ConnectAsync(host, daemon);

        string output = await WithTimeoutAsync(device.ExecuteShellAsync("true"), TestBudget);

        Assert.Equal("", output);
    }

    [Fact]
    public async Task ExecuteShellAsync_DaemonClosesWithZeroHops_ReturnsOutput()
    {
        // 对端发 CLOSE[0] 属“已确认关闭”，同样是终结信号，不得挂起
        using var daemon = new FakeDaemon(new FakeDaemonOptions { ShellChunks = ["done"u8.ToArray()], ShellCloseHops = 0 });
        await using var host = new HdcHost();
        HdcDevice device = await ConnectAsync(host, daemon);

        string output = await WithTimeoutAsync(device.ExecuteShellAsync("x"), TestBudget);

        Assert.Equal("done", output);
    }

    [Fact]
    public async Task ExecuteShellAsync_DaemonReportsEchoFail_ThrowsWithDaemonMessage()
    {
        using var daemon = new FakeDaemon(new FakeDaemonOptions
        {
            ShellEchoMessages = [(MessageLevel.Fail, "[E003004] Device does not support this shell option")],
        });
        await using var host = new HdcHost();
        HdcDevice device = await ConnectAsync(host, daemon);

        HdcException ex = await Assert.ThrowsAsync<HdcException>(
            () => WithTimeoutAsync(device.ExecuteShellAsync("ls"), TestBudget));

        Assert.Contains("[E003004]", ex.Message);
    }

    [Fact]
    public async Task ExecuteShellAsync_ConnectionLost_ThrowsDisconnected()
    {
        using var daemon = new FakeDaemon(new FakeDaemonOptions { ShellClosesAfterOutput = false });
        await using var host = new HdcHost();
        HdcDevice device = await ConnectAsync(host, daemon);
        Task<string> pending = device.ExecuteShellAsync("hold");
        await WaitForAsync(() => daemon.ReceivedFrames.Any(f => f.Command == HdcCommand.UnityExecute), TestBudget);

        daemon.Dispose();

        HdcException ex = await Assert.ThrowsAsync<HdcException>(() => WithTimeoutAsync(pending, TestBudget));
        Assert.Contains("连接已断开", ex.Message);
    }

    [Fact]
    public async Task StreamShellOutputAsync_YieldsChunksInOrder()
    {
        using var daemon = FakeDaemon.WithShellScript("a", "b", "c");
        await using var host = new HdcHost();
        HdcDevice device = await ConnectAsync(host, daemon);
        List<string> chunks = [];

        await foreach (byte[] chunk in device.StreamShellOutputAsync("abc"))
        {
            chunks.Add(Encoding.UTF8.GetString(chunk));
        }

        Assert.Equal(["a", "b", "c"], chunks);
    }

    [Fact]
    public async Task StreamShellOutputAsync_Cancellation_SendsChannelClose()
    {
        using var daemon = new FakeDaemon(new FakeDaemonOptions
        {
            ShellChunks = ["first"u8.ToArray()],
            ShellClosesAfterOutput = false,
        });
        await using var host = new HdcHost();
        HdcDevice device = await ConnectAsync(host, daemon);
        using var cts = new CancellationTokenSource();
        List<string> received = [];

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (byte[] chunk in device.StreamShellOutputAsync("stream", cts.Token))
            {
                received.Add(Encoding.UTF8.GetString(chunk));
                cts.Cancel();
            }
        });

        Assert.Equal(["first"], received);
        uint channelId = Assert.Single(daemon.ReceivedFrames, f => f.Command == HdcCommand.UnityExecute).ChannelId;
        await WaitForAsync(
            () => daemon.ReceivedFrames.Any(f =>
                f.ChannelId == channelId && f.Command == HdcCommand.KernelChannelClose && f.Payload.SequenceEqual(new byte[] { 0 })),
            TestBudget);
    }

    [Fact]
    public async Task OpenInteractiveShellAsync_WriteAndRead_RoundTrip()
    {
        using var daemon = FakeDaemon.WithInteractiveEcho();
        await using var host = new HdcHost();
        HdcDevice device = await ConnectAsync(host, daemon);
        IInteractiveShell shell = await WithTimeoutAsync(device.OpenInteractiveShellAsync(), TestBudget);
        // SHELL_INIT 的到达是异步的（宿主发送完成 ≠ 替身已读到），须等其被记录后再断言
        await WaitForAsync(() => daemon.ReceivedFrames.Any(f => f.Command == HdcCommand.ShellInit), TestBudget);
        string initPayload = Encoding.UTF8.GetString(
            Assert.Single(daemon.ReceivedFrames, f => f.Command == HdcCommand.ShellInit).Payload);
        try
        {
            byte[] line = "ls\n"u8.ToArray();
            shell.Input.Write(line, 0, line.Length);

            byte[] echoed = await WithTimeoutAsync(shell.Output.ReadAsync().AsTask(), TestBudget);

            Assert.Equal(line, echoed);
            Assert.Equal("\0", initPayload);
            await shell.Input.WriteAsync(new byte[] { 0x04 });
            await WithTimeoutAsync(shell.WaitUntilClosedAsync(), TestBudget);
        }
        finally
        {
            await shell.DisposeAsync();
        }
    }

    [Fact]
    public async Task OpenInteractiveShellAsync_Dispose_ClosesChannelAndUnregisters()
    {
        using var daemon = FakeDaemon.WithInteractiveEcho();
        await using var host = new HdcHost();
        HdcDevice device = await ConnectAsync(host, daemon);
        IInteractiveShell shell = await WithTimeoutAsync(device.OpenInteractiveShellAsync(), TestBudget);
        // SHELL_INIT 的到达是异步的（宿主发送完成 ≠ 替身已读到），须等其被记录后再断言
        await WaitForAsync(() => daemon.ReceivedFrames.Any(f => f.Command == HdcCommand.ShellInit), TestBudget);
        uint channelId = Assert.Single(daemon.ReceivedFrames, f => f.Command == HdcCommand.ShellInit).ChannelId;

        await shell.DisposeAsync();

        await WaitForAsync(
            () => daemon.ReceivedFrames.Any(f =>
                f.ChannelId == channelId && f.Command == HdcCommand.KernelChannelClose && f.Payload.SequenceEqual(new byte[] { 0 })),
            TestBudget);
        Assert.Null(device.Dispatcher.Get(channelId));
        await WithTimeoutAsync(shell.WaitUntilClosedAsync(), TestBudget);
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => shell.Input.WriteAsync(new byte[] { 0x01 }).AsTask());
    }

    [Fact]
    public async Task ExecuteUnityAsync_CppGeneration_SendsTlv32Payload()
    {
        using var daemon = new FakeDaemon(new FakeDaemonOptions
        {
            Generation = DaemonGeneration.Cpp,
            ShellChunks = ["unity"u8.ToArray()],
        });
        await using var host = new HdcHost();
        HdcDevice device = await ConnectAsync(host, daemon);

        string output = await WithTimeoutAsync(
            device.ExecuteUnityAsync("ls /", new ShellOptions { BundleName = "com.example.app" }), TestBudget);

        Assert.Equal("unity", output);
        Frame execute = Assert.Single(daemon.ReceivedFrames, f => f.Command == HdcCommand.UnityExecuteEx);
        Dictionary<uint, byte[]> tags = Tlv32.Parse(execute.Payload);
        Assert.Equal("ls /", Encoding.UTF8.GetString(tags[Tlv32.TagShellCmd]));
        Assert.Equal("com.example.app", Encoding.UTF8.GetString(tags[Tlv32.TagShellBundle]));
    }

    [Fact]
    public async Task ExecuteUnityAsync_RustGeneration_Throws()
    {
        using var daemon = new FakeDaemon(new FakeDaemonOptions
        {
            Generation = DaemonGeneration.Rust,
            ShellChunks = ["never"u8.ToArray()],
        });
        await using var host = new HdcHost();
        HdcDevice device = await ConnectAsync(host, daemon);

        HdcException ex = await Assert.ThrowsAsync<HdcException>(
            () => device.ExecuteUnityAsync("ls", new ShellOptions { BundleName = "com.example.app" }));

        Assert.Contains("C++", ex.Message);
        Assert.DoesNotContain(daemon.ReceivedFrames, f => f.Command == HdcCommand.UnityExecuteEx);
    }

    [Fact]
    public async Task ExecuteUnityAsync_WithoutBundle_ThrowsArgumentException()
    {
        using var daemon = new FakeDaemon(new FakeDaemonOptions { Generation = DaemonGeneration.Cpp });
        await using var host = new HdcHost();
        HdcDevice device = await ConnectAsync(host, daemon);

        ArgumentException ex = await Assert.ThrowsAsync<ArgumentException>(() => device.ExecuteUnityAsync("ls"));

        Assert.Equal("options", ex.ParamName);
    }

    [Fact]
    public async Task ExecuteShell_ConcurrentOnSameDevice_DistinctChannelsNoInterleave()
    {
        // 剧本按通道回显各自命令：若帧路由串通道，先完成的任务会读到对方输出或永久悬挂
        using var daemon = new FakeDaemon(new FakeDaemonOptions { EchoShellCommand = true });
        await using var host = new HdcHost();
        HdcDevice device = await ConnectAsync(host, daemon);

        Task<string> first = device.ExecuteShellAsync("alpha-task");
        Task<string> second = device.ExecuteShellAsync("beta-task");
        string[] results = await WithTimeoutAsync(Task.WhenAll(first, second), TestBudget);

        Assert.Equal("alpha-task", results[0]);
        Assert.Equal("beta-task", results[1]);
        Frame[] executes = daemon.ReceivedFrames.Where(f => f.Command == HdcCommand.UnityExecute).ToArray();
        Assert.Equal(2, executes.Length);
        Assert.NotEqual(executes[0].ChannelId, executes[1].ChannelId);
    }

    [Fact]
    public async Task UnregisteredChannel_ReceivesCommand_HostRepliesCloseZero()
    {
        // 未注册通道收到命令时宿主回 CHANNEL_CLOSE[0] 促使对端终结（spec §4.11）
        const uint strayChannelId = 0x00C0FFEE;
        using var daemon = new FakeDaemon(new FakeDaemonOptions { StrayEchoChannelId = strayChannelId });
        await using var host = new HdcHost();
        _ = await ConnectAsync(host, daemon);

        await WaitForAsync(
            () => daemon.ReceivedFrames.Any(f =>
                f.ChannelId == strayChannelId && f.Command == HdcCommand.KernelChannelClose && f.Payload.SequenceEqual(new byte[] { 0 })),
            TestBudget);
    }

    private static ConnectOptions CreateOptions()
    {
        return new ConnectOptions { KeyStore = new UnusedKeyStore(), AuthTimeout = TestBudget };
    }

    private static async Task<HdcDevice> ConnectAsync(HdcHost host, FakeDaemon daemon)
    {
        return await WithTimeoutAsync(
            host.ConnectAsync($"127.0.0.1:{daemon.Port}", CreateOptions()), TestBudget);
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.True(condition(), "等待条件在超时前未满足");
    }

    private static async Task<T> WithTimeoutAsync<T>(Task<T> task, TimeSpan timeout)
    {
        if (await Task.WhenAny(task, Task.Delay(timeout)) != task)
        {
            throw new TimeoutException($"操作未在 {timeout} 内完成");
        }

        return await task;
    }

    private static async Task WithTimeoutAsync(Task task, TimeSpan timeout)
    {
        if (await Task.WhenAny(task, Task.Delay(timeout)) != task)
        {
            throw new TimeoutException($"操作未在 {timeout} 内完成");
        }

        await task;
    }

    private sealed class UnusedKeyStore : IHostKeyStore
    {
        public RSA GetPrivateKey() => throw new InvalidOperationException("测试未启用认证，不应访问密钥库");

        public string GetPublicKeyPem() => throw new InvalidOperationException("测试未启用认证，不应访问密钥库");
    }
}
