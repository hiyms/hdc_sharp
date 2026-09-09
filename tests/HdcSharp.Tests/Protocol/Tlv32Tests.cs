using HdcSharp.Protocol;
using Xunit;

namespace HdcSharp.Tests.Protocol;

public class Tlv32Tests
{
    [Fact]
    public void Serialize_AscendingTagOrder_LeLayout()
    {
        var data = Tlv32.Serialize(new Dictionary<uint, byte[]> { [1] = "com.demo"u8.ToArray(), [0] = "ls"u8.ToArray() });
        // tag0 在前：0,0,0,0 | 2,0,0,0 | 'l','s' | 1,0,0,0 | 8,0,0,0 | "com.demo"
        var expected = new byte[] { 0,0,0,0, 2,0,0,0, (byte)'l', (byte)'s', 1,0,0,0, 8,0,0,0,
            (byte)'c',(byte)'o',(byte)'m',(byte)'.',(byte)'d',(byte)'e',(byte)'m',(byte)'o' };
        Assert.Equal(expected, data);
    }

    [Fact]
    public void Parse_RoundTrip_ShellOptions()
    {
        var entries = new Dictionary<uint, byte[]>
        {
            [Tlv32.TagShellCmd] = "ls -l"u8.ToArray(),
            [Tlv32.TagShellBundle] = "com.demo"u8.ToArray(),
        };

        var map = Tlv32.Parse(Tlv32.Serialize(entries));

        Assert.Equal(2, map.Count);
        Assert.Equal("ls -l"u8.ToArray(), map[Tlv32.TagShellCmd]);
        Assert.Equal("com.demo"u8.ToArray(), map[Tlv32.TagShellBundle]);
    }

    [Fact]
    public void Parse_MalformedValueLength_Throws()
    {
        // tag=0, len=99，但 value 仅 2 字节
        var data = new byte[] { 0,0,0,0, 99,0,0,0, (byte)'l', (byte)'s' };
        Assert.Throws<HdcException>(() => Tlv32.Parse(data));
    }
}
