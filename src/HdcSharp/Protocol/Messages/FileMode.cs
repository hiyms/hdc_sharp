using HdcSharp.Protocol.SerialStruct;

namespace HdcSharp.Protocol.Messages;

/// <summary>文件/目录权限元数据消息（FILE_MODE/DIR_MODE 载荷）：权限位、属主与 SELinux 上下文。</summary>
internal sealed class FileMode
{
    public ulong Perm { get; set; }

    public ulong Uid { get; set; }

    public ulong Gid { get; set; }

    public string Context { get; set; } = "";

    public string FullName { get; set; } = "";

    public byte[] Serialize()
    {
        var writer = new SerialWriter();
        writer.WriteVarintField(1, Perm);
        writer.WriteVarintField(2, Uid);
        writer.WriteVarintField(3, Gid);
        writer.WriteStringField(4, Context);
        writer.WriteStringField(5, FullName);
        return writer.ToArray();
    }

    public static FileMode Parse(ReadOnlySpan<byte> data)
    {
        var result = new FileMode();
        var reader = new SerialReader(data);
        while (reader.ReadTag(out int field, out _))
        {
            switch (field)
            {
                case 1:
                    result.Perm = reader.ReadVarint();
                    break;
                case 2:
                    result.Uid = reader.ReadVarint();
                    break;
                case 3:
                    result.Gid = reader.ReadVarint();
                    break;
                case 4:
                    result.Context = reader.ReadString();
                    break;
                case 5:
                    result.FullName = reader.ReadString();
                    break;
                default:
                    throw new HdcException($"FileMode 不支持的字段号 {field}");
            }
        }

        return result;
    }
}
