using System.Security.Cryptography;
using System.Text;
using HdcSharp.Protocol;
using HdcSharp.Security;
using HdcSharp.Tests.TestDoubles;
using HdcSharp.Transport;
using Xunit;

namespace HdcSharp.Tests.Operations;

/// <summary>
/// Task 18 Unity 命令测试。线缆细节以 D:\work\developtools_hdc 源码为准：
/// 命令号为 1002 REMOUNT / 1003 REBOOT / 1004 RUNMODE / 1005 HILOG / 1007 ROOTRUN / 1011 BUGREPORT_INIT，
/// 载荷分别见 src/daemon/daemon_unity.cpp:437-491（daemon 侧）与 src/host/translate.cpp:431-600（host 侧归一化）；
/// hilog 输出走 ECHO_RAW(10)（daemon_unity.cpp:29），bugreport 输出走 BUGREPORT_DATA(1012)（daemon_unity.cpp:487-490）；
/// 完成信号统一为 daemon 的 CHANNEL_CLOSE[1]（src/common/task.cpp:49-59）。
/// </summary>
public class UnityCommandTests
{
    private static readonly TimeSpan TestBudget = TimeSpan.FromSeconds(10);

    /// <summary>RunMode 全部取值与线上载荷的映射（载荷拼装见 src/host/translate.cpp:431-460）。</summary>
    public static TheoryData<RunMode, string> RunModePayloads => new()
    {
        { RunMode.Usb, "usb" },
        { RunMode.Tcp, "port" },
        { RunMode.TcpClose, "port close" },
        { RunMode.TcpPort(5555), "port 5555" },
        { RunMode.TcpPort(1), "port 1" },
        { RunMode.TcpPort(65535), "port 65535" },
    };

    [Fact]
    public async Task Reboot_Default_SendsEmptyPayload()
    {
        using var daemon = FakeDaemon.WithUnityCapture();
        await using var host = new HdcHost();
        HdcDevice device = await ConnectAsync(host, daemon);

        await WithTimeoutAsync(device.RebootAsync(), TestBudget);

        Frame reboot = Assert.Single(daemon.ReceivedFrames, f => f.Command == HdcCommand.UnityReboot);
        Assert.Empty(reboot.Payload);
    }

    [Theory]
    [InlineData(RebootMode.Bootloader, "bootloader")]
    [InlineData(RebootMode.Recovery, "recovery")]
    [InlineData(RebootMode.Flashd, "flashd")]
    public async Task Reboot_EachMode_SendsModeString(RebootMode mode, string expected)
    {
        using var daemon = FakeDaemon.WithUnityCapture();
        await using var host = new HdcHost();
        HdcDevice device = await ConnectAsync(host, daemon);

        await WithTimeoutAsync(device.RebootAsync(mode), TestBudget);

        Frame reboot = Assert.Single(daemon.ReceivedFrames, f => f.Command == HdcCommand.UnityReboot);
        // 上游 CLI 的 target boot -bootloader 会被剥掉前导 '-'（translate.cpp:462-471），载荷不带 '-'
        Assert.Equal(expected, Encoding.UTF8.GetString(reboot.Payload));
    }

    [Fact]
    public async Task Reboot_UnknownMode_ThrowsBeforeSending()
    {
        // 非枚举值不应被静默当成 Default 发上网：上游 host 的归一化只认三种模式串
        using var daemon = FakeDaemon.WithUnityCapture();
        await using var host = new HdcHost();
        HdcDevice device = await ConnectAsync(host, daemon);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => device.RebootAsync((RebootMode)42));

        Assert.DoesNotContain(daemon.ReceivedFrames, f => f.Command == HdcCommand.UnityReboot);
    }

    [Fact]
    public async Task Remount_SendsSingleFrame_Succeeds()
    {
        using var daemon = FakeDaemon.WithUnityEcho(MessageLevel.Ok, "Mount finish");
        await using var host = new HdcHost();
        HdcDevice device = await ConnectAsync(host, daemon);

        await WithTimeoutAsync(device.RemountAsync(), TestBudget);

        Frame remount = Assert.Single(daemon.ReceivedFrames, f => f.Command == HdcCommand.UnityRemount);
        Assert.Empty(remount.Payload);
    }

    [Fact]
    public async Task Remount_DaemonFailureEcho_ThrowsHdcException()
    {
        using var daemon = FakeDaemon.WithUnityEcho(MessageLevel.Fail, "[E007100] Operate need running under debug mode");
        await using var host = new HdcHost();
        HdcDevice device = await ConnectAsync(host, daemon);

        HdcException ex = await Assert.ThrowsAsync<HdcException>(
            () => WithTimeoutAsync(device.RemountAsync(), TestBudget));

        Assert.Contains("[E007100]", ex.Message);
    }

    [Theory]
    [MemberData(nameof(RunModePayloads))]
    public async Task SetRunMode_EachValue_SendsExpectedPayload(RunMode mode, string expected)
    {
        using var daemon = FakeDaemon.WithUnityEcho(MessageLevel.Ok, "Set device run mode successful.");
        await using var host = new HdcHost();
        HdcDevice device = await ConnectAsync(host, daemon);

        await WithTimeoutAsync(device.SetRunModeAsync(mode), TestBudget);

        Frame runmode = Assert.Single(daemon.ReceivedFrames, f => f.Command == HdcCommand.UnityRunmode);
        Assert.Equal(expected, Encoding.UTF8.GetString(runmode.Payload));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    [InlineData(-1)]
    public void RunMode_TcpPortOutOfRange_Throws(int port)
    {
        // 上游 CLI 在 translate.cpp:452 拒绝 port <= 0 或 > 65535（MAX_IP_PORT=65535，define.h:76）
        Assert.Throws<ArgumentOutOfRangeException>(() => RunMode.TcpPort(port));
    }

    [Fact]
    public void RunMode_ValueSemantics_CompareByPayload()
    {
        Assert.Equal(RunMode.TcpPort(5555), RunMode.TcpPort(5555));
        Assert.NotEqual(RunMode.TcpPort(5555), RunMode.TcpPort(5556));
        Assert.NotEqual(RunMode.Tcp, RunMode.TcpClose);
        Assert.Equal("port 5555", RunMode.TcpPort(5555).ToString());
        Assert.Equal("port close", RunMode.TcpClose.ToString());
    }

    [Fact]
    public async Task RootRun_And_Unroot_SendDistinctPayloads()
    {
        using var daemon = FakeDaemon.WithUnityEcho(MessageLevel.Ok, "Set root run mode successful.");
        await using var host = new HdcHost();
        HdcDevice device = await ConnectAsync(host, daemon);

        await WithTimeoutAsync(device.RootRunAsync(), TestBudget);
        await WithTimeoutAsync(device.RootRunAsync(unroot: true), TestBudget);

        Frame[] frames = daemon.ReceivedFrames.Where(f => f.Command == HdcCommand.UnityRootrun).ToArray();
        Assert.Equal(2, frames.Length);
        // 上游 smode：空载荷=以 root 运行，"-r" 归一为 "r"=取消 root（translate.cpp:583-587、daemon_unity.cpp:466-486）
        Assert.Empty(frames[0].Payload);
        Assert.Equal("r", Encoding.UTF8.GetString(frames[1].Payload));
    }

    [Fact]
    public async Task StreamHilog_YieldsLines_AndTerminatesOnChannelClose()
    {
        // 分块故意切开行与多字节字符（"你" = E4 BD A0），验证按行切分跨帧拼接且不破坏 UTF-8
        byte[] chinese = "你好"u8.ToArray();
        using var daemon = FakeDaemon.WithHilogScript(
            "line1\nlin"u8.ToArray(),
            "e2 "u8.ToArray(),
            chinese[..2],
            chinese[2..],
            "\nline3\r\n"u8.ToArray(),
            "tail-without-newline"u8.ToArray());
        await using var host = new HdcHost();
        HdcDevice device = await ConnectAsync(host, daemon);
        List<string> lines = [];

        await foreach (string line in device.StreamHilogAsync())
        {
            lines.Add(line);
        }

        Assert.Equal(["line1", "line2 你好", "line3", "tail-without-newline"], lines);
        Frame hilog = Assert.Single(daemon.ReceivedFrames, f => f.Command == HdcCommand.UnityHilog);
        Assert.Empty(hilog.Payload);
    }

    [Fact]
    public async Task StreamHilog_LongRunning_CancellationSendsChannelClose()
    {
        using var daemon = new FakeDaemon(new FakeDaemonOptions
        {
            UnityHilogChunks = ["l1\n"u8.ToArray(), "l2\n"u8.ToArray()],
            UnityHilogClosesAfterOutput = false,
        });
        await using var host = new HdcHost();
        HdcDevice device = await ConnectAsync(host, daemon);
        using var cts = new CancellationTokenSource();
        List<string> received = [];

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (string line in device.StreamHilogAsync(cts.Token))
            {
                received.Add(line);
                if (received.Count == 2)
                {
                    cts.Cancel();
                }
            }
        });

        Assert.Equal(["l1", "l2"], received);
        uint channelId = Assert.Single(daemon.ReceivedFrames, f => f.Command == HdcCommand.UnityHilog).ChannelId;
        await WaitForAsync(
            () => daemon.ReceivedFrames.Any(f =>
                f.ChannelId == channelId
                && f.Command == HdcCommand.KernelChannelClose
                && f.Payload.SequenceEqual(new byte[] { 0 })),
            TestBudget);
    }

    [Fact]
    public async Task StreamHilog_DaemonReportsEchoFail_ThrowsHdcException()
    {
        using var daemon = new FakeDaemon(new FakeDaemonOptions
        {
            UnityHilogChunks = ["partial\n"u8.ToArray()],
            UnityEchoMessages = [(MessageLevel.Fail, "[E001001]Unknown command")],
            UnityHilogClosesAfterOutput = true,
        });
        await using var host = new HdcHost();
        HdcDevice device = await ConnectAsync(host, daemon);
        List<string> lines = [];

        HdcException ex = await Assert.ThrowsAsync<HdcException>(async () =>
        {
            await foreach (string line in device.StreamHilogAsync())
            {
                lines.Add(line);
            }
        });

        Assert.Equal(["partial"], lines);
        Assert.Contains("[E001001]", ex.Message);
    }

    [Fact]
    public async Task StreamBugReport_CollectsAllChunks()
    {
        byte[] head = "Bug report head\n"u8.ToArray();
        byte[] tail = new byte[] { 0x00, 0xFF, 0x10 };
        using var daemon = FakeDaemon.WithBugReportScript(head, [], tail);
        await using var host = new HdcHost();
        HdcDevice device = await ConnectAsync(host, daemon);
        List<byte[]> chunks = [];

        await foreach (byte[] chunk in device.StreamBugReportAsync())
        {
            chunks.Add(chunk);
        }

        Assert.Equal(2, chunks.Count);
        Assert.Equal(head, chunks[0]);
        Assert.Equal(tail, chunks[1]);
        Frame init = Assert.Single(daemon.ReceivedFrames, f => f.Command == HdcCommand.UnityBugreportInit);
        Assert.Empty(init.Payload);
    }

    [Fact]
    public async Task StreamBugReport_CancellationSendsChannelClose()
    {
        using var daemon = new FakeDaemon(new FakeDaemonOptions
        {
            UnityBugReportChunks = ["chunk-1"u8.ToArray(), "chunk-2"u8.ToArray()],
            UnityBugReportClosesAfterOutput = false,
        });
        await using var host = new HdcHost();
        HdcDevice device = await ConnectAsync(host, daemon);
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (byte[] _ in device.StreamBugReportAsync(cts.Token))
            {
                cts.Cancel();
            }
        });

        uint channelId = Assert.Single(daemon.ReceivedFrames, f => f.Command == HdcCommand.UnityBugreportInit).ChannelId;
        await WaitForAsync(
            () => daemon.ReceivedFrames.Any(f =>
                f.ChannelId == channelId
                && f.Command == HdcCommand.KernelChannelClose
                && f.Payload.SequenceEqual(new byte[] { 0 })),
            TestBudget);
    }

    [Fact]
    public async Task SingleFrameCommand_SilentDaemon_CancellationSendsChannelClose()
    {
        using var daemon = new FakeDaemon(new FakeDaemonOptions { UnityClosesAfterCommand = false });
        await using var host = new HdcHost();
        HdcDevice device = await ConnectAsync(host, daemon);
        using var cts = new CancellationTokenSource();
        Task pending = device.RootRunAsync(ct: cts.Token);
        await WaitForAsync(() => daemon.ReceivedFrames.Any(f => f.Command == HdcCommand.UnityRootrun), TestBudget);

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        uint channelId = Assert.Single(daemon.ReceivedFrames, f => f.Command == HdcCommand.UnityRootrun).ChannelId;
        await WaitForAsync(
            () => daemon.ReceivedFrames.Any(f =>
                f.ChannelId == channelId
                && f.Command == HdcCommand.KernelChannelClose
                && f.Payload.SequenceEqual(new byte[] { 0 })),
            TestBudget);
    }

    [Fact]
    public async Task UnityCommand_ConnectionLost_ThrowsDisconnected()
    {
        using var daemon = new FakeDaemon(new FakeDaemonOptions { UnityClosesAfterCommand = false });
        await using var host = new HdcHost();
        HdcDevice device = await ConnectAsync(host, daemon);
        Task pending = device.RemountAsync();
        await WaitForAsync(() => daemon.ReceivedFrames.Any(f => f.Command == HdcCommand.UnityRemount), TestBudget);

        daemon.Dispose();

        HdcException ex = await Assert.ThrowsAsync<HdcException>(() => WithTimeoutAsync(pending, TestBudget));
        Assert.Contains("连接已断开", ex.Message);
    }

    [Theory]
    [InlineData(DaemonGeneration.Rust)]
    [InlineData(DaemonGeneration.Cpp)]
    public async Task SingleFrameCommands_BothGenerations_AreSupported(DaemonGeneration generation)
    {
        // 上游两世代 daemon 都在同一命令号上实现这五个命令：
        // C++：src/daemon/daemon_unity.cpp:437-491 + daemon.cpp:303-312；
        // Rust：hdc_rust/src/daemon_lib/task.rs:204-215/340-360 + daemon_lib/daemon_unity.rs:239-261。
        using var daemon = new FakeDaemon(new FakeDaemonOptions
        {
            Generation = generation,
            UnityEchoMessages = [(MessageLevel.Ok, "Mount finish")],
        });
        await using var host = new HdcHost();
        HdcDevice device = await ConnectAsync(host, daemon);

        await WithTimeoutAsync(device.RemountAsync(), TestBudget);

        Assert.Equal(generation, device.Generation);
        Assert.Single(daemon.ReceivedFrames, f => f.Command == HdcCommand.UnityRemount);
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
