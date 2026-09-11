using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using HdcSharp.Protocol;
using HdcSharp.Protocol.Messages;
using HdcSharp.Transport;

namespace HdcSharp.Security;

/// <summary>
/// 宿主侧握手与认证状态机（spec §4.6）：发送首个握手帧后，按 daemon 回包依次完成
/// AUTH_PUBLICKEY（提交 hostname + PEM 公钥）、AUTH_SIGNATURE（按 AUTH_PUBLICKEY 阶段确定的
/// 签名方案对挑战 token 签名）与 AUTH_OK，返回能力快照。仅供 HdcHost.ConnectAsync 调用，
/// 连接读循环须已由调用方启动。
/// </summary>
internal static class AuthHandler
{
    private const byte AuthTypeSignature = 2;

    private const byte AuthTypePublicKey = 3;

    /// <summary>等待 daemon 握手通道拆除帧的宽限窗口。</summary>
    private static readonly TimeSpan HandshakeCloseGrace = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// 执行完整认证流程：发首个握手 → 处理 AUTH_PUBLICKEY/AUTH_SIGNATURE 挑战 → 等待 AUTH_OK 与
    /// daemon 的握手通道拆除帧。
    /// </summary>
    /// <param name="conn">已建立 TCP 且读循环已启动的连接。</param>
    /// <param name="connectKey">连接键（ip:port 形式），写入握手消息。</param>
    /// <param name="keys">宿主密钥库，提供 PEM 公钥与每次新建的 RSA 私钥实例。</param>
    /// <param name="authTimeout">认证总超时（含设备端授权弹窗等待）。</param>
    /// <param name="requestHeartbeat">是否在首个握手的 supportfeatures 中声明心跳（仅 C++ 世代生效）。</param>
    /// <param name="onAuthorizationRequested">收到 AUTH_PUBLICKEY（设备端可能弹窗）时的回调，可为 null。</param>
    /// <param name="ct">调用方取消令牌。</param>
    /// <returns>认证成功后的 daemon 能力快照（Scheme 由 AUTH_PUBLICKEY 阶段补入）。</returns>
    /// <exception cref="HdcException">banner 非法、daemon 拒绝认证、认证超时或连接断开。</exception>
    internal static async Task<DaemonCapabilities> RunAsync(
        HdcConnection conn,
        string connectKey,
        IHostKeyStore keys,
        TimeSpan authTimeout,
        bool requestHeartbeat,
        Action? onAuthorizationRequested,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(conn);
        ArgumentNullException.ThrowIfNull(connectKey);
        ArgumentNullException.ThrowIfNull(keys);

        using var authCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        authCts.CancelAfter(authTimeout);
        var inbox = Channel.CreateUnbounded<Frame>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        var closeSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool terminated = false;

        Action<Frame> frameHandler = frame =>
        {
            if (frame.ChannelId == 0 && frame.Command == HdcCommand.KernelHandshake)
            {
                inbox.Writer.TryWrite(frame);
            }
        };
        Action<Frame> closeHandler = frame =>
        {
            if (frame.ChannelId == 0)
            {
                closeSignal.TrySetResult();
            }
        };
        Action terminatedHandler = () =>
        {
            terminated = true;
            try
            {
                authCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // 与退订/释放竞态时令牌源已释放：认证流程已结束，无需再取消
            }
        };

        conn.RegisterChannel(0);
        conn.FrameReceived += frameHandler;
        conn.ChannelClosed += closeHandler;
        conn.Terminated += terminatedHandler;
        try
        {
            SessionHandShake hello = AuthMessages.BuildInitialHandshake(conn.SessionId, connectKey, requestHeartbeat);
            await conn.SendAsync(0, HdcCommand.KernelHandshake, hello.Serialize(), authCts.Token).ConfigureAwait(false);

            AuthScheme scheme = AuthScheme.Unknown;
            bool cppGenerationConfirmed = false;
            while (true)
            {
                Frame frame = await inbox.Reader.ReadAsync(authCts.Token).ConfigureAwait(false);
                SessionHandShake message = SessionHandShake.Parse(frame.Payload);
                AuthPhase phase = AuthMessages.ParseDaemonHandshake(message, out DaemonCapabilities caps, out byte[] token, out string errorText);
                if (phase == AuthPhase.BannerInvalid)
                {
                    throw new HdcException("设备返回了无效的握手 banner");
                }

                if (phase == AuthPhase.AuthFailed)
                {
                    throw new HdcException(errorText.Length > 0 ? errorText : "认证失败");
                }

                if (phase == AuthPhase.AuthOk)
                {
                    caps.Scheme = scheme;
                    // AUTH_PUBLICKEY 阶段的 authtype TLV 是定论性 C++ 信号，优先于 AUTH_OK 阶段的推断
                    if (cppGenerationConfirmed)
                    {
                        caps.Generation = DaemonGeneration.Cpp;
                    }

                    await AwaitHandshakeCloseAsync(closeSignal.Task, ct).ConfigureAwait(false);
                    if (terminated)
                    {
                        throw new HdcException("连接已断开");
                    }

                    return caps;
                }

                // 应答复用首个握手消息模板（banner/sessionId/connectKey/version 不变），与官方 host
                // “改 authType/buf 后回发”一致；AuthMessages 的两个构建器只产出 buf 内容
                if (message.AuthType == AuthTypePublicKey)
                {
                    scheme = caps.Scheme;
                    cppGenerationConfirmed = caps.Generation == DaemonGeneration.Cpp;
                    InvokeAuthorizationRequested(onAuthorizationRequested);
                    hello.AuthType = AuthTypePublicKey;
                    hello.Buf = Encoding.UTF8.GetString(AuthMessages.BuildPublicKeyResponse(Environment.MachineName, keys.GetPublicKeyPem()));
                }
                else
                {
                    hello.AuthType = AuthTypeSignature;
                    using RSA key = keys.GetPrivateKey();
                    hello.Buf = Encoding.UTF8.GetString(AuthMessages.BuildSignatureResponse(token, scheme, key));
                }

                await conn.SendAsync(0, HdcCommand.KernelHandshake, hello.Serialize(), authCts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw terminated ? new HdcException("连接已断开") : new HdcException("认证超时");
        }
        finally
        {
            conn.FrameReceived -= frameHandler;
            conn.ChannelClosed -= closeHandler;
            conn.Terminated -= terminatedHandler;
            conn.UnregisterChannel(0);
        }
    }

    private static async Task AwaitHandshakeCloseAsync(Task closeSignal, CancellationToken ct)
    {
        using var graceCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        graceCts.CancelAfter(HandshakeCloseGrace);
        try
        {
            await closeSignal.WaitAsync(graceCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // C++ daemon 与启用认证的 Rust daemon 会在 AUTH_OK 后紧随拆除帧（载荷 [1]/[0]）；
            // Rust 世代未启用认证时不发（hdc_rust daemon_lib/auth.rs 的 handshake_task 只回 AUTH_OK），
            // 一味等待会挂到认证超时，故只给宽限窗口。CLOSE 递减回发由 HdcConnection 通用分发负责
        }
    }

    private static void InvokeAuthorizationRequested(Action? callback)
    {
        try
        {
            callback?.Invoke();
        }
        catch (Exception)
        {
            // 用户回调异常不得影响认证状态机（与连接事件的隔离策略一致）
        }
    }
}
