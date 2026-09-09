namespace HdcSharp.Protocol.SerialStruct;

/// <summary>
/// serial_struct 线类型常量，存放于 tag 的低 3 位。
/// </summary>
public static class WireType
{
    /// <summary>变长整数（LEB128 varint）。</summary>
    public const byte Varint = 0;

    /// <summary>长度前缀型（varint 长度 + 原始字节），字符串与字节块使用。</summary>
    public const byte Len = 2;
}
