namespace HdcSharp.Transport;

/// <summary>连接层日志级别。</summary>
public enum HdcLogLevel
{
    /// <summary>调试信息。</summary>
    Debug,

    /// <summary>一般信息。</summary>
    Info,

    /// <summary>警告。</summary>
    Warn,

    /// <summary>错误。</summary>
    Error,
}

/// <summary>
/// <see cref="HdcConnection"/> 连接选项。
/// </summary>
public sealed class HdcConnectionOptions
{
    /// <summary>日志回调，默认 null（静默）。回调在读循环/心跳线程触发，应尽快返回。</summary>
    public Action<HdcLogLevel, string>? Logger { get; init; }

    /// <summary>心跳发送间隔，仅 C++ 世代由上层显式调用 <see cref="HdcConnection.StartHeartbeat"/> 时生效。</summary>
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(5);
}
