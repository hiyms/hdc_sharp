using System.Buffers.Binary;

namespace HdcSharp.Protocol;

/// <summary>
/// Tlv32 编解码器：shell 选项（命令文本、应用包名）专用的 Tag-Length-Value 结构（spec §4.9）。
/// 单条布局：<c>[tag u32 小端][len u32 小端][value 原始字节]</c>，序列化按 tag 升序输出。
/// </summary>
/// <remarks>
/// 与 Tlv16 是完全不同的两套编码：Tlv32 头部为 u32 小端且无 16 字节空格填充。两套并存源于 daemon
/// 历史实现差异——握手 buf 沿用 Tlv16（两世代通用），shell 选项仅 C++ 世代 daemon（Ver: 3.2.0*）使用 Tlv32。
/// </remarks>
public static class Tlv32
{
    /// <summary>shell 选项 tag：命令文本。</summary>
    public const uint TagShellCmd = 0;

    /// <summary>shell 选项 tag：应用包名。</summary>
    public const uint TagShellBundle = 1;

    private const int HeadSize = sizeof(uint) + sizeof(uint);

    /// <summary>
    /// 将全部键值对编码为 Tlv32 字节序列，按 tag 升序输出。
    /// </summary>
    /// <param name="entries">tag 到 value 的映射。</param>
    /// <returns>编码后的字节序列；映射为空时返回空数组。</returns>
    public static byte[] Serialize(IReadOnlyDictionary<uint, byte[]> entries)
    {
        int total = 0;
        foreach (KeyValuePair<uint, byte[]> entry in entries)
        {
            total += HeadSize + entry.Value.Length;
        }

        byte[] result = new byte[total];
        int pos = 0;
        foreach (KeyValuePair<uint, byte[]> entry in entries.OrderBy(static e => e.Key))
        {
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(pos), entry.Key);
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(pos + sizeof(uint)), (uint)entry.Value.Length);
            entry.Value.CopyTo(result, pos + HeadSize);
            pos += HeadSize + entry.Value.Length;
        }

        return result;
    }

    /// <summary>
    /// 解析 Tlv32 字节序列，重复 tag 以最后一次出现为准，允许空 value。
    /// </summary>
    /// <param name="data">Tlv32 编码的字节序列。</param>
    /// <returns>tag 到 value 的映射；输入为空时返回空映射。</returns>
    /// <exception cref="HdcException">条目头部不足 8 字节，或 value 长度超出剩余数据。</exception>
    public static Dictionary<uint, byte[]> Parse(ReadOnlySpan<byte> data)
    {
        Dictionary<uint, byte[]> result = new();
        int pos = 0;
        while (pos < data.Length)
        {
            if (data.Length - pos < HeadSize)
            {
                throw new HdcException($"Tlv32 条目头部不完整：剩余 {data.Length - pos} 字节不足 {HeadSize} 字节");
            }

            uint tag = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(pos));
            uint len = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(pos + sizeof(uint)));
            pos += HeadSize;
            if (len > (uint)(data.Length - pos))
            {
                throw new HdcException($"Tlv32 value 长度 {len} 字节超出剩余 {data.Length - pos} 字节");
            }

            result[tag] = data.Slice(pos, (int)len).ToArray();
            pos += (int)len;
        }

        return result;
    }
}
