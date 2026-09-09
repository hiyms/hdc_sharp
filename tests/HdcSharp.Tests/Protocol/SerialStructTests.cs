using HdcSharp.Protocol.SerialStruct;
using Xunit;

namespace HdcSharp.Tests.Protocol;

public class SerialStructTests
{
    [Fact]
    public void Varint_SmallValue_SingleByte()
    {
        var w = new SerialWriter();
        w.WriteVarint(42);
        Assert.Equal(new byte[] { 0x2A }, w.ToArray());
    }

    [Fact]
    public void Varint_MultiByte_Leb128()
    {
        var w = new SerialWriter();
        w.WriteVarint(300);
        Assert.Equal(new byte[] { 0xAC, 0x02 }, w.ToArray());
    }

    [Fact]
    public void Varint_Zero_EmitsByte()
    {
        var w = new SerialWriter();
        w.WriteVarint(0);
        Assert.Equal(new byte[] { 0x00 }, w.ToArray());
    }

    [Fact]
    public void StringField_Empty_StillEmitted()
    {
        var w = new SerialWriter();
        w.WriteStringField(5, "");
        Assert.Equal(new byte[] { 0x2A, 0x00 }, w.ToArray());
    }

    [Fact]
    public void VarintField_Zero_StillEmitted()
    {
        var w = new SerialWriter();
        w.WriteVarintField(2, 0);
        Assert.Equal(new byte[] { 0x10, 0x00 }, w.ToArray());
    }

    [Fact]
    public void PayloadProtect_GoldenBytes() // spec §4.2 黄金向量
    {
        var w = new SerialWriter();
        w.WriteVarintField(1, 42).WriteVarintField(2, 9).WriteVarintField(3, 0).WriteVarintField(4, 9);
        Assert.Equal(new byte[] { 0x08, 0x2A, 0x10, 0x09, 0x18, 0x00, 0x20, 0x09 }, w.ToArray());
    }

    [Fact]
    public void Reader_RoundTrip_TagsAndValues()
    {
        var w = new SerialWriter();
        w.WriteVarintField(1, 42).WriteStringField(5, "OHOS HDC").WriteVarintField(3, 0);
        var r = new SerialReader(w.ToArray());
        Assert.True(r.ReadTag(out var f1, out var t1));
        Assert.Equal((1, WireType.Varint), (f1, t1));
        Assert.Equal(42UL, r.ReadVarint());
        Assert.True(r.ReadTag(out var f2, out var t2));
        Assert.Equal((5, WireType.Len), (f2, t2));
        Assert.Equal("OHOS HDC", r.ReadString());
        Assert.True(r.ReadTag(out var f3, out _));
        Assert.Equal(3, f3);
        Assert.Equal(0UL, r.ReadVarint());
        Assert.False(r.ReadTag(out _, out _));
    }
}
