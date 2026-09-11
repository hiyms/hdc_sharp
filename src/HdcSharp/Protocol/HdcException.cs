namespace HdcSharp.Protocol;

/// <summary>
/// HDC 操作结果级别，与 KERNEL_ECHO 的 level 字节语义对齐。
/// </summary>
public enum MessageLevel
{
    /// <summary>失败。</summary>
    Fail,

    /// <summary>信息。</summary>
    Info,

    /// <summary>成功。</summary>
    Ok,
}

/// <summary>
/// HDC 协议与操作异常，可携带设备端/协议错误码与结果级别。
/// </summary>
public sealed class HdcException : Exception
{
    /// <summary>
    /// 创建 HDC 异常。
    /// </summary>
    /// <param name="message">异常描述。</param>
    /// <param name="errorCode">设备端/协议错误码（如 [E000002]），无则为 null。</param>
    /// <param name="level">结果级别，默认为失败。</param>
    public HdcException(string message, string? errorCode = null, MessageLevel level = MessageLevel.Fail)
        : base(message)
    {
        ErrorCode = errorCode;
        Level = level;
    }

    /// <summary>创建带内层异常的 HDC 异常（库内部使用）。</summary>
    internal HdcException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>设备端/协议错误码，无则为 null。</summary>
    public string? ErrorCode { get; }

    /// <summary>结果级别。</summary>
    public MessageLevel Level { get; }
}
