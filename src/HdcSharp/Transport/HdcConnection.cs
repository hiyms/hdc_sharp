using System.Buffers;
using System.IO.Pipelines;
using System.Net.Sockets;
using HdcSharp.Protocol;
using HdcSharp.Protocol.Messages;

namespace HdcSharp.Transport;

/// <summary>
/// 单条 TCP 连接的传输核心：帧泵（Pipe 读循环 + 写信号量串行化）、按 channelId 的帧分发、
/// 通道注册表、CHANNEL_CLOSE 跳数语义与心跳（spec §3、§4.11）。
/// 连接层不自动发送任何帧（握手、心跳均由上层显式驱动）。
/// </summary>
public sealed class HdcConnection : IAsyncDisposable
{
    /// <summary>CLOSE 回发的固定载荷 [0]：host 主动取消与未知通道终结均用此值。</summary>
    private static readonly byte[] CloseZeroPayload = [0];

    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly HdcConnectionOptions _options;
    private readonly FrameDecoder _decoder = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly HashSet<uint> _channels = [];
    private readonly CancellationTokenSource _heartbeatCts = new();
    private readonly object _disposeGate = new();
    private Task? _heartbeatTask;
    private Task? _runTask;
    private Task? _disposeTask;
    private int _runStarted;
    private int _heartbeatStarted;
    private int _closed;

    /// <summary>初始化连接：持有传入的 <see cref="TcpClient"/>，不立即产生任何收发。</summary>
    /// <param name="client">已连接的 TCP 客户端，其生命周期自此由连接管理。</param>
    /// <param name="options">连接选项，null 时使用默认值。</param>
    public HdcConnection(TcpClient client, HdcConnectionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        _stream = client.GetStream();
        _options = options ?? new HdcConnectionOptions();
        uint id;
        do
        {
            id = ((uint)Random.Shared.Next(ushort.MaxValue + 1) << 16) | (uint)Random.Shared.Next(ushort.MaxValue + 1);
        }
        while (id == 0);
        SessionId = id;
    }

    /// <summary>会话号：构造时生成的随机非零 u32，daemon 采纳后用于标识本连接。</summary>
    public uint SessionId { get; }

    /// <summary>连接是否已关闭（对端断开、协议错误、取消或释放）。</summary>
    public bool IsClosed => Volatile.Read(ref _closed) != 0;

    /// <summary>每帧到达时触发（CHANNEL_CLOSE 也照常触发），在读循环线程同步调用，处理器应尽快返回。</summary>
    public event Action<Frame>? FrameReceived;

    /// <summary>收到 CHANNEL_CLOSE 时触发（先于 FrameReceived），表示对端发起的通道终结/完成信号。</summary>
    public event Action<Frame>? ChannelClosed;

    /// <summary>
    /// 连接终结（对端断开、协议错误、取消或释放）时触发一次，在读循环线程同步调用。
    /// 供内部上层在等待中快速感知断开；处理器必须立即返回且不得回调本连接。
    /// </summary>
    internal event Action? Terminated;

    /// <summary>
    /// 发送一帧：入口检查连接状态，信号量串行化后整帧写出并刷新。
    /// </summary>
    /// <param name="channelId">通道号。</param>
    /// <param name="cmd">命令字。</param>
    /// <param name="payload">命令负载。</param>
    /// <param name="ct">取消令牌。</param>
    /// <exception cref="HdcException">连接已关闭或发送中断开。</exception>
    public async Task SendAsync(uint channelId, HdcCommand cmd, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        ThrowIfClosed();
        byte[] frame = FrameCodec.Encode(channelId, cmd, payload.Span);
        try
        {
            await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // 与 DisposeAsync 竞态时信号量可能已被释放，按断开语义上报
            throw new HdcException("连接已断开");
        }

        try
        {
            ThrowIfClosed();
            await _stream.WriteAsync(frame, ct).ConfigureAwait(false);
            await _stream.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
        {
            MarkClosed();
            throw new HdcException($"连接已断开：{ex.Message}");
        }
        finally
        {
            ReleaseSendLock();
        }
    }

    private void ReleaseSendLock()
    {
        try
        {
            _sendLock.Release();
        }
        catch (ObjectDisposedException)
        {
            // DisposeAsync 已释放信号量，无需补偿
        }
    }

    /// <summary>
    /// 主动关闭指定通道：发送 CHANNEL_CLOSE 载荷 [0]（host 发起，daemon 清任务且不回传，spec §4.11）。
    /// </summary>
    /// <param name="channelId">要关闭的通道号。</param>
    /// <param name="ct">取消令牌。</param>
    public Task CloseChannelAsync(uint channelId, CancellationToken ct = default)
    {
        return SendAsync(channelId, HdcCommand.KernelChannelClose, CloseZeroPayload, ct);
    }

    /// <summary>注册本地已打开的通道；未注册通道收到命令时将回发 CHANNEL_CLOSE[0]（握手/心跳豁免）。</summary>
    /// <param name="channelId">通道号。</param>
    public void RegisterChannel(uint channelId)
    {
        lock (_channels)
        {
            _channels.Add(channelId);
        }
    }

    /// <summary>注销通道。</summary>
    /// <param name="channelId">通道号。</param>
    public void UnregisterChannel(uint channelId)
    {
        lock (_channels)
        {
            _channels.Remove(channelId);
        }
    }

    /// <summary>
    /// 启动读循环：解码入站字节流并按命令分发事件，直到对端断开、协议错误、取消或释放。
    /// 调用方取消时正常返回；对端关闭连接（含 Windows 回环上将 RST 呈现为 0 字节读的情况，
    /// 实测 SO_ERROR 与写探测均无法区分 RST 与 FIN）以 HdcException("连接已断开") 结束；
    /// 协议非法帧以原始 HdcException 结束且连接立即作废。
    /// </summary>
    /// <param name="ct">取消令牌。</param>
    /// <exception cref="InvalidOperationException">重复调用。</exception>
    public Task RunAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _runStarted, 1) != 0)
        {
            throw new InvalidOperationException("RunAsync 只能调用一次");
        }

        Task run = RunCoreAsync(ct);
        Volatile.Write(ref _runTask, run);
        return run;
    }

    /// <summary>
    /// 启动心跳：每 <see cref="HdcConnectionOptions.HeartbeatInterval"/> 发送一条 Count 递增的
    /// HeartbeatMsg（channelId=0）。仅 C++ 世代协商成功后由上层调用；重复调用无效果。
    /// </summary>
    public void StartHeartbeat()
    {
        if (Interlocked.Exchange(ref _heartbeatStarted, 1) != 0)
        {
            return;
        }

        Task heartbeat = RunHeartbeatAsync(_heartbeatCts.Token);
        Volatile.Write(ref _heartbeatTask, heartbeat);
    }

    /// <summary>关闭连接并释放资源：关 TCP、取消心跳并等待读循环/心跳任务结束。幂等。</summary>
    public ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        lock (_disposeGate)
        {
            _disposeTask ??= DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private async Task RunCoreAsync(CancellationToken ct)
    {
        // leaveOpen：流生命周期由连接统一管理，读循环退出时不能顺手关流
        PipeReader reader = PipeReader.Create(_stream, new StreamPipeReaderOptions(leaveOpen: true));
        try
        {
            while (true)
            {
                ReadResult result = await reader.ReadAsync(ct).ConfigureAwait(false);
                ReadOnlySequence<byte> buffer = result.Buffer;
                try
                {
                    FeedDecoder(buffer);
                    while (_decoder.TryRead(out Frame frame))
                    {
                        await DispatchAsync(frame, ct).ConfigureAwait(false);
                    }
                }
                finally
                {
                    reader.AdvanceTo(buffer.End);
                }

                if (result.IsCompleted || result.IsCanceled)
                {
                    break;
                }
            }
        }
        catch (HdcException ex)
        {
            MarkClosed();
            await ShutdownReaderAsync(reader).ConfigureAwait(false);
            Log(HdcLogLevel.Error, $"会话 {SessionId} 收到协议非法帧，连接作废：{ex.Message}");
            throw;
        }
        catch (ObjectDisposedException)
        {
            // 本地释放关闭 TCP 后在途读会抛 ObjectDisposedException，视为正常关闭
            await ShutdownReaderAsync(reader).ConfigureAwait(false);
            MarkClosed();
            Log(HdcLogLevel.Debug, $"会话 {SessionId} 读循环结束：连接已被本地释放");
            return;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await ShutdownReaderAsync(reader).ConfigureAwait(false);
            MarkClosed();
            Log(HdcLogLevel.Debug, $"会话 {SessionId} 读循环结束：调用方取消");
            return;
        }
        catch (Exception ex)
        {
            MarkClosed();
            await ShutdownReaderAsync(reader).ConfigureAwait(false);
            Log(HdcLogLevel.Error, $"会话 {SessionId} 连接已断开：{ex.Message}");
            throw new HdcException($"连接已断开：{ex.Message}");
        }

        // 对端关闭一律按断开上报：Windows 回环上对端 RST 同样呈现为 0 字节读，
        // 与优雅 FIN 从读路径无法区分，故统一对齐异常断开分支的语义
        await ShutdownReaderAsync(reader).ConfigureAwait(false);
        MarkClosed();
        Log(HdcLogLevel.Error, $"会话 {SessionId} 读循环结束：对端已断开连接");
        throw new HdcException("连接已断开");
    }

    private async ValueTask DispatchAsync(Frame frame, CancellationToken ct)
    {
        if (frame.Command == HdcCommand.KernelChannelClose)
        {
            RaiseChannelClosed(frame);
            // 跳数语义（spec §4.11）：daemon 发 [n]，host 回发 [n-1] 一次；n==0 为对端确认，不再回发
            byte hops = frame.Payload.Length > 0 ? frame.Payload[0] : (byte)0;
            if (hops > 0)
            {
                await SendAsync(frame.ChannelId, HdcCommand.KernelChannelClose, new byte[] { (byte)(hops - 1) }, ct).ConfigureAwait(false);
            }
        }
        else if (!IsControlCommand(frame.Command) && !IsRegistered(frame.ChannelId))
        {
            // 未知通道收到命令时回发 CLOSE[0] 促使对端终结通道，对齐原版 server.cpp:875-899；
            // 握手与心跳在原版于通道查找前处理，须豁免
            Log(HdcLogLevel.Warn, $"会话 {SessionId} 未知通道 {frame.ChannelId} 收到 {frame.Command}，回发 CHANNEL_CLOSE[0]");
            await SendAsync(frame.ChannelId, HdcCommand.KernelChannelClose, CloseZeroPayload, ct).ConfigureAwait(false);
        }

        RaiseFrameReceived(frame);
    }

    private async Task RunHeartbeatAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(_options.HeartbeatInterval);
            ulong count = 0;
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                count++;
                var message = new HeartbeatMsg { Count = count };
                await SendAsync(0, HdcCommand.HeartbeatMsg, message.Serialize(), ct).ConfigureAwait(false);
                Log(HdcLogLevel.Debug, $"会话 {SessionId} 心跳已发送，计数 {count}");
            }
        }
        catch (OperationCanceledException)
        {
            // 连接关闭/释放时停止心跳，属正常路径
        }
        catch (Exception ex)
        {
            MarkClosed();
            Log(HdcLogLevel.Error, $"会话 {SessionId} 心跳发送失败，连接关闭：{ex.Message}");
        }
    }

    private void FeedDecoder(ReadOnlySequence<byte> buffer)
    {
        if (buffer.IsSingleSegment)
        {
            _decoder.Append(buffer.FirstSpan);
            return;
        }

        foreach (ReadOnlyMemory<byte> segment in buffer)
        {
            _decoder.Append(segment.Span);
        }
    }

    private void RaiseChannelClosed(Frame frame)
    {
        Action<Frame>? handler = ChannelClosed;
        if (handler is null)
        {
            return;
        }

        try
        {
            handler(frame);
        }
        catch (Exception ex)
        {
            // 事件回调异常必须与连接隔离，否则会炸掉读循环
            Log(HdcLogLevel.Error, $"ChannelClosed 事件处理器异常：{ex.Message}");
        }
    }

    private void RaiseFrameReceived(Frame frame)
    {
        Action<Frame>? handler = FrameReceived;
        if (handler is null)
        {
            return;
        }

        try
        {
            handler(frame);
        }
        catch (Exception ex)
        {
            // 事件回调异常必须与连接隔离，否则会炸掉读循环
            Log(HdcLogLevel.Error, $"FrameReceived 事件处理器异常：{ex.Message}");
        }
    }

    private bool IsRegistered(uint channelId)
    {
        lock (_channels)
        {
            return _channels.Contains(channelId);
        }
    }

    private static bool IsControlCommand(HdcCommand cmd)
    {
        return cmd is HdcCommand.KernelHandshake or HdcCommand.HeartbeatMsg;
    }

    private static async Task ShutdownReaderAsync(PipeReader reader)
    {
        try
        {
            await reader.CompleteAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 收尾阶段的清理失败不影响关闭结果
        }
    }

    private void ThrowIfClosed()
    {
        if (IsClosed)
        {
            throw new HdcException("连接已断开");
        }
    }

    private void MarkClosed()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        // 先关 TCP 再取消心跳：让在途读写与心跳尽快退出
        _client.Close();
        _heartbeatCts.Cancel();
        RaiseTerminated();
    }

    private void RaiseTerminated()
    {
        Action? handler = Terminated;
        if (handler is null)
        {
            return;
        }

        try
        {
            handler();
        }
        catch (Exception ex)
        {
            // 事件回调异常必须与连接隔离，否则会炸掉读循环
            Log(HdcLogLevel.Error, $"Terminated 事件处理器异常：{ex.Message}");
        }
    }

    private void Log(HdcLogLevel level, string message)
    {
        _options.Logger?.Invoke(level, message);
    }

    private async Task DisposeCoreAsync()
    {
        MarkClosed();
        await (Volatile.Read(ref _heartbeatTask) ?? Task.CompletedTask).ConfigureAwait(false);
        Task run = Volatile.Read(ref _runTask) ?? Task.CompletedTask;
        try
        {
            await run.ConfigureAwait(false);
        }
        catch (HdcException)
        {
            // 断开/协议错误由 RunAsync 的调用方观察，释放时不再上抛
        }
        catch (OperationCanceledException)
        {
        }

        _stream.Dispose();
        _client.Dispose();
        _heartbeatCts.Dispose();
        _sendLock.Dispose();
    }
}
