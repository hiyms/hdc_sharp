using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using HdcSharp.Protocol;
using HdcSharp.Protocol.Messages;
using HdcSharp.Transport;

namespace HdcSharp.Tests.TestDoubles;

/// <summary>FakeDaemon 的剧本选项。</summary>
public sealed class FakeDaemonOptions
{
    /// <summary>daemon 世代指纹，决定版本串、AUTH_OK 的 TLV 与验签方式。</summary>
    public DaemonGeneration Generation { get; set; } = DaemonGeneration.Rust;

    /// <summary>是否要求认证；true 走 AUTH_PUBLICKEY → AUTH_SIGNATURE → AUTH_OK 三阶段。</summary>
    public bool RequireAuth { get; set; }

    /// <summary>收到首个握手后回一个 banner 非 "OHOS HDC" 的握手，连接保持到对端关闭。</summary>
    public bool SendBadBanner { get; set; }

    /// <summary>AUTH_OK 中 devname 的取值。</summary>
    public string DeviceName { get; set; } = "fake-dev";

    /// <summary>收到 host 的 AUTH_PUBLICKEY 后不回包也不断开，用于认证超时用例。</summary>
    public bool StallAfterPublicKey { get; set; }

    /// <summary>一次性 shell（1001/1200）的输出分块剧本；null 时按 <see cref="EchoShellCommand"/> 决定是否回显命令。</summary>
    public IReadOnlyList<byte[]>? ShellChunks { get; set; }

    /// <summary>一次性 shell 是否把收到的命令原文回显为输出（并发隔离用例据此按通道区分响应）。</summary>
    public bool EchoShellCommand { get; set; }

    /// <summary>输出分块与 ECHO 消息发完后是否发送 CHANNEL_CLOSE[1] 终结通道；false 用于模拟长命/挂起命令。</summary>
    public bool ShellClosesAfterOutput { get; set; } = true;

    /// <summary>一次性 shell 结束帧 CHANNEL_CLOSE 的跳数值；0 模拟对端确认式关闭。</summary>
    public byte ShellCloseHops { get; set; } = 1;

    /// <summary>交互式 shell（SHELL_INIT）剧本：对每个 SHELL_DATA 原样回显，载荷含 0x04 时关闭通道。</summary>
    public bool InteractiveEcho { get; set; }

    /// <summary>一次性 shell 输出后追加的 KERNEL_ECHO 消息，用于覆盖 daemon 报错回显路径。</summary>
    public IReadOnlyList<(MessageLevel Level, string Text)>? ShellEchoMessages { get; set; }

    /// <summary>认证完成后在指定通道上发一条 ECHO_RAW 的野帧，用于覆盖未注册通道边界；null 不发。</summary>
    public uint? StrayEchoChannelId { get; set; }

    /// <summary>send 方向文件剧本：非 null 时按上游从端语义接收 FILE_CHECK/DATA/FINISH，并把文件落到该目录（文件名取远端 path 的 basename）。</summary>
    public string? FileRecvSinkDirectory { get; set; }

    /// <summary>send 剧本 FILE_BEGIN 的载荷；null 为空载荷（Rust 世代），设为 8 字节可模拟 C++ FeatureFlags。</summary>
    public byte[]? FileBeginPayload { get; set; }

    /// <summary>send 剧本收到 FILE_FINISH[1] 后是否回 FILE_FINISH[0] 与 ECHO(Ok)；false 模拟从端不再响应（取消用例）。</summary>
    public bool FileAcknowledgeFinish { get; set; } = true;

    /// <summary>recv 方向剧本：非 null 时 daemon 收到 FILE_INIT 后主动推 FILE_CHECK/DATA/FINISH，内容为该字节序列。</summary>
    public byte[]? FilePushData { get; set; }

    /// <summary>recv 剧本 FILE_CHECK 的 optionalName。</summary>
    public string FilePushName { get; set; } = "pushed.bin";

    /// <summary>recv 方向目录剧本：非 null 时按序逐文件推 FILE_CHECK/DATA（optionalName 即相对路径），
    /// 宿主每文件回 FILE_FINISH[1] 后推进下一文件，队列耗尽回 FILE_FINISH[0]（上游 file.cpp:645-668）。</summary>
    public IReadOnlyList<(string Name, byte[] Content)>? FilePushFiles { get; set; }

    /// <summary>应用安装剧本：非 null 时按上游从端语义接收 APP_CHECK/APP_DATA，并把包按 optionalName 落到该目录。</summary>
    public string? AppSinkDirectory { get; set; }

    /// <summary>应用剧本收到 APP_CHECK 后是否回 APP_BEGIN；false 时立即回失败 APP_FINISH，模拟 Rust daemon 建临时文件失败（daemon_app.rs:385-392）。</summary>
    public bool AppSendBegin { get; set; } = true;

    /// <summary>APP_FINISH 载荷的 success 字节（1=成功，0=失败）。</summary>
    public byte AppFinishSuccess { get; set; } = 1;

    /// <summary>APP_FINISH 载荷自偏移 2 起的 bm 输出文本。</summary>
    public string AppFinishMessage { get; set; } = "Success";

    /// <summary>每个 APP_DATA 落盘前的延迟毫秒数，用于制造确定性的「传输中」窗口（模拟慢设备）。</summary>
    public int AppDataDelayMs { get; set; }

    /// <summary>卸载剧本：收到 APP_UNINSTALL 后回 APP_FINISH（mode=2）。</summary>
    public bool AppUninstallEnabled { get; set; }

    /// <summary>
    /// fport 剧本：非 null 时按上游 daemon 从端语义处理 FORWARD_CHECK/ACTIVE_SLAVE/DATA/FREE_CONTEXT，
    /// 并把 ACTIVE_SLAVE 载荷里的节点（<c>tcp:&lt;port&gt;</c>）真实拨通（等价 daemon 侧连接目标服务）。
    /// </summary>
    public bool ForwardEnabled { get; set; }

    /// <summary>fport 剧本收到 FORWARD_CHECK 时回「仅含 cid、无结果字节」的 CHECK_RESULT，模拟设备侧拒绝（上游视为非法载荷）。</summary>
    public bool ForwardRejectCheck { get; set; }

    /// <summary>fport 剧本 CHECK_RESULT 的结果字节取值；上游两世代 daemon 均回 0，故默认 0。</summary>
    public byte ForwardCheckResultFlag { get; set; }

    /// <summary>
    /// rport 剧本：非 null 时按上游 daemon 主端语义接收 FORWARD_INIT（载荷 <c>tcp:&lt;远端&gt; tcp:&lt;本地&gt;</c>），
    /// 在设备侧监听远端端口，并向宿主推 FORWARD_CHECK/ACTIVE_SLAVE。
    /// </summary>
    public bool ForwardReverseEnabled { get; set; }
}

/// <summary>
/// 回环 TCP 上的 HDC daemon 测试替身，按 spec §4.6 剧本实现免认证与两世代认证全流程
/// （Rust 世代用公钥幂还原 PKCS#1 块类型 1 并比对 token 原文，C++ 世代用 PSS+SHA512 验签），
/// 记录收到的非握手命令并暴露验签结果与 host 公钥，供认证及后续操作类测试断言。
/// 每个连接独立执行剧本，支持多连接并发；Dispose 会断开全部连接并等待后台任务退出。
/// </summary>
public sealed class FakeDaemon : IDisposable
{
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(5);

    private readonly FakeDaemonOptions _options;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<(uint ChannelId, HdcCommand Cmd)> _received = [];
    private readonly List<Frame> _receivedFrames = [];
    private readonly ConcurrentDictionary<int, TcpClient> _clients = new();
    private readonly ConcurrentDictionary<int, Task> _connections = new();
    private readonly object _gate = new();
    // 转发泵与帧循环会并发写同一 NetworkStream，帧必须串行写出
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly Task _acceptTask;
    private string? _hostPublicKeyPem;
    private bool _signatureVerified;
    private int _disposed;
    private int _nextConnectionId;

    private enum FileTaskKind
    {
        Sink,
        Push,
        AppSink,
    }

    private sealed class FileTaskState
    {
        public FileTaskKind Kind { get; init; }

        public FileStream? Sink { get; set; }

        public bool PushPending { get; set; }

        public long Received { get; set; }

        /// <summary>从端待收的总字节数（真机 daemon 以 indexIO>=fileSize 判定自身 IO 完成）。</summary>
        public long FileSize { get; set; }

        /// <summary>从端是否已因自身 IO 完成而发出过 FILE_FINISH[1]。</summary>
        public bool SlaveFinished { get; set; }

        /// <summary>落盘状态的互斥锁：每个 DATA 帧都会调度一个 20ms 延迟落盘任务，它们会并发触达同一 state。</summary>
        public object SyncRoot { get; } = new();

        /// <summary>已收到但尚未落盘的一块（模拟真机 daemon uv_fs_write 的异步滞后）。</summary>
        public (long Index, byte[] Data)? Pending { get; set; }

        /// <summary>延迟落盘任务：模拟写完成回调晚于下一帧到达。</summary>
        public Task? PendingFlush { get; set; }

        /// <summary>已真正落盘的字节数（真机 daemon 的 indexIO）。</summary>
        public long Written { get; set; }

        /// <summary>本通道累计接收字节与文件数（供收尾 ECHO 汇总文本）。</summary>
        public long TotalReceived { get; set; }

        public int FilesReceived { get; set; }

        /// <summary>push 目录剧本：待推文件序列、下一个待 CHECK 的下标、当前推的文件与宿主落盘目标。</summary>
        public IReadOnlyList<(string Name, byte[] Content)>? PushFiles { get; set; }

        public int PushIndex { get; set; }

        public (string Name, byte[] Content) PushCurrent { get; set; }

        public string PushPath { get; set; } = ".";
    }

    /// <summary>转发剧本里 daemon 侧与某个 cid 绑定的目标连接。</summary>
    private sealed class TargetLink
    {
        public required TcpClient Client { get; init; }

        public required NetworkStream Stream { get; init; }

        /// <summary>fport 由 daemon 自己发 ACTIVE_MASTER，连接成功即可推流；rport 需等宿主回 ACTIVE_MASTER。</summary>
        public TaskCompletionSource Active { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Pump { get; set; } = Task.CompletedTask;
    }

    /// <summary>单个宿主连接上的转发剧本状态。</summary>
    private sealed class ForwardRuntime
    {
        public ConcurrentDictionary<uint, TargetLink> Targets { get; } = new();

        public TcpListener? ReverseListener { get; set; }

        public uint ReverseCheckCid { get; set; }

        public string ReverseCommand { get; set; } = "";

        public Task? ReverseAcceptTask { get; set; }
    }

    /// <summary>在回环随机端口上启动假 daemon，并立即开始接受连接。</summary>
    /// <param name="options">剧本选项；null 时使用默认（Rust 世代、免认证、devname="fake-dev"）。</param>
    public FakeDaemon(FakeDaemonOptions? options = null)
    {
        _options = options ?? new FakeDaemonOptions();
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptTask = Task.Run(AcceptLoopAsync);
    }

    /// <summary>监听端口（绑定在 127.0.0.1）。</summary>
    public int Port { get; }

    /// <summary>收到的非握手帧快照（通道号与命令字），按到达顺序；认证剧本中的握手帧不入内。</summary>
    public IReadOnlyList<(uint ChannelId, HdcCommand Cmd)> ReceivedCommands
    {
        get
        {
            lock (_gate)
            {
                return _received.ToArray();
            }
        }
    }

    /// <summary>收到的全部帧快照（含握手帧与载荷），按到达顺序。</summary>
    public IReadOnlyList<Frame> ReceivedFrames
    {
        get
        {
            lock (_gate)
            {
                return _receivedFrames.ToArray();
            }
        }
    }

    /// <summary>host 在 AUTH_PUBLICKEY 中提交的 PEM 公钥；尚未收到时为 null。</summary>
    public string? RecordedHostPublicKeyPem
    {
        get
        {
            lock (_gate)
            {
                return _hostPublicKeyPem;
            }
        }
    }

    /// <summary>host 对签名挑战的应答是否通过校验（仅认证剧本会置 true）。</summary>
    public bool SignatureVerified
    {
        get
        {
            lock (_gate)
            {
                return _signatureVerified;
            }
        }
    }

    /// <summary>停止监听、断开全部连接并等待后台任务退出；可重复调用。</summary>
    public void Dispose()
    {
        GC.SuppressFinalize(this);
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _cts.Cancel();
        _listener.Stop();
        foreach (TcpClient client in _clients.Values)
        {
            client.Close();
        }

        WaitQuietly(_acceptTask);
        WaitQuietly(Task.WhenAll(_connections.Values.ToArray()));
        _sendGate.Dispose();
        _cts.Dispose();
    }

    private static void WaitQuietly(Task task)
    {
        try
        {
            task.Wait(ShutdownTimeout);
        }
        catch (AggregateException)
        {
            // 测试替身的后台异常不影响释放语义
        }
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (true)
            {
                TcpClient client = await _listener.AcceptTcpClientAsync(_cts.Token);
                int id = Interlocked.Increment(ref _nextConnectionId);
                _clients[id] = client;
                _connections[id] = Task.Run(() => HandleConnectionAsync(client, id));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (SocketException)
        {
        }
    }

    private async Task HandleConnectionAsync(TcpClient client, int id)
    {
        Dictionary<uint, FileTaskState> fileStates = [];
        ForwardRuntime forward = new();
        try
        {
            NetworkStream stream = client.GetStream();
            await RunScriptAsync(stream, new FrameDecoder(), new byte[8192], fileStates, forward);
        }
        catch (Exception ex) when (IsConnectionFault(ex))
        {
            // 测试替身中，对端关闭或帧非法都视为该连接剧本结束
        }
        finally
        {
            foreach (FileTaskState state in fileStates.Values)
            {
                state.Sink?.Dispose();
            }

            await DisposeForwardAsync(forward);
            _clients.TryRemove(id, out _);
            _connections.TryRemove(id, out _);
            client.Dispose();
        }
    }

    private static bool IsConnectionFault(Exception ex)
    {
        return ex is HdcException or IOException or SocketException or ObjectDisposedException or OperationCanceledException;
    }

    private async Task RunScriptAsync(
        NetworkStream stream, FrameDecoder decoder, byte[] buffer, Dictionary<uint, FileTaskState> fileStates, ForwardRuntime forward)
    {
        CancellationToken ct = _cts.Token;
        if (await ReadFrameAsync(decoder, stream, buffer, ct) is not { Command: HdcCommand.KernelHandshake } helloFrame)
        {
            return;
        }

        SessionHandShake hello = SessionHandShake.Parse(helloFrame.Payload);
        if (_options.SendBadBanner)
        {
            // 保持连接等待对端关闭：让 host 必定先解析到非法 banner，而不是竞态地读到 EOF
            await SendHandshakeAsync(stream, "OTHER", 0, hello.SessionId, "", "", ct);
            await DrainAsync(decoder, stream, buffer, ct);
            return;
        }

        if (hello.Banner != HdcConstants.HandshakeMessage)
        {
            await SendHandshakeAsync(stream, HdcConstants.HandshakeFailed, 0, hello.SessionId, "", Version, ct);
            return;
        }

        if (!_options.RequireAuth)
        {
            await SendAuthOkAsync(stream, hello.SessionId, ct);
            await SendHandshakeCloseAsync(stream, ct);
            await SendStrayFrameIfConfiguredAsync(stream, ct);
            await RunTaskLoopAsync(decoder, stream, buffer, fileStates, forward, ct);
            return;
        }

        await SendHandshakeAsync(stream, HdcConstants.HandshakeMessage, 3, hello.SessionId, BuildAuthChallengeBuf(), Version, ct);
        if (_options.StallAfterPublicKey)
        {
            await DrainAsync(decoder, stream, buffer, ct);
            return;
        }

        if (await ReadFrameAsync(decoder, stream, buffer, ct) is not { Command: HdcCommand.KernelHandshake } publicKeyFrame)
        {
            return;
        }

        SessionHandShake publicKey = SessionHandShake.Parse(publicKeyFrame.Payload);
        if (publicKey.AuthType != 3)
        {
            return;
        }

        (string _, string publicKeyPem) = SplitHostAndPublicKey(publicKey.Buf);
        lock (_gate)
        {
            _hostPublicKeyPem = publicKeyPem;
        }

        string token = CreateToken();
        await SendHandshakeAsync(stream, HdcConstants.HandshakeMessage, 2, hello.SessionId, token, Version, ct);
        if (await ReadFrameAsync(decoder, stream, buffer, ct) is not { Command: HdcCommand.KernelHandshake } signatureFrame)
        {
            return;
        }

        SessionHandShake signature = SessionHandShake.Parse(signatureFrame.Payload);
        if (signature.AuthType != 2)
        {
            return;
        }

        if (!VerifySignature(publicKeyPem, token, signature.Buf, Scheme))
        {
            await SendHandshakeAsync(stream, HdcConstants.HandshakeMessage, 5, hello.SessionId, "signature not match", Version, ct);
            return;
        }

        lock (_gate)
        {
            _signatureVerified = true;
        }

        await SendAuthOkAsync(stream, hello.SessionId, ct);
        await SendHandshakeCloseAsync(stream, ct);
        await SendStrayFrameIfConfiguredAsync(stream, ct);
        await RunTaskLoopAsync(decoder, stream, buffer, fileStates, forward, ct);
    }

    private async Task RunTaskLoopAsync(
        FrameDecoder decoder, NetworkStream stream, byte[] buffer, Dictionary<uint, FileTaskState> fileStates, ForwardRuntime forward, CancellationToken ct)
    {
        while (await ReadFrameAsync(decoder, stream, buffer, ct) is { } frame)
        {
            await HandleTaskFrameAsync(stream, frame, fileStates, forward, ct);
        }
    }

    private async Task HandleTaskFrameAsync(
        NetworkStream stream, Frame frame, Dictionary<uint, FileTaskState> fileStates, ForwardRuntime forward, CancellationToken ct)
    {
        if (await TryHandleForwardFrameAsync(stream, frame, forward, ct))
        {
            return;
        }

        if (await TryHandleAppFrameAsync(stream, frame, fileStates, ct))
        {
            return;
        }

        if (await TryHandleFileFrameAsync(stream, frame, fileStates, ct))
        {
            return;
        }

        switch (frame.Command)
        {
            case HdcCommand.UnityExecute:
                await RunShellOutputScriptAsync(stream, frame, Encoding.UTF8.GetString(frame.Payload), ct);
                break;
            case HdcCommand.UnityExecuteEx:
                await RunShellOutputScriptAsync(stream, frame, string.Empty, ct);
                break;
            case HdcCommand.ShellData when _options.InteractiveEcho:
                await SendFrameAsync(stream, frame.ChannelId, HdcCommand.KernelEchoRaw, frame.Payload, ct);
                if (Array.IndexOf(frame.Payload, (byte)0x04) >= 0)
                {
                    await SendFrameAsync(stream, frame.ChannelId, HdcCommand.KernelChannelClose, [1], ct);
                }

                break;
            case HdcCommand.KernelChannelClose when frame.Payload.Length > 0 && frame.Payload[0] > 0:
                // 与 daemon 的跳数语义一致：收到非零 CLOSE 时递减回发一次
                await SendFrameAsync(
                    stream, frame.ChannelId, HdcCommand.KernelChannelClose, [(byte)(frame.Payload[0] - 1)], ct);
                break;
            default:
                break;
        }
    }

    /// <summary>
    /// 端口转发剧本（对齐上游 daemon：src/common/forward.cpp、hdc_rust/src/common/forward.rs）。
    /// fport：CHECK → CHECK_RESULT；ACTIVE_SLAVE → 拨通节点并回 ACTIVE_MASTER；DATA 双向搬运；FREE_CONTEXT 关目标连接。
    /// rport：INIT → 设备侧监听远端端口 → 推 WAKEUP + CHECK → 收 CHECK_RESULT 后回 FORWARD_SUCCESS；accept 后推 ACTIVE_SLAVE。
    /// </summary>
    private async Task<bool> TryHandleForwardFrameAsync(
        NetworkStream stream, Frame frame, ForwardRuntime forward, CancellationToken ct)
    {
        if (!_options.ForwardEnabled && !_options.ForwardReverseEnabled)
        {
            return false;
        }

        switch (frame.Command)
        {
            case HdcCommand.KernelWakeupSlavetask:
                // 宿主 fport 用它预建 daemon 侧任务槽；本替身无任务槽，仅吸收
                return true;
            case HdcCommand.ForwardCheck when _options.ForwardEnabled:
                await HandleForwardCheckAsync(stream, frame, ct).ConfigureAwait(false);
                return true;
            case HdcCommand.ForwardActiveSlave when _options.ForwardEnabled:
                await HandleForwardActiveSlaveAsync(stream, frame, forward, ct).ConfigureAwait(false);
                return true;
            case HdcCommand.ForwardInit when _options.ForwardReverseEnabled:
                await HandleReverseInitAsync(stream, frame, forward, ct).ConfigureAwait(false);
                return true;
            case HdcCommand.ForwardCheckResult when forward.ReverseCheckCid != 0:
                await HandleReverseCheckResultAsync(stream, frame, forward, ct).ConfigureAwait(false);
                return true;
            case HdcCommand.ForwardActiveMaster when TryReadCid(frame.Payload, out uint masterCid):
                if (forward.Targets.TryGetValue(masterCid, out TargetLink? master))
                {
                    master.Active.TrySetResult();
                }

                return true;
            case HdcCommand.ForwardData:
                await HandleForwardDataAsync(stream, frame, forward, ct).ConfigureAwait(false);
                return true;
            case HdcCommand.ForwardFreeContext:
                if (TryReadCid(frame.Payload, out uint freeCid) && forward.Targets.TryRemove(freeCid, out TargetLink? freeTarget))
                {
                    freeTarget.Client.Close();
                }

                return true;
            default:
                return false;
        }
    }

    private async Task HandleForwardCheckAsync(NetworkStream stream, Frame frame, CancellationToken ct)
    {
        if (!TryReadCid(frame.Payload, out uint cid))
        {
            return;
        }

        // 上游两世代 daemon 对 TCP 节点均回 0（真相“可达”由载荷存在与否表达，宿主的取值判断是历史缺陷）
        byte[] payload = _options.ForwardRejectCheck
            ? CidPayload(cid)
            : [.. CidPayload(cid), _options.ForwardCheckResultFlag];
        await SendFrameAsync(stream, frame.ChannelId, HdcCommand.ForwardCheckResult, payload, ct).ConfigureAwait(false);
    }

    private async Task HandleForwardActiveSlaveAsync(
        NetworkStream stream, Frame frame, ForwardRuntime forward, CancellationToken ct)
    {
        if (!TryReadCid(frame.Payload, out uint cid) || !TryParseNode(ReadNode(frame.Payload), out int port))
        {
            return;
        }

        TcpClient? client = null;
        TargetLink? link;
        try
        {
            client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port, ct).ConfigureAwait(false);
            client.NoDelay = true;
            link = new TargetLink { Client = client, Stream = client.GetStream() };
        }
        catch (Exception ex) when (ex is SocketException or IOException or ObjectDisposedException or OperationCanceledException)
        {
            client?.Dispose();
            await SendFrameAsync(stream, frame.ChannelId, HdcCommand.ForwardFreeContext, CidPayload(cid), ct).ConfigureAwait(false);
            return;
        }

        link.Active.TrySetResult();
        if (!forward.Targets.TryAdd(cid, link))
        {
            link.Client.Close();
            await SendFrameAsync(stream, frame.ChannelId, HdcCommand.ForwardFreeContext, CidPayload(cid), ct).ConfigureAwait(false);
            return;
        }

        await SendFrameAsync(stream, frame.ChannelId, HdcCommand.ForwardActiveMaster, CidPayload(cid), ct).ConfigureAwait(false);
        link.Pump = Task.Run(() => PumpTargetAsync(stream, frame.ChannelId, cid, link, forward, ct), CancellationToken.None);
    }

    private async Task HandleForwardDataAsync(
        NetworkStream stream, Frame frame, ForwardRuntime forward, CancellationToken ct)
    {
        if (frame.Payload.Length <= ForwardCidSize || !TryReadCid(frame.Payload, out uint cid))
        {
            return;
        }

        if (!forward.Targets.TryGetValue(cid, out TargetLink? link))
        {
            return;
        }

        try
        {
            await link.Stream.WriteAsync(frame.Payload.AsMemory(ForwardCidSize), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            if (forward.Targets.TryRemove(cid, out TargetLink? dead))
            {
                dead.Client.Close();
            }

            await SendFrameAsync(stream, frame.ChannelId, HdcCommand.ForwardFreeContext, CidPayload(cid), ct).ConfigureAwait(false);
        }
    }

    private async Task PumpTargetAsync(
        NetworkStream stream, uint channelId, uint cid, TargetLink link, ForwardRuntime forward, CancellationToken ct)
    {
        byte[] buffer = new byte[32 * 1024];
        try
        {
            await link.Active.Task.WaitAsync(ct).ConfigureAwait(false);
            while (true)
            {
                int read = await link.Stream.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                byte[] payload = new byte[ForwardCidSize + read];
                BinaryPrimitives.WriteUInt32BigEndian(payload, cid);
                buffer.AsSpan(0, read).CopyTo(payload.AsSpan(ForwardCidSize));
                await SendFrameAsync(stream, channelId, HdcCommand.ForwardData, payload, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
        }
        finally
        {
            if (forward.Targets.TryRemove(cid, out _))
            {
                try
                {
                    await SendFrameAsync(
                        stream, channelId, HdcCommand.ForwardFreeContext, CidPayload(cid), CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
                {
                }
            }

            link.Client.Dispose();
        }
    }

    private async Task HandleReverseInitAsync(
        NetworkStream stream, Frame frame, ForwardRuntime forward, CancellationToken ct)
    {
        string command = Encoding.UTF8.GetString(frame.Payload);
        string[] nodes = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (nodes.Length < 2 || !TryParseNode(nodes[0], out int remotePort) || !TryParseNode(nodes[1], out int localNodePort))
        {
            byte[] failure = [(byte)MessageLevel.Fail, .. Encoding.UTF8.GetBytes("Forward parament failed")];
            await SendFrameAsync(stream, frame.ChannelId, HdcCommand.KernelEcho, failure, ct).ConfigureAwait(false);
            await SendFrameAsync(stream, frame.ChannelId, HdcCommand.KernelChannelClose, [1], ct).ConfigureAwait(false);
            return;
        }

        var listener = new TcpListener(IPAddress.Loopback, remotePort);
        listener.Start();
        forward.ReverseListener = listener;
        forward.ReverseCommand = command;
        forward.ReverseCheckCid = NewCid();
        string localNode = $"tcp:{localNodePort}";
        await SendFrameAsync(stream, frame.ChannelId, HdcCommand.KernelWakeupSlavetask, [], ct).ConfigureAwait(false);
        await SendFrameAsync(
            stream, frame.ChannelId, HdcCommand.ForwardCheck, ParameterPayload(forward.ReverseCheckCid, localNode), ct)
            .ConfigureAwait(false);
        uint channelId = frame.ChannelId;
        forward.ReverseAcceptTask = Task.Run(
            () => ReverseAcceptLoopAsync(stream, channelId, listener, localNode, forward, ct), CancellationToken.None);
    }

    private async Task HandleReverseCheckResultAsync(
        NetworkStream stream, Frame frame, ForwardRuntime forward, CancellationToken ct)
    {
        if (!TryReadCid(frame.Payload, out uint cid) || cid != forward.ReverseCheckCid)
        {
            return;
        }

        // serverOrDaemon=false（daemon 侧任务）→ "0|"，宿主据此识别为反向转发
        byte[] payload = Encoding.UTF8.GetBytes("0|" + forward.ReverseCommand);
        await SendFrameAsync(stream, frame.ChannelId, HdcCommand.ForwardSuccess, payload, ct).ConfigureAwait(false);
    }

    private async Task ReverseAcceptLoopAsync(
        NetworkStream stream, uint channelId, TcpListener listener, string localNode, ForwardRuntime forward, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                TcpClient client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                client.NoDelay = true;
                uint cid = NewCid();
                var link = new TargetLink { Client = client, Stream = client.GetStream() };
                if (!forward.Targets.TryAdd(cid, link))
                {
                    client.Dispose();
                    continue;
                }

                await SendFrameAsync(stream, channelId, HdcCommand.ForwardActiveSlave, ParameterPayload(cid, localNode), ct)
                    .ConfigureAwait(false);
                link.Pump = Task.Run(() => PumpTargetAsync(stream, channelId, cid, link, forward, ct), CancellationToken.None);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException or IOException)
        {
        }
    }

    private static async Task DisposeForwardAsync(ForwardRuntime forward)
    {
        forward.ReverseListener?.Stop();
        TargetLink[] links = [.. forward.Targets.Values];
        foreach (TargetLink link in links)
        {
            link.Client.Close();
        }

        if (forward.ReverseAcceptTask is { } accept)
        {
            await WaitQuietlyAsync(accept).ConfigureAwait(false);
        }

        foreach (TargetLink link in links)
        {
            await WaitQuietlyAsync(link.Pump).ConfigureAwait(false);
        }
    }

    private static async Task WaitQuietlyAsync(Task task)
    {
        try
        {
            await task.WaitAsync(ShutdownTimeout).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 测试替身后台异常不影响释放语义
        }
    }

    private const int ForwardCidSize = 4;

    private const int ForwardParameterPrefixSize = 8;

    private static byte[] CidPayload(uint cid)
    {
        byte[] payload = new byte[ForwardCidSize];
        BinaryPrimitives.WriteUInt32BigEndian(payload, cid);
        return payload;
    }

    private static byte[] ParameterPayload(uint cid, string node)
    {
        byte[] nodeBytes = Encoding.UTF8.GetBytes(node);
        byte[] payload = new byte[ForwardCidSize + ForwardParameterPrefixSize + nodeBytes.Length + 1];
        BinaryPrimitives.WriteUInt32BigEndian(payload, cid);
        nodeBytes.CopyTo(payload, ForwardCidSize + ForwardParameterPrefixSize);
        return payload;
    }

    private static bool TryReadCid(byte[] payload, out uint cid)
    {
        if (payload.Length < ForwardCidSize)
        {
            cid = 0;
            return false;
        }

        cid = BinaryPrimitives.ReadUInt32BigEndian(payload);
        return true;
    }

    private static string ReadNode(byte[] payload)
    {
        int start = ForwardCidSize + ForwardParameterPrefixSize;
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

    private static bool TryParseNode(string node, out int port)
    {
        port = 0;
        return node.StartsWith("tcp:", StringComparison.Ordinal)
            && int.TryParse(node.AsSpan(4), out port)
            && port is > 0 and <= 65535;
    }

    private static uint NewCid()
    {
        return (uint)Random.Shared.NextInt64(1, 1L + uint.MaxValue);
    }

    /// <summary>
    /// 应用安装/卸载剧本（对齐上游从端语义）：APP_CHECK 建临时文件并回 APP_BEGIN（APP 流程无 FINISH 握手），
    /// APP_DATA 按槽内 index 落盘并在收到声明字节数后回 APP_FINISH；APP_UNINSTALL 直接回 APP_FINISH(mode=2)。
    /// </summary>
    private async Task<bool> TryHandleAppFrameAsync(
        NetworkStream stream, Frame frame, Dictionary<uint, FileTaskState> fileStates, CancellationToken ct)
    {
        switch (frame.Command)
        {
            case HdcCommand.AppCheck when _options.AppSinkDirectory is { } sinkDirectory:
                await HandleAppCheckAsync(stream, frame, fileStates, sinkDirectory, ct).ConfigureAwait(false);
                return true;
            case HdcCommand.AppData when fileStates.TryGetValue(frame.ChannelId, out FileTaskState? state) &&
                                          state.Kind == FileTaskKind.AppSink:
                await HandleAppDataAsync(stream, frame, state, ct).ConfigureAwait(false);
                return true;
            case HdcCommand.AppUninstall when _options.AppUninstallEnabled:
                await SendAppFinishAsync(stream, frame.ChannelId, AppModeUninstall, ct).ConfigureAwait(false);
                return true;
            default:
                return false;
        }
    }

    private async Task HandleAppCheckAsync(
        NetworkStream stream, Frame frame, Dictionary<uint, FileTaskState> fileStates, string sinkDirectory, CancellationToken ct)
    {
        TransferConfig config = TransferConfig.Parse(frame.Payload);
        Directory.CreateDirectory(sinkDirectory);
        string target = Path.Combine(sinkDirectory, NormalizeSeparators(config.OptionalName));
        fileStates[frame.ChannelId] = new FileTaskState
        {
            Kind = FileTaskKind.AppSink,
            Sink = new FileStream(target, System.IO.FileMode.Create, FileAccess.Write, FileShare.Read),
            FileSize = (long)config.FileSize,
        };
        if (_options.AppSendBegin)
        {
            await SendFrameAsync(stream, frame.ChannelId, HdcCommand.AppBegin, [], ct).ConfigureAwait(false);
            return;
        }

        await SendAppFinishAsync(stream, frame.ChannelId, AppModeInstall, ct).ConfigureAwait(false);
    }

    private async Task HandleAppDataAsync(
        NetworkStream stream, Frame frame, FileTaskState state, CancellationToken ct)
    {
        if (frame.Payload.Length < HdcConstants.TransferSlotSize || state.Sink is null)
        {
            return;
        }

        TransferPayload head = TransferPayload.ParseSlot(frame.Payload.AsSpan(0, HdcConstants.TransferSlotSize));
        int size = (int)head.CompressSize;
        if (size < 0 || frame.Payload.Length - HdcConstants.TransferSlotSize < size)
        {
            return;
        }

        if (_options.AppDataDelayMs > 0)
        {
            await Task.Delay(_options.AppDataDelayMs, ct).ConfigureAwait(false);
        }

        state.Sink.Seek((long)head.Index, SeekOrigin.Begin);
        state.Sink.Write(frame.Payload, HdcConstants.TransferSlotSize, size);
        state.Sink.Flush();
        state.Received += size;
        if (state.Received >= state.FileSize)
        {
            // 真机 daemon 在传输完成后立即关闭临时文件再跑 bm（daemon_app.cpp:143-148），此处同样落盘并释放句柄
            CloseSink(state);
            await SendAppFinishAsync(stream, frame.ChannelId, AppModeInstall, ct).ConfigureAwait(false);
        }
    }

    private async Task SendAppFinishAsync(NetworkStream stream, uint channelId, byte mode, CancellationToken ct)
    {
        byte[] text = Encoding.UTF8.GetBytes(_options.AppFinishMessage);
        byte[] payload = new byte[text.Length + 2];
        payload[0] = mode;
        payload[1] = _options.AppFinishSuccess;
        text.CopyTo(payload, 2);
        await SendFrameAsync(stream, channelId, HdcCommand.AppFinish, payload, ct).ConfigureAwait(false);
    }

    private async Task<bool> TryHandleFileFrameAsync(
        NetworkStream stream, Frame frame, Dictionary<uint, FileTaskState> fileStates, CancellationToken ct)
    {
        switch (frame.Command)
        {
            case HdcCommand.KernelWakeupSlavetask:
                return _options.FileRecvSinkDirectory is not null || _options.FilePushData is not null;
            case HdcCommand.FileInit when _options.FilePushData is not null || _options.FilePushFiles is not null:
                return await HandleFileInitPushAsync(stream, frame, fileStates, ct);
            case HdcCommand.FileCheck when _options.FileRecvSinkDirectory is { } sinkDirectory:
                return await HandleFileCheckAsync(stream, frame, fileStates, sinkDirectory, ct);
            case HdcCommand.FileBegin when fileStates.TryGetValue(frame.ChannelId, out FileTaskState? beginState) &&
                                           beginState.Kind == FileTaskKind.Push && beginState.PushPending:
                return await PushFileDataAsync(stream, frame.ChannelId, beginState, ct);
            case HdcCommand.FileData when fileStates.TryGetValue(frame.ChannelId, out FileTaskState? dataState) &&
                                          dataState.Kind == FileTaskKind.Sink:
                await WriteSinkDataAsync(stream, frame, dataState, ct);
                return true;
            case HdcCommand.FileFinish when fileStates.TryGetValue(frame.ChannelId, out FileTaskState? finishState) &&
                                            finishState.Kind == FileTaskKind.Push:
                return await HandlePushFinishAsync(stream, frame, finishState, ct);
            case HdcCommand.FileFinish when fileStates.TryGetValue(frame.ChannelId, out FileTaskState? sinkState) &&
                                            sinkState.Kind == FileTaskKind.Sink:
                return await HandleSinkFinishAsync(stream, frame, sinkState, ct);
            default:
                return false;
        }
    }

    private async Task<bool> HandleFileInitPushAsync(
        NetworkStream stream, Frame frame, Dictionary<uint, FileTaskState> fileStates, CancellationToken ct)
    {
        IReadOnlyList<(string Name, byte[] Content)> files = _options.FilePushFiles is { Count: > 0 } directory
            ? directory
            : [(_options.FilePushName, _options.FilePushData ?? [])];
        string[] tokens = Encoding.UTF8.GetString(frame.Payload).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string localPath = tokens.Length > 1 ? tokens[1] : ".";
        var state = new FileTaskState
        {
            Kind = FileTaskKind.Push,
            PushPending = true,
            PushFiles = files,
            PushIndex = 1,
            PushCurrent = files[0],
            PushPath = localPath,
        };
        fileStates[frame.ChannelId] = state;
        await SendFrameAsync(stream, frame.ChannelId, HdcCommand.KernelWakeupSlavetask, [], ct);
        await SendPushCheckAsync(stream, frame.ChannelId, state, ct);
        return true;
    }

    private Task SendPushCheckAsync(NetworkStream stream, uint channelId, FileTaskState state, CancellationToken ct)
    {
        var config = new TransferConfig
        {
            FileSize = (ulong)state.PushCurrent.Content.Length,
            Path = state.PushPath,
            OptionalName = state.PushCurrent.Name,
        };
        return SendFrameAsync(stream, channelId, HdcCommand.FileCheck, config.Serialize(), ct);
    }

    private async Task<bool> HandleFileCheckAsync(
        NetworkStream stream, Frame frame, Dictionary<uint, FileTaskState> fileStates, string sinkDirectory, CancellationToken ct)
    {
        TransferConfig config = TransferConfig.Parse(frame.Payload);
        Directory.CreateDirectory(sinkDirectory);
        long totalReceived = 0;
        int filesReceived = 0;
        if (fileStates.TryGetValue(frame.ChannelId, out FileTaskState? previous))
        {
            // 目录模式同一通道的下一个 CHECK：真机 daemon 在前一文件收尾时 CloseCtxFd；此处关闭上一文件流
            long previousTotal;
            int previousFiles;
            lock (previous.SyncRoot)
            {
                CloseSink(previous);
                previousTotal = previous.TotalReceived;
                previousFiles = previous.FilesReceived;
            }

            totalReceived = previousTotal;
            filesReceived = previousFiles;
        }

        string relative = ResolveSinkRelativePath(config);
        string target = Path.Combine(sinkDirectory, NormalizeSeparators(relative));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target))!);
        fileStates[frame.ChannelId] = new FileTaskState
        {
            Kind = FileTaskKind.Sink,
            Sink = new FileStream(target, System.IO.FileMode.Create, FileAccess.Write, FileShare.Read),
            FileSize = (long)config.FileSize,
            TotalReceived = totalReceived,
            FilesReceived = filesReceived + 1,
        };
        await SendFrameAsync(stream, frame.ChannelId, HdcCommand.FileBegin, _options.FileBeginPayload ?? [], ct);
        return true;
    }

    /// <summary>
    /// 按上游 daemon 的落盘路径拼接建模 optionalName（transfer.cpp:857-873 SmartSlavePath、801-855 CheckFilename）：
    /// optionalName 无分隔符 = 单文件（既有剧本）；含分隔符 = 目录模式——远端路径以分隔符结尾视为「已存在目录」
    /// 保留全路径，否则视为「目标不存在」丢弃首层（真机 daemon 的 targetDirNotExist 语义）。
    /// </summary>
    private static string ResolveSinkRelativePath(TransferConfig config)
    {
        string optionalName = config.OptionalName;
        if (optionalName.Length == 0)
        {
            return Path.GetFileName(config.Path);
        }

        bool directoryMode = optionalName.Contains('/') || optionalName.Contains('\\');
        if (!directoryMode)
        {
            return config.Path.EndsWith('/') || config.Path.EndsWith('\\')
                ? optionalName
                : Path.GetFileName(config.Path);
        }

        if (config.Path.EndsWith('/') || config.Path.EndsWith('\\'))
        {
            return optionalName;
        }

        int separator = optionalName.IndexOfAny(['/', '\\']);
        return separator >= 0 && separator + 1 < optionalName.Length ? optionalName[(separator + 1)..] : optionalName;
    }

    private static string NormalizeSeparators(string relative)
    {
        return relative.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
    }

    private async Task WriteSinkDataAsync(NetworkStream stream, Frame frame, FileTaskState state, CancellationToken ct)
    {
        if (frame.Payload.Length < HdcConstants.TransferSlotSize || state.Sink is null)
        {
            return;
        }

        TransferPayload head = TransferPayload.ParseSlot(frame.Payload.AsSpan(0, HdcConstants.TransferSlotSize));
        int size = (int)head.CompressSize;
        if (size < 0 || frame.Payload.Length - HdcConstants.TransferSlotSize < size)
        {
            return;
        }

        // 真机 daemon 的 uv_fs_write 是异步的：写完成回调晚于后续帧到达。此处把每块延后一小段
        // 时间落盘，从而确定性地重现「主端抢先发 FILE_FINISH[1] → daemon 立即关 fd → 挂起写丢失」的丢尾
        lock (state.SyncRoot)
        {
            FlushPending(state);
            state.Pending = ((long)head.Index, frame.Payload[HdcConstants.TransferSlotSize..(HdcConstants.TransferSlotSize + size)]);
            state.Received += size;
            state.TotalReceived += size;
        }

        state.PendingFlush = CompletePendingAfterDelayAsync(stream, frame.ChannelId, state);
    }

    private async Task CompletePendingAfterDelayAsync(NetworkStream stream, uint channelId, FileTaskState state)
    {
        await Task.Delay(20, _cts.Token).ConfigureAwait(false);
        bool completed;
        lock (state.SyncRoot)
        {
            FlushPending(state);
            completed = !state.SlaveFinished && state.Written >= state.FileSize;
            if (completed)
            {
                state.SlaveFinished = true;
            }
        }

        if (completed)
        {
            await SendFrameAsync(stream, channelId, HdcCommand.FileFinish, [1], _cts.Token).ConfigureAwait(false);
        }
    }

    private static void FlushPending(FileTaskState state)
    {
        lock (state.SyncRoot)
        {
            if (state.Pending is not { } pending || state.Sink is null)
            {
                state.Pending = null;
                return;
            }

            state.Sink.Seek(pending.Index, SeekOrigin.Begin);
            state.Sink.Write(pending.Data);
            state.Written += pending.Data.Length;
            state.Pending = null;
        }
    }

    private async Task<bool> HandleSinkFinishAsync(NetworkStream stream, Frame frame, FileTaskState state, CancellationToken ct)
    {
        if (frame.Payload.Length == 0 || !_options.FileAcknowledgeFinish)
        {
            return true;
        }

        if (frame.Payload[0] == 1)
        {
            // 模拟真机 daemon 收到主端 FILE_FINISH[1] 的行为：立即 CloseCtxFd 不等挂起写完成，
            // 回 [0] 且不走 TaskFinish（src/common/file.cpp:647-668）。主端不应抢先发 [1]——
            // 抢先发会丢弃尚未落盘的尾块，正是该剧本要抓的回归
            lock (state.SyncRoot)
            {
                state.PendingFlush = null;
                state.Pending = null;
                CloseSink(state);
            }

            await SendFrameAsync(stream, frame.ChannelId, HdcCommand.FileFinish, [0], ct);
            return true;
        }

        // 主端回 [0] → 从端 TransferSummary + TaskFinish（ECHO + CHANNEL_CLOSE）
        long totalReceived;
        int filesReceived;
        lock (state.SyncRoot)
        {
            state.PendingFlush = null;
            FlushPending(state);
            CloseSink(state);
            totalReceived = state.TotalReceived;
            filesReceived = state.FilesReceived;
        }

        byte[] text = Encoding.UTF8.GetBytes(
            $"FileTransfer finish, Size:{totalReceived}, File count = {filesReceived}, time:0ms rate:0.00kB/s");
        byte[] echo = new byte[text.Length + 1];
        echo[0] = (byte)MessageLevel.Ok;
        text.CopyTo(echo, 1);
        await SendFrameAsync(stream, frame.ChannelId, HdcCommand.KernelEcho, echo, ct);
        await SendFrameAsync(stream, frame.ChannelId, HdcCommand.FileFinish, [1], ct);
        await SendFrameAsync(stream, frame.ChannelId, HdcCommand.KernelChannelClose, [1], ct);
        return true;
    }

    private static void CloseSink(FileTaskState state)
    {
        state.Sink?.Flush();
        state.Sink?.Dispose();
        state.Sink = null;
    }

    private async Task<bool> PushFileDataAsync(NetworkStream stream, uint channelId, FileTaskState state, CancellationToken ct)
    {
        state.PushPending = false;
        byte[] data = state.PushCurrent.Content;
        int offset = 0;
        do
        {
            int size = Math.Min(HdcConstants.MaxFileChunkSize, data.Length - offset);
            byte[] payload = new byte[HdcConstants.TransferSlotSize + size];
            var head = new TransferPayload
            {
                Index = (ulong)offset,
                CompressType = 0,
                CompressSize = (uint)size,
                UncompressSize = (uint)size,
            };
            head.Serialize().CopyTo(payload, 0);
            data.AsSpan(offset, size).CopyTo(payload.AsSpan(HdcConstants.TransferSlotSize));
            await SendFrameAsync(stream, channelId, HdcCommand.FileData, payload, ct);
            offset += size;
        }
        while (offset < data.Length);

        await SendFrameAsync(stream, channelId, HdcCommand.FileFinish, [1], ct);
        return true;
    }

    private async Task<bool> HandlePushFinishAsync(
        NetworkStream stream, Frame frame, FileTaskState state, CancellationToken ct)
    {
        if (frame.Payload.Length == 0 || frame.Payload[0] != 1)
        {
            return true;
        }

        if (state.PushFiles is { } files && state.PushIndex < files.Count)
        {
            // 上游主端收写端 [1] 后推进下一文件（file.cpp:651-653 TransferNext→CheckMaster），不先回 [0]
            state.PushCurrent = files[state.PushIndex];
            state.PushIndex++;
            state.PushPending = true;
            await SendPushCheckAsync(stream, frame.ChannelId, state, ct);
            return true;
        }

        await SendFrameAsync(stream, frame.ChannelId, HdcCommand.FileFinish, [0], ct);
        return true;
    }

    private async Task RunShellOutputScriptAsync(NetworkStream stream, Frame frame, string command, CancellationToken ct)
    {
        foreach (byte[] chunk in ResolveShellChunks(command))
        {
            await SendFrameAsync(stream, frame.ChannelId, HdcCommand.KernelEchoRaw, chunk, ct);
        }

        if (_options.ShellEchoMessages is { } messages)
        {
            foreach ((MessageLevel level, string text) in messages)
            {
                byte[] textBytes = Encoding.UTF8.GetBytes(text);
                byte[] payload = new byte[textBytes.Length + 1];
                payload[0] = (byte)level;
                textBytes.CopyTo(payload, 1);
                await SendFrameAsync(stream, frame.ChannelId, HdcCommand.KernelEcho, payload, ct);
            }
        }

        if (_options.ShellClosesAfterOutput)
        {
            await SendFrameAsync(stream, frame.ChannelId, HdcCommand.KernelChannelClose, [_options.ShellCloseHops], ct);
        }
    }

    private IReadOnlyList<byte[]> ResolveShellChunks(string command)
    {
        if (_options.ShellChunks is { } chunks)
        {
            return chunks;
        }

        return _options.EchoShellCommand ? [Encoding.UTF8.GetBytes(command)] : [];
    }

    private async Task SendStrayFrameIfConfiguredAsync(NetworkStream stream, CancellationToken ct)
    {
        if (_options.StrayEchoChannelId is { } channelId)
        {
            await SendFrameAsync(stream, channelId, HdcCommand.KernelEchoRaw, "stray"u8.ToArray(), ct);
        }
    }

    private async Task SendFrameAsync(NetworkStream stream, uint channelId, HdcCommand command, byte[] payload, CancellationToken ct)
    {
        // 转发泵与帧循环会并发写同一 NetworkStream，必须串行化整帧写出
        await _sendGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(FrameCodec.Encode(channelId, command, payload), ct).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private async Task DrainAsync(FrameDecoder decoder, NetworkStream stream, byte[] buffer, CancellationToken ct)
    {
        while (await ReadFrameAsync(decoder, stream, buffer, ct) is not null)
        {
        }
    }

    private async Task<Frame?> ReadFrameAsync(FrameDecoder decoder, NetworkStream stream, byte[] buffer, CancellationToken ct)
    {
        while (true)
        {
            if (decoder.TryRead(out Frame frame))
            {
                RecordCommand(frame);
                return frame;
            }

            int read = await stream.ReadAsync(buffer, ct);
            if (read == 0)
            {
                return null;
            }

            decoder.Append(buffer.AsSpan(0, read));
        }
    }

    private void RecordCommand(Frame frame)
    {
        lock (_gate)
        {
            _receivedFrames.Add(frame);
            if (frame.Command != HdcCommand.KernelHandshake)
            {
                _received.Add((frame.ChannelId, frame.Command));
            }
        }
    }

    /// <summary>创建“收到一次性 shell 后依次回各分块并关闭通道”的假 daemon（Rust 世代、免认证）。</summary>
    /// <param name="chunks">按 UTF-8 编码的回显分块。</param>
    public static FakeDaemon WithShellScript(params string[] chunks)
    {
        return new FakeDaemon(new FakeDaemonOptions { ShellChunks = chunks.Select(Encoding.UTF8.GetBytes).ToArray() });
    }

    /// <summary>创建“交互式 shell 回显”的假 daemon（Rust 世代、免认证）。</summary>
    public static FakeDaemon WithInteractiveEcho()
    {
        return new FakeDaemon(new FakeDaemonOptions { InteractiveEcho = true });
    }

    /// <summary>创建“按上游从端剧本接收 send 文件并落盘”的假 daemon（Rust 世代、免认证）。</summary>
    /// <param name="directory">落盘目录（文件名取远端 path 的 basename）。</param>
    public static FakeDaemon WithFileRecvSink(string directory)
    {
        return new FakeDaemon(new FakeDaemonOptions { FileRecvSinkDirectory = directory });
    }

    /// <summary>创建“收到 FILE_INIT 后主动推文件”的假 daemon（Rust 世代、免认证）。</summary>
    /// <param name="content">要推送的文件内容。</param>
    /// <param name="fileName">FILE_CHECK 中 optionalName 的取值。</param>
    public static FakeDaemon WithFilePush(byte[] content, string fileName)
    {
        return new FakeDaemon(new FakeDaemonOptions { FilePushData = content, FilePushName = fileName });
    }

    /// <summary>创建“收到 FILE_INIT 后按序推送多文件目录”的假 daemon（Rust 世代、免认证）。</summary>
    /// <param name="files">optionalName（含相对路径）与内容的序列，按给定顺序推送。</param>
    public static FakeDaemon WithFilePushDirectory(params (string Name, byte[] Content)[] files)
    {
        return new FakeDaemon(new FakeDaemonOptions { FilePushFiles = files });
    }

    private static readonly byte AppModeInstall = 1;

    private static readonly byte AppModeUninstall = 2;

    /// <summary>创建“收到 APP_CHECK/DATA 后把包落盘并回 APP_FINISH”的假 daemon（Rust 世代、免认证）。</summary>
    /// <param name="sinkDirectory">包落盘目录（文件名取 optionalName）；null 时不接管 APP 命令。</param>
    /// <param name="output">APP_FINISH 载荷自偏移 2 起的 bm 输出文本。</param>
    /// <param name="success">APP_FINISH 的 success 字节；false 时以输出文本报错。</param>
    public static FakeDaemon WithInstallScript(string? sinkDirectory = null, string output = "Success", bool success = true)
    {
        return new FakeDaemon(new FakeDaemonOptions
        {
            AppSinkDirectory = sinkDirectory,
            AppFinishMessage = output,
            AppFinishSuccess = success ? (byte)1 : (byte)0,
        });
    }

    /// <summary>创建“收到 APP_UNINSTALL 后回 APP_FINISH(mode=2)”的假 daemon（Rust 世代、免认证）。</summary>
    /// <param name="output">APP_FINISH 载荷自偏移 2 起的 bm 输出文本。</param>
    /// <param name="success">APP_FINISH 的 success 字节；false 时以输出文本报错。</param>
    public static FakeDaemon WithUninstallScript(string output = "Success", bool success = true)
    {
        return new FakeDaemon(new FakeDaemonOptions
        {
            AppUninstallEnabled = true,
            AppFinishMessage = output,
            AppFinishSuccess = success ? (byte)1 : (byte)0,
        });
    }

    private Task SendAuthOkAsync(NetworkStream stream, uint sessionId, CancellationToken ct)
    {
        var items = new List<(string Tag, string Value)>
        {
            (HdcConstants.TlvDevName, _options.DeviceName),
            (HdcConstants.TlvDaemonAuthStatus, HdcConstants.AuthStatusSuccess),
            (HdcConstants.TlvEmgMsg, ""),
        };
        if (_options.Generation == DaemonGeneration.Cpp)
        {
            items.Add(("1200", "enable"));
            items.Add((HdcConstants.TlvSupportFeatures, "heartbeat"));
        }

        return SendHandshakeAsync(stream, HdcConstants.HandshakeMessage, 4, sessionId, Tlv16.Serialize(items), Version, ct);
    }

    private static Task SendHandshakeCloseAsync(NetworkStream stream, CancellationToken ct)
    {
        return stream.WriteAsync(FrameCodec.Encode(0, HdcCommand.KernelChannelClose, new byte[] { 1 }), ct).AsTask();
    }

    private static Task SendHandshakeAsync(
        NetworkStream stream, string banner, byte authType, uint sessionId, string buf, string version, CancellationToken ct)
    {
        var message = new SessionHandShake
        {
            Banner = banner,
            AuthType = authType,
            SessionId = sessionId,
            Buf = buf,
            Version = version,
        };
        return stream.WriteAsync(FrameCodec.Encode(0, HdcCommand.KernelHandshake, message.Serialize()), ct).AsTask();
    }

    private static (string HostName, string PublicKeyPem) SplitHostAndPublicKey(string buf)
    {
        int separator = buf.IndexOf((char)HdcConstants.HostDaemonBufSeparator);
        return separator < 0
            ? (string.Empty, string.Empty)
            : (buf[..separator], buf[(separator + 1)..]);
    }

    private static bool VerifySignature(string publicKeyPem, string token, string signatureBase64, AuthScheme scheme)
    {
        if (publicKeyPem.Length == 0)
        {
            return false;
        }

        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(signatureBase64);
        }
        catch (FormatException)
        {
            return false;
        }

        using RSA rsa = RSA.Create();
        try
        {
            rsa.ImportFromPem(publicKeyPem);
        }
        catch (ArgumentException)
        {
            return false;
        }

        byte[] tokenBytes = Encoding.UTF8.GetBytes(token);
        if (scheme == AuthScheme.PssSha512)
        {
            return rsa.VerifyData(tokenBytes, signature, HashAlgorithmName.SHA512, RSASignaturePadding.Pss);
        }

        return VerifyPkcs1Block(rsa, tokenBytes, signature);
    }

    private static bool VerifyPkcs1Block(RSA rsa, byte[] token, byte[] signature)
    {
        RSAParameters parameters = rsa.ExportParameters(false);
        int modulusLength = (rsa.KeySize + 7) / 8;
        if (parameters.Modulus is null || parameters.Exponent is null || signature.Length != modulusLength)
        {
            return false;
        }

        BigInteger n = new(parameters.Modulus, isUnsigned: true, isBigEndian: true);
        BigInteger e = new(parameters.Exponent, isUnsigned: true, isBigEndian: true);
        BigInteger restored = BigInteger.ModPow(new BigInteger(signature, isUnsigned: true, isBigEndian: true), e, n);
        byte[] block = restored.ToByteArray(isUnsigned: true, isBigEndian: true);
        if (block.Length > modulusLength)
        {
            return false;
        }

        byte[] padded = new byte[modulusLength];
        block.CopyTo(padded, modulusLength - block.Length);
        return padded.AsSpan().SequenceEqual(BuildType1Block(token, modulusLength));
    }

    private static byte[] BuildType1Block(byte[] data, int modulusLength)
    {
        byte[] block = new byte[modulusLength];
        block[0] = 0x00;
        block[1] = 0x01;
        int paddingLength = modulusLength - 3 - data.Length;
        Array.Fill(block, (byte)0xFF, 2, paddingLength);
        block[2 + paddingLength] = 0x00;
        data.CopyTo(block, 3 + paddingLength);
        return block;
    }

    private string CreateToken()
    {
        return _options.Generation == DaemonGeneration.Cpp
            ? ToHex(RandomNumberGenerator.GetBytes(10), upperCase: false)
            : ToHex(RandomNumberGenerator.GetBytes(32), upperCase: true);
    }

    private static string ToHex(byte[] bytes, bool upperCase)
    {
        string alphabet = upperCase ? "0123456789ABCDEF" : "0123456789abcdef";
        var builder = new StringBuilder(bytes.Length * 2);
        foreach (byte value in bytes)
        {
            builder.Append(alphabet[value >> 4]).Append(alphabet[value & 0x0F]);
        }

        return builder.ToString();
    }

    private string BuildAuthChallengeBuf()
    {
        var items = new List<(string Tag, string Value)>();
        if (_options.Generation == DaemonGeneration.Cpp)
        {
            items.Add((HdcConstants.TlvAuthType, "1"));
        }
        else
        {
            items.Add((HdcConstants.TlvSupportFeatures, Version));
        }

        return Tlv16.Serialize(items);
    }

    private AuthScheme Scheme => _options.Generation == DaemonGeneration.Cpp ? AuthScheme.PssSha512 : AuthScheme.Pkcs1;

    private string Version => _options.Generation == DaemonGeneration.Cpp ? "Ver: 3.2.0fabcdef0123456789" : "Ver: 3.0.0e";
}
