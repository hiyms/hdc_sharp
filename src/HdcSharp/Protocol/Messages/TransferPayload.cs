using HdcSharp.Protocol.SerialStruct;

namespace HdcSharp.Protocol.Messages;

/// <summary>文件 DATA 阶段 64 字节槽的前段：块索引、压缩方式与压缩前后大小。</summary>
internal sealed class TransferPayload
{
    public ulong Index { get; set; }

    public byte CompressType { get; set; }

    public uint CompressSize { get; set; }

    public uint UncompressSize { get; set; }

    public byte[] Serialize()
    {
        var writer = new SerialWriter();
        writer.WriteVarintField(1, Index);
        writer.WriteVarintField(2, CompressType);
        writer.WriteVarintField(3, CompressSize);
        writer.WriteVarintField(4, UncompressSize);
        return writer.ToArray();
    }

    public static TransferPayload Parse(ReadOnlySpan<byte> data)
    {
        var result = new TransferPayload();
        var reader = new SerialReader(data);
        while (reader.ReadTag(out int field, out _))
        {
            switch (field)
            {
                case 1:
                    result.Index = reader.ReadVarint();
                    break;
                case 2:
                    result.CompressType = (byte)reader.ReadVarint();
                    break;
                case 3:
                    result.CompressSize = (uint)reader.ReadVarint();
                    break;
                case 4:
                    result.UncompressSize = (uint)reader.ReadVarint();
                    break;
                default:
                    throw new HdcException($"TransferPayload 不支持的字段号 {field}");
            }
        }

        return result;
    }

    /// <summary>
    /// 解析文件 DATA 报文的 64 字节定长槽：序列化字节之后的零填充（含上游 C++ 写入的结尾 NUL）视作消息结束，
    /// 与上游 SerialStruct 对未知 tag 0 的忽略语义一致（transfer.cpp:SendIOPayload / hdctransfer.rs:spawn_handler）。
    /// </summary>
    /// <param name="slot">64 字节槽内容。</param>
    /// <returns>槽内解析出的传输头。</returns>
    /// <exception cref="HdcException">字段号非法或 varint 编码损坏。</exception>
    public static TransferPayload ParseSlot(ReadOnlySpan<byte> slot)
    {
        var result = new TransferPayload();
        var reader = new SerialReader(slot);
        while (reader.ReadTag(out int field, out _))
        {
            if (field == 0)
            {
                break;
            }

            switch (field)
            {
                case 1:
                    result.Index = reader.ReadVarint();
                    break;
                case 2:
                    result.CompressType = (byte)reader.ReadVarint();
                    break;
                case 3:
                    result.CompressSize = (uint)reader.ReadVarint();
                    break;
                case 4:
                    result.UncompressSize = (uint)reader.ReadVarint();
                    break;
                default:
                    throw new HdcException($"TransferPayload 不支持的字段号 {field}");
            }
        }

        return result;
    }
}
