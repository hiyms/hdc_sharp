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
/// Task 15b 目录传输测试。线缆语义以上游源码为准：
/// - 主端一次性递归枚举文件（file.cpp:288 SetMasterParameters、transfer.cpp:658-722 GetSubFilesRecursively；
///   Rust hdc_rust/src/common/hdcfile.rs:294-313），optionalName=「源目录名/相对路径」（transfer.cpp:717）；
/// - 每文件走完整 CHECK→BEGIN→DATA 周期，写端（从端）在自身 IO 完成后发 FILE_FINISH[1]
///   （transfer.cpp:291-302、file.cpp:347-353）；主端收 [1] 后推进下一文件（TransferNext file.cpp:572-576、651-653），
///   队列耗尽才回 FILE_FINISH[0]（file.cpp:654-660），从端收 [0] 后 TransferSummary + TaskFinish（file.cpp:662-666）；
/// - WAKEUP_SLAVETASK 整次目录传输只发一次（task.cpp:30-32、hdcfile.rs:558-566）；
/// - daemon 端按 optionalName 逐级建目录（transfer.cpp:801-873 SmartSlavePath/CheckFilename，目标不存在时剥首层）。
/// </summary>
public class DirectoryTransferTests : IDisposable
{
    private static readonly TimeSpan TestBudget = TimeSpan.FromSeconds(15);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hdcsharp-dir-" + Guid.NewGuid().ToString("N"));
    private readonly string _src;
    private readonly string _dst;

    public DirectoryTransferTests()
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
    public async Task SendDirectory_WritesAllFilesWithRelativePaths()
    {
        // 3 个嵌套子目录文件 + 1 个根文件；远端以分隔符结尾 = 已存在目录，daemon 保留「源目录名/相对路径」
        byte[] rootBytes = RandomNumberGenerator.GetBytes(1_000);
        byte[] level1Bytes = RandomNumberGenerator.GetBytes(2_000);
        byte[] level2Bytes = RandomNumberGenerator.GetBytes(3_000);
        byte[] level3Bytes = RandomNumberGenerator.GetBytes(4_000);
        string source = Path.Combine(_src, "mydir");
        WriteFile(source, "a.bin", rootBytes);
        WriteFile(source, "d1/b.bin", level1Bytes);
        WriteFile(source, "d1/d2/c.bin", level2Bytes);
        WriteFile(source, "d1/d2/d3/d.bin", level3Bytes);
        using var daemon = FakeDaemon.WithFileRecvSink(_dst);
        await using var host = new HdcHost();
        HdcDevice device = await ConnectAsync(host, daemon);

        await WithTimeoutAsync(device.SendDirectoryAsync(source, "/data/local/tmp/"), TestBudget);

        Assert.Equal(rootBytes, await File.ReadAllBytesAsync(Path.Combine(_dst, "mydir", "a.bin")));
        Assert.Equal(level1Bytes, await File.ReadAllBytesAsync(Path.Combine(_dst, "mydir", "d1", "b.bin")));
        Assert.Equal(level2Bytes, await File.ReadAllBytesAsync(Path.Combine(_dst, "mydir", "d1", "d2", "c.bin")));
        Assert.Equal(level3Bytes, await File.ReadAllBytesAsync(Path.Combine(_dst, "mydir", "d1", "d2", "d3", "d.bin")));

        Frame[] checks = daemon.ReceivedFrames.Where(f => f.Command == HdcCommand.FileCheck).ToArray();
        Assert.Equal(4, checks.Length);
        string[] expectedNames = ["mydir/a.bin", "mydir/d1/b.bin", "mydir/d1/d2/c.bin", "mydir/d1/d2/d3/d.bin"];
        byte[][] expectedBytes = [rootBytes, level1Bytes, level2Bytes, level3Bytes];
        for (int i = 0; i < checks.Length; i++)
        {
            TransferConfig config = TransferConfig.Parse(checks[i].Payload);
            Assert.Equal("/data/local/tmp/", config.Path);
            Assert.Equal(expectedNames[i], config.OptionalName);
            Assert.Equal((ulong)expectedBytes[i].Length, config.FileSize);
        }

        // 整次目录传输共用一个通道、一次 WAKEUP（task.cpp:30-32）；主端只在最后回一次 [0]，
        // 全程不得自行发 [1]（真机丢尾缺陷回归保护，spec §4.7.1 第 5 条）
        Assert.Single(checks.Select(f => f.ChannelId).Distinct());
        Assert.Single(daemon.ReceivedFrames, f => f.Command == HdcCommand.KernelWakeupSlavetask);
        Assert.Equal(
            1,
            daemon.ReceivedFrames.Count(f => f.Command == HdcCommand.FileFinish && f.Payload.SequenceEqual(new byte[] { 0 })));
        Assert.DoesNotContain(
            daemon.ReceivedFrames, f => f.Command == HdcCommand.FileFinish && f.Payload.SequenceEqual(new byte[] { 1 }));
    }

    [Fact]
    public async Task SendDirectory_Progress_AccumulatesAcrossFiles()
    {
        byte[] first = RandomNumberGenerator.GetBytes(HdcConstants.MaxFileChunkSize + 17);
        byte[] second = RandomNumberGenerator.GetBytes(30_000);
        string source = Path.Combine(_src, "prog");
        WriteFile(source, "a.bin", first);
        WriteFile(source, "nested/b.bin", second);
        using var daemon = FakeDaemon.WithFileRecvSink(_dst);
        await using var host = new HdcHost();
        HdcDevice device = await ConnectAsync(host, daemon);
        List<FileProgress> reports = [];

        await WithTimeoutAsync(
            device.SendDirectoryAsync(source, "/data/local/tmp/", new InlineProgress<FileProgress>(reports.Add)),
            TestBudget);

        long total = first.Length + second.Length;
        Assert.Equal(3, reports.Count);
        Assert.Equal("a.bin", reports[0].FileName);
        Assert.Equal("nested/b.bin", reports[^1].FileName);
        for (int i = 0; i < reports.Count; i++)
        {
            Assert.Equal(total, reports[i].TotalBytes);
            Assert.Equal(total, reports[^1].BytesTransferred);
            if (i > 0)
            {
                Assert.True(reports[i].BytesTransferred > reports[i - 1].BytesTransferred);
            }
        }
    }

    [Fact]
    public async Task SendDirectory_EmptyDirectory_SucceedsWithoutFrames()
    {
        // 协议只承载文件（GetSubFilesRecursively 仅把普通文件入队，transfer.cpp:717）：空目录不产生任何线上帧。
        // 上游 CLI 在同场景直接报错（file.cpp:289-292 "source folder is empty"），本库按其协议事实选择成功直返
        string source = Path.Combine(_src, "empty");
        Directory.CreateDirectory(source);
        using var daemon = FakeDaemon.WithFileRecvSink(_dst);
        await using var host = new HdcHost();
        HdcDevice device = await ConnectAsync(host, daemon);

        await WithTimeoutAsync(device.SendDirectoryAsync(source, "/data/local/tmp/empty"), TestBudget);

        // 除握手收尾的 CHANNEL_CLOSE(channel 0) 外不得有任何文件命令
        Assert.DoesNotContain(daemon.ReceivedCommands, c => c.Cmd == HdcCommand.FileCheck);
        Assert.DoesNotContain(daemon.ReceivedCommands, c => c.Cmd == HdcCommand.KernelWakeupSlavetask);
    }

    [Fact]
    public async Task SendDirectory_SkipsSymlinkDirectory()
    {
        byte[] inner = RandomNumberGenerator.GetBytes(512);
        byte[] rootBytes = RandomNumberGenerator.GetBytes(256);
        string source = Path.Combine(_src, "links");
        WriteFile(source, "real/a.bin", inner);
        WriteFile(source, "root.bin", rootBytes);
        string linkPath = Path.Combine(source, "linkdir");
        try
        {
            Directory.CreateSymbolicLink(linkPath, Path.Combine(source, "real"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Windows 未开启开发者模式且令牌无 SeCreateSymbolicLinkPrivilege 时无法建链：环境不支持则跳过断言
            return;
        }

        // 自检：确保确实建成了符号链接（否则上述 catch 路径的静默跳过会掩盖测试失效）
        Assert.True(File.GetAttributes(linkPath).HasFlag(FileAttributes.ReparsePoint));

        using var daemon = FakeDaemon.WithFileRecvSink(_dst);
        await using var host = new HdcHost();
        HdcDevice device = await ConnectAsync(host, daemon);

        await WithTimeoutAsync(device.SendDirectoryAsync(source, "/data/local/tmp/"), TestBudget);

        // 符号链接被跳过（防目录环与重复内容），只发送两个普通文件
        Assert.Equal(inner, await File.ReadAllBytesAsync(Path.Combine(_dst, "links", "real", "a.bin")));
        Assert.Equal(rootBytes, await File.ReadAllBytesAsync(Path.Combine(_dst, "links", "root.bin")));
        Frame[] checks = daemon.ReceivedFrames.Where(f => f.Command == HdcCommand.FileCheck).ToArray();
        Assert.Equal(2, checks.Length);
        Assert.DoesNotContain(
            checks,
            f => TransferConfig.Parse(f.Payload).OptionalName.Contains("linkdir", StringComparison.Ordinal));
        Assert.False(Directory.Exists(Path.Combine(_dst, "links", "linkdir")));
    }

    [Fact]
    public async Task SendDirectory_EmptySubdirectories_NotPreserved_MatchesUpstream()
    {
        // 上游 GetSubFilesRecursively 只把普通文件入队（transfer.cpp:704-708 目录仅用于递归下降、717 入队），
        // 因此空子目录在协议层不可表达（目录元数据仅 -m 模式的 CMD_DIR_MODE=3007 承载，file.cpp:376-430、
        // define_enum.h:193；spec §4.7.3 一期不实现）。本测试明确记录该事实：空目录不产生任何条目
        byte[] payload = RandomNumberGenerator.GetBytes(300);
        string source = Path.Combine(_src, "e");
        WriteFile(source, "a.bin", payload);
        Directory.CreateDirectory(Path.Combine(source, "emptySub", "deeper"));
        using var daemon = FakeDaemon.WithFileRecvSink(_dst);
        await using var host = new HdcHost();
        HdcDevice device = await ConnectAsync(host, daemon);

        await WithTimeoutAsync(device.SendDirectoryAsync(source, "/data/local/tmp/"), TestBudget);

        Frame check = Assert.Single(daemon.ReceivedFrames, f => f.Command == HdcCommand.FileCheck);
        Assert.Equal("e/a.bin", TransferConfig.Parse(check.Payload).OptionalName);
        Assert.Equal(payload, await File.ReadAllBytesAsync(Path.Combine(_dst, "e", "a.bin")));
        Assert.False(Directory.Exists(Path.Combine(_dst, "e", "emptySub")));
    }

    [Fact]
    public async Task SendDirectory_SingleFileInRoot_WorksLikeSendFile()
    {
        byte[] payload = RandomNumberGenerator.GetBytes(70_000);
        string source = Path.Combine(_src, "one");
        WriteFile(source, "just.bin", payload);
        using var daemon = FakeDaemon.WithFileRecvSink(_dst);
        await using var host = new HdcHost();
        HdcDevice device = await ConnectAsync(host, daemon);

        await WithTimeoutAsync(device.SendDirectoryAsync(source, "/data/local/tmp/"), TestBudget);

        Assert.Equal(payload, await File.ReadAllBytesAsync(Path.Combine(_dst, "one", "just.bin")));
        Frame check = Assert.Single(daemon.ReceivedFrames, f => f.Command == HdcCommand.FileCheck);
        TransferConfig config = TransferConfig.Parse(check.Payload);
        Assert.Equal("one/just.bin", config.OptionalName);
        Assert.Equal((ulong)payload.Length, config.FileSize);
        Assert.Single(daemon.ReceivedFrames, f => f.Command == HdcCommand.KernelWakeupSlavetask);
        Assert.Equal(
            1,
            daemon.ReceivedFrames.Count(f => f.Command == HdcCommand.FileFinish && f.Payload.SequenceEqual(new byte[] { 0 })));
    }

    [Fact]
    public async Task SendDirectory_MissingSource_ThrowsBeforeSending()
    {
        using var daemon = FakeDaemon.WithFileRecvSink(_dst);
        await using var host = new HdcHost();
        HdcDevice device = await ConnectAsync(host, daemon);

        await Assert.ThrowsAsync<DirectoryNotFoundException>(
            () => device.SendDirectoryAsync(Path.Combine(_src, "missing"), "/data/local/tmp/x"));

        Assert.DoesNotContain(daemon.ReceivedCommands, c => c.Cmd == HdcCommand.FileCheck);
        Assert.DoesNotContain(daemon.ReceivedCommands, c => c.Cmd == HdcCommand.KernelWakeupSlavetask);
    }

    [Fact]
    public async Task SendDirectory_CancellationMidDirectory_ClosesChannelCleanly()
    {
        byte[] first = RandomNumberGenerator.GetBytes(200_000);
        string source = Path.Combine(_src, "cancel");
        WriteFile(source, "a.bin", first);
        WriteFile(source, "s/b.bin", RandomNumberGenerator.GetBytes(200_000));
        using var daemon = FakeDaemon.WithFileRecvSink(_dst);
        await using var host = new HdcHost();
        HdcDevice device = await ConnectAsync(host, daemon);
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WithTimeoutAsync(
            device.SendDirectoryAsync(
                source,
                "/data/local/tmp/",
                new InlineProgress<FileProgress>(p =>
                {
                    if (p.BytesTransferred > 100_000)
                    {
                        cts.Cancel();
                    }
                }),
                cts.Token),
            TestBudget));

        // 取消发生在第一文件数据中途：第二个文件的 CHECK 从未发出，通道以 CHANNEL_CLOSE[0] 清理
        Frame[] checks = daemon.ReceivedFrames.Where(f => f.Command == HdcCommand.FileCheck).ToArray();
        Assert.All(checks, f => Assert.Equal("cancel/a.bin", TransferConfig.Parse(f.Payload).OptionalName));
        uint channelId = Assert.Single(checks).ChannelId;
        await WaitForAsync(
            () => daemon.ReceivedFrames.Any(f =>
                f.ChannelId == channelId
                && f.Command == HdcCommand.KernelChannelClose
                && f.Payload.SequenceEqual(new byte[] { 0 })),
            TestBudget);
        Assert.Null(device.Dispatcher.Get(channelId));
    }

    [Fact]
    public async Task ReceiveDirectory_RecreatesNestedLayout()
    {
        byte[] a = RandomNumberGenerator.GetBytes(5_000);
        byte[] b = RandomNumberGenerator.GetBytes(6_000);
        byte[] c = RandomNumberGenerator.GetBytes(7_000);
        using var daemon = FakeDaemon.WithFilePushDirectory(
            ("top/a.bin", a),
            ("top/l1/b.bin", b),
            ("top/l1/l2/c.bin", c));
        await using var host = new HdcHost();
        HdcDevice device = await ConnectAsync(host, daemon);
        string destination = Path.Combine(_dst, "incoming");
        Directory.CreateDirectory(destination);

        await WithTimeoutAsync(device.ReceiveDirectoryAsync("/data/local/tmp/top", destination), TestBudget);

        // 本地目标已存在：保留 daemon 给出的顶层目录名（上游 SmartSlavePath 追加 optionalName，transfer.cpp:857-873）
        Assert.Equal(a, await File.ReadAllBytesAsync(Path.Combine(destination, "top", "a.bin")));
        Assert.Equal(b, await File.ReadAllBytesAsync(Path.Combine(destination, "top", "l1", "b.bin")));
        Assert.Equal(c, await File.ReadAllBytesAsync(Path.Combine(destination, "top", "l1", "l2", "c.bin")));
        Frame init = Assert.Single(daemon.ReceivedFrames, f => f.Command == HdcCommand.FileInit);
        Assert.Equal("/data/local/tmp/top " + destination, Encoding.UTF8.GetString(init.Payload));
        // 每文件一个 BEGIN；写端（host）每文件 IO 完成后发 [1]（transfer.cpp:291-302）
        Assert.Equal(3, daemon.ReceivedFrames.Count(f => f.Command == HdcCommand.FileBegin));
        Assert.Equal(
            3,
            daemon.ReceivedFrames.Count(f => f.Command == HdcCommand.FileFinish && f.Payload.SequenceEqual(new byte[] { 1 })));
    }

    [Fact]
    public async Task ReceiveDirectory_MissingTarget_StripsTopDirectoryName()
    {
        byte[] a = RandomNumberGenerator.GetBytes(4_000);
        byte[] b = RandomNumberGenerator.GetBytes(5_000);
        using var daemon = FakeDaemon.WithFilePushDirectory(("top/a.bin", a), ("top/l1/b.bin", b));
        await using var host = new HdcHost();
        HdcDevice device = await ConnectAsync(host, daemon);
        string destination = Path.Combine(_dst, "fresh");
        Assert.False(Directory.Exists(destination));

        await WithTimeoutAsync(device.ReceiveDirectoryAsync("/data/local/tmp/top", destination), TestBudget);

        // 目标不存在：上游记为 targetDirNotExist 并剥掉 optionalName 首层（transfer.cpp:768-780、801-812），
        // 即本地路径充当「重命名后的顶层目录」
        Assert.Equal(a, await File.ReadAllBytesAsync(Path.Combine(destination, "a.bin")));
        Assert.Equal(b, await File.ReadAllBytesAsync(Path.Combine(destination, "l1", "b.bin")));
    }

    private static void WriteFile(string root, string relative, byte[] content)
    {
        string path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
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
