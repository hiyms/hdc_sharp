using System.Security.Cryptography;
using System.Text;
using HdcSharp.Protocol;
using HdcSharp.Security;
using HdcSharp.Transport;
using Xunit;

namespace HdcSharp.Tests.Security;

public class AuthMessagesTests
{
    [Fact]
    public void InitialHandshake_ContainsTlvs()
    {
        var hs = AuthMessages.BuildInitialHandshake(sessionId: 7, connectKey: "192.168.2.161:44221", heartbeat: true);
        Assert.Equal("OHOS HDC", hs.Banner);
        Assert.Equal(0, hs.AuthType);
        Assert.Equal(7u, hs.SessionId);
        Assert.Equal("192.168.2.161:44221", hs.ConnectKey);
        Assert.StartsWith("authtype", hs.Buf);
        Assert.Contains("supportfeatures", hs.Buf);
        Assert.Contains("Ver: 3.2.0f", hs.Buf);
        Assert.Equal("Ver: 3.2.0f" + new string('0', 16), hs.Version);
    }

    [Fact]
    public void ParseAuthOk_CppStyle_FeaturesPresent()
    {
        var buf = HdcSharp.Protocol.Tlv16.Serialize(new[]
        {
            ("emgmsg", ""), ("devname", "my-dev"), ("daemonauthstatus", "SUCCESS"),
            ("1200", "enable"), ("supportfeatures", "heartbeat,encrypt_tcp")
        });
        var msg = new HdcSharp.Protocol.Messages.SessionHandShake { Banner = "OHOS HDC", AuthType = 4, Buf = buf, Version = "Ver: 3.2.0fabcdef0123456789" };
        var phase = AuthMessages.ParseDaemonHandshake(msg, out var caps, out var token, out var err);
        Assert.Equal(AuthPhase.AuthOk, phase);
        Assert.Equal(DaemonGeneration.Cpp, caps.Generation);
        Assert.True(caps.Authenticated);
        Assert.Equal("my-dev", caps.DeviceName);
        Assert.True(caps.Heartbeat);
        Assert.Null(caps.ErrorMessage);
        Assert.Empty(token);
        Assert.Equal("", err);
    }

    [Fact]
    public void ParseAuthOk_RustStyle_SubsetTlvs()
    {
        var buf = HdcSharp.Protocol.Tlv16.Serialize(new[]
        {
            ("devname", "rust-dev"), ("daemonauthstatus", "SUCCESS"), ("emgmsg", "")
        });
        var msg = new HdcSharp.Protocol.Messages.SessionHandShake { Banner = "OHOS HDC", AuthType = 4, Buf = buf, Version = "Ver: 3.0.0e" };
        var phase = AuthMessages.ParseDaemonHandshake(msg, out var caps, out _, out _);
        Assert.Equal(AuthPhase.AuthOk, phase);
        Assert.Equal(DaemonGeneration.Rust, caps.Generation);
        Assert.False(caps.Heartbeat);                          // Rust 世代无心跳
    }

    [Fact]
    public void ParsePublicKeyRequest_SetsTokenPhase()
    {
        var cpp = new HdcSharp.Protocol.Messages.SessionHandShake { Banner = "OHOS HDC", AuthType = 3, Buf = HdcSharp.Protocol.Tlv16.Serialize(new[] { ("authtype", "1") }) };
        var phaseCpp = AuthMessages.ParseDaemonHandshake(cpp, out var capsCpp, out var token, out _);
        Assert.Equal(AuthPhase.AuthRequired, phaseCpp);
        Assert.True(token.Length == 0);                        // token 在 AUTH_SIGNATURE 到达
        Assert.Equal(AuthScheme.PssSha512, capsCpp.Scheme);

        var rust = new HdcSharp.Protocol.Messages.SessionHandShake { Banner = "OHOS HDC", AuthType = 3 };
        var phaseRust = AuthMessages.ParseDaemonHandshake(rust, out var capsRust, out _, out _);
        Assert.Equal(AuthPhase.AuthRequired, phaseRust);
        Assert.Equal(AuthScheme.Pkcs1, capsRust.Scheme);
    }

    [Fact]
    public void ParsePublicKeyRequest_RustRawTokenBuf_FallsBackToPkcs1()
    {
        // Rust 世代 daemon 的 AUTH_PUBLICKEY 把 64 字符大写 hex token 直接放在 buf（非 TLV）；
        // 官方 host 对 TLV 解析失败回退旧式 RSA 加密
        string token = string.Concat(Enumerable.Repeat("3F2A9C7E1B4D8056", 4));
        var msg = new HdcSharp.Protocol.Messages.SessionHandShake { Banner = "OHOS HDC", AuthType = 3, Buf = token };
        var phase = AuthMessages.ParseDaemonHandshake(msg, out var caps, out _, out _);
        Assert.Equal(64, token.Length);
        Assert.Equal(AuthPhase.AuthRequired, phase);
        Assert.Equal(AuthScheme.Pkcs1, caps.Scheme);
    }

    [Fact]
    public void ParseAuthOk_CppGeneration_NotDoubtedByEchoedHostVersion()
    {
        // 真机实测：C++ daemon 就地改写收到的手握消息后重发，version 恒为 host 自己的串。
        // 若 host 声明 3.0.x，仅靠 version 前缀会误判为 Rust；1200/supportfeatures TLV 优先
        var buf = HdcSharp.Protocol.Tlv16.Serialize(new[]
        {
            ("devname", "cpp-dev"), ("daemonauthstatus", "SUCCESS"), ("emgmsg", ""), ("1200", "enable")
        });
        var msg = new HdcSharp.Protocol.Messages.SessionHandShake { Banner = "OHOS HDC", AuthType = 4, Buf = buf, Version = "Ver: 3.0.0e" };
        var phase = AuthMessages.ParseDaemonHandshake(msg, out var caps, out _, out _);
        Assert.Equal(AuthPhase.AuthOk, phase);
        Assert.Equal(DaemonGeneration.Cpp, caps.Generation);
    }

    [Fact]
    public void ParsePublicKeyRequest_AuthTypeTlv_MarksCppGeneration()
    {
        // authtype TLV 仅 C++ daemon 会附（HandDaemonAuthInit），与 PSS 方案合并作为定论性世代信号
        var cpp = new HdcSharp.Protocol.Messages.SessionHandShake
        {
            Banner = "OHOS HDC", AuthType = 3,
            Buf = HdcSharp.Protocol.Tlv16.Serialize(new[] { ("authtype", "1") }),
            Version = "Ver: 3.0.0e",
        };
        AuthMessages.ParseDaemonHandshake(cpp, out var cppCaps, out _, out _);
        Assert.Equal(DaemonGeneration.Cpp, cppCaps.Generation);

        var rust = new HdcSharp.Protocol.Messages.SessionHandShake
        {
            Banner = "OHOS HDC", AuthType = 3, Buf = "ABC", Version = "Ver: 3.0.0e",
        };
        AuthMessages.ParseDaemonHandshake(rust, out var rustCaps, out _, out _);
        Assert.Equal(DaemonGeneration.Unknown, rustCaps.Generation);
    }

    [Fact]
    public void ParseSignatureChallenge_CppToken20Chars()
    {
        var msg = new HdcSharp.Protocol.Messages.SessionHandShake { Banner = "OHOS HDC", AuthType = 2, Buf = "0123456789abcdefghij" };
        var phase = AuthMessages.ParseDaemonHandshake(msg, out var caps, out var token, out _);
        Assert.Equal(AuthPhase.AuthRequired, phase);
        Assert.Equal(20, token.Length);
        Assert.Equal("0123456789abcdefghij", Encoding.UTF8.GetString(token));

        using var rsa = RSA.Create(2048);
        byte[] pss = AuthMessages.BuildSignatureResponse(token, AuthScheme.PssSha512, rsa);
        Assert.True(rsa.VerifyData(token, Convert.FromBase64String(Encoding.UTF8.GetString(pss)), HashAlgorithmName.SHA512, RSASignaturePadding.Pss));

        byte[] pkcs1 = AuthMessages.BuildSignatureResponse(token, AuthScheme.Pkcs1, rsa);
        Assert.Equal(256, Convert.FromBase64String(Encoding.UTF8.GetString(pkcs1)).Length);
    }

    [Fact]
    public void ParseDaemonHandshake_RejectionPaths_FailAsSpecified()
    {
        Assert.Equal(AuthPhase.BannerInvalid, AuthMessages.ParseDaemonHandshake(
            new HdcSharp.Protocol.Messages.SessionHandShake { Banner = "HS FAILED" }, out _, out _, out var failedText));
        Assert.Equal("", failedText);

        Assert.Equal(AuthPhase.BannerInvalid, AuthMessages.ParseDaemonHandshake(
            new HdcSharp.Protocol.Messages.SessionHandShake { Banner = "ADB GOOGLE" }, out _, out _, out var unknownText));
        Assert.Contains("banner", unknownText);

        Assert.Equal(AuthPhase.AuthFailed, AuthMessages.ParseDaemonHandshake(
            new HdcSharp.Protocol.Messages.SessionHandShake { Banner = "OHOS HDC", AuthType = 4, Version = "Ver: 2.9.9z" }, out _, out _, out var oldVersionText));
        Assert.Contains("版本", oldVersionText);

        Assert.Equal(AuthPhase.AuthFailed, AuthMessages.ParseDaemonHandshake(
            new HdcSharp.Protocol.Messages.SessionHandShake { Banner = "OHOS HDC", AuthType = 5 }, out var caps, out var token, out _));
        Assert.False(caps.Authenticated);
        Assert.Empty(token);
    }
}
