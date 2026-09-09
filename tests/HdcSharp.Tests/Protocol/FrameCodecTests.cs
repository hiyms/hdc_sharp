using HdcSharp.Protocol;
using Xunit;

namespace HdcSharp.Tests.Protocol;

public class FrameCodecTests
{
    [Fact]
    public void Encode_WorkedExample_MatchesSpec() // spec §2.3：channelId=42, ECHO(9), payload "hi"
    {
        var frame = FrameCodec.Encode(42, HdcCommand.KernelEcho, "hi"u8);
        var expected = new byte[]
        {
            0x48, 0x57, 0x00, 0x00, 0x01, 0x00, 0x08, 0x00, 0x00, 0x00, 0x02,
            0x08, 0x2A, 0x10, 0x09, 0x18, 0x00, 0x20, 0x09,
            (byte)'h', (byte)'i'
        };
        Assert.Equal(expected, frame);
    }

    [Fact]
    public void Decoder_ReassemblesSplitFrames()
    {
        var frame = FrameCodec.Encode(42, HdcCommand.KernelEcho, "hi"u8);
        var dec = new FrameDecoder();
        dec.Append(frame.AsSpan(0, 7));                 // 半帧
        Assert.False(dec.TryRead(out _));
        dec.Append(frame.AsSpan(7));                    // 剩余
        Assert.True(dec.TryRead(out var f));
        Assert.Equal(42u, f.ChannelId);
        Assert.Equal(HdcCommand.KernelEcho, f.Command);
        Assert.Equal("hi"u8.ToArray(), f.Payload);
        Assert.False(dec.TryRead(out _));
    }

    [Fact]
    public void Decoder_HandlesCoalescedFrames()
    {
        var a = FrameCodec.Encode(1, HdcCommand.KernelEcho, "a"u8);
        var b = FrameCodec.Encode(2, HdcCommand.KernelEchoRaw, "bb"u8);
        var dec = new FrameDecoder();
        dec.Append(a.AsSpan().ToArray().Concat(b.AsSpan().ToArray()).ToArray());
        Assert.True(dec.TryRead(out var f1) && f1.ChannelId == 1);
        Assert.True(dec.TryRead(out var f2) && f2.ChannelId == 2 && f2.Payload.Length == 2);
    }

    [Fact]
    public void Decoder_BadVCode_Throws()
    {
        var frame = FrameCodec.Encode(42, HdcCommand.KernelEcho, "hi"u8);
        frame[17] = 0x00;                               // 破坏 vCode（偏移 11+6）
        var dec = new FrameDecoder();
        dec.Append(frame);
        Assert.Throws<HdcException>(() => dec.TryRead(out _));
    }

    [Fact]
    public void Decoder_BadVCodeValue_Throws()
    {
        var frame = FrameCodec.Encode(42, HdcCommand.KernelEcho, "hi"u8);
        frame[18] = 0x00;                               // 破坏 vCode 值（偏移 11+7，Protect 字段 4 的值字节）
        var dec = new FrameDecoder();
        dec.Append(frame);
        Assert.Throws<HdcException>(() => dec.TryRead(out _));
    }

    [Fact]
    public void Decoder_OversizedFrame_Throws()
    {
        var head = new byte[]
        {
            (byte)'H', (byte)'W', 0x00, 0x00, HdcConstants.ProtocolVer,
            0x00, 0x08, 0xFF, 0xFF, 0xFF, 0xFF,
        };
        var dec = new FrameDecoder();
        dec.Append(head);
        Assert.Throws<HdcException>(() => dec.TryRead(out _));
    }
}
