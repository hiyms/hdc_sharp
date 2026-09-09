namespace HdcSharp.Transport;

/// <summary>
/// daemon 世代指纹：按 daemon 版本串前缀判定（spec §4.6）。
/// </summary>
public enum DaemonGeneration
{
    /// <summary>版本串不符合任一世代特征（判定前的初值）。</summary>
    Unknown,

    /// <summary>Rust 世代，版本串以 "Ver: 3.0." 开头。</summary>
    Rust,

    /// <summary>C++ 世代，版本串以 "Ver: 3.2." 开头。</summary>
    Cpp,
}

/// <summary>
/// 认证签名方案：AUTH_PUBLICKEY 阶段按 daemon 是否声明 authtype=1 确定。
/// </summary>
public enum AuthScheme
{
    /// <summary>尚未确定（判定前的初值）。</summary>
    Unknown,

    /// <summary>PSS+SHA512 签名（daemon 声明 authtype=1，C++ 世代）。</summary>
    PssSha512,

    /// <summary>PKCS#1 v1.5 块类型 1 私钥运算（daemon 未声明 authtype，Rust 世代）。</summary>
    Pkcs1,
}

/// <summary>
/// 认证过程中逐步填充的 daemon 能力快照：ParseDaemonHandshake 每次返回新实例，
/// 由调用方跨认证阶段持有并按阶段覆盖字段。
/// </summary>
public sealed class DaemonCapabilities
{
    /// <summary>daemon 世代，AUTH_OK 阶段按版本串判定，默认 Unknown。</summary>
    public DaemonGeneration Generation { get; set; } = DaemonGeneration.Unknown;

    /// <summary>签名方案，AUTH_PUBLICKEY 阶段确定，默认 Unknown。</summary>
    public AuthScheme Scheme { get; set; } = AuthScheme.Unknown;

    /// <summary>daemon 是否声明支持心跳（仅 C++ 世代，supportfeatures 含 heartbeat）。</summary>
    public bool Heartbeat { get; set; }

    /// <summary>设备名（AUTH_OK 的 devname），缺失时为空串。</summary>
    public string DeviceName { get; set; } = "";

    /// <summary>daemonauthstatus 是否为 SUCCESS。</summary>
    public bool Authenticated { get; set; }

    /// <summary>emgmsg 错误消息，空串归一为 null。</summary>
    public string? ErrorMessage { get; set; }
}
