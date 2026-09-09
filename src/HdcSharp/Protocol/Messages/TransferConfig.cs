using HdcSharp.Protocol.SerialStruct;

namespace HdcSharp.Protocol.Messages;

/// <summary>文件/应用传输 CHECK 阶段的配置消息，13 个字段全部恒输出，bool 以 varint 0/1 编码。</summary>
internal sealed class TransferConfig
{
    public ulong FileSize { get; set; }

    public ulong Atime { get; set; }

    public ulong Mtime { get; set; }

    public string Options { get; set; } = "";

    public string Path { get; set; } = "";

    public string OptionalName { get; set; } = "";

    public bool UpdateIfNew { get; set; }

    public byte CompressType { get; set; }

    public bool HoldTimestamp { get; set; }

    public string FunctionName { get; set; } = "";

    public string ClientCwd { get; set; } = "";

    public string Reserve1 { get; set; } = "";

    public string Reserve2 { get; set; } = "";

    public byte[] Serialize()
    {
        var writer = new SerialWriter();
        writer.WriteVarintField(1, FileSize);
        writer.WriteVarintField(2, Atime);
        writer.WriteVarintField(3, Mtime);
        writer.WriteStringField(4, Options);
        writer.WriteStringField(5, Path);
        writer.WriteStringField(6, OptionalName);
        writer.WriteVarintField(7, UpdateIfNew ? 1UL : 0UL);
        writer.WriteVarintField(8, CompressType);
        writer.WriteVarintField(9, HoldTimestamp ? 1UL : 0UL);
        writer.WriteStringField(10, FunctionName);
        writer.WriteStringField(11, ClientCwd);
        writer.WriteStringField(12, Reserve1);
        writer.WriteStringField(13, Reserve2);
        return writer.ToArray();
    }

    public static TransferConfig Parse(ReadOnlySpan<byte> data)
    {
        var result = new TransferConfig();
        var reader = new SerialReader(data);
        while (reader.ReadTag(out int field, out _))
        {
            switch (field)
            {
                case 1:
                    result.FileSize = reader.ReadVarint();
                    break;
                case 2:
                    result.Atime = reader.ReadVarint();
                    break;
                case 3:
                    result.Mtime = reader.ReadVarint();
                    break;
                case 4:
                    result.Options = reader.ReadString();
                    break;
                case 5:
                    result.Path = reader.ReadString();
                    break;
                case 6:
                    result.OptionalName = reader.ReadString();
                    break;
                case 7:
                    result.UpdateIfNew = reader.ReadVarint() != 0;
                    break;
                case 8:
                    result.CompressType = (byte)reader.ReadVarint();
                    break;
                case 9:
                    result.HoldTimestamp = reader.ReadVarint() != 0;
                    break;
                case 10:
                    result.FunctionName = reader.ReadString();
                    break;
                case 11:
                    result.ClientCwd = reader.ReadString();
                    break;
                case 12:
                    result.Reserve1 = reader.ReadString();
                    break;
                case 13:
                    result.Reserve2 = reader.ReadString();
                    break;
                default:
                    throw new HdcException($"TransferConfig 不支持的字段号 {field}");
            }
        }

        return result;
    }
}
