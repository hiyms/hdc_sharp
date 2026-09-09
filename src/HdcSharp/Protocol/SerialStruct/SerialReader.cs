using System.Text;

namespace HdcSharp.Protocol.SerialStruct;

/// <summary>
/// serial_struct 解析读取器：按「tag(varint) + 值」顺序消费字节。
/// 调用方按字段号分发；字段值必须以与线类型匹配的方法读取，且不容忍未知 wireType，否则抛 <see cref="HdcException"/>。
/// </summary>
public sealed class SerialReader
{
    private readonly byte[] _data;
    private int _position;
    private int _pendingField = -1;
    private byte _pendingWireType;

    /// <summary>
    /// 以待解析字节创建读取器（内部拷贝一份，解析不依赖外部缓冲生命周期）。
    /// </summary>
    /// <param name="data">完整的 serial_struct 字节序列。</param>
    public SerialReader(ReadOnlySpan<byte> data) => _data = data.ToArray();

    /// <summary>
    /// 当前游标所在字段号；尚未读取或已无更多字段时为 -1。
    /// </summary>
    public int Field { get; private set; } = -1;

    /// <summary>
    /// 读取下一个字段 tag。
    /// </summary>
    /// <param name="field">字段号。</param>
    /// <param name="wireType">线类型，取 <see cref="WireType"/> 常量。</param>
    /// <returns>是否读到字段；数据耗尽时返回 false，且 <see cref="Field"/> 置为 -1。</returns>
    /// <exception cref="HdcException">wireType 既非 Varint 也非 Len 时抛出。</exception>
    public bool ReadTag(out int field, out byte wireType)
    {
        if (_position >= _data.Length)
        {
            field = -1;
            wireType = 0;
            Field = -1;
            return false;
        }

        ulong key = ReadVarintCore();
        field = (int)(key >> 3);
        wireType = (byte)(key & 0x07);
        if (wireType is not (WireType.Varint or WireType.Len))
        {
            throw new HdcException($"字段 {field} 的 wireType {wireType} 非法（仅支持 0=Varint、2=Len）");
        }

        _pendingField = field;
        _pendingWireType = wireType;
        Field = field;
        return true;
    }

    /// <summary>
    /// 以 varint 读取当前字段的值。
    /// </summary>
    /// <returns>字段值。</returns>
    /// <exception cref="HdcException">未先读取 tag、线类型不匹配或 varint 被截断/非法时抛出。</exception>
    public ulong ReadVarint()
    {
        _ = ConsumePendingField(WireType.Varint);
        return ReadVarintCore();
    }

    /// <summary>
    /// 读取当前字段的字符串值（varint 长度 + UTF-8 字节）。
    /// </summary>
    /// <returns>解码后的字符串。</returns>
    /// <exception cref="HdcException">未先读取 tag、线类型不匹配或长度超出剩余数据时抛出。</exception>
    public string ReadString()
    {
        int field = ConsumePendingField(WireType.Len);
        ulong length = ReadVarintCore();
        if (length > (ulong)(_data.Length - _position))
        {
            throw new HdcException($"字段 {field} 的字符串长度 {length} 超出剩余数据 {_data.Length - _position} 字节");
        }

        string result = Encoding.UTF8.GetString(_data, _position, (int)length);
        _position += (int)length;
        return result;
    }

    private ulong ReadVarintCore()
    {
        ulong result = 0;
        int shift = 0;
        while (true)
        {
            if (_position >= _data.Length)
            {
                throw new HdcException("varint 被截断：数据在第 10 字节前耗尽");
            }

            byte b = _data[_position++];
            if (shift == 63 && b > 0x01)
            {
                throw new HdcException("varint 编码非法：最高组超出 64 位范围");
            }

            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return result;
            }

            shift += 7;
            if (shift > 63)
            {
                throw new HdcException("varint 编码非法：超过 10 字节");
            }
        }
    }

    private int ConsumePendingField(byte expectedWireType)
    {
        if (_pendingField < 0)
        {
            throw new HdcException("当前无待读取字段，须先调用 ReadTag");
        }

        if (_pendingWireType != expectedWireType)
        {
            throw new HdcException($"字段 {_pendingField} 的 wireType 为 {_pendingWireType}，与读取方法期望的 {expectedWireType} 不匹配");
        }

        int field = _pendingField;
        _pendingField = -1;
        return field;
    }
}
