using HdcSharp.Protocol;
using Xunit;

namespace HdcSharp.Tests.Protocol;

public class Tlv16Tests
{
    [Fact]
    public void Encode_AuthType_MatchesSpecExample() // spec §4.4：33 字节
    {
        var bytes = Tlv16.Encode(("authtype", "1"));
        Assert.Equal(33, bytes.Length);
        Assert.Equal("authtype        ", System.Text.Encoding.ASCII.GetString(bytes, 0, 16));
        Assert.Equal("1               ", System.Text.Encoding.ASCII.GetString(bytes, 16, 16));
        Assert.Equal("1", System.Text.Encoding.ASCII.GetString(bytes, 32, 1));
    }

    [Fact]
    public void Parse_RoundTrip_MultipleEntries()
    {
        var buf = Tlv16.Serialize(new[] { ("authtype", "1"), ("supportfeatures", "Ver: 3.2.0f,TCP,win,heartbeat") });
        var map = Tlv16.Parse(buf);
        Assert.Equal("1", map["authtype"]);
        Assert.Equal("Ver: 3.2.0f,TCP,win,heartbeat", map["supportfeatures"]);
    }
}
