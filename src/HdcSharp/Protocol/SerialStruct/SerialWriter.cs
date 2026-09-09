using System.Text;

namespace HdcSharp.Protocol.SerialStruct;

/// <summary>
/// serial_struct 编码写入器：按「tag(varint) + 值」的 proto 风格布局累积字节。
/// 与原版字节级一致：varint 用 LEB128；所有标量（含 0）与字符串（含空串）一律输出。
/// </summary>
public sealed class SerialWriter
{
    private readonly List<byte> _buffer = new();

    /// <summary>
    /// 写入一个 LEB128 varint；值为 0 时输出单字节 0x00。
    /// </summary>
    /// <param name="value">待写入的无符号 64 位整数。</param>
    /// <returns>当前写入器，支持链式调用。</returns>
    public SerialWriter WriteVarint(ulong value)
    {
        while (value >= 0x80)
        {
            _buffer.Add((byte)(value | 0x80));
            value >>= 7;
        }

        _buffer.Add((byte)value);
        return this;
    }

    /// <summary>
    /// 写入字段 tag：<c>(fieldNumber &lt;&lt; 3) | wireType</c>，以 varint 输出。
    /// </summary>
    /// <param name="field">字段号（正整数）。</param>
    /// <param name="wireType">线类型，取 <see cref="WireType"/> 常量。</param>
    /// <returns>当前写入器，支持链式调用。</returns>
    public SerialWriter WriteTag(int field, byte wireType) =>
        WriteVarint((uint)((field << 3) | wireType));

    /// <summary>
    /// 写入 varint 字段（tag + 值）；值为 0 也必须输出。
    /// </summary>
    /// <param name="field">字段号。</param>
    /// <param name="value">字段值。</param>
    /// <returns>当前写入器，支持链式调用。</returns>
    public SerialWriter WriteVarintField(int field, ulong value) =>
        WriteTag(field, WireType.Varint).WriteVarint(value);

    /// <summary>
    /// 写入 string 字段（tag + UTF-8 字节数 varint + UTF-8 字节）；空串也必须输出（tag + 长度 0）。
    /// </summary>
    /// <param name="field">字段号。</param>
    /// <param name="value">字符串值，按 UTF-8 编码。</param>
    /// <returns>当前写入器，支持链式调用。</returns>
    public SerialWriter WriteStringField(int field, string value)
    {
        WriteTag(field, WireType.Len);
        byte[] utf8 = Encoding.UTF8.GetBytes(value);
        WriteVarint((ulong)utf8.Length);
        _buffer.AddRange(utf8);
        return this;
    }

    /// <summary>
    /// 输出当前累积的完整编码字节。
    /// </summary>
    /// <returns>编码结果副本，写入器可继续使用。</returns>
    public byte[] ToArray() => _buffer.ToArray();
}
