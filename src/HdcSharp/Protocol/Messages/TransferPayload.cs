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
}
