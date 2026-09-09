using System.Globalization;
using System.Text;

namespace HdcSharp.Protocol;

/// <summary>
/// Tlv16 编解码器：握手消息 SessionHandShake.buf 专用的 Tag-Length-Value 结构（spec §4.4）。
/// 单条布局：tag 16 字节 ASCII（空格右填充）+ len 十进制 ASCII（空格右填充至 16 字节）+ value UTF-8 字节。
/// </summary>
public static class Tlv16
{
    private const int FieldSize = 16;

    private const int MaxValueBytes = 1024;

    /// <summary>
    /// 编码单条 TLV。
    /// </summary>
    /// <param name="item">tag 与 value 键值对。</param>
    /// <returns>单条 TLV 字节，总长恒为 32 + value 的 UTF-8 字节数。</returns>
    /// <exception cref="HdcException">tag 或长度字段超过 16 字符，或 value 超过 1024 字节。</exception>
    public static byte[] Encode((string Tag, string Value) item)
    {
        byte[] valueBytes = Encoding.UTF8.GetBytes(item.Value);
        string prefix = BuildPrefix(item.Tag, valueBytes.Length);
        byte[] result = new byte[(FieldSize * 2) + valueBytes.Length];
        Encoding.ASCII.GetBytes(prefix, 0, prefix.Length, result, 0);
        valueBytes.CopyTo(result, FieldSize * 2);
        return result;
    }

    /// <summary>
    /// 将多条 TLV 依序拼接为单个字符串，用于填充 SessionHandShake.Buf。
    /// </summary>
    /// <param name="items">tag 与 value 键值对序列。</param>
    /// <returns>拼接后的 buf 字符串；序列为空时返回空串。</returns>
    /// <exception cref="HdcException">tag 或长度字段超过 16 字符，或 value 超过 1024 字节。</exception>
    public static string Serialize(IEnumerable<(string Tag, string Value)> items)
    {
        StringBuilder sb = new();
        foreach ((string tag, string value) in items)
        {
            sb.Append(BuildPrefix(tag, Encoding.UTF8.GetByteCount(value)));
            sb.Append(value);
        }

        return sb.ToString();
    }

    /// <summary>
    /// 解析握手 buf 中的全部 TLV，重复 tag 以最后一次出现为准，value 允许空串，尾部不足 32 字节的残块忽略。
    /// </summary>
    /// <param name="buf">Tlv16 编码的握手 buf 字符串。</param>
    /// <returns>tag 到 value 的映射。</returns>
    public static Dictionary<string, string> Parse(string buf)
    {
        Dictionary<string, string> result = new();
        int pos = 0;
        while (pos + (FieldSize * 2) <= buf.Length)
        {
            string tag = buf.Substring(pos, FieldSize).TrimEnd(' ');
            string lenText = buf.Substring(pos + FieldSize, FieldSize).TrimEnd(' ');
            int len = int.Parse(lenText, CultureInfo.InvariantCulture);
            pos += FieldSize * 2;
            result[tag] = buf.Substring(pos, len);
            pos += len;
        }

        return result;
    }

    private static string BuildPrefix(string tag, int valueByteCount)
    {
        if (valueByteCount > MaxValueBytes)
        {
            throw new HdcException($"Tlv16 value 长度 {valueByteCount} 字节超过上限 {MaxValueBytes} 字节");
        }

        if (tag.Length > FieldSize)
        {
            throw new HdcException($"Tlv16 tag 长度 {tag.Length} 字符超过上限 {FieldSize} 字符");
        }

        string lenText = valueByteCount.ToString(CultureInfo.InvariantCulture);
        if (lenText.Length > FieldSize)
        {
            throw new HdcException($"Tlv16 长度字段 {lenText.Length} 字符超过上限 {FieldSize} 字符");
        }

        return tag.PadRight(FieldSize) + lenText.PadRight(FieldSize);
    }
}
