using System.Security.Cryptography;
using System.Text;
using HdcSharp.Protocol;
using HdcSharp.Protocol.Messages;
using HdcSharp.Security;
using HdcSharp.Tests.TestDoubles;
using HdcSharp.Transport;
using Xunit;

namespace HdcSharp.Tests.Operations;

/// <summary>
/// Task 15a 单文件收发测试：send 走 WAKEUP(12)+FILE_CHECK/FILE_BEGIN/FILE_DATA/FILE_FINISH，
/// recv 走 FILE_INIT(「远端 本地」)+FILE_CHECK/FILE_BEGIN/FILE_DATA/FILE_FINISH。
/// 线缆细节以 D:\work\developtools_hdc 源码为准（transfer.cpp / file.cpp / hdcfile.rs / hdctransfer.rs）。
/// </summary>
public class FileTransferTests : IDisposable
{
    private static readonly TimeSpan TestBudget = TimeSpan.FromSeconds(15);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hdcsharp-file-" + Guid.NewGuid().ToString("N"));
    private readonly string _src;
    private readonly string _dst;

    public FileTransferTests()
    {
        _src = Path.Combine(_root, "src");
        _dst = Path.Combine(_root, "dst");
        Directory.CreateDirectory(_src);
        Directory.CreateDirectory(_dst);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task SendFile_RoundTrip_FakeDaemonReceivesIdenticalBytes()
    {
        // 200KB = 4 整块 + 残余，覆盖多块分片与 index 拼接；BEGIN 回 8 字节 FeatureFlags（C++ 世代形态）
        byte[] payload = RandomNumberGenerator.GetBytes(200_000);
        string source = Path.Combine(_src, "a.bin");
        await File.WriteAllBytesAsync(source, payload);
        using var daemon = new FakeDaemon(new FakeDaemonOptions
        {
            FileRecvSinkDirectory = _dst,
            FileBeginPayload = new byte[8],
        });
        await using var host = new HdcHost();
        HdcDevice device = await ConnectAsync(host, daemon);

        await WithTimeoutAsync(device.SendFileAsync(source, "/data/local/tmp/a.bin"), TestBudget);

        Assert.Equal(payload, await File.ReadAllBytesAsync(Path.Combine(_dst, "a.bin")));
        Frame wakeup = Assert.Single(daemon.ReceivedFrames, f => f.Command == HdcCommand.KernelWakeupSlavetask);
        Assert.Empty(wakeup.Payload);
        Frame checkFrame = Assert.Single(daemon.ReceivedFrames, f => f.Command == HdcCommand.FileCheck);
        TransferConfig config = TransferConfig.Parse(checkFrame.Payload);
        Assert.Equal((ulong)payload.Length, config.FileSize);
        Assert.Equal("/data/local/tmp/a.bin", config.Path);
        Assert.Equal("a.bin", config.OptionalName);
        Assert.Equal("", config.ClientCwd);
        Assert.False(config.UpdateIfNew);
        Assert.False(config.HoldTimestamp);
        Assert.Equal(0, config.CompressType);
        Frame[] dataFrames = daemon.ReceivedFrames
            .Where(f => f.ChannelId == checkFrame.ChannelId && f.Command == HdcCommand.FileData)
            .ToArray();
        Assert.Equal(5, dataFrames.Length);
        for (int i = 0; i < dataFrames.Length; i++)
        {
            TransferPayload head = TransferPayload.ParseSlot(dataFrames[i].Payload.AsSpan(0, HdcConstants.TransferSlotSize));
            Assert.Equal((ulong)(i * HdcConstants.MaxFileChunkSize), head.Index);
            Assert.Equal(0, head.CompressType);
            Assert.Equal(head.CompressSize, head.UncompressSize);
            int size = (int)head.CompressSize;
            Assert.InRange(size, 1, HdcConstants.MaxFileChunkSize);
            Assert.Equal(size, dataFrames[i].Payload.Length - HdcConstants.TransferSlotSize);
            Assert.Equal(
                payload.AsSpan(i * HdcConstants.MaxFileChunkSize, size).ToArray(),
                dataFrames[i].Payload.AsSpan(HdcConstants.TransferSlotSize, size).ToArray());
        }
    }

    [Fact]
    public async Task SendFile_Progress_ReportsMonotonicIncreasing()
    {
        byte[] payload = RandomNumberGenerator.GetBytes(150_000);
        string source = Path.Combine(_src, "progress.bin");
        await File.WriteAllBytesAsync(source, payload);
        using var daemon = FakeDaemon.WithFileRecvSink(_dst);
        await using var host = new HdcHost();
        HdcDevice device = await ConnectAsync(host, daemon);
        List<FileProgress> reports = [];

        await WithTimeoutAsync(
            device.SendFileAsync(source, "/data/local/tmp/progress.bin", new InlineProgress<FileProgress>(reports.Add)),
            TestBudget);

        Assert.Equal(4, reports.Count);
        Assert.Equal(payload.Length, reports[^1].BytesTransferred);
        Assert.Equal(payload.Length, reports[^1].TotalBytes);
        Assert.Equal("progress.bin", reports[^1].FileName);
        for (int i = 0; i < reports.Count; i++)
        {
            Assert.Equal(payload.Length, reports[i].TotalBytes);
            if (i > 0)
            {
                Assert.True(reports[i].BytesTransferred > reports[i - 1].BytesTransferred);
            }
        }
    }

    [Fact]
    public async Task SendFile_EmptyFile_Succeeds()
    {
        string source = Path.Combine(_src, "empty.bin");
        await File.WriteAllBytesAsync(source, []);
        using var daemon = FakeDaemon.WithFileRecvSink(_dst);
        await using var host = new HdcHost();
        HdcDevice device = await ConnectAsync(host, daemon);

        await WithTimeoutAsync(device.SendFileAsync(source, "/data/local/tmp/empty.bin"), TestBudget);

        Assert.Empty(await File.ReadAllBytesAsync(Path.Combine(_dst, "empty.bin")));
        Frame checkFrame = Assert.Single(daemon.ReceivedFrames, f => f.Command == HdcCommand.FileCheck);
        // 空文件仍需一个零长度 DATA 帧作为从端完成信号（对齐上游 ProcressFileIORead 的 0 字节读分支）
        Frame marker = Assert.Single(
            daemon.ReceivedFrames,
            f => f.ChannelId == checkFrame.ChannelId && f.Command == HdcCommand.FileData);
        Assert.Equal(HdcConstants.TransferSlotSize, marker.Payload.Length);
        Assert.Equal(0u, TransferPayload.ParseSlot(marker.Payload).CompressSize);
    }

    [Fact]
    public async Task SendFile_ExactlyOneSlot_MultipleOfChunkSize_Succeeds()
    {
        // 恰为 2 个整块：循环不得多发 0 长度块，index 恰好是 0 与 49152
        byte[] payload = RandomNumberGenerator.GetBytes(2 * HdcConstants.MaxFileChunkSize);
        string source = Path.Combine(_src, "exact.bin");
        await File.WriteAllBytesAsync(source, payload);
        using var daemon = FakeDaemon.WithFileRecvSink(_dst);
        await using var host = new HdcHost();
        HdcDevice device = await ConnectAsync(host, daemon);

        await WithTimeoutAsync(device.SendFileAsync(source, "/data/local/tmp/exact.bin"), TestBudget);

        Assert.Equal(payload, await File.ReadAllBytesAsync(Path.Combine(_dst, "exact.bin")));
        Frame checkFrame = Assert.Single(daemon.ReceivedFrames, f => f.Command == HdcCommand.FileCheck);
        Frame[] dataFrames = daemon.ReceivedFrames
            .Where(f => f.ChannelId == checkFrame.ChannelId && f.Command == HdcCommand.FileData)
            .ToArray();
        Assert.Equal(2, dataFrames.Length);
        Assert.All(dataFrames, f => Assert.Equal(HdcConstants.MaxFileChunkSize, f.Payload.Length - HdcConstants.TransferSlotSize));
        Assert.Equal(0UL, TransferPayload.ParseSlot(dataFrames[0].Payload.AsSpan(0, HdcConstants.TransferSlotSize)).Index);
        Assert.Equal(
            (ulong)HdcConstants.MaxFileChunkSize,
            TransferPayload.ParseSlot(dataFrames[1].Payload.AsSpan(0, HdcConstants.TransferSlotSize)).Index);
    }

    [Fact]
    public async Task SendFile_SourceMissing_ThrowsBeforeSending()
    {
        using var daemon = FakeDaemon.WithFileRecvSink(_dst);
        await using var host = new HdcHost();
        HdcDevice device = await ConnectAsync(host, daemon);

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => device.SendFileAsync(Path.Combine(_src, "missing.bin"), "/data/local/tmp/missing.bin"));

        Assert.DoesNotContain(daemon.ReceivedCommands, c => c.Cmd == HdcCommand.FileCheck);
        Assert.DoesNotContain(daemon.ReceivedCommands, c => c.Cmd == HdcCommand.KernelWakeupSlavetask);
    }

    [Fact]
    public async Task ReceiveFile_FakeDaemonPushes_ContentMatches()
    {
        byte[] payload = RandomNumberGenerator.GetBytes(200_000);
        using var daemon = FakeDaemon.WithFilePush(payload, "pushed.bin");
        await using var host = new HdcHost();
        HdcDevice device = await ConnectAsync(host, daemon);
        string destination = Path.Combine(_dst, "received.bin");

        await WithTimeoutAsync(device.ReceiveFileAsync("/data/local/tmp/pushed.bin", destination), TestBudget);

        Assert.Equal(payload, await File.ReadAllBytesAsync(destination));
        Frame init = Assert.Single(daemon.ReceivedFrames, f => f.Command == HdcCommand.FileInit);
        Assert.Equal("/data/local/tmp/pushed.bin " + destination, Encoding.UTF8.GetString(init.Payload));
        Frame begin = Assert.Single(daemon.ReceivedFrames, f => f.Command == HdcCommand.FileBegin);
        Assert.Empty(begin.Payload);
    }

    [Fact]
    public async Task ReceiveFile_WritesToDisk_AndCreatesMissingDirectory()
    {
        byte[] payload = RandomNumberGenerator.GetBytes(70_000);
        using var daemon = FakeDaemon.WithFilePush(payload, "deep.bin");
        await using var host = new HdcHost();
        HdcDevice device = await ConnectAsync(host, daemon);
        string destination = Path.Combine(_dst, "nested", "deeper", "deep.bin");
        Assert.False(Directory.Exists(Path.GetDirectoryName(destination)));

        await WithTimeoutAsync(device.ReceiveFileAsync("/data/local/tmp/deep.bin", destination), TestBudget);

        Assert.True(Directory.Exists(Path.GetDirectoryName(destination)));
        Assert.Equal(payload, await File.ReadAllBytesAsync(destination));
    }

    [Fact]
    public async Task ReceiveFile_EmptyPush_Succeeds()
    {
        using var daemon = FakeDaemon.WithFilePush([], "empty.push");
        await using var host = new HdcHost();
        HdcDevice device = await ConnectAsync(host, daemon);
        string destination = Path.Combine(_dst, "empty.push");

        await WithTimeoutAsync(device.ReceiveFileAsync("/data/local/tmp/empty.push", destination), TestBudget);

        Assert.Empty(await File.ReadAllBytesAsync(destination));
    }

    [Fact]
    public async Task SendFile_CancellationMidTransfer_SendsChannelCloseAndCleansUp()
    {
        byte[] payload = RandomNumberGenerator.GetBytes(200_000);
        string source = Path.Combine(_src, "cancel.bin");
        await File.WriteAllBytesAsync(source, payload);
        using var daemon = new FakeDaemon(new FakeDaemonOptions
        {
            FileRecvSinkDirectory = _dst,
            FileAcknowledgeFinish = false,
        });
        await using var host = new HdcHost();
        HdcDevice device = await ConnectAsync(host, daemon);
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WithTimeoutAsync(
            device.SendFileAsync(
                source,
                "/data/local/tmp/cancel.bin",
                new InlineProgress<FileProgress>(p =>
                {
                    if (p.BytesTransferred > 0)
                    {
                        cts.Cancel();
                    }
                }),
                cts.Token),
            TestBudget));

        uint channelId = Assert.Single(daemon.ReceivedFrames, f => f.Command == HdcCommand.FileCheck).ChannelId;
        await WaitForAsync(
            () => daemon.ReceivedFrames.Any(f =>
                f.ChannelId == channelId
                && f.Command == HdcCommand.KernelChannelClose
                && f.Payload.SequenceEqual(new byte[] { 0 })),
            TestBudget);
        Assert.Null(device.Dispatcher.Get(channelId));
    }

    [Fact]
    public async Task SendFile_ChannelIdRegistered_NoStrayCloseResponse()
    {
        // 文件通道全程注册：daemon 的 BEGIN/ECHO 不得触发连接层「未注册通道回 CLOSE[0]」；
        // 与并发 shell 各占通道互不串扰
        byte[] payload = RandomNumberGenerator.GetBytes(3 * HdcConstants.MaxFileChunkSize + 17);
        string source = Path.Combine(_src, "isolated.bin");
        await File.WriteAllBytesAsync(source, payload);
        using var daemon = new FakeDaemon(new FakeDaemonOptions
        {
            FileRecvSinkDirectory = _dst,
            EchoShellCommand = true,
        });
        await using var host = new HdcHost();
        HdcDevice device = await ConnectAsync(host, daemon);

        Task<string> shellTask = device.ExecuteShellAsync("concurrent-shell");
        await WithTimeoutAsync(device.SendFileAsync(source, "/data/local/tmp/isolated.bin"), TestBudget);
        string output = await WithTimeoutAsync(shellTask, TestBudget);

        Assert.Equal("concurrent-shell", output);
        Assert.Equal(payload, await File.ReadAllBytesAsync(Path.Combine(_dst, "isolated.bin")));
        Frame checkFrame = Assert.Single(daemon.ReceivedFrames, f => f.Command == HdcCommand.FileCheck);
        Frame[] fileFrames = daemon.ReceivedFrames.Where(f => f.ChannelId == checkFrame.ChannelId).ToArray();
        int finishIndex = Array.FindIndex(fileFrames, f => f.Command == HdcCommand.FileFinish);
        Assert.True(finishIndex >= 0);
        Assert.DoesNotContain(fileFrames.Take(finishIndex), f => f.Command == HdcCommand.KernelChannelClose);
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

    private sealed class InlineProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;

        public InlineProgress(Action<T> handler) => _handler = handler;

        public void Report(T value) => _handler(value);
    }

    private sealed class UnusedKeyStore : IHostKeyStore
    {
        public RSA GetPrivateKey() => throw new InvalidOperationException("测试未启用认证，不应访问密钥库");

        public string GetPublicKeyPem() => throw new InvalidOperationException("测试未启用认证，不应访问密钥库");
    }
}
