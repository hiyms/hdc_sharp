using HdcSharp.Protocol.SerialStruct;

namespace HdcSharp.Protocol.Messages;

/// <summary>心跳消息：递增计数加恒空保留串，仅 C++ 世代协商开启后使用。</summary>
internal sealed class HeartbeatMsg
{
    public ulong Count { get; set; }

    public byte[] Serialize()
    {
        var writer = new SerialWriter();
        writer.WriteVarintField(1, Count);
        writer.WriteStringField(2, "");
        return writer.ToArray();
    }

    public static HeartbeatMsg Parse(ReadOnlySpan<byte> data)
    {
        var result = new HeartbeatMsg();
        var reader = new SerialReader(data);
        while (reader.ReadTag(out int field, out _))
        {
            switch (field)
            {
                case 1:
                    result.Count = reader.ReadVarint();
                    break;
                case 2:
                    _ = reader.ReadString();
                    break;
                default:
                    throw new HdcException($"HeartbeatMsg 不支持的字段号 {field}");
            }
        }

        return result;
    }
}
