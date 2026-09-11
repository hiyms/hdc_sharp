using System.Buffers;
using System.Text;
using HdcSharp.Protocol;
using HdcSharp.Protocol.Messages;
using HdcSharp.Transport;

namespace HdcSharp.Operations;

/// <summary>
/// 单文件收发实现（Task 15a）。
/// 线缆流程对齐上游主从语义（src/common/file.cpp、src/common/transfer.cpp、hdc_rust/src/common/hdcfile.rs）：
/// send：H→D WAKEUP_SLAVETASK(12) 预置从端任务 → FILE_CHECK(TransferConfig) → D→H FILE_BEGIN →
/// H→D FILE_DATA(64 字节槽 + ≤48KiB 数据，index=文件绝对偏移) → H→D FILE_FINISH[1]；
/// recv：H→D FILE_INIT(「远端路径 本地路径」参数串，无首词) → D→H WAKEUP_SLAVETASK/FILE_CHECK(TransferConfig) →
/// H→D FILE_BEGIN(空载荷) → D→H FILE_DATA。
/// 双方均在自身 IO 完成时发 FILE_FINISH[1]、收到 [1] 回 [0]；收到 [0] 即本次传输完成，随后本地主动关闭通道。
/// </summary>
internal static class FileOperation
{
    private static readonly byte[] FinishOneFilePayload = [HdcConstants.FileFinishOneFile];

    private static readonly byte[] FinishAllPayload = [HdcConstants.FileFinishAll];

    /// <summary>发送单个本地文件到设备（host 为主端）。</summary>
    /// <param name="device">目标设备。</param>
    /// <param name="localPath">本地源文件路径；不存在或不可读在发出任何文件命令前抛出。</param>
    /// <param name="remotePath">设备端目标路径；若为已存在目录，daemon 会拼接本地文件名。</param>
    /// <param name="progress">进度回调，每发送一个数据块调用一次。</param>
    /// <param name="ct">取消令牌；取消或失败时发 CHANNEL_CLOSE[0] 并清理通道。</param>
    internal static async Task SendAsync(
        HdcDevice device, string localPath, string remotePath, IProgress<FileProgress>? progress, CancellationToken ct)
    {
        await using FileStream source = new(
            localPath, System.IO.FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
        string fileName = Path.GetFileName(localPath);
        long total = source.Length;
        uint channelId = device.NewChannelId();
        device.Connection.RegisterChannel(channelId);
        ChannelContext context = device.Dispatcher.Open(channelId);
        List<string> errors = [];
        try
        {
            await device.Connection
                .SendAsync(channelId, HdcCommand.KernelWakeupSlavetask, ReadOnlyMemory<byte>.Empty, ct)
                .ConfigureAwait(false);
            var config = new TransferConfig
            {
                FileSize = (ulong)total,
                Path = remotePath,
                OptionalName = fileName,
            };
            await device.Connection.SendAsync(channelId, HdcCommand.FileCheck, config.Serialize(), ct).ConfigureAwait(false);
            await AwaitBeginAsync(device, context, errors, ct).ConfigureAwait(false);
            await SendDataAsync(device, channelId, source, total, fileName, progress, ct).ConfigureAwait(false);
            // 主端不得抢先发 FILE_FINISH[1]：daemon 收到 [1] 会立即 CloseCtxFd 而不等挂起的异步写完成，
            // 导致文件尾部间歇丢失（真机实测 500KB 偶发只落 491520B）。协议顺序为——仅写端（从端）
            // 在自身 IO 完成后设 closeNotify 并主动发 [1]，主端收到后回 [0]，从端随即 TaskFinish
            // （ECHO + CHANNEL_CLOSE）。依据 src/common/transfer.cpp:291-302、src/common/file.cpp:647-668
            await AwaitSlaveFinishAsync(device, context, errors, ct).ConfigureAwait(false);
            ThrowIfErrors(errors);
        }
        finally
        {
            await FinalizeAsync(device, channelId).ConfigureAwait(false);
        }
    }

    /// <summary>从设备接收单个文件（host 为从端）。</summary>
    /// <param name="device">目标设备。</param>
    /// <param name="remotePath">设备端源文件路径（daemon 作为主端读取）。</param>
    /// <param name="localPath">本地目标路径；目录不存在时逐级创建，已存在文件被截断。</param>
    /// <param name="progress">进度回调，每收到一个数据块调用一次。</param>
    /// <param name="ct">取消令牌；取消或失败时发 CHANNEL_CLOSE[0] 并清理通道。</param>
    internal static async Task ReceiveAsync(
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
            TransferConfig config = await AwaitCheckAsync(device, context, errors, ct).ConfigureAwait(false);
            if (config.CompressType != 0)
            {
                throw new HdcException($"FILE_CHECK 声明压缩类型 {config.CompressType}，一期恒不请求压缩");
            }

            long total = config.FileSize > long.MaxValue
                ? throw new HdcException($"FILE_CHECK 声明文件大小 {config.FileSize} 超出 long 范围")
                : (long)config.FileSize;
            string target = ResolveTargetPath(localPath, config.OptionalName);
            string? parent = Path.GetDirectoryName(Path.GetFullPath(target));
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            await using FileStream destination = new(
                target, System.IO.FileMode.Create, FileAccess.Write, FileShare.None, 1, FileOptions.Asynchronous);
            await device.Connection.SendAsync(channelId, HdcCommand.FileBegin, ReadOnlyMemory<byte>.Empty, ct).ConfigureAwait(false);
            string fileName = config.OptionalName.Length > 0 ? config.OptionalName : Path.GetFileName(remotePath);
            await ReceiveLoopAsync(device, context, destination, total, fileName, progress, errors, ct).ConfigureAwait(false);
            await destination.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            await FinalizeAsync(device, channelId).ConfigureAwait(false);
        }
    }

    private static async Task SendDataAsync(
        HdcDevice device,
        uint channelId,
        FileStream source,
        long total,
        string fileName,
        IProgress<FileProgress>? progress,
        CancellationToken ct)
    {
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
                    throw new IOException($"源文件 {fileName} 在传输途中提前到达文件尾（已发 {sent}/{total} 字节）");
                }

                WriteSlot(buffer, (ulong)sent, read);
                await device.Connection
                    .SendAsync(channelId, HdcCommand.FileData, buffer.AsMemory(0, HdcConstants.TransferSlotSize + read), ct)
                    .ConfigureAwait(false);
                sent += read;
                progress?.Report(new FileProgress(sent, total, fileName));
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
                progress?.Report(new FileProgress(0, 0, fileName));
            }
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

    private static async Task AwaitSlaveFinishAsync(
        HdcDevice device, ChannelContext context, List<string> errors, CancellationToken ct)
    {
        bool replied = false;
        while (true)
        {
            Frame frame = await ReadFrameOrThrowAsync(device, context, errors, "等待 FILE_FINISH", ct).ConfigureAwait(false);
            switch (frame.Command)
            {
                case HdcCommand.FileFinish when !IsAllFinished(frame.Payload):
                    if (!replied)
                    {
                        await device.Connection
                            .SendAsync(frame.ChannelId, HdcCommand.FileFinish, FinishAllPayload, ct)
                            .ConfigureAwait(false);
                        replied = true;
                    }

                    break;
                case HdcCommand.FileFinish:
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

    private static async Task AwaitFinishAckAsync(
        HdcDevice device, ChannelContext context, List<string> errors, CancellationToken ct)
    {
        while (true)
        {
            Frame frame = await ReadFrameOrThrowAsync(device, context, errors, "等待 FILE_FINISH 确认", ct).ConfigureAwait(false);
            switch (frame.Command)
            {
                case HdcCommand.FileFinish when IsAllFinished(frame.Payload):
                    return;
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

    private static async Task ReceiveLoopAsync(
        HdcDevice device,
        ChannelContext context,
        FileStream destination,
        long total,
        string fileName,
        IProgress<FileProgress>? progress,
        List<string> errors,
        CancellationToken ct)
    {
        long received = 0;
        bool finishSent = false;
        while (true)
        {
            Frame frame = await ReadFrameOrThrowAsync(device, context, errors, "等待 FILE_DATA", ct).ConfigureAwait(false);
            switch (frame.Command)
            {
                case HdcCommand.FileData:
                    received += await WriteDataFrameAsync(destination, frame.Payload, ct).ConfigureAwait(false);
                    progress?.Report(new FileProgress(received, total, fileName));
                    if (received >= total && !finishSent)
                    {
                        await device.Connection
                            .SendAsync(frame.ChannelId, HdcCommand.FileFinish, FinishOneFilePayload, ct)
                            .ConfigureAwait(false);
                        finishSent = true;
                    }

                    break;
                case HdcCommand.FileFinish when IsAllFinished(frame.Payload):
                    if (received != total)
                    {
                        throw new HdcException($"接收字节数 {received} 与 FILE_CHECK 声明的 {total} 不一致");
                    }

                    ThrowIfErrors(errors);
                    return;
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

    private static string ResolveTargetPath(string localPath, string optionalName)
    {
        bool directoryTarget = Directory.Exists(localPath)
            || localPath.EndsWith(Path.DirectorySeparatorChar)
            || localPath.EndsWith(Path.AltDirectorySeparatorChar)
            || localPath.EndsWith('/');
        return directoryTarget && optionalName.Length > 0 ? Path.Combine(localPath, optionalName) : localPath;
    }
}
