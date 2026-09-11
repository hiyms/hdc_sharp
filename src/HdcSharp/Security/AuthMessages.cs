using System.Security.Cryptography;
using System.Text;
using HdcSharp.Protocol;
using HdcSharp.Protocol.Messages;
using HdcSharp.Transport;

namespace HdcSharp.Security;

/// <summary>
/// 认证阶段枚举：ParseDaemonHandshake 的判定结果，驱动宿主侧认证状态机流转。
/// </summary>
public enum AuthPhase
{
    /// <summary>需要继续认证（等待公钥请求或签名挑战）。</summary>
    AuthRequired,

    /// <summary>认证成功（AUTH_OK 且 daemonauthstatus 为 SUCCESS）。</summary>
    AuthOk,

    /// <summary>认证失败或 daemon 版本过低，连接应终止。</summary>
    AuthFailed,

    /// <summary>握手 banner 无效（含 HS FAILED），连接应终止。</summary>
    BannerInvalid,
}

/// <summary>
/// 握手与认证消息的纯函数构建/解析（spec §4.6 步骤 1-8），不涉及网络 IO。
/// ParseDaemonHandshake 无状态：每次调用返回新的 DaemonCapabilities，
/// 调用方（AuthHandler）跨认证阶段持有并按阶段覆盖字段。
/// </summary>
internal static class AuthMessages
{
    private const byte DaemonAuthTypeSignature = 2;

    private const byte DaemonAuthTypePublicKey = 3;

    private const byte DaemonAuthTypeOk = 4;

    private const byte DaemonAuthTypeFail = 5;

    private const string PssAuthTypeValue = "1";

    private const string HeartbeatFeature = "heartbeat";

    private const int VersionHashPlaceholderLength = 16;

    /// <summary>
    /// 构建宿主侧首个握手帧载荷（KERNEL_HANDSHAKE，authType=0）：
    /// buf = Tlv16(authtype="1") + Tlv16(supportfeatures="Ver: 3.2.0f,TCP,&lt;os&gt;[,heartbeat]")。
    /// </summary>
    /// <param name="sessionId">本次连接的会话号（随机 u32），daemon 将采纳。</param>
    /// <param name="connectKey">连接键（ip:port 形式）。</param>
    /// <param name="heartbeat">是否向 daemon 声明支持心跳（仅 C++ 世代生效）。</param>
    /// <returns>可直接作为 KERNEL_HANDSHAKE 帧载荷的握手消息。</returns>
    internal static SessionHandShake BuildInitialHandshake(uint sessionId, string connectKey, bool heartbeat)
    {
        string features = HdcConstants.HostVersion + ",TCP," + ResolvePlatformToken();
        if (heartbeat)
        {
            features += "," + HeartbeatFeature;
        }

        return new SessionHandShake
        {
            Banner = HdcConstants.HandshakeMessage,
            AuthType = 0,
            SessionId = sessionId,
            ConnectKey = connectKey,
            Buf = Tlv16.Serialize(
            [
                (HdcConstants.TlvAuthType, PssAuthTypeValue),
                (HdcConstants.TlvSupportFeatures, features),
            ]),
            // version 末尾 16 个 '0' 为构建哈希占位：原版为源码结构哈希，daemon 仅记录不校验
            Version = HdcConstants.HostVersion + new string('0', VersionHashPlaceholderLength),
        };
    }

    /// <summary>
    /// 构建 AUTH_PUBLICKEY 应答载荷：hostname + 分隔字节 0x0C + PEM 公钥。
    /// </summary>
    /// <param name="hostName">宿主机名。</param>
    /// <param name="publicKeyPem">SPKI PEM 格式公钥（RSA-3072 时恒 625 字符）。</param>
    /// <returns>AUTH_PUBLICKEY 帧载荷字节。</returns>
    internal static byte[] BuildPublicKeyResponse(string hostName, string publicKeyPem)
        => Encoding.UTF8.GetBytes(hostName + (char)HdcConstants.HostDaemonBufSeparator + publicKeyPem);

    /// <summary>
    /// 构建 AUTH_SIGNATURE 应答载荷：按方案对挑战 token 签名后 Base64 编码。
    /// </summary>
    /// <param name="token">daemon 下发的挑战 token（AUTH_SIGNATURE 消息的 buf 原文，C++ 20 字符 / Rust 64 字符）。</param>
    /// <param name="scheme">签名方案，由 AUTH_PUBLICKEY 阶段确定的 caps.Scheme 传入。</param>
    /// <param name="key">宿主私钥。</param>
    /// <returns>Base64 签名串的 UTF-8 字节。</returns>
    /// <exception cref="HdcException">scheme 为 Unknown 等无法识别的值。</exception>
    internal static byte[] BuildSignatureResponse(byte[] token, AuthScheme scheme, RSA key)
    {
        byte[] signature = scheme switch
        {
            AuthScheme.PssSha512 => RsaRaw.PssSign(key, token),
            AuthScheme.Pkcs1 => RsaRaw.Pkcs1PrivateEncrypt(key, token),
            _ => throw new HdcException($"未知的认证方案 {scheme}，无法构建签名应答"),
        };
        return Encoding.UTF8.GetBytes(Convert.ToBase64String(signature));
    }

    /// <summary>
    /// 解析 daemon 侧握手/认证消息（AUTH_OK=4 / AUTH_PUBLICKEY=3 / AUTH_SIGNATURE=2 / AUTH_FAIL=5）。
    /// banner 非法或版本低于 "Ver: 3.0.0b" 时连接作废。
    /// </summary>
    /// <param name="msg">daemon 发来的握手/认证消息。</param>
    /// <param name="caps">本次新建的能力快照；AuthType==3 覆盖 Scheme，AuthType==4 覆盖 Generation/DeviceName/Authenticated/Heartbeat/ErrorMessage。</param>
    /// <param name="token">AUTH_SIGNATURE 阶段的挑战 token（buf 的 UTF-8 原文，不作 Tlv16 解析）；其余阶段为空数组。</param>
    /// <param name="errorText">失败原因文本；成功或 daemon 未提供原因时为空串。</param>
    /// <returns>认证阶段判定结果。</returns>
    internal static AuthPhase ParseDaemonHandshake(SessionHandShake msg, out DaemonCapabilities caps, out byte[] token, out string errorText)
    {
        caps = new DaemonCapabilities();
        token = [];
        errorText = "";

        if (msg.Banner == HdcConstants.HandshakeFailed)
        {
            return AuthPhase.BannerInvalid;
        }

        if (msg.Banner != HdcConstants.HandshakeMessage)
        {
            errorText = $"无法识别的握手 banner：{msg.Banner}";
            return AuthPhase.BannerInvalid;
        }

        if (msg.Version.Length > 0 && string.CompareOrdinal(msg.Version, HdcConstants.MinDaemonVersion) < 0)
        {
            errorText = $"daemon 版本 {msg.Version} 低于最低要求 {HdcConstants.MinDaemonVersion}";
            return AuthPhase.AuthFailed;
        }

        switch (msg.AuthType)
        {
            case DaemonAuthTypeOk:
                return ParseAuthOk(msg, caps, out errorText);
            case DaemonAuthTypePublicKey:
                caps.Scheme = ResolveAuthScheme(msg.Buf);
                return AuthPhase.AuthRequired;
            case DaemonAuthTypeSignature:
                token = Encoding.UTF8.GetBytes(msg.Buf);
                return AuthPhase.AuthRequired;
            case DaemonAuthTypeFail:
                return AuthPhase.AuthFailed;
            default:
                errorText = $"未知的认证消息类型 {msg.AuthType}";
                return AuthPhase.AuthFailed;
        }
    }

    private static AuthScheme ResolveAuthScheme(string buf)
    {
        // Rust 世代 daemon 会把原始 64 字符大写 hex token 直接放在 AUTH_PUBLICKEY 的 buf（非 TLV，
        // hdc_rust daemon_lib/auth.rs 的 handshake_init）；官方 host 对 TLV 解析失败一律回退旧式
        // RSA_ENCRYPT（server.cpp GetDaemonAuthType），此处对齐
        try
        {
            Dictionary<string, string> keyRequest = Tlv16.Parse(buf);
            return keyRequest.TryGetValue(HdcConstants.TlvAuthType, out string? authType) && authType == PssAuthTypeValue
                ? AuthScheme.PssSha512
                : AuthScheme.Pkcs1;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentOutOfRangeException or HdcException)
        {
            return AuthScheme.Pkcs1;
        }
    }

    private static AuthPhase ParseAuthOk(SessionHandShake msg, DaemonCapabilities caps, out string errorText)
    {
        Dictionary<string, string> map = Tlv16.Parse(msg.Buf);
        caps.Generation = ClassifyGeneration(msg.Version);
        caps.DeviceName = map.GetValueOrDefault(HdcConstants.TlvDevName, "");
        caps.Authenticated = map.GetValueOrDefault(HdcConstants.TlvDaemonAuthStatus) == HdcConstants.AuthStatusSuccess;
        string emgMsg = map.GetValueOrDefault(HdcConstants.TlvEmgMsg, "");
        caps.ErrorMessage = emgMsg.Length > 0 ? emgMsg : null;
        caps.Heartbeat = map.GetValueOrDefault(HdcConstants.TlvSupportFeatures, "").Contains(HeartbeatFeature, StringComparison.Ordinal);
        if (caps.Authenticated)
        {
            errorText = "";
            return AuthPhase.AuthOk;
        }

        errorText = caps.ErrorMessage ?? "";
        return AuthPhase.AuthFailed;
    }

    private static DaemonGeneration ClassifyGeneration(string version)
    {
        if (version.StartsWith("Ver: 3.0.", StringComparison.Ordinal))
        {
            return DaemonGeneration.Rust;
        }

        if (version.StartsWith("Ver: 3.2.", StringComparison.Ordinal))
        {
            return DaemonGeneration.Cpp;
        }

        return DaemonGeneration.Unknown;
    }

    private static string ResolvePlatformToken()
    {
        if (OperatingSystem.IsWindows())
        {
            return "win";
        }

        if (OperatingSystem.IsLinux())
        {
            return "linux";
        }

        if (OperatingSystem.IsMacOS())
        {
            return "mac";
        }

        throw new PlatformNotSupportedException("HdcSharp 宿主仅支持 Windows/Linux/macOS 平台");
    }
}
