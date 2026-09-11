using HdcSharp.Security;

namespace HdcSharp;

/// <summary>
/// <see cref="HdcHost.ConnectAsync"/> 的连接与认证选项。
/// </summary>
public sealed class ConnectOptions
{
    /// <summary>是否向 daemon 声明并启用心跳；Rust 世代不支持心跳，仅在 C++ 世代生效。默认 true。</summary>
    public bool Heartbeat { get; init; } = true;

    /// <summary>
    /// 是否启用 TLS-PSK 加密通道。一期固定 false；置 true 时本次连接忽略该项按明文处理，并记录警告。
    /// </summary>
    public bool EnableEncryption { get; init; }

    /// <summary>认证总超时（含设备端授权弹窗等待），默认 3.5 分钟。</summary>
    public TimeSpan AuthTimeout { get; init; } = TimeSpan.FromMinutes(3.5);

    /// <summary>单次操作（当前用于 TCP 连接建立）的超时，默认 30 秒。</summary>
    public TimeSpan OperationTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>主机密钥库；null 时使用默认的 ~/.harmony/hdckey（与官方 hdc 共享，缺失时自动生成）。</summary>
    public IHostKeyStore? KeyStore { get; init; }
}
