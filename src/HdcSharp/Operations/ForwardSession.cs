using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using HdcSharp.Protocol;
using HdcSharp.Transport;

namespace HdcSharp.Operations;

/// <summary>
/// 端口转发会话实现（Task 17，spec §4.8）。帧形态以上游源码为准
/// （src/common/forward.cpp、src/host/host_forward.cpp、hdc_rust/src/common/forward.rs）：
/// 全部 FORWARD_* 载荷都以 4 字节大端 cid 开头（上游 SendToTask 的 htonl 前缀，forward.cpp:236-256）；
/// CHECK/ACTIVE_SLAVE 在 cid 之后还有 8 字节保留零位与 <c>tcp:&lt;port&gt;</c> 节点串（NUL 结尾，forward.cpp:722-726、84-88）。
/// 本类持有本地 TcpListener（仅 fport）与全部已接受连接，并在通道终结时统一收尾。
/// </summary>
internal sealed class ForwardSession : IForwardSession
{
    private const int CidSize = 4;
    private const int ParameterPrefixSize = 8;
    private const int MaxChunkSize = 32 * 1024;
    private static readonly byte[] CloseZeroPayload = [0];

    private readonly HdcDevice _device;
    private readonly uint _channelId;
    private readonly ChannelContext _context;
    private readonly ForwardDirection _direction;
    private readonly TcpListener? _listener;
    private readonly string _node;
    private readonly string? _reverseInitCommand;
    private readonly int _expectedConnectPort;
    private readonly uint _checkCid;
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource _established = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentDictionary<uint, ForwardConnection> _connections = new();
    private readonly List<string> _errors = [];
    private readonly object _closeGate = new();
    private readonly Task _frameTask;
    private readonly Task? _acceptTask;
    private readonly CancellationTokenRegistration _ctRegistration;
    private Task? _closeTask;
    private int _closedFlag;

    /// <summary>创建会话并立即启动帧循环（fport 同时启动监听接受循环）。</summary>
    /// <param name="device">所属设备。</param>
    /// <param name="direction">转发方向。</param>
    /// <param name="listenPort">向调用方暴露的监听端口（fport 为本机实际端口，rport 为设备侧远端端口）。</param>
    /// <param name="listener">fport 的本地监听器；rport 为 null。</param>
    /// <param name="node">fport 为远端节点（随 ACTIVE_SLAVE 下发）；rport 为本机节点（校验设备给出的节点）。</param>
    /// <param name="reverseInitCommand">rport 的 FORWARD_INIT 载荷；fport 为 null。</param>
    /// <param name="expectedConnectPort">rport 允许连接的本机端口；fport 忽略。</param>
    /// <param name="channelId">转发通道号。</param>
    /// <param name="context">转发通道的帧邮箱。</param>
    /// <param name="ct">会话级取消令牌；取消与释放同义。</param>
    internal ForwardSession(
        HdcDevice device,
        ForwardDirection direction,
        int listenPort,
        TcpListener? listener,
        string node,
        string? reverseInitCommand,
        int expectedConnectPort,
        uint channelId,
        ChannelContext context,
        CancellationToken ct)
    {
        _device = device;
        _direction = direction;
        _listener = listener;
        _node = node;
        _reverseInitCommand = reverseInitCommand;
        _expectedConnectPort = expectedConnectPort;
        _channelId = channelId;
        _context = context;
        _checkCid = NewCid();
        ListenPort = listenPort;
        _frameTask = Task.Run(FrameLoopAsync, CancellationToken.None);
        _acceptTask = listener is null ? null : Task.Run(AcceptLoopAsync, CancellationToken.None);
        if (ct.CanBeCanceled)
        {
            _ctRegistration = ct.Register(static state => _ = ((ForwardSession)state!).CloseAsync(), this);
        }
    }

    /// <summary>fport 为本机实际监听端口；rport 为设备侧远端端口（本机不监听）。</summary>
    public int ListenPort { get; }

    /// <summary>转发方向。</summary>
    public ForwardDirection Direction => _direction;

    /// <summary>会话是否仍然存活。</summary>
    public bool IsActive => Volatile.Read(ref _closedFlag) == 0;

    /// <summary>会话终结时触发一次。</summary>
    public event EventHandler? Closed;

    /// <summary>执行转发建立握手：fport 发 WAKEUP + CHECK 并等 CHECK_RESULT；rport 发 INIT 并等 FORWARD_SUCCESS。</summary>
    /// <param name="ct">建立阶段的取消令牌。</param>
    /// <exception cref="HdcException">设备侧拒绝或连接断开。</exception>
    /// <exception cref="OperationCanceledException">建立阶段被取消。</exception>
    internal async Task EstablishAsync(CancellationToken ct)
    {
        if (_direction == ForwardDirection.Forward)
        {
            await _device.Connection
                .SendAsync(_channelId, HdcCommand.KernelWakeupSlavetask, ReadOnlyMemory<byte>.Empty, ct)
                .ConfigureAwait(false);
            await _device.Connection
                .SendAsync(_channelId, HdcCommand.ForwardCheck, BuildParameterPayload(_checkCid, _node), ct)
                .ConfigureAwait(false);
        }
        else
        {
            await _device.Connection
                .SendAsync(_channelId, HdcCommand.ForwardInit, Encoding.UTF8.GetBytes(_reverseInitCommand!), ct)
                .ConfigureAwait(false);
        }

        await _established.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>释放会话：停监听、断开全部连接、向设备端注销规则并关闭通道。幂等。</summary>
    public ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return new ValueTask(CloseAsync());
    }

    private Task CloseAsync()
    {
        lock (_closeGate)
        {
            _closeTask ??= CloseCoreAsync();
            return _closeTask;
        }
    }

    private async Task CloseCoreAsync()
    {
        _cts.Cancel();
        try
        {
            _listener?.Stop();
        }
        catch (Exception)
        {
            // 释放阶段不因监听器状态异常中断
        }

        foreach (ForwardConnection connection in _connections.Values)
        {
            connection.CloseLocal();
        }

        foreach (uint cid in _connections.Keys)
        {
            await TrySendAsync(HdcCommand.ForwardFreeContext, CidPayload(cid)).ConfigureAwait(false);
        }

        await TrySendAsync(HdcCommand.KernelChannelClose, CloseZeroPayload).ConfigureAwait(false);

        if (_acceptTask is not null)
        {
            await SafeAwaitAsync(_acceptTask).ConfigureAwait(false);
        }

        await SafeAwaitAsync(_frameTask).ConfigureAwait(false);

        _device.Connection.UnregisterChannel(_channelId);
        _device.Dispatcher.Close(_channelId);
        _context.Complete();
        _established.TrySetCanceled();
        MarkClosed();
        _ctRegistration.Dispose();
    }

    private async Task FrameLoopAsync()
    {
        try
        {
            while (await _context.Frames.WaitToReadAsync(_cts.Token).ConfigureAwait(false))
            {
                while (_context.Frames.TryRead(out Frame frame))
                {
                    await HandleFrameAsync(frame).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 会话释放路径，由 CloseCoreAsync 统一收尾
        }
        catch (Exception ex) when (IsTransportFault(ex))
        {
            // 连接断开或对端异常同样按会话终结处理
        }
        finally
        {
            _ = CloseAsync();
        }
    }

    private async Task HandleFrameAsync(Frame frame)
    {
        switch (frame.Command)
        {
            case HdcCommand.ForwardCheck:
                await ReplyCheckAsync(frame.Payload).ConfigureAwait(false);
                break;
            case HdcCommand.ForwardCheckResult:
                ResolveCheck(frame.Payload);
                break;
            case HdcCommand.ForwardActiveSlave:
                await AcceptRemoteConnectionAsync(frame.Payload).ConfigureAwait(false);
                break;
            case HdcCommand.ForwardActiveMaster:
                if (TryReadCid(frame.Payload, out uint masterCid) &&
                    _connections.TryGetValue(masterCid, out ForwardConnection? master))
                {
                    master.MarkActive();
                }

                break;
            case HdcCommand.ForwardData:
                await DeliverAsync(frame.Payload).ConfigureAwait(false);
                break;
            case HdcCommand.ForwardFreeContext:
                if (TryReadCid(frame.Payload, out uint freeCid) &&
                    _connections.TryRemove(freeCid, out ForwardConnection? freed))
                {
                    freed.CloseLocal();
                }

                break;
            case HdcCommand.ForwardSuccess:
                _established.TrySetResult();
                break;
            case HdcCommand.KernelEcho when IsFailLevel(frame.Payload):
                _errors.Add(DecodeEchoText(frame.Payload));
                break;
            case HdcCommand.KernelChannelClose:
                _established.TrySetException(new HdcException(
                    _errors.Count > 0 ? string.Join(Environment.NewLine, _errors) : "转发通道被对端关闭"));
                break;
            default:
                break;
        }
    }

    private async Task ReplyCheckAsync(byte[] payload)
    {
        if (!TryReadCid(payload, out uint cid))
        {
            return;
        }

        // 结果字节恒回 1：上游两世代 daemon 都只判 CHECK_RESULT 载荷是否存在（forward.cpp:974）
        byte[] result = new byte[CidSize + 1];
        BinaryPrimitives.WriteUInt32BigEndian(result, cid);
        result[CidSize] = 1;
        await TrySendAsync(HdcCommand.ForwardCheckResult, result).ConfigureAwait(false);
    }

    private void ResolveCheck(byte[] payload)
    {
        if (_direction != ForwardDirection.Forward || !TryReadCid(payload, out uint cid) || cid != _checkCid)
        {
            return;
        }

        if (payload.Length > CidSize)
        {
            // 上游 C++/Rust 两世代 daemon 对 TCP 节点都回结果字节 0 且宿主忽略取值：
            // 只要载荷存在即视为可达，否则真机 fport 全部失败（forward.cpp:974、forward.rs:1121-1130）
            _established.TrySetResult();
        }
        else
        {
            _established.TrySetException(new HdcException("转发校验失败：daemon 返回空的 FORWARD_CHECK_RESULT"));
        }
    }

    private async Task AcceptRemoteConnectionAsync(byte[] payload)
    {
        if (_direction != ForwardDirection.Reverse || !TryReadCid(payload, out uint cid))
        {
            return;
        }

        string node = ReadNode(payload);
        if (!TryParseTcpNode(node, out int port) || port != _expectedConnectPort)
        {
            await TrySendAsync(HdcCommand.ForwardFreeContext, CidPayload(cid)).ConfigureAwait(false);
            return;
        }

        TcpClient? client = null;
        ForwardConnection connection;
        try
        {
            client = new TcpClient { NoDelay = true };
            await client.ConnectAsync(IPAddress.Loopback, port, _cts.Token).ConfigureAwait(false);
            connection = new ForwardConnection(client);
        }
        catch (Exception ex) when (ex is SocketException or IOException or ObjectDisposedException or OperationCanceledException)
        {
            client?.Dispose();
            await TrySendAsync(HdcCommand.ForwardFreeContext, CidPayload(cid)).ConfigureAwait(false);
            return;
        }

        if (!_connections.TryAdd(cid, connection))
        {
            connection.CloseLocal();
            await TrySendAsync(HdcCommand.ForwardFreeContext, CidPayload(cid)).ConfigureAwait(false);
            return;
        }

        connection.MarkActive();
        await TrySendAsync(HdcCommand.ForwardActiveMaster, CidPayload(cid)).ConfigureAwait(false);
        _ = Task.Run(() => PumpLocalToRemoteAsync(cid, connection), CancellationToken.None);
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (true)
            {
                TcpClient client = await _listener!.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
                client.NoDelay = true;
                uint cid = NewCid();
                var connection = new ForwardConnection(client);
                if (!_connections.TryAdd(cid, connection))
                {
                    connection.CloseLocal();
                    continue;
                }

                await TrySendAsync(HdcCommand.ForwardActiveSlave, BuildParameterPayload(cid, _node)).ConfigureAwait(false);
                _ = Task.Run(() => PumpLocalToRemoteAsync(cid, connection), CancellationToken.None);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            // 释放时 Stop() 会让挂起的 Accept 退出，属正常路径
        }
    }

    private async Task PumpLocalToRemoteAsync(uint cid, ForwardConnection connection)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(MaxChunkSize + CidSize);
        try
        {
            // fport 等 ACTIVE_MASTER（设备侧目标已连通）后再读本地，rport 连接成功即已激活
            await connection.Active.WaitAsync(_cts.Token).ConfigureAwait(false);
            BinaryPrimitives.WriteUInt32BigEndian(buffer, cid);
            while (true)
            {
                int read = await connection.Stream
                    .ReadAsync(buffer.AsMemory(CidSize, MaxChunkSize), _cts.Token)
                    .ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                await _device.Connection
                    .SendAsync(_channelId, HdcCommand.ForwardData, buffer.AsMemory(0, CidSize + read), _cts.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 会话释放
        }
        catch (Exception ex) when (ex is HdcException or IOException or SocketException or ObjectDisposedException)
        {
            // 本地连接或设备连接断开：由 finally 通知对端
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            if (_connections.TryRemove(cid, out _))
            {
                await TrySendAsync(HdcCommand.ForwardFreeContext, CidPayload(cid)).ConfigureAwait(false);
            }

            connection.CloseLocal();
        }
    }

    private async Task DeliverAsync(byte[] payload)
    {
        if (payload.Length <= CidSize || !TryReadCid(payload, out uint cid))
        {
            return;
        }

        if (!_connections.TryGetValue(cid, out ForwardConnection? connection))
        {
            return;
        }

        try
        {
            await connection.Stream.WriteAsync(payload.AsMemory(CidSize), _cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
            if (_connections.TryRemove(cid, out ForwardConnection? dead))
            {
                dead.CloseLocal();
                await TrySendAsync(HdcCommand.ForwardFreeContext, CidPayload(cid)).ConfigureAwait(false);
            }
        }
    }

    private async Task TrySendAsync(HdcCommand command, byte[] payload)
    {
        try
        {
            await _device.Connection.SendAsync(_channelId, command, payload, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HdcException or ObjectDisposedException)
        {
            // 收尾通知是尽力而为
        }
    }

    private void MarkClosed()
    {
        if (Interlocked.Exchange(ref _closedFlag, 1) != 0)
        {
            return;
        }

        EventHandler? handler = Closed;
        if (handler is null)
        {
            return;
        }

        try
        {
            handler(this, EventArgs.Empty);
        }
        catch (Exception)
        {
            // 用户事件处理器异常不得打断会话收尾
        }
    }

    private static async Task SafeAwaitAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 后台循环的异常已在各自路径处理，收尾不再上抛
        }
    }

    private static byte[] CidPayload(uint cid)
    {
        byte[] payload = new byte[CidSize];
        BinaryPrimitives.WriteUInt32BigEndian(payload, cid);
        return payload;
    }

    private static byte[] BuildParameterPayload(uint cid, string node)
    {
        byte[] nodeBytes = Encoding.UTF8.GetBytes(node);
        byte[] payload = new byte[CidSize + ParameterPrefixSize + nodeBytes.Length + 1];
        BinaryPrimitives.WriteUInt32BigEndian(payload, cid);
        nodeBytes.CopyTo(payload, CidSize + ParameterPrefixSize);
        return payload;
    }

    private static bool TryReadCid(byte[] payload, out uint cid)
    {
        if (payload.Length < CidSize)
        {
            cid = 0;
            return false;
        }

        cid = BinaryPrimitives.ReadUInt32BigEndian(payload);
        return true;
    }

    private static string ReadNode(byte[] payload)
    {
        int start = CidSize + ParameterPrefixSize;
        if (payload.Length <= start)
        {
            return string.Empty;
        }

        int end = Array.IndexOf(payload, (byte)0, start);
        if (end < 0)
        {
            end = payload.Length;
        }

        return Encoding.UTF8.GetString(payload, start, end - start);
    }

    private static bool TryParseTcpNode(string node, out int port)
    {
        port = 0;
        return node.StartsWith("tcp:", StringComparison.Ordinal)
            && int.TryParse(node.AsSpan(4), out port)
            && port is > 0 and <= 65535;
    }

    private static bool IsFailLevel(byte[] payload)
    {
        return payload.Length > 0 && payload[0] == (byte)MessageLevel.Fail;
    }

    private static string DecodeEchoText(byte[] payload)
    {
        return Encoding.UTF8.GetString(payload.AsSpan(1));
    }

    private static bool IsTransportFault(Exception ex)
    {
        return ex is HdcException or IOException or SocketException or ObjectDisposedException;
    }

    private static uint NewCid()
    {
        return (uint)Random.Shared.NextInt64(1, 1L + uint.MaxValue);
    }

    /// <summary>一条已转发连接：本地套接字 + 「设备侧已激活」信号，负责本地→设备的读取泵。</summary>
    private sealed class ForwardConnection
    {
        private readonly TcpClient _client;
        private readonly TaskCompletionSource _active = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal ForwardConnection(TcpClient client)
        {
            _client = client;
            Stream = client.GetStream();
        }

        internal NetworkStream Stream { get; }

        internal Task Active => _active.Task;

        internal void MarkActive()
        {
            _active.TrySetResult();
        }

        internal void CloseLocal()
        {
            try
            {
                _client.Close();
            }
            catch (Exception)
            {
                // 关闭本地连接的失败不影响会话收尾
            }
        }
    }
}
