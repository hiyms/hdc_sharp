using System.Text;
using HdcSharp.Protocol;
using HdcSharp.Protocol.Tar;
using Xunit;

namespace HdcSharp.Tests.Protocol;

public class TarTests
{
    [Fact]
    public void Header_ChecksumIsSumPlus256_Octal()
    {
        var block = new byte[512];
        TarHeader.WriteTo(block, "a.hap", 5, TarEntryType.NormalFile);
        // spec §4.7.4：校验和 = 除 chksum 外全字节和 + 256（等于 POSIX「8 空格占位」值）
        int sum = 0;
        for (int i = 0; i < 512; i++)
        {
            if (i < 148 || i >= 156) sum += block[i];
        }

        var chksumStr = Encoding.ASCII.GetString(block, 148, 8).TrimEnd('\0', ' ');
        Assert.Equal(Convert.ToString(sum + 256, 8).PadLeft(6, '0'), chksumStr);
    }

    [Fact]
    public void WriterReader_RoundTrip()
    {
        using var ms = new MemoryStream();
        var w = new TarWriter(ms);
        w.AddDirectory("dir");
        w.AddFile("dir/a.txt", new MemoryStream("hello"u8.ToArray()));
        w.Finish();
        ms.Position = 0;
        var r = new TarReader(ms);
        Assert.True(r.TryReadEntry(out var e1));
        Assert.Equal("dir", e1.Name);
        Assert.Equal(TarEntryType.Directory, e1.Type);
        Assert.Equal(0, e1.Size);
        Assert.True(r.TryReadEntry(out var e2));
        Assert.Equal("dir/a.txt", e2.Name);
        Assert.Equal(TarEntryType.NormalFile, e2.Type);
        Assert.Equal(5, e2.Size);
        var buf = new byte[16];
        Assert.Equal(5, r.ReadContent(buf));
        Assert.Equal("hello"u8.ToArray(), buf.AsSpan(0, 5).ToArray());
        Assert.False(r.TryReadEntry(out _));
    }

    [Fact]
    public void Header_WriteTo_LayoutFields()
    {
        var block = new byte[512];
        TarHeader.WriteTo(block, "f.bin", 0, TarEntryType.NormalFile);
        Assert.Equal("ustar ", Encoding.ASCII.GetString(block, 257, 6));
        Assert.Equal(new byte[] { 0x20, 0x00 }, block[263..265].ToArray());
        Assert.Equal("0000644\0", Encoding.ASCII.GetString(block, 100, 8));
        Assert.Equal("00000000000\0", Encoding.ASCII.GetString(block, 124, 12));
        Assert.Equal((byte)'0', block[156]);
    }

    [Fact]
    public void Header_WriteTo_DirectoryModeAndSize()
    {
        var block = new byte[512];
        TarHeader.WriteTo(block, "dir", 0, TarEntryType.Directory);
        Assert.Equal("0000755\0", Encoding.ASCII.GetString(block, 100, 8));
        Assert.Equal("00000000000\0", Encoding.ASCII.GetString(block, 124, 12));
        Assert.Equal((byte)'5', block[156]);
    }

    [Fact]
    public void Header_LongName_SplitsPrefix_RoundTrip()
    {
        string name = string.Concat(Enumerable.Repeat("dir/", 30)) + "leaf.bin";
        var block = new byte[512];
        TarHeader.WriteTo(block, name, 0, TarEntryType.NormalFile);

        // daemon 端（C++ Header::Name / Rust Header::name）将 prefix 与 name 直接拼接，prefix 须以 '/' 结尾
        string prefix = Encoding.UTF8.GetString(block[345..500].TakeWhile(b => b != 0).ToArray());
        string nameField = Encoding.UTF8.GetString(block[0..100].TakeWhile(b => b != 0).ToArray());
        Assert.EndsWith("/", prefix);
        Assert.Equal(name, prefix + nameField);

        Assert.Equal(name, TarHeader.Parse(block).Name);
    }

    [Fact]
    public void Header_NameTooLongWithoutSplitPoint_Throws()
    {
        var block = new byte[512];
        Assert.Throws<HdcException>(() => TarHeader.WriteTo(block, new string('x', 300), 0, TarEntryType.NormalFile));
        Assert.Throws<HdcException>(() => TarHeader.WriteTo(block, new string('x', 150) + "/" + new string('x', 160), 0, TarEntryType.NormalFile));
    }

    [Fact]
    public void Header_Parse_AcceptsPosixMagicAndReserveTypeflag()
    {
        var block = new byte[512];
        "ustar\0"u8.CopyTo(block.AsSpan(257));
        "00"u8.CopyTo(block.AsSpan(263));
        "some/file.txt"u8.CopyTo(block.AsSpan(0));
        "00000000006\0"u8.CopyTo(block.AsSpan(124));
        block[156] = (byte)'7';

        var info = TarHeader.Parse(block);
        Assert.Equal(TarEntryType.NormalFile, info.Type);
        Assert.Equal(6, info.Size);
        Assert.Equal("some/file.txt", info.Name);
    }

    [Fact]
    public void Header_Parse_RejectsUnknownMagicAndTypeflag()
    {
        var magic = new byte[512];
        TarHeader.WriteTo(magic, "f", 0, TarEntryType.NormalFile);
        magic[257] = (byte)'x';
        Assert.Throws<HdcException>(() => TarHeader.Parse(magic));

        var typeflag = new byte[512];
        TarHeader.WriteTo(typeflag, "f", 0, TarEntryType.NormalFile);
        typeflag[156] = (byte)'2';
        Assert.Throws<HdcException>(() => TarHeader.Parse(typeflag));
    }

    [Fact]
    public void Header_IsZeroBlock()
    {
        Assert.True(TarHeader.IsZeroBlock(new byte[512]));
        var b = new byte[512];
        b[511] = 1;
        Assert.False(TarHeader.IsZeroBlock(b));
    }

    [Fact]
    public void WriterReader_PartialReadThenSkip()
    {
        using var ms = new MemoryStream();
        var w = new TarWriter(ms);
        w.AddFile("a.bin", new MemoryStream(new byte[1000]));
        w.AddFile("b.bin", new MemoryStream(new byte[] { 1, 2, 3 }));
        w.Finish();
        ms.Position = 0;
        var r = new TarReader(ms);
        Assert.True(r.TryReadEntry(out var e1));
        Assert.Equal("a.bin", e1.Name);
        Assert.Equal(1000, e1.Size);
        Assert.Equal(10, r.ReadContent(new byte[10]));
        r.SkipToNextEntry();
        Assert.True(r.TryReadEntry(out var e2));
        Assert.Equal("b.bin", e2.Name);
        var buf = new byte[8];
        Assert.Equal(3, r.ReadContent(buf));
        Assert.Equal(new byte[] { 1, 2, 3 }, buf.AsSpan(0, 3).ToArray());
        Assert.False(r.TryReadEntry(out _));
    }

    [Fact]
    public void Writer_NormalizesBackslash()
    {
        using var ms = new MemoryStream();
        var w = new TarWriter(ms);
        w.AddDirectory("dir");
        w.AddFile("dir\\sub\\a.txt", new MemoryStream("x"u8.ToArray()));
        w.Finish();
        ms.Position = 0;
        var r = new TarReader(ms);
        Assert.True(r.TryReadEntry(out _));
        Assert.True(r.TryReadEntry(out var e));
        Assert.Equal("dir/sub/a.txt", e.Name);
    }

    [Fact]
    public void Writer_RejectsWriteAfterFinish()
    {
        using var ms = new MemoryStream();
        var w = new TarWriter(ms);
        w.Finish();
        Assert.Throws<HdcException>(() => w.AddDirectory("x"));
        Assert.Throws<HdcException>(() => w.AddFile("x", new MemoryStream()));
    }

    [Fact]
    public void Reader_TruncatedHeader_Throws()
    {
        var r = new TarReader(new MemoryStream(new byte[100]));
        Assert.Throws<HdcException>(() => r.TryReadEntry(out _));
    }

    [Fact]
    public void Reader_TruncatedContent_SkipThrows()
    {
        using var ms = new MemoryStream();
        var w = new TarWriter(ms);
        w.AddFile("a.bin", new MemoryStream(new byte[600]));
        w.Finish();
        var truncated = new byte[512 + 100];
        Array.Copy(ms.ToArray(), truncated, truncated.Length);
        var r = new TarReader(new MemoryStream(truncated));
        Assert.True(r.TryReadEntry(out _));
        Assert.Throws<HdcException>(r.SkipToNextEntry);
    }
}
