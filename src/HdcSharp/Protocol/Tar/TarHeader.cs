using System.Buffers;
using System.Text;

namespace HdcSharp.Protocol.Tar;

/// <summary>
/// tar 条目类型，typeflag 字节取值与官方 TypeFlage 对齐（'0'=普通文件，'5'=目录）。
/// </summary>
public enum TarEntryType
{
    /// <summary>普通文件（typeflag '0'）。</summary>
    NormalFile = '0',

    /// <summary>目录（typeflag '5'）。</summary>
    Directory = '5',
}

/// <summary>
/// 解析得到的 tar 条目头信息。
/// </summary>
/// <param name="Name">条目名，已重组 prefix，恒用 '/' 分隔。</param>
/// <param name="Size">条目负载字节数，目录恒为 0。</param>
/// <param name="Type">条目类型。</param>
public readonly record struct TarHeaderInfo(string Name, long Size, TarEntryType Type);

/// <summary>
/// 512 字节 ustar 头的写入与解析（spec §4.7.4，目录安装打包用）。
/// 布局：name[100] mode[8] uid[8] gid[8] size[12] mtime[12] chksum[8] typeflag[1] linkname[100]
/// magic[6] version[2] uname[32] gname[32] devmajor[8] devminor[8] prefix[155] pad[12]。
/// </summary>
public static class TarHeader
{
    /// <summary>tar 块大小（头与负载对齐单位均为 512 字节）。</summary>
    public const int BlockSize = 512;

    private const int NameOffset = 0;
    private const int NameLength = 100;
    private const int ModeOffset = 100;
    private const int ModeLength = 8;
    private const int SizeOffset = 124;
    private const int SizeLength = 12;
    private const int MtimeOffset = 136;
    private const int MtimeLength = 12;
    private const int ChksumOffset = 148;
    private const int ChksumLength = 8;
    private const int TypeflagOffset = 156;
    private const int MagicOffset = 257;
    private const int MagicLength = 6;
    private const int VersionOffset = 263;
    private const int VersionLength = 2;
    private const int PrefixOffset = 345;
    private const int PrefixLength = 155;

    // 11 位八进制可表示的最大负载（2^33 - 1）
    private const long MaxPayloadSize = (1L << 33) - 1;

    private static readonly byte[] GnuMagicBytes = { (byte)'u', (byte)'s', (byte)'t', (byte)'a', (byte)'r', 0x20 };
    private static readonly byte[] PosixMagicBytes = { (byte)'u', (byte)'s', (byte)'t', (byte)'a', (byte)'r', 0x00 };

    /// <summary>
    /// 将一条 ustar 头写入 512 字节块：先清零，再填 name/mode/size/mtime/typeflag/magic，最后计算校验和。
    /// </summary>
    /// <param name="block">至少 512 字节的目标块。</param>
    /// <param name="name">条目名，UTF-8 编码；超过 100 字节时在 '/' 处拆分为 prefix/name，拆不开则抛出。</param>
    /// <param name="size">负载字节数，11 位八进制可表示（0..8589934591），目录恒传 0。</param>
    /// <param name="type">条目类型。</param>
    /// <exception cref="HdcException">块不足 512 字节、size 超出 11 位八进制范围，或名字超长且拆不开。</exception>
    public static void WriteTo(Span<byte> block, string name, long size, TarEntryType type)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (block.Length < BlockSize)
        {
            throw new HdcException($"tar 头块仅 {block.Length} 字节，至少需要 {BlockSize} 字节");
        }

        if (size < 0 || size > MaxPayloadSize)
        {
            throw new HdcException($"tar 条目 size {size} 非法（须在 0..{MaxPayloadSize}）");
        }

        block.Slice(0, BlockSize).Clear();
        WriteName(block, name);
        WriteMode(block, type);
        WriteOctalField(block.Slice(SizeOffset, SizeLength), size, digits: SizeLength - 1, terminated: true);
        WriteOctalField(block.Slice(MtimeOffset, MtimeLength), 0, digits: MtimeLength - 1, terminated: true);
        block[TypeflagOffset] = (byte)type;
        GnuMagicBytes.CopyTo(block.Slice(MagicOffset, MagicLength));
        // 官方写法 version = { 0x20, 0x00 }（GNU 世代格式）
        block[VersionOffset] = 0x20;
        block[VersionOffset + 1] = 0x00;
        WriteChecksum(block);
    }

    /// <summary>
    /// 将一条 ustar 头写入 <see cref="IBufferWriter{T}"/>，供流式打包复用缓冲。
    /// </summary>
    /// <param name="sink">字节写入目标。</param>
    /// <param name="name">条目名，规则同 <see cref="WriteTo"/>。</param>
    /// <param name="size">负载字节数。</param>
    /// <param name="type">条目类型。</param>
    public static void Write(IBufferWriter<byte> sink, string name, long size, TarEntryType type)
    {
        Span<byte> span = sink.GetSpan(BlockSize);
        WriteTo(span, name, size, type);
        sink.Advance(BlockSize);
    }

    /// <summary>
    /// 解析一条 512 字节 ustar 头。
    /// </summary>
    /// <param name="block">至少 512 字节的块。</param>
    /// <returns>条目名（prefix 已重组）、负载大小与类型。</returns>
    /// <exception cref="HdcException">块不足 512 字节、magic 非法、size 字段非八进制，或 typeflag 不受支持。</exception>
    public static TarHeaderInfo Parse(ReadOnlySpan<byte> block)
    {
        if (block.Length < BlockSize)
        {
            throw new HdcException($"tar 头块仅 {block.Length} 字节，至少需要 {BlockSize} 字节");
        }

        ValidateMagic(block);
        byte typeflag = block[TypeflagOffset];
        // '7' 为官方 TypeFlage::RESERVE 保留字，两世代 daemon 均按普通文件处理
        TarEntryType type = typeflag switch
        {
            (byte)'0' => TarEntryType.NormalFile,
            (byte)'5' => TarEntryType.Directory,
            (byte)'7' => TarEntryType.NormalFile,
            _ => throw new HdcException($"tar 条目 typeflag '{(char)typeflag}'（0x{typeflag:X2}）不受支持"),
        };

        long size = ParseOctal(block.Slice(SizeOffset, SizeLength - 1));
        string name = ReadFullName(block);
        return new TarHeaderInfo(name, size, type);
    }

    /// <summary>
    /// 判断块是否全为 0x00（tar 结束标记由两个全零块构成）。
    /// </summary>
    /// <param name="block">待判断的块。</param>
    /// <returns>整块全零时返回 true。</returns>
    public static bool IsZeroBlock(ReadOnlySpan<byte> block)
    {
        return block.IndexOfAnyExcept((byte)0) < 0;
    }

    private static void WriteName(Span<byte> block, string name)
    {
        byte[] nameBytes = Encoding.UTF8.GetBytes(name);
        if (nameBytes.Length <= NameLength)
        {
            nameBytes.CopyTo(block.Slice(NameOffset, NameLength));
            return;
        }

        if (!TrySplitName(nameBytes, out int slashIndex))
        {
            throw new HdcException(
                $"tar 条目名 UTF-8 长度 {nameBytes.Length} 字节超过 {NameLength}，且无法在 '/' 处拆为 prefix(≤{PrefixLength})+name(≤{NameLength})（总长须 ≤{PrefixLength + NameLength}）");
        }

        // daemon 端（C++ Header::Name / Rust Header::name）将 prefix 与 name 直接拼接、无分隔符，
        // 故拆分时把 '/' 保留在 prefix 尾部，保证设备端重组出原路径
        nameBytes.AsSpan(slashIndex + 1).CopyTo(block.Slice(NameOffset, NameLength));
        nameBytes.AsSpan(0, slashIndex + 1).CopyTo(block.Slice(PrefixOffset, PrefixLength));
    }

    private static bool TrySplitName(byte[] nameBytes, out int slashIndex)
    {
        // prefix=nameBytes[..slashIndex+1] ≤155 且 name=nameBytes[slashIndex+1..] ≤100
        int min = Math.Max(0, nameBytes.Length - NameLength - 1);
        int max = Math.Min(PrefixLength - 1, nameBytes.Length - 2);
        for (int i = min; i <= max; i++)
        {
            if (nameBytes[i] == (byte)'/')
            {
                slashIndex = i;
                return true;
            }
        }

        slashIndex = -1;
        return false;
    }

    private static void WriteMode(Span<byte> block, TarEntryType type)
    {
        string mode = type == TarEntryType.Directory ? "0000755" : "0000644";
        _ = Encoding.ASCII.GetBytes(mode, block.Slice(ModeOffset, ModeLength));
        block[ModeOffset + ModeLength - 1] = 0;
    }

    private static void WriteChecksum(Span<byte> block)
    {
        // chksum 字段当前仍为全零：官方 C++/Rust 按「全块和 + 256」计算，等价于 POSIX 的 8 空格占位 unsigned 校验和
        long sum = 256;
        for (int i = 0; i < BlockSize; i++)
        {
            sum += block[i];
        }

        WriteOctalField(block.Slice(ChksumOffset, ChksumLength), sum, digits: 6, terminated: false);
        block[ChksumOffset + 6] = 0;
        block[ChksumOffset + 7] = 0x20;
    }

    private static void WriteOctalField(Span<byte> field, long value, int digits, bool terminated)
    {
        string octal = Convert.ToString(value, 8).PadLeft(digits, '0');
        _ = Encoding.ASCII.GetBytes(octal, field.Slice(0, digits));
        if (terminated)
        {
            field[digits] = 0;
        }
    }

    private static void ValidateMagic(ReadOnlySpan<byte> block)
    {
        ReadOnlySpan<byte> magic = block.Slice(MagicOffset, MagicLength);
        if (magic.SequenceEqual(GnuMagicBytes) || magic.SequenceEqual(PosixMagicBytes))
        {
            return;
        }

        throw new HdcException($"tar magic \"{Encoding.ASCII.GetString(magic)}\" 非法（应为 \"ustar \" 或 \"ustar\\0\"）");
    }

    private static long ParseOctal(ReadOnlySpan<byte> field)
    {
        int length = 0;
        while (length < field.Length && field[length] is not (0 or (byte)' '))
        {
            length++;
        }

        long value = 0;
        for (int i = 0; i < length; i++)
        {
            byte b = field[i];
            if (b is < (byte)'0' or > (byte)'7')
            {
                throw new HdcException($"tar size 字段含非八进制字符 '{(char)b}'（0x{b:X2}）");
            }

            value = (value << 3) + (b - (byte)'0');
        }

        return value;
    }

    private static string ReadFullName(ReadOnlySpan<byte> block)
    {
        string name = Encoding.UTF8.GetString(ReadCString(block.Slice(NameOffset, NameLength)));
        string prefix = Encoding.UTF8.GetString(ReadCString(block.Slice(PrefixOffset, PrefixLength)));
        if (prefix.Length == 0)
        {
            return name;
        }

        // 官方拆分把 '/' 留在 prefix 尾部，直接拼接；POSIX 第三方风格 prefix 不带尾 '/'，需补分隔符
        return prefix.EndsWith('/') ? prefix + name : prefix + "/" + name;
    }

    private static ReadOnlySpan<byte> ReadCString(ReadOnlySpan<byte> field)
    {
        int end = field.IndexOf((byte)0);
        return end < 0 ? field : field.Slice(0, end);
    }
}
