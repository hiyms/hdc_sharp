using HdcSharp.Protocol.SerialStruct;

namespace HdcSharp.Protocol.Messages;

/// <summary>握手/认证消息（KERNEL_HANDSHAKE 载荷）：banner、认证类型、会话号、连接键、TLV 缓冲与版本。</summary>
internal sealed class SessionHandShake
{
    public string Banner { get; set; } = "";

    public byte AuthType { get; set; }

    public uint SessionId { get; set; }

    public string ConnectKey { get; set; } = "";

    public string Buf { get; set; } = "";

    public string Version { get; set; } = "";

    public byte[] Serialize()
    {
        var writer = new SerialWriter();
        writer.WriteStringField(1, Banner);
        writer.WriteVarintField(2, AuthType);
        writer.WriteVarintField(3, SessionId);
        writer.WriteStringField(4, ConnectKey);
        writer.WriteStringField(5, Buf);
        writer.WriteStringField(6, Version);
        return writer.ToArray();
    }

    public static SessionHandShake Parse(ReadOnlySpan<byte> data)
    {
        var result = new SessionHandShake();
        var reader = new SerialReader(data);
        while (reader.ReadTag(out int field, out _))
        {
            switch (field)
            {
                case 1:
                    result.Banner = reader.ReadString();
                    break;
                case 2:
                    result.AuthType = (byte)reader.ReadVarint();
                    break;
                case 3:
                    result.SessionId = (uint)reader.ReadVarint();
                    break;
                case 4:
                    result.ConnectKey = reader.ReadString();
                    break;
                case 5:
                    result.Buf = reader.ReadString();
                    break;
                case 6:
                    result.Version = reader.ReadString();
                    break;
                default:
                    throw new HdcException($"SessionHandShake 不支持的字段号 {field}");
            }
        }

        return result;
    }
}
