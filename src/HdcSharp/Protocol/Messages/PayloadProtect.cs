using HdcSharp.Protocol.SerialStruct;

namespace HdcSharp.Protocol.Messages;

/// <summary>帧保护段：通道号、命令字、恒零校验和与恒 0x09 的 vCode。</summary>
internal sealed class PayloadProtect
{
    public uint ChannelId { get; set; }

    public HdcCommand Command { get; set; }

    public byte[] Serialize()
    {
        var writer = new SerialWriter();
        writer.WriteVarintField(1, ChannelId);
        writer.WriteVarintField(2, (ulong)Command);
        writer.WriteVarintField(3, 0);
        writer.WriteVarintField(4, HdcConstants.PayloadVCode);
        return writer.ToArray();
    }

    public static PayloadProtect Parse(ReadOnlySpan<byte> data)
    {
        var result = new PayloadProtect();
        var reader = new SerialReader(data);
        while (reader.ReadTag(out int field, out _))
        {
            switch (field)
            {
                case 1:
                    result.ChannelId = (uint)reader.ReadVarint();
                    break;
                case 2:
                    result.Command = (HdcCommand)reader.ReadVarint();
                    break;
                case 3:
                case 4:
                    _ = reader.ReadVarint();
                    break;
                default:
                    throw new HdcException($"PayloadProtect 不支持的字段号 {field}");
            }
        }

        return result;
    }
}
