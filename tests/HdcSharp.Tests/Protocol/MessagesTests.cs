using HdcSharp.Protocol.Messages;
using Xunit;

namespace HdcSharp.Tests.Protocol;

public class MessagesTests
{
    [Fact]
    public void SessionHandShake_GoldenBytes() // spec §4.2 向量2
    {
        var m = new SessionHandShake { Banner = "OHOS HDC", AuthType = 0, SessionId = 1, ConnectKey = "", Buf = "", Version = "" };
        var expected = new byte[]
        {
            0x0A, 0x08, (byte)'O', (byte)'H', (byte)'O', (byte)'S', (byte)' ', (byte)'H', (byte)'D', (byte)'C',
            0x10, 0x00, 0x18, 0x01, 0x22, 0x00, 0x2A, 0x00, 0x32, 0x00
        };
        Assert.Equal(expected, m.Serialize());
    }

    [Fact]
    public void HeartbeatMsg_GoldenBytes() // spec §4.2 向量3
    {
        Assert.Equal(new byte[] { 0x08, 0x00, 0x12, 0x00 }, new HeartbeatMsg { Count = 0 }.Serialize());
    }

    [Fact]
    public void TransferConfig_RoundTrip_AllFields()
    {
        var c = new TransferConfig { FileSize = 12345, Path = "/data/x", OptionalName = "y.log", UpdateIfNew = true, CompressType = 0, FunctionName = "install" };
        var parsed = TransferConfig.Parse(c.Serialize());
        Assert.Equal(c.FileSize, parsed.FileSize);
        Assert.Equal(c.Path, parsed.Path);
        Assert.Equal(c.OptionalName, parsed.OptionalName);
        Assert.Equal(c.UpdateIfNew, parsed.UpdateIfNew);
        Assert.Equal(c.FunctionName, parsed.FunctionName);
    }

    [Fact]
    public void TransferPayload_RoundTrip()
    {
        var p = new TransferPayload { Index = 98304, CompressType = 0, CompressSize = 1024, UncompressSize = 1024 };
        var parsed = TransferPayload.Parse(p.Serialize());
        Assert.Equal(98304UL, parsed.Index);
        Assert.Equal(1024u, parsed.CompressSize);
    }
}
