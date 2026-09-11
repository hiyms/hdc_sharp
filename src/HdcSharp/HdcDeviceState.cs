namespace HdcSharp;

/// <summary>
/// 设备（连接）状态：随 TCP 连接与握手认证流程推进，断开或失败后转 <see cref="Offline"/>。
/// </summary>
public enum HdcDeviceState
{
    /// <summary>正在建立 TCP 连接。</summary>
    Connecting,

    /// <summary>TCP 已连接，正在握手/认证（可能等待设备端用户授权确认）。</summary>
    Authorizing,

    /// <summary>认证通过，连接可执行操作。</summary>
    Online,

    /// <summary>连接已断开，或连接/认证失败。</summary>
    Offline,
}
