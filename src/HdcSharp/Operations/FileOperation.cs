using System.Buffers;
using System.Text;
using HdcSharp.Protocol;
using HdcSharp.Protocol.Messages;
using HdcSharp.Transport;

namespace HdcSharp.Operations;

/// <summary>
/// 文件收发实现（Task 15a 单文件 / 15b 目录）。
/// 线缆流程对齐上游主从语义（src/common/file.cpp、src/common/transfer.cpp、hdc_rust/src/common/hdcfile.rs）：
/// send：H→D WAKEUP_SLAVETASK(12) 预置从端任务（整次传输仅一次）→ 逐文件 FILE_CHECK(TransferConfig) →
/// D→H FILE_BEGIN → H→D FILE_DATA(64 字节槽 + ≤48KiB 数据，index=文件绝对偏移)；
/// recv：H→D FILE_INIT(「远端路径 本地路径」参数串，无首词) → D→H WAKEUP_SLAVETASK/FILE_CHECK(TransferConfig) →
/// H→D FILE_BEGIN(空载荷) → D→H FILE_DATA。
/// 仅写端在自身 IO 完成后发 FILE_FINISH[1]；主端收 [1] 后推进下一文件（FILE_CHECK，不发 [0]）或队列耗尽回 [0]，
/// 从端（写端）收 [0] 即整次传输完成并回 ECHO + CHANNEL_CLOSE（file.cpp:645-668、transfer.cpp:291-302）。
/// 目录模式：主端一次性递归枚举，optionalName=「源目录名/相对路径」（'/' 分隔），daemon 自动逐级建目录。
/// </summary>
internal static class FileOperation
{
    private static readonly byte[] FinishOneFilePayload = [HdcConstants.FileFinishOneFile];

    private static readonly byte[] FinishAllPayload = [HdcConstants.FileFinishAll];

    /// <summary>待发送文件条目：本地路径、optionalName（目录模式含源目录名）、进度显示名（相对源目录）、字节数。</summary>
    private readonly record struct SendEntry(string LocalPath, string OptionalName, string DisplayName, long Size);

    /// <summary>等待单文件收尾帧的结果。</summary>
    private enum FileOutcome
    {
        /// <summary>收到写端 FILE_FINISH[1]：本文件完成，由调用方决定推进或收尾。</summary>
        SlaveFinished,

        /// <summary>收到 FILE_FINISH[0] 或对端关闭：整次传输结束。</summary>
        TransferEnded,
    }

    /// <summary>接收会话的跨文件状态。</summary>
    private sealed class ReceiveState
    {
        /// <summary>本地目标根在首个 FILE_CHECK 时是否已存在（决定是否保留 daemon 给出的顶层目录名）。</summary>
        public bool? RootExisted { get; set; }

        /// <summary>已接收的累计字节数。</summary>
        public long Transferred { get; set; }

        /// <summary>已发现文件的声明大小累计（目录接收前无法预知总大小）。</summary>
        public long KnownTotal { get; set; }
    }

    /// <summary>发送单个本地文件到设备（host 为主端）。</summary>
    /// <param name="device">目标设备。</param>
    /// <param name="localPath">本地源文件路径；不存在或不可读在发出任何文件命令前抛出。</param>
    /// <param name="remotePath">设备端目标路径；若为已存在目录，daemon 会拼接本地文件名。</param>
    /// <param name="progress">进度回调，每发送一个数据块调用一次。</param>
    /// <param name="ct">取消令牌；取消或失败时发 CHANNEL_CLOSE[0] 并清理通道。</param>
    internal static Task SendAsync(
        HdcDevice device, string localPath, string remotePath, IProgress<FileProgress>? progress, CancellationToken ct)
    {
        if (Directory.Exists(localPath))
        {
            throw new ArgumentException($"源路径是目录，请使用 SendDirectoryAsync：{localPath}", nameof(localPath));
        }

        var info = new FileInfo(localPath);
        if (!info.Exists)
        {
            throw new FileNotFoundException($"源文件不存在：{localPath}", localPath);
        }

        string fileName = Path.GetFileName(localPath);
        return SendFilesAsync(device, [new SendEntry(localPath, fileName, fileName, info.Length)], remotePath, progress, ct);
    }

    /// <summary>递归发送本地目录到设备（host 为主端）：逐文件沿单文件流程推进，空目录不产生线上帧。</summary>
    /// <param name="device">目标设备。</param>
    /// <param name="localDirectory">本地源目录；不存在时抛出。</param>
    /// <param name="remotePath">设备端目标路径（daemon 按 optionalName 拼接）。</param>
    /// <param name="progress">进度回调，每发送一个数据块调用一次（BytesTransferred 跨文件累计）。</param>
    /// <param name="ct">取消令牌；取消或失败时发 CHANNEL_CLOSE[0] 并清理通道。</param>
    internal static Task SendDirectoryAsync(
        HdcDevice device, string localDirectory, string remotePath, IProgress<FileProgress>? progress, CancellationToken ct)
    {
        if (!Directory.Exists(localDirectory))
        {
            throw new DirectoryNotFoundException($"源目录不存在：{localDirectory}");
        }

        List<SendEntry> entries = EnumerateDirectory(localDirectory);
        return entries.Count == 0
            ? Task.CompletedTask
            : SendFilesAsync(device, entries, remotePath, progress, ct);
    }

    /// <summary>从设备接收单个文件（host 为从端）。</summary>
    /// <param name="device">目标设备。</param>
    /// <param name="remotePath">设备端源文件路径（daemon 作为主端读取）。</param>
    /// <param name="localPath">本地目标路径；目录不存在时逐级创建，已存在文件被截断。</param>
    /// <param name="progress">进度回调，每收到一个数据块调用一次。</param>
    /// <param name="ct">取消令牌；取消或失败时发 CHANNEL_CLOSE[0] 并清理通道。</param>
    internal static Task ReceiveAsync(
        HdcDevice device, string remotePath, string localPath, IProgress<FileProgress>? progress, CancellationToken ct)
    {
        return ReceiveFilesAsync(device, remotePath, localPath, progress, ct);
    }

    /// <summary>从设备接收目录（host 为从端）：daemon 逐文件推送 FILE_CHECK，本库按 optionalName 重建目录树。</summary>
    /// <param name="device">目标设备。</param>
    /// <param name="remoteDirectory">设备端源目录（daemon 作为主端递归枚举）。</param>
    /// <param name="localPath">本地目标根；已存在目录时保留 daemon 顶层目录名，不存在时以自身为「重命名后的顶层目录」。</param>
    /// <param name="progress">进度回调，每收到一个数据块调用一次（BytesTransferred 跨文件累计）。</param>
    /// <param name="ct">取消令牌；取消或失败时发 CHANNEL_CLOSE[0] 并清理通道。</param>
    internal static Task ReceiveDirectoryAsync(
        HdcDevice device, string remoteDirectory, string localPath, IProgress<FileProgress>? progress, CancellationToken ct)
    {
        return ReceiveFilesAsync(device, remoteDirectory, localPath, progress, ct);
    }

    /// <summary>主端发送流程：一次 WAKEUP + 逐文件 CHECK/BEGIN/DATA，收 [1] 推进或回 [0] 收尾。</summary>
    private static async Task SendFilesAsync(
        HdcDevice device, IReadOnlyList<SendEntry> files, string remotePath, IProgress<FileProgress>? progress, CancellationToken ct)
    {
        long totalBytes = 0;
        foreach (SendEntry file in files)
        {
            totalBytes += file.Size;
        }

        uint channelId = device.NewChannelId();
        device.Connection.RegisterChannel(channelId);
        ChannelContext context = device.Dispatcher.Open(channelId);
        List<string> errors = [];
        try
        {
            await device.Connection
                .SendAsync(channelId, HdcCommand.KernelWakeupSlavetask, ReadOnlyMemory<byte>.Empty, ct)
                .ConfigureAwait(false);
            long transferred = 0;
            for (int index = 0; index < files.Count; index++)
            {
                SendEntry file = files[index];
                await using FileStream source = new(
                    file.LocalPath,
                    System.IO.FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    1,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                var config = new TransferConfig
                {
                    FileSize = (ulong)source.Length,
                    Path = remotePath,
                    OptionalName = file.OptionalName,
                };
                await device.Connection
                    .SendAsync(channelId, HdcCommand.FileCheck, config.Serialize(), ct)
                    .ConfigureAwait(false);
                await AwaitBeginAsync(device, context, errors, ct).ConfigureAwait(false);
                transferred = await SendDataAsync(
                        device, channelId, source, file.DisplayName, transferred, totalBytes, progress, ct)
                    .ConfigureAwait(false);
                // 写端（从端）在自身 IO 完成后才会发 FILE_FINISH[1]；主端收 [1] 后推进下一文件或收尾
                // （file.cpp:645-668）。主端全程不得自行发 [1]——真机 500KB 丢尾缺陷的根因（spec §4.7.1 第 5 条）
                FileOutcome outcome = await AwaitOneFileFinishAsync(device, context, errors, ct).ConfigureAwait(false);
                if (outcome == FileOutcome.TransferEnded)
                {
                    break;
                }

                if (index < files.Count - 1)
                {
                    continue;
                }

                await device.Connection
                    .SendAsync(channelId, HdcCommand.FileFinish, FinishAllPayload, ct)
                    .ConfigureAwait(false);
                await AwaitTransferEndAsync(device, context, errors, ct).ConfigureAwait(false);
            }

            ThrowIfErrors(errors);
        }
        finally
        {
            await FinalizeAsync(device, channelId).ConfigureAwait(false);
        }
    }

    /// <summary>从端接收流程：FILE_INIT 后逐文件 CHECK 建文件、DATA 落盘、写完后发 [1]，收 [0] 结束。</summary>
    private static async Task ReceiveFilesAsync(
        HdcDevice device, string remotePath, string localPath, IProgress<FileProgress>? progress, CancellationToken ct)
    {
        uint channelId = device.NewChannelId();
        device.Connection.RegisterChannel(channelId);
        ChannelContext context = device.Dispatcher.Open(channelId);
        List<string> errors = [];
        try
        {
            byte[] initPayload = Encoding.UTF8.GetBytes(
                remotePath + HdcConstants.FileInitArgumentSeparator + localPath);
            await device.Connection.SendAsync(channelId, HdcCommand.FileInit, initPayload, ct).ConfigureAwait(false);
            var state = new ReceiveState();
            TransferConfig? config = await AwaitCheckAsync(device, context, errors, ct).ConfigureAwait(false);
            while (config is not null)
            {
                config = await ReceiveOneFileAsync(
                        device, context, config, remotePath, localPath, state, progress, errors, ct)
                    .ConfigureAwait(false);
            }

            ThrowIfErrors(errors);
        }
        finally
        {
            await FinalizeAsync(device, channelId).ConfigureAwait(false);
        }
    }

    /// <summary>接收单个文件；返回下一个文件的 FILE_CHECK（主端已推进），整次结束返回 null。</summary>
    private static async Task<TransferConfig?> ReceiveOneFileAsync(
        HdcDevice device,
        ChannelContext context,
        TransferConfig config,
        string remotePath,
        string localPath,
        ReceiveState state,
        IProgress<FileProgress>? progress,
        List<string> errors,
        CancellationToken ct)
    {
        if (config.CompressType != 0)
        {
            throw new HdcException($"FILE_CHECK 声明压缩类型 {config.CompressType}，一期恒不请求压缩");
        }

        long total = config.FileSize > long.MaxValue
            ? throw new HdcException($"FILE_CHECK 声明文件大小 {config.FileSize} 超出 long 范围")
            : (long)config.FileSize;
        string target = ResolveTargetPath(localPath, config.OptionalName, state);
        string? parent = Path.GetDirectoryName(Path.GetFullPath(target));
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        state.KnownTotal += total;
        string fileName = config.OptionalName.Length > 0 ? config.OptionalName : Path.GetFileName(remotePath);
        await using FileStream destination = new(
            target, System.IO.FileMode.Create, FileAccess.Write, FileShare.None, 1, FileOptions.Asynchronous);
        await device.Connection
            .SendAsync(context.ChannelId, HdcCommand.FileBegin, ReadOnlyMemory<byte>.Empty, ct)
            .ConfigureAwait(false);
        long received = 0;
        bool finishSent = false;
        while (true)
        {
            Frame frame = await ReadFrameOrThrowAsync(device, context, errors, "等待 FILE_DATA", ct).ConfigureAwait(false);
            switch (frame.Command)
            {
                case HdcCommand.FileData:
                    long written = await WriteDataFrameAsync(destination, frame.Payload, ct).ConfigureAwait(false);
                    received += written;
                    state.Transferred += written;
                    progress?.Report(new FileProgress(state.Transferred, state.KnownTotal, fileName));
                    if (received >= total && !finishSent)
                    {
                        // host 为写端：自身 IO 完成后发 [1]（transfer.cpp:291-302）；主端收 [1] 后推进或回 [0]
                        await device.Connection
                            .SendAsync(frame.ChannelId, HdcCommand.FileFinish, FinishOneFilePayload, ct)
                            .ConfigureAwait(false);
                        finishSent = true;
                    }

                    break;
                case HdcCommand.FileCheck:
                    VerifyReceived(received, total);
                    await destination.FlushAsync(ct).ConfigureAwait(false);
                    return TransferConfig.Parse(frame.Payload);
                case HdcCommand.FileFinish when IsAllFinished(frame.Payload):
                    VerifyReceived(received, total);
                    await destination.FlushAsync(ct).ConfigureAwait(false);
                    ThrowIfErrors(errors);
                    return null;
                case HdcCommand.FileFinish:
                    await device.Connection
                        .SendAsync(frame.ChannelId, HdcCommand.FileFinish, FinishAllPayload, ct)
                        .ConfigureAwait(false);
                    break;
                case HdcCommand.KernelEcho when IsFailLevel(frame.Payload):
                    errors.Add(DecodeEchoText(frame.Payload));
                    break;
                case HdcCommand.KernelChannelClose:
                    ThrowIfErrors(errors);
                    throw new HdcException("FILE_FINISH 确认到达前通道被对端关闭");
                default:
                    break;
            }
        }
    }

    /// <summary>递归枚举目录下全部普通文件（跳过符号链接，'/' 分隔相对路径，按相对路径序排列保证确定性）。</summary>
    private static List<SendEntry> EnumerateDirectory(string localDirectory)
    {
        string fullRoot = Path.GetFullPath(localDirectory);
        string topName = Path.GetFileName(Path.TrimEndingDirectorySeparator(fullRoot));
        if (topName.Length == 0)
        {
            throw new ArgumentException("目录根（如盘符根）无法形成线缆相对路径，请传入具体目录", nameof(localDirectory));
        }

        List<(string Relative, string Absolute, long Size)> files = [];
        CollectFiles(fullRoot, "", files);
        files.Sort(static (left, right) => string.CompareOrdinal(left.Relative, right.Relative));
        List<SendEntry> entries = new(files.Count);
        foreach ((string relative, string absolute, long size) in files)
        {
            // optionalName 含源目录名（上游 GetSubFilesRecursively 以 localName=目录名 为前缀，transfer.cpp:717）
            entries.Add(new SendEntry(absolute, topName + "/" + relative, relative, size));
        }

        return entries;
    }

    private static void CollectFiles(
        string directory, string relativePrefix, List<(string Relative, string Absolute, long Size)> output)
    {
        foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
        {
            FileAttributes attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                // 符号链接/Junction：跳过，避免目录环与重复内容（协议不承载链接语义）
                continue;
            }

            string name = Path.GetFileName(entry);
            string relative = relativePrefix.Length == 0 ? name : relativePrefix + "/" + name;
            if ((attributes & FileAttributes.Directory) != 0)
            {
                CollectFiles(entry, relative, output);
            }
            else
            {
                output.Add((relative, entry, new FileInfo(entry).Length));
            }
        }
    }

    /// <summary>按 index（文件内绝对偏移）发送一个文件的全部数据块，返回跨文件累计进度基准。</summary>
    private static async Task<long> SendDataAsync(
        HdcDevice device,
        uint channelId,
        FileStream source,
        string displayName,
        long transferredBefore,
        long totalBytes,
        IProgress<FileProgress>? progress,
        CancellationToken ct)
    {
        long total = source.Length;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(HdcConstants.TransferSlotSize + HdcConstants.MaxFileChunkSize);
        try
        {
            long sent = 0;
            while (sent < total)
            {
                int wanted = (int)Math.Min(HdcConstants.MaxFileChunkSize, total - sent);
                int read = await source
                    .ReadAsync(buffer.AsMemory(HdcConstants.TransferSlotSize, wanted), ct)
                    .ConfigureAwait(false);
                if (read <= 0)
                {
                    throw new IOException(
                        $"源文件 {displayName} 在传输途中提前到达文件尾（已发 {sent}/{total} 字节）");
                }

                WriteSlot(buffer, (ulong)sent, read);
                await device.Connection
                    .SendAsync(channelId, HdcCommand.FileData, buffer.AsMemory(0, HdcConstants.TransferSlotSize + read), ct)
                    .ConfigureAwait(false);
                sent += read;
                progress?.Report(new FileProgress(transferredBefore + sent, totalBytes, displayName));
            }

            if (total == 0)
            {
                // 空文件的完成信号：主端读取循环首次读到 0 字节时仍会发一个零长度 DATA 帧
                // （ProcressFileIORead → SendIOPayload(index, buf, 0)），从端写 0 字节的回调
                // 命中 req->result == 0 完成分支，才会回 FILE_FINISH[1]
                WriteSlot(buffer, 0, 0);
                await device.Connection
                    .SendAsync(channelId, HdcCommand.FileData, buffer.AsMemory(0, HdcConstants.TransferSlotSize), ct)
                    .ConfigureAwait(false);
                progress?.Report(new FileProgress(transferredBefore, totalBytes, displayName));
            }

            return transferredBefore + total;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task AwaitBeginAsync(
        HdcDevice device, ChannelContext context, List<string> errors, CancellationToken ct)
    {
        while (true)
        {
            Frame frame = await ReadFrameOrThrowAsync(device, context, errors, "等待 FILE_BEGIN", ct).ConfigureAwait(false);
            switch (frame.Command)
            {
                case HdcCommand.FileBegin:
                    return;
                case HdcCommand.KernelEcho when IsFailLevel(frame.Payload):
                    errors.Add(DecodeEchoText(frame.Payload));
                    break;
                case HdcCommand.KernelChannelClose:
                    ThrowIfErrors(errors);
                    throw new HdcException("FILE_BEGIN 到达前通道被对端关闭（设备可能拒绝写入目标路径）");
                default:
                    break;
            }
        }
    }

    /// <summary>等待写端（从端）单文件 IO 完成信号 FILE_FINISH[1]（或整次结束信号）。</summary>
    private static async Task<FileOutcome> AwaitOneFileFinishAsync(
        HdcDevice device, ChannelContext context, List<string> errors, CancellationToken ct)
    {
        while (true)
        {
            Frame frame = await ReadFrameOrThrowAsync(device, context, errors, "等待 FILE_FINISH", ct).ConfigureAwait(false);
            switch (frame.Command)
            {
                case HdcCommand.FileFinish when !IsAllFinished(frame.Payload):
                    return FileOutcome.SlaveFinished;
                case HdcCommand.FileFinish:
                    return FileOutcome.TransferEnded;
                case HdcCommand.KernelEcho when IsFailLevel(frame.Payload):
                    errors.Add(DecodeEchoText(frame.Payload));
                    break;
                case HdcCommand.KernelChannelClose:
                    ThrowIfErrors(errors);
                    return FileOutcome.TransferEnded;
                default:
                    break;
            }
        }
    }

    /// <summary>发送 FILE_FINISH[0] 后等待从端收尾（ECHO + CHANNEL_CLOSE）。</summary>
    private static async Task AwaitTransferEndAsync(
        HdcDevice device, ChannelContext context, List<string> errors, CancellationToken ct)
    {
        while (true)
        {
            Frame frame = await ReadFrameOrThrowAsync(device, context, errors, "等待 FILE_FINISH 确认", ct)
                .ConfigureAwait(false);
            switch (frame.Command)
            {
                case HdcCommand.FileFinish when IsAllFinished(frame.Payload):
                    return;
                case HdcCommand.KernelEcho when IsFailLevel(frame.Payload):
                    errors.Add(DecodeEchoText(frame.Payload));
                    break;
                case HdcCommand.KernelChannelClose:
                    ThrowIfErrors(errors);
                    return;
                default:
                    break;
            }
        }
    }

    private static async Task<TransferConfig> AwaitCheckAsync(
        HdcDevice device, ChannelContext context, List<string> errors, CancellationToken ct)
    {
        while (true)
        {
            Frame frame = await ReadFrameOrThrowAsync(device, context, errors, "等待 FILE_CHECK", ct).ConfigureAwait(false);
            switch (frame.Command)
            {
                case HdcCommand.KernelWakeupSlavetask:
                    break;
                case HdcCommand.FileCheck:
                    return TransferConfig.Parse(frame.Payload);
                case HdcCommand.KernelEcho when IsFailLevel(frame.Payload):
                    errors.Add(DecodeEchoText(frame.Payload));
                    break;
                case HdcCommand.KernelChannelClose:
                    ThrowIfErrors(errors);
                    throw new HdcException("FILE_CHECK 到达前通道被对端关闭（设备端文件可能不存在或不可读）");
                default:
                    break;
            }
        }
    }

    private static async Task<long> WriteDataFrameAsync(FileStream destination, byte[] payload, CancellationToken ct)
    {
        if (payload.Length < HdcConstants.TransferSlotSize)
        {
            throw new HdcException($"FILE_DATA 载荷 {payload.Length} 字节不足 64 字节槽");
        }

        TransferPayload head = TransferPayload.ParseSlot(payload.AsSpan(0, HdcConstants.TransferSlotSize));
        if (head.CompressType != 0)
        {
            throw new HdcException($"FILE_DATA 声明压缩类型 {head.CompressType}，一期恒不请求压缩");
        }

        if (head.Index > long.MaxValue || head.CompressSize != head.UncompressSize)
        {
            throw new HdcException("FILE_DATA 槽内 index 或压缩前后大小非法");
        }

        int size = (int)head.CompressSize;
        if (payload.Length - HdcConstants.TransferSlotSize < size)
        {
            throw new HdcException("FILE_DATA 载荷短于槽内声明的数据长度");
        }

        destination.Seek((long)head.Index, SeekOrigin.Begin);
        await destination
            .WriteAsync(payload.AsMemory(HdcConstants.TransferSlotSize, size), ct)
            .ConfigureAwait(false);
        return size;
    }

    private static void WriteSlot(byte[] buffer, ulong index, int dataSize)
    {
        Array.Clear(buffer, 0, HdcConstants.TransferSlotSize);
        var head = new TransferPayload
        {
            Index = index,
            CompressType = 0,
            CompressSize = (uint)dataSize,
            UncompressSize = (uint)dataSize,
        };
        head.Serialize().CopyTo(buffer, 0);
    }

    private static async ValueTask<Frame> ReadFrameOrThrowAsync(
        HdcDevice device, ChannelContext context, List<string> errors, string phase, CancellationToken ct)
    {
        while (await context.Frames.WaitToReadAsync(ct).ConfigureAwait(false))
        {
            if (context.Frames.TryRead(out Frame frame))
            {
                return frame;
            }
        }

        ThrowIfErrors(errors);
        throw new HdcException(context.IsTerminated || device.Connection.IsClosed
            ? "连接已断开"
            : $"{phase}：通道被对端关闭");
    }

    private static async Task FinalizeAsync(HdcDevice device, uint channelId)
    {
        await ShellOperation.TryCloseAsync(device.Connection, channelId).ConfigureAwait(false);
        device.Connection.UnregisterChannel(channelId);
        device.Dispatcher.Close(channelId);
    }

    private static void VerifyReceived(long received, long total)
    {
        if (received != total)
        {
            throw new HdcException($"接收字节数 {received} 与 FILE_CHECK 声明的 {total} 不一致");
        }
    }

    private static void ThrowIfErrors(List<string> errors)
    {
        if (errors.Count > 0)
        {
            throw new HdcException(string.Join(Environment.NewLine, errors));
        }
    }

    private static bool IsFailLevel(byte[] payload)
    {
        return payload.Length > 0 && payload[0] == (byte)MessageLevel.Fail;
    }

    private static bool IsAllFinished(byte[] payload)
    {
        return payload.Length > 0 && payload[0] == HdcConstants.FileFinishAll;
    }

    private static string DecodeEchoText(byte[] payload)
    {
        return Encoding.UTF8.GetString(payload.AsSpan(1));
    }

    /// <summary>
    /// 解析接收落盘路径：单文件沿用 15a 语义；目录模式（optionalName 含分隔符）对齐上游 daemon——
    /// 本地根已存在时保留 daemon 顶层目录名（SmartSlavePath 追加，transfer.cpp:857-873），
    /// 不存在时创建本地根并剥掉 optionalName 首层（targetDirNotExist，transfer.cpp:768-780、801-812）。
    /// </summary>
    private static string ResolveTargetPath(string localPath, string optionalName, ReceiveState state)
    {
        bool separatorTerminated = localPath.EndsWith(Path.DirectorySeparatorChar)
            || localPath.EndsWith(Path.AltDirectorySeparatorChar)
            || localPath.EndsWith('/');
        bool directoryMode = optionalName.Contains('/') || optionalName.Contains('\\');
        if (!directoryMode)
        {
            bool directoryTarget = Directory.Exists(localPath) || separatorTerminated;
            return directoryTarget && optionalName.Length > 0 ? Path.Combine(localPath, optionalName) : localPath;
        }

        if (File.Exists(localPath))
        {
            throw new HdcException($"本地目标已是文件，无法作为目录接收：{localPath}");
        }

        state.RootExisted ??= Directory.Exists(localPath) || separatorTerminated;
        string relative = optionalName;
        if (state.RootExisted == false)
        {
            int separator = relative.IndexOfAny(['/', '\\']);
            if (separator >= 0 && separator + 1 < relative.Length)
            {
                relative = relative[(separator + 1)..];
            }
        }

        return Path.Combine(
            localPath,
            relative.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar));
    }
}
