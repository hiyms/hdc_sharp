using System.Security.Cryptography;
using System.Text;
using HdcSharp.Protocol;
using HdcSharp.Protocol.Messages;
using HdcSharp.Protocol.Tar;
using HdcSharp.Security;
using HdcSharp.Tests.TestDoubles;
using Xunit;

namespace HdcSharp.Tests.Operations;

/// <summary>
/// Task 16 应用安装/卸载测试。线缆细节以上游源码为准（D:\work\developtools_hdc）：
/// - 安装：H→D WAKEUP_SLAVETASK(12) 预置从端任务槽（daemon 侧无槽会丢弃 APP_CHECK，session.cpp:1644-1663、1726）
///   → APP_CHECK(3501)=TransferConfig{functionName="install", options, optionalName=随机名+扩展名, fileSize}
///   （host_app.cpp:94-158）→ D→H APP_BEGIN(3502) → H→D APP_DATA(3503)（64 字节槽 + ≤48KiB，index=绝对偏移）
///   → D→H APP_FINISH(3504) 载荷 [mode u8][1=成功 u8][bm 输出文本]（daemon_app.cpp:137-166、host_app.rs:169-215）；
/// - **APP 没有 FINISH 握手回合**：从端收完数据立即异步跑 bm install，完成才用 APP_FINISH 回结果；
/// - 卸载：H→D APP_UNINSTALL(3505) 单帧（C++/Rust daemon 均以该命令自建任务，session.cpp:1638-1647、
///   daemon_app.rs:389-398），host 不发 WAKEUP；
/// - **CMD_APP_INIT(3500) 永不上设备线**：它是 client→本机 host agent 的命令串，被 host 本地消化
///   （server_for_client.cpp:1089-1097 "install " 前缀走 local do、host_app.cpp:220-223）；
/// - 安装目录由 host 先打 tar（host_app.cpp:32-54 Dir2Tar），optionalName 扩展名 .tar（host_app.cpp:148-150）。
/// </summary>
public class AppInstallTests : IDisposable
{
    private static readonly TimeSpan TestBudget = TimeSpan.FromSeconds(15);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hdcsharp-app-" + Guid.NewGuid().ToString("N"));
    private readonly string _src;
    private readonly string _sink;

    public AppInstallTests()
    {
        _src = Path.Combine(_root, "src");
        _sink = Path.Combine(_root, "sink");
        Directory.CreateDirectory(_src);
        Directory.CreateDirectory(_sink);
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
    public async Task Install_HapFile_SendsInitCheckDataFinishSequence()
    {
        byte[] payload = RandomNumberGenerator.GetBytes(200_000);
        string hap = Path.Combine(_src, "demo.hap");
        await File.WriteAllBytesAsync(hap, payload);
        using var daemon = FakeDaemon.WithInstallScript(sinkDirectory: _sink, output: "install success");
        await using var host = new HdcHost();
        HdcDevice device = await ConnectAsync(host, daemon);

        string output = await WithTimeoutAsync(device.InstallAsync(hap), TestBudget);

        Assert.Equal("install success", output);
        // 终结帧 CHANNEL_CLOSE 的到达是异步的（daemon 收到 CLOSE[0] 不回执，daemon.cpp:1251-1257），须等其被记录
        await WaitForAsync(
            () => daemon.ReceivedFrames.Any(f => f.Command == HdcCommand.KernelChannelClose), TestBudget);
        Frame[] appFrames = daemon.ReceivedFrames.Where(f => f.ChannelId != 0).ToArray();
        Assert.Equal(HdcCommand.KernelWakeupSlavetask, appFrames[0].Command);
        Assert.Empty(appFrames[0].Payload);
        Assert.Equal(HdcCommand.AppCheck, appFrames[1].Command);
        Assert.Equal(HdcCommand.KernelChannelClose, appFrames[^1].Command);
        // APP_INIT 只是 client→本机 host agent 的命令串，不得上线；文件命令也不得混入应用通道
        Assert.DoesNotContain(daemon.ReceivedFrames, f => f.Command == HdcCommand.AppInit);
        Assert.DoesNotContain(appFrames, f => f.Command is HdcCommand.FileCheck or HdcCommand.FileData);

        TransferConfig config = TransferConfig.Parse(appFrames[1].Payload);
        Assert.Equal("install", config.FunctionName);
        Assert.Equal("-r", config.Options);
        Assert.Equal((ulong)payload.Length, config.FileSize);
        Assert.Equal("", config.Path);
        Assert.Equal("", config.ClientCwd);
        Assert.Equal(0, config.CompressType);
        Assert.False(config.HoldTimestamp);
        Assert.False(config.UpdateIfNew);
        Assert.Matches(@"^\d{9}\.hap$", config.OptionalName);

        Frame[] dataFrames = appFrames.Where(f => f.Command == HdcCommand.AppData).ToArray();
        Assert.Equal(5, dataFrames.Length);
        Assert.Equal(payload, await File.ReadAllBytesAsync(Path.Combine(_sink, config.OptionalName)));
    }

    [Fact]
    public async Task Install_ReplaceOption_SetsExpectedConfig()
    {
        string hap = Path.Combine(_src, "demo.hap");
        await File.WriteAllBytesAsync(hap, RandomNumberGenerator.GetBytes(64));
        using var daemon = FakeDaemon.WithInstallScript(sinkDirectory: _sink);
        await using var host = new HdcHost();
        HdcDevice device = await ConnectAsync(host, daemon);

        await WithTimeoutAsync(device.InstallAsync(hap), TestBudget);
        await WithTimeoutAsync(device.InstallAsync(hap, new InstallOptions { Replace = false }), TestBudget);
        await WithTimeoutAsync(
            device.InstallAsync(
                hap,
                new InstallOptions { Replace = false, Downgrade = true, Shared = true, GrantPermissions = true }),
            TestBudget);

        string[] options = daemon.ReceivedFrames
            .Where(f => f.Command == HdcCommand.AppCheck)
            .Select(f => TransferConfig.Parse(f.Payload).Options)
            .ToArray();
        Assert.Equal(["-r", "", "-d -s -g"], options);
    }

    [Fact]
    public async Task Install_BmOutput_ReturnedAsString()
    {
        string message = "Success: install bundle com.example.demo version 1.0.0 (code 100)";
        string hap = Path.Combine(_src, "demo.hap");
        await File.WriteAllBytesAsync(hap, RandomNumberGenerator.GetBytes(64));
        using var daemon = FakeDaemon.WithInstallScript(sinkDirectory: _sink, output: message);
        await using var host = new HdcHost();
        HdcDevice device = await ConnectAsync(host, daemon);

        string output = await WithTimeoutAsync(device.InstallAsync(hap), TestBudget);

        // APP_FINISH 载荷前两字节为 mode/success，文本自偏移 2 起原样回传
        Assert.Equal(message, output);
    }

    [Fact]
    public async Task Install_Failure_ThrowsHdcException()
    {
        string hap = Path.Combine(_src, "demo.hap");
        await File.WriteAllBytesAsync(hap, RandomNumberGenerator.GetBytes(64));
        using var daemon = FakeDaemon.WithInstallScript(
            success: false, output: "[E006001] Not any installation package was found", sinkDirectory: _sink);
        await using var host = new HdcHost();
        HdcDevice device = await ConnectAsync(host, daemon);

        HdcException error = await Assert.ThrowsAsync<HdcException>(
            () => WithTimeoutAsync(device.InstallAsync(hap), TestBudget));

        Assert.Contains("Not any installation package was found", error.Message);
        Assert.Equal("[E006001]", error.ErrorCode);
    }

    [Fact]
    public async Task Install_BeginNeverArrives_FailsWithDaemonMessage()
    {
        // Rust daemon 建临时文件失败时只回 APP_FINISH（不先回 APP_BEGIN，daemon_app.rs:385-392）
        string hap = Path.Combine(_src, "demo.hap");
        await File.WriteAllBytesAsync(hap, RandomNumberGenerator.GetBytes(64));
        using var daemon = new FakeDaemon(new FakeDaemonOptions
        {
            AppSinkDirectory = _sink,
            AppSendBegin = false,
            AppFinishSuccess = 0,
            AppFinishMessage = "check file fail.",
        });
        await using var host = new HdcHost();
        HdcDevice device = await ConnectAsync(host, daemon);

        HdcException error = await Assert.ThrowsAsync<HdcException>(
            () => WithTimeoutAsync(device.InstallAsync(hap), TestBudget));

        Assert.Contains("check file fail.", error.Message);
        Assert.DoesNotContain(daemon.ReceivedFrames, f => f.Command == HdcCommand.AppData);
    }

    [Fact]
    public async Task Uninstall_SendsPackageName_Succeeds()
    {
        using var daemon = FakeDaemon.WithUninstallScript(output: "uninstall success");
        await using var host = new HdcHost();
        HdcDevice device = await ConnectAsync(host, daemon);

        string output = await WithTimeoutAsync(device.UninstallAsync("com.example.demo"), TestBudget);

        Assert.Equal("uninstall success", output);
        Frame request = Assert.Single(daemon.ReceivedFrames, f => f.Command == HdcCommand.AppUninstall);
        Assert.Equal("com.example.demo", Encoding.UTF8.GetString(request.Payload));
        // 终结帧 CHANNEL_CLOSE 的到达是异步的（daemon 收到 CLOSE[0] 不回执，daemon.cpp:1251-1257），须等其被记录
        await WaitForAsync(
            () => daemon.ReceivedFrames.Any(f => f.Command == HdcCommand.KernelChannelClose), TestBudget);
        Assert.Equal(HdcCommand.KernelChannelClose, daemon.ReceivedFrames[^1].Command);
        // 上游 host 对卸载不发 WAKEUP/APP_CHECK/APP_INIT，也不传包数据
        Assert.DoesNotContain(
            daemon.ReceivedFrames,
            f => f.Command is HdcCommand.KernelWakeupSlavetask or HdcCommand.AppInit
                or HdcCommand.AppCheck or HdcCommand.AppData);
    }

    [Fact]
    public async Task Uninstall_KeepDataAndShared_SetsOptions()
    {
        using var daemon = FakeDaemon.WithUninstallScript();
        await using var host = new HdcHost();
        HdcDevice device = await ConnectAsync(host, daemon);

        await WithTimeoutAsync(
            device.UninstallAsync("com.example.demo", new UninstallOptions { KeepData = true, Shared = true }),
            TestBudget);

        Frame request = Assert.Single(daemon.ReceivedFrames, f => f.Command == HdcCommand.AppUninstall);
        // 设备端把选项原样拼进 bm uninstall 命令行（daemon_app.cpp:177-186、daemon_app.rs:232-241）
        Assert.Equal("-k -s com.example.demo", Encoding.UTF8.GetString(request.Payload));
    }

    [Fact]
    public async Task Uninstall_Failure_ThrowsHdcException()
    {
        using var daemon = FakeDaemon.WithUninstallScript(success: false, output: "[E003001] bundle not found");
        await using var host = new HdcHost();
        HdcDevice device = await ConnectAsync(host, daemon);

        HdcException error = await Assert.ThrowsAsync<HdcException>(
            () => WithTimeoutAsync(device.UninstallAsync("com.example.demo"), TestBudget));

        Assert.Contains("bundle not found", error.Message);
        Assert.Equal("[E003001]", error.ErrorCode);
    }

    [Fact]
    public async Task InstallDirectory_PacksUstarTar_EntriesMatchFiles()
    {
        byte[] first = RandomNumberGenerator.GetBytes(1_000);
        byte[] second = RandomNumberGenerator.GetBytes(700);
        string directory = Path.Combine(_src, "packages");
        Directory.CreateDirectory(Path.Combine(directory, "sub"));
        await File.WriteAllBytesAsync(Path.Combine(directory, "a.hap"), first);
        await File.WriteAllBytesAsync(Path.Combine(directory, "sub", "b.hsp"), second);
        using var daemon = FakeDaemon.WithInstallScript(sinkDirectory: _sink);
        await using var host = new HdcHost();
        HdcDevice device = await ConnectAsync(host, daemon);

        string output = await WithTimeoutAsync(device.InstallAsync(directory), TestBudget);

        Assert.Equal("Success", output);
        Frame check = Assert.Single(daemon.ReceivedFrames, f => f.Command == HdcCommand.AppCheck);
        TransferConfig config = TransferConfig.Parse(check.Payload);
        Assert.Equal("install", config.FunctionName);
        Assert.Matches(@"^\d{9}\.tar$", config.OptionalName);
        // 官方 host 打包器只写条目、不写结尾的两个全零块（compress.cpp:107-114、compress.rs:97-114），
        // 上游 daemon 解包循环把每个 512 字节块都当条目头，遇全零块即判非法并中止（decompress.cpp:36-50）
        long paddedFirst = 512 + RoundUp(first.Length);
        long paddedSecond = 512 + RoundUp(second.Length);
        Assert.Equal((ulong)(paddedFirst + 512 + paddedSecond), config.FileSize);

        string tarPath = Path.Combine(_sink, config.OptionalName);
        Assert.Equal((ulong)new FileInfo(tarPath).Length, config.FileSize);
        byte[] tarBytes = await File.ReadAllBytesAsync(tarPath);
        AssertUstarHeaders(tarBytes);

        using FileStream stream = File.OpenRead(tarPath);
        var reader = new TarReader(stream);
        List<(string Name, TarEntryType Type, byte[] Content)> entries = [];
        while (reader.TryReadEntry(out TarHeaderInfo info))
        {
            byte[] content = new byte[info.Size];
            int total = 0;
            while (total < content.Length)
            {
                int read = reader.ReadContent(content.AsSpan(total));
                Assert.True(read > 0, "tar 条目负载被截断");
                total += read;
            }

            entries.Add((info.Name, info.Type, content));
        }

        Assert.Equal(["a.hap", "sub", "sub/b.hsp"], entries.Select(e => e.Name));
        Assert.Equal(TarEntryType.NormalFile, entries[0].Type);
        Assert.Equal(TarEntryType.Directory, entries[1].Type);
        Assert.Empty(entries[1].Content);
        Assert.Equal(first, entries[0].Content);
        Assert.Equal(second, entries[2].Content);
    }

    [Fact]
    public async Task Install_PackageMissing_ThrowsBeforeSendingFrames()
    {
        using var daemon = FakeDaemon.WithInstallScript(sinkDirectory: _sink);
        await using var host = new HdcHost();
        HdcDevice device = await ConnectAsync(host, daemon);

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => device.InstallAsync(Path.Combine(_src, "missing.hap")));

        Assert.DoesNotContain(daemon.ReceivedFrames, f => f.ChannelId != 0);
    }

    [Fact]
    public async Task Install_EmptyDirectory_ThrowsBeforeSendingFrames()
    {
        string empty = Path.Combine(_src, "empty-packages");
        Directory.CreateDirectory(empty);
        using var daemon = FakeDaemon.WithInstallScript(sinkDirectory: _sink);
        await using var host = new HdcHost();
        HdcDevice device = await ConnectAsync(host, daemon);

        await Assert.ThrowsAsync<ArgumentException>(() => device.InstallAsync(empty));

        Assert.DoesNotContain(daemon.ReceivedFrames, f => f.ChannelId != 0);
    }

    [Fact]
    public async Task Install_CancellationMidTransfer_SendsChannelCloseAndCleansUp()
    {
        byte[] payload = RandomNumberGenerator.GetBytes(4 * HdcConstants.MaxFileChunkSize);
        string hap = Path.Combine(_src, "cancel.hap");
        await File.WriteAllBytesAsync(hap, payload);
        using var daemon = new FakeDaemon(new FakeDaemonOptions
        {
            AppSinkDirectory = _sink,
            // 每帧延迟落盘，制造确定性的「传输中」窗口（模拟慢设备）
            AppDataDelayMs = 30,
        });
        await using var host = new HdcHost();
        HdcDevice device = await ConnectAsync(host, daemon);
        using var cts = new CancellationTokenSource();

        Task<string> install = device.InstallAsync(hap, null, cts.Token);
        await WaitForAsync(() => daemon.ReceivedFrames.Any(f => f.Command == HdcCommand.AppData), TestBudget);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => WithTimeoutAsync(install, TestBudget));

        uint channelId = Assert.Single(
            daemon.ReceivedFrames, f => f.Command == HdcCommand.AppCheck).ChannelId;
        await WaitForAsync(
            () => daemon.ReceivedFrames.Any(f =>
                f.ChannelId == channelId
                && f.Command == HdcCommand.KernelChannelClose
                && f.Payload.SequenceEqual(new byte[] { 0 })),
            TestBudget);
        Assert.Null(device.Dispatcher.Get(channelId));
    }

    [Fact]
    public async Task Install_LargeHap_MultipleDataChunks_IndexIsAbsoluteOffset()
    {
        byte[] payload = RandomNumberGenerator.GetBytes(3 * HdcConstants.MaxFileChunkSize + 17);
        string hap = Path.Combine(_src, "large.hap");
        await File.WriteAllBytesAsync(hap, payload);
        using var daemon = FakeDaemon.WithInstallScript(sinkDirectory: _sink);
        await using var host = new HdcHost();
        HdcDevice device = await ConnectAsync(host, daemon);

        await WithTimeoutAsync(device.InstallAsync(hap), TestBudget);

        Frame check = Assert.Single(daemon.ReceivedFrames, f => f.Command == HdcCommand.AppCheck);
        TransferConfig config = TransferConfig.Parse(check.Payload);
        Frame[] dataFrames = daemon.ReceivedFrames
            .Where(f => f.ChannelId == check.ChannelId && f.Command == HdcCommand.AppData)
            .ToArray();
        Assert.Equal(4, dataFrames.Length);
        for (int i = 0; i < dataFrames.Length; i++)
        {
            TransferPayload head = TransferPayload.ParseSlot(
                dataFrames[i].Payload.AsSpan(0, HdcConstants.TransferSlotSize));
            Assert.Equal((ulong)(i * HdcConstants.MaxFileChunkSize), head.Index);
            Assert.Equal(0, head.CompressType);
            Assert.Equal(head.CompressSize, head.UncompressSize);
            int expected = Math.Min(HdcConstants.MaxFileChunkSize, payload.Length - i * HdcConstants.MaxFileChunkSize);
            Assert.Equal((uint)expected, head.CompressSize);
        }

        Assert.Equal(payload, await File.ReadAllBytesAsync(Path.Combine(_sink, config.OptionalName)));
    }

    private static long RoundUp(int length)
    {
        return (length + TarHeader.BlockSize - 1) / TarHeader.BlockSize * TarHeader.BlockSize;
    }

    private static void AssertUstarHeaders(byte[] tarBytes)
    {
        Assert.Equal(0, tarBytes.Length % TarHeader.BlockSize);
        for (int offset = 0; offset + TarHeader.BlockSize <= tarBytes.Length; offset += TarHeader.BlockSize)
        {
            ReadOnlySpan<byte> block = tarBytes.AsSpan(offset, TarHeader.BlockSize);
            if (TarHeader.IsZeroBlock(block))
            {
                Assert.Fail($"tar 第 {offset / TarHeader.BlockSize} 块为全零结束块：上游 daemon 解包器不接受（decompress.cpp:36-50）");
            }

            Assert.Equal("ustar ", Encoding.ASCII.GetString(block.Slice(257, 6)));
            int sum = 0;
            for (int i = 0; i < TarHeader.BlockSize; i++)
            {
                if (i is < 148 or >= 156)
                {
                    sum += block[i];
                }
            }

            string checksum = Encoding.ASCII.GetString(block.Slice(148, 8).ToArray()).TrimEnd('\0', ' ');
            Assert.Equal(Convert.ToString(sum + 256, 8).PadLeft(6, '0'), checksum);
            // 负载按 512 对齐补齐（上游 entry.cpp:237-241 写 tar 时补零；本测试 236 行的 FileSize 亦按 RoundUp 累加），
            // 故下一头块位于 512 + RoundUp(size)
            offset += (int)RoundUp((int)TarHeader.Parse(block).Size);
        }
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

    private sealed class UnusedKeyStore : IHostKeyStore
    {
        public System.Security.Cryptography.RSA GetPrivateKey() =>
            throw new InvalidOperationException("测试未启用认证，不应访问密钥库");

        public string GetPublicKeyPem() =>
            throw new InvalidOperationException("测试未启用认证，不应访问密钥库");
    }
}
