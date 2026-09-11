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
    private readonly ConcurrentDictionary<int, TcpClient> _clients = new();
    private readonly ConcurrentDictionary<int, Task> _connections = new();
    private readonly object _gate = new();
    private readonly Task _acceptTask;
    private string? _hostPublicKeyPem;
    private bool _signatureVerified;
    private int _disposed;
    private int _nextConnectionId;

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
        try
        {
            NetworkStream stream = client.GetStream();
            await RunScriptAsync(stream, new FrameDecoder(), new byte[8192]);
        }
        catch (Exception ex) when (IsConnectionFault(ex))
        {
            // 测试替身中，对端关闭或帧非法都视为该连接剧本结束
        }
        finally
        {
            _clients.TryRemove(id, out _);
            _connections.TryRemove(id, out _);
            client.Dispose();
        }
    }

    private static bool IsConnectionFault(Exception ex)
    {
        return ex is HdcException or IOException or SocketException or ObjectDisposedException or OperationCanceledException;
    }

    private async Task RunScriptAsync(NetworkStream stream, FrameDecoder decoder, byte[] buffer)
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
            await DrainAsync(decoder, stream, buffer, ct);
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
        await DrainAsync(decoder, stream, buffer, ct);
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
        if (frame.Command == HdcCommand.KernelHandshake)
        {
            return;
        }

        lock (_gate)
        {
            _received.Add((frame.ChannelId, frame.Command));
        }
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
