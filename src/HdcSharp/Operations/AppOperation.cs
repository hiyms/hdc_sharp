using System.Buffers;
using System.Text;
using System.Threading.Channels;
using HdcSharp.Protocol;
using HdcSharp.Protocol.Messages;
using HdcSharp.Protocol.Tar;

namespace HdcSharp.Operations;

/// <summary>
/// 应用安装/卸载实现（Task 16）。线缆细节以上游源码为准：
/// 安装：H→D WAKEUP_SLAVETASK(12) 预置从端任务槽（daemon 侧无槽会丢弃 APP_CHECK，session.cpp:1644-1663、1726）
/// → APP_CHECK(3501)=TransferConfig{functionName="install", options, optionalName=9 位随机名+扩展名, fileSize}
/// （host_app.cpp:94-158）→ D→H APP_BEGIN(3502) → H→D APP_DATA(3503)（64 字节槽 + ≤48KiB，index=绝对偏移）
/// → D→H APP_FINISH(3504)，载荷 [mode u8][success u8][bm 输出文本]（daemon_app.cpp:137-166、host_app.rs:169-215）。
/// APP 无 FINISH 握手回合：从端收完数据立即异步跑 bm install，完成才回 APP_FINISH；主端收齐后不再发数据命令。
/// 卸载：H→D APP_UNINSTALL(3505) 单帧（payload="&lt;opts&gt; &lt;package&gt;"，C++/Rust daemon 均以该命令自建任务，
/// session.cpp:1644-1647、daemon_app.rs:389-398），host 不发 WAKEUP/APP_CHECK/APP_DATA。
/// 目录安装由 host 先打 ustar tar（host_app.cpp:32-54 Dir2Tar），optionalName 扩展名 .tar；打包不写结尾全零块
/// （compress.cpp:107-114、compress.rs:97-114），且逐块流式送 APP_DATA、不整包驻留内存。
/// </summary>
internal static class AppOperation
{
    /// <summary>线上 functionName 取值（config.rs:316 TRANSFER_FUNC_NAME）。</summary>
    private const string FunctionNameInstall = "install";

    /// <summary>打包流到发送任务之间的有界队列深度：限制在途字节数，避免整包驻留内存。</summary>
    private const int TarQueueCapacity = 4;

    /// <summary>待安装包的文件系统条目（目录安装打包用）。</summary>
    private readonly record struct TarEntrySource(string RelativePath, string AbsolutePath, bool IsDirectory, long Size);

    /// <summary>安装单个包或目录到设备，返回设备端 bm 输出文本。</summary>
    /// <param name="device">目标设备。</param>
    /// <param name="packagePath">本地 .hap/.hsp/.app 文件或待打包目录。</param>
    /// <param name="options">安装选项；null 使用默认（-r）。</param>
    /// <param name="ct">取消令牌；取消或失败时发 CHANNEL_CLOSE[0] 并清理通道。</param>
    /// <returns>bm install 输出文本（APP_FINISH 载荷自偏移 2 起原样解码）。</returns>
    /// <exception cref="FileNotFoundException">本地包文件不存在。</exception>
    /// <exception cref="ArgumentException">路径为空或目录没有任何可打包条目。</exception>
    /// <exception cref="HdcException">设备端安装失败（success=0，携带 bm 输出与错误码）或连接断开。</exception>
    internal static async Task<string> InstallAsync(
        HdcDevice device, string packagePath, InstallOptions? options, CancellationToken ct)
    {
        string optionsText = BuildInstallOptions(options);
        if (Directory.Exists(packagePath))
        {
            List<TarEntrySource> entries = EnumerateTarEntries(packagePath);
            if (entries.Count == 0)
            {
                throw new ArgumentException($"待安装目录为空，没有任何可打包条目：{packagePath}", nameof(packagePath));
            }

            long tarSize = MeasureTarSize(entries);
            return await SendPackageAsync(
                    device,
                    optionsText,
                    NewRandomName() + ".tar",
                    tarSize,
                    (channelId, token) => SendTarAsync(device, channelId, entries, tarSize, token),
                    ct)
                .ConfigureAwait(false);
        }

        var info = new FileInfo(packagePath);
        if (!info.Exists)
        {
            throw new FileNotFoundException($"待安装包不存在：{packagePath}", packagePath);
        }

        return await SendPackageAsync(
                device,
                optionsText,
                NewRandomName() + ResolveExtension(packagePath),
                info.Length,
                async (channelId, token) =>
                {
                    await using FileStream source = new(
                        packagePath,
                        System.IO.FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        1,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);
                    await SendAppDataAsync(device, channelId, source, info.Length, token).ConfigureAwait(false);
                },
                ct)
            .ConfigureAwait(false);
    }

    /// <summary>按包名卸载设备端应用，返回设备端 bm 输出文本。</summary>
    /// <param name="device">目标设备。</param>
    /// <param name="packageName">应用包名（bundle name）。</param>
    /// <param name="options">卸载选项；null 表示无选项。</param>
    /// <param name="ct">取消令牌；取消或失败时发 CHANNEL_CLOSE[0] 并清理通道。</param>
    /// <returns>bm uninstall 输出文本（APP_FINISH 载荷自偏移 2 起原样解码）。</returns>
    /// <exception cref="ArgumentException">包名为空。</exception>
    /// <exception cref="HdcException">设备端卸载失败（success=0，携带 bm 输出与错误码）或连接断开。</exception>
    internal static async Task<string> UninstallAsync(
        HdcDevice device, string packageName, UninstallOptions? options, CancellationToken ct)
    {
        string optionsText = BuildUninstallOptions(options);
        string payload = optionsText.Length == 0 ? packageName : optionsText + " " + packageName;
        uint channelId = device.NewChannelId();
        device.Connection.RegisterChannel(channelId);
        ChannelContext context = device.Dispatcher.Open(channelId);
        try
        {
            await device.Connection
                .SendAsync(channelId, HdcCommand.AppUninstall, Encoding.UTF8.GetBytes(payload), ct)
                .ConfigureAwait(false);
            return await AwaitAppFinishAsync(device, context, ct).ConfigureAwait(false);
        }
        finally
        {
            await FinalizeAsync(device, channelId).ConfigureAwait(false);
        }
    }

    /// <summary>WAKEUP → APP_CHECK → 等 APP_BEGIN → 送载荷 → 等 APP_FINISH 的完整主端流程。</summary>
    private static async Task<string> SendPackageAsync(
        HdcDevice device,
        string optionsText,
        string optionalName,
        long fileSize,
        Func<uint, CancellationToken, Task> sendPayload,
        CancellationToken ct)
    {
        uint channelId = device.NewChannelId();
        device.Connection.RegisterChannel(channelId);
        ChannelContext context = device.Dispatcher.Open(channelId);
        try
        {
            await device.Connection
                .SendAsync(channelId, HdcCommand.KernelWakeupSlavetask, ReadOnlyMemory<byte>.Empty, ct)
                .ConfigureAwait(false);
            var config = new TransferConfig
            {
                FileSize = (ulong)fileSize,
                Options = optionsText,
                OptionalName = optionalName,
                FunctionName = FunctionNameInstall,
            };
            await device.Connection
                .SendAsync(channelId, HdcCommand.AppCheck, config.Serialize(), ct)
                .ConfigureAwait(false);
            await AwaitAppBeginAsync(device, context, ct).ConfigureAwait(false);
            await sendPayload(channelId, ct).ConfigureAwait(false);
            return await AwaitAppFinishAsync(device, context, ct).ConfigureAwait(false);
        }
        finally
        {
            await FinalizeAsync(device, channelId).ConfigureAwait(false);
        }
    }

    /// <summary>按 index（包内绝对偏移）发送一个包的 APP_DATA 分块；空包补一帧零长度数据。</summary>
    private static async Task SendAppDataAsync(
        HdcDevice device, uint channelId, Stream source, long total, CancellationToken ct)
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
                    throw new IOException($"待安装包在传输途中提前到达文件尾（已发 {sent}/{total} 字节）");
                }

                WriteSlot(buffer, (ulong)sent, read);
                await device.Connection
                    .SendAsync(channelId, HdcCommand.AppData, buffer.AsMemory(0, HdcConstants.TransferSlotSize + read), ct)
                    .ConfigureAwait(false);
                sent += read;
            }

            if (total == 0)
            {
                // 空包：从端仍需要一个回调才会走完传输判定（对齐 FILE 流程的零长度 DATA 帧）
                WriteSlot(buffer, 0, 0);
                await device.Connection
                    .SendAsync(channelId, HdcCommand.AppData, buffer.AsMemory(0, HdcConstants.TransferSlotSize), ct)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// 流式打包目录并发送：TarWriter 写入有界队列，后台任务逐块取出发 APP_DATA，
    /// 内存占用被队列深度限制（不整包驻留）。不调用 TarWriter.Finish——上游打包器不写结尾全零块。
    /// </summary>
    private static async Task SendTarAsync(
        HdcDevice device, uint channelId, List<TarEntrySource> entries, long expectedSize, CancellationToken ct)
    {
        Channel<byte[]> chunks = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(TarQueueCapacity)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        // 发送任务跑在线程池上，避免捕获调用方的同步上下文（生产者会同步阻塞等待队列腾空）；
        // 刻意不把 ct 传给 Task.Run——预取消时委托不执行会让无人消费的队列永久阻塞生产者，
        // 改由 SendChunksAsync 内的 ReadAllAsync(ct) 立即以取消收尾
        Task sender = Task.Run(() => SendChunksAsync(device, channelId, chunks, ct), CancellationToken.None);
        var sink = new ChunkWriterStream(chunks.Writer, ct);
        try
        {
            var writer = new TarWriter(sink);
            foreach (TarEntrySource entry in entries)
            {
                if (entry.IsDirectory)
                {
                    writer.AddDirectory(entry.RelativePath);
                    continue;
                }

                await using FileStream content = new(
                    entry.AbsolutePath,
                    System.IO.FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    1,
                    FileOptions.SequentialScan);
                writer.AddFile(entry.RelativePath, content);
            }

            sink.Complete();
            if (sink.Position != expectedSize)
            {
                throw new HdcException($"tar 实际大小 {sink.Position} 与 APP_CHECK 声明的 {expectedSize} 不一致");
            }

            await sender.ConfigureAwait(false);
        }
        catch
        {
            chunks.Writer.TryComplete();
            await ObserveQuietlyAsync(sender).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>后台从有界队列取分块并逐帧发送；退出时完结队列以唤醒被阻塞的生产者。</summary>
    private static async Task SendChunksAsync(
        HdcDevice device, uint channelId, Channel<byte[]> chunks, CancellationToken ct)
    {
        try
        {
            await foreach (byte[] payload in chunks.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                await device.Connection
                    .SendAsync(channelId, HdcCommand.AppData, payload, ct)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            chunks.Writer.TryComplete();
        }
    }

    /// <summary>递归枚举目录下全部条目（含目录本身，跳过符号链接），按相对路径序排列保证确定性。</summary>
    private static List<TarEntrySource> EnumerateTarEntries(string directory)
    {
        List<TarEntrySource> entries = [];
        CollectTarEntries(Path.GetFullPath(directory), "", entries);
        entries.Sort(static (left, right) => string.CompareOrdinal(left.RelativePath, right.RelativePath));
        return entries;
    }

    private static void CollectTarEntries(string directory, string prefix, List<TarEntrySource> output)
    {
        foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
        {
            FileAttributes attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                // 符号链接/Junction：跳过，避免目录环与重复内容（tar 不承载链接语义）
                continue;
            }

            string name = Path.GetFileName(entry);
            string relative = prefix.Length == 0 ? name : prefix + "/" + name;
            if ((attributes & FileAttributes.Directory) != 0)
            {
                output.Add(new TarEntrySource(relative, entry, true, 0));
                CollectTarEntries(entry, relative, output);
            }
            else
            {
                output.Add(new TarEntrySource(relative, entry, false, new FileInfo(entry).Length));
            }
        }
    }

    /// <summary>按 TarWriter 的输出格式预估 tar 总字节数：每条目 512 头 + 普通文件 512 对齐负载。</summary>
    private static long MeasureTarSize(List<TarEntrySource> entries)
    {
        long total = 0;
        foreach (TarEntrySource entry in entries)
        {
            total += TarHeader.BlockSize;
            if (!entry.IsDirectory)
            {
                total += RoundUp(entry.Size);
            }
        }

        return total;
    }

    private static long RoundUp(long size)
    {
        return (size + TarHeader.BlockSize - 1) / TarHeader.BlockSize * TarHeader.BlockSize;
    }

    /// <summary>随机 9 位十进制名，避免非法应用名让设备端 pm 无法安装（host_app.cpp:144、define.h:98 EXPECTED_LEN）。</summary>
    private static string NewRandomName()
    {
        Span<char> digits = stackalloc char[9];
        for (int i = 0; i < digits.Length; i++)
        {
            digits[i] = (char)('0' + Random.Shared.Next(10));
        }

        return new string(digits);
    }

    /// <summary>保留原扩展名（host_app.cpp:146-157：.hap/.hsp/.tar/.app 原样，其它退化为 .bundle）。</summary>
    private static string ResolveExtension(string path)
    {
        string extension = Path.GetExtension(path).ToLowerInvariant();
        return extension is ".hap" or ".hsp" or ".tar" or ".app" ? extension : ".bundle";
    }

    private static string BuildInstallOptions(InstallOptions? options)
    {
        InstallOptions value = options ?? new InstallOptions();
        List<string> parts = [];
        if (value.Replace)
        {
            parts.Add("-r");
        }

        if (value.Downgrade)
        {
            parts.Add("-d");
        }

        if (value.Shared)
        {
            parts.Add("-s");
        }

        if (value.GrantPermissions)
        {
            parts.Add("-g");
        }

        return string.Join(' ', parts);
    }

    private static string BuildUninstallOptions(UninstallOptions? options)
    {
        UninstallOptions value = options ?? new UninstallOptions();
        List<string> parts = [];
        if (value.KeepData)
        {
            parts.Add("-k");
        }

        if (value.Shared)
        {
            parts.Add("-s");
        }

        return string.Join(' ', parts);
    }

    /// <summary>等待 APP_BEGIN；从端在建临时文件失败时只回 APP_FINISH（daemon_app.rs:385-392）。</summary>
    private static async Task AwaitAppBeginAsync(HdcDevice device, ChannelContext context, CancellationToken ct)
    {
        List<string> errors = [];
        while (true)
        {
            Frame frame = await ReadFrameAsync(device, context, "等待 APP_BEGIN", ct).ConfigureAwait(false);
            switch (frame.Command)
            {
                case HdcCommand.AppBegin:
                    return;
                case HdcCommand.AppFinish:
                    string message = ReadAppFinishPayload(frame.Payload);
                    throw new HdcException(message);
                case HdcCommand.KernelEcho when IsFailLevel(frame.Payload):
                    errors.Add(DecodeEchoText(frame.Payload));
                    break;
                case HdcCommand.KernelChannelClose:
                    ThrowIfErrors(errors);
                    throw new HdcException("APP_BEGIN 到达前通道被对端关闭（设备端可能无法创建安装临时文件）");
                default:
                    break;
            }
        }
    }

    /// <summary>等待 APP_FINISH 并返回 bm 输出；success=0 时以 bm 文本与错误码抛出。</summary>
    private static async Task<string> AwaitAppFinishAsync(HdcDevice device, ChannelContext context, CancellationToken ct)
    {
        List<string> errors = [];
        while (true)
        {
            Frame frame = await ReadFrameAsync(device, context, "等待 APP_FINISH", ct).ConfigureAwait(false);
            switch (frame.Command)
            {
                case HdcCommand.AppFinish:
                    return ReadAppFinishPayload(frame.Payload);
                case HdcCommand.KernelEcho when IsFailLevel(frame.Payload):
                    errors.Add(DecodeEchoText(frame.Payload));
                    break;
                case HdcCommand.KernelChannelClose:
                    ThrowIfErrors(errors);
                    throw new HdcException("APP_FINISH 到达前通道被对端关闭");
                default:
                    break;
            }
        }
    }

    /// <summary>
    /// 解析 APP_FINISH 载荷 [mode u8][success u8][bm 输出文本]（daemon_app.cpp:150-158、daemon_app.rs:169-215）。
    /// 成功判定以**文本**为准：官方 host 两个世代都直接跳过 success 字节，只取偏移 2 起的文本
    /// 作信息输出（src/host/host_app.cpp:224-228、hdc_rust/src/host/host_app.rs:143-155）。
    /// 真机实测 bm 打印 install bundle successfully 时该字节仍可能为 0，仅凭字节会误报失败；
    /// 故仅当文本出现 error/fail 字样才视为失败，错误码取文本中首个 [Exxxxxx]
    /// （上游 ECHO 错误码格式 [E%06x]，server_for_client.cpp:1606）。
    /// </summary>
    private static string ReadAppFinishPayload(byte[] payload)
    {
        if (payload.Length < 2)
        {
            throw new HdcException($"APP_FINISH 载荷仅 {payload.Length} 字节，不足 mode/success 两字节");
        }

        string message = Encoding.UTF8.GetString(payload.AsSpan(2));
        // error/fail 字样一律失败；否则须有正面成功证据（真机成功时字节也可能为 0）
        bool failed = LooksLikeFailure(message)
            || (payload[1] == 0 && !message.Contains("success", StringComparison.OrdinalIgnoreCase));
        return failed ? throw new HdcException(message, TryExtractErrorCode(message)) : message;
    }

    private static bool LooksLikeFailure(string message) =>
        message.Contains("error", StringComparison.OrdinalIgnoreCase)
        || message.Contains("fail", StringComparison.OrdinalIgnoreCase);

    private static string? TryExtractErrorCode(string message)
    {
        for (int i = 0; i + 9 <= message.Length; i++)
        {
            if (message[i] != '[' || message[i + 1] != 'E' || message[i + 8] != ']')
            {
                continue;
            }

            bool digits = true;
            for (int j = i + 2; j < i + 8; j++)
            {
                if (!Uri.IsHexDigit(message[j]))
                {
                    digits = false;
                    break;
                }
            }

            if (digits)
            {
                return message.Substring(i, 9);
            }
        }

        return null;
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

    private static async Task<Frame> ReadFrameAsync(
        HdcDevice device, ChannelContext context, string phase, CancellationToken ct)
    {
        while (await context.Frames.WaitToReadAsync(ct).ConfigureAwait(false))
        {
            if (context.Frames.TryRead(out Frame frame))
            {
                return frame;
            }
        }

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

    private static async Task ObserveQuietlyAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 已由生产者异常报告，此处仅回收后台任务
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

    private static string DecodeEchoText(byte[] payload)
    {
        return Encoding.UTF8.GetString(payload.AsSpan(1));
    }

    /// <summary>
    /// 写侧流：把 TarWriter 的同步写入切成 ≤48KiB 的分块并交给后台发送任务（有界队列提供背压），
    /// 每块携带 64 字节槽且 index 为包内绝对偏移。不接管底层队列所有权。
    /// </summary>
    private sealed class ChunkWriterStream : Stream
    {
        private readonly ChannelWriter<byte[]> _writer;
        private readonly CancellationToken _ct;
        private readonly byte[] _buffer = new byte[HdcConstants.MaxFileChunkSize];
        private int _buffered;
        private long _offset;

        internal ChunkWriterStream(ChannelWriter<byte[]> writer, CancellationToken ct)
        {
            _writer = writer;
            _ct = ct;
        }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => _offset + _buffered;

        public override long Position
        {
            get => _offset + _buffered;
            set => throw new NotSupportedException();
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            while (!buffer.IsEmpty)
            {
                int take = Math.Min(_buffer.Length - _buffered, buffer.Length);
                buffer[..take].CopyTo(_buffer.AsSpan(_buffered));
                _buffered += take;
                buffer = buffer[take..];
                if (_buffered == _buffer.Length)
                {
                    Emit();
                }
            }
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            Write(buffer.AsSpan(offset, count));
        }

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        internal void Complete()
        {
            if (_buffered > 0)
            {
                Emit();
            }

            _writer.TryComplete();
        }

        private void Emit()
        {
            byte[] payload = new byte[HdcConstants.TransferSlotSize + _buffered];
            WriteSlot(payload, (ulong)_offset, _buffered);
            _buffer.AsSpan(0, _buffered).CopyTo(payload.AsSpan(HdcConstants.TransferSlotSize));
            _offset += _buffered;
            _buffered = 0;
            while (!_writer.TryWrite(payload))
            {
                if (!_writer.WaitToWriteAsync().AsTask().GetAwaiter().GetResult())
                {
                    _ct.ThrowIfCancellationRequested();
                    throw new HdcException("应用安装数据传输已中止（发送任务已结束）");
                }
            }
        }
    }
}
