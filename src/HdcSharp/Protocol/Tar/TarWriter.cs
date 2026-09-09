namespace HdcSharp.Protocol.Tar;

/// <summary>
/// ustar tar 归档打包器：顺序写入条目头与负载（负载按 512 字节对齐补零），Finish() 写两个全零块终止归档。
/// 不接管底层 <see cref="Stream"/> 的所有权，由调用方负责关闭。
/// </summary>
public sealed class TarWriter
{
    private const int CopyBufferSize = 64 * 1024;

    private readonly Stream _stream;
    private readonly byte[] _block = new byte[TarHeader.BlockSize];
    private readonly byte[] _copyBuffer = new byte[CopyBufferSize];
    private bool _finished;

    /// <summary>
    /// 以目标流创建打包器。
    /// </summary>
    /// <param name="stream">归档字节写入目标。</param>
    public TarWriter(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _stream = stream;
    }

    /// <summary>
    /// 写入一个普通文件条目：头（size=剩余长度）+ 内容 + 512 对齐补零。
    /// </summary>
    /// <param name="entryName">条目名，'\' 会归一为 '/'，超过 100 UTF-8 字节时在 '/' 处拆 prefix/name。</param>
    /// <param name="content">文件内容流，须可定位（CanSeek，tar 头要求预知大小）；从当前位置写到末尾。</param>
    /// <exception cref="HdcException">已调用 <see cref="Finish"/>、条目名为空、流不可定位、名字拆不开或流提前结束时抛出。</exception>
    public void AddFile(string entryName, Stream content)
    {
        ArgumentNullException.ThrowIfNull(content);
        EnsureNotFinished();
        long size = GetContentLength(content);
        TarHeader.WriteTo(_block, NormalizeEntryName(entryName), size, TarEntryType.NormalFile);
        _stream.Write(_block);
        CopyExactly(content, size);
        WritePadding(size);
    }

    /// <summary>
    /// 写入一个目录条目（size 恒为 0，无负载）。
    /// </summary>
    /// <param name="entryName">条目名，'\' 会归一为 '/'。</param>
    /// <exception cref="HdcException">已调用 <see cref="Finish"/> 或条目名为空时抛出。</exception>
    public void AddDirectory(string entryName)
    {
        EnsureNotFinished();
        TarHeader.WriteTo(_block, NormalizeEntryName(entryName), 0, TarEntryType.Directory);
        _stream.Write(_block);
    }

    /// <summary>
    /// 写入两个 512 字节全零块作为归档结束标记；重复调用为幂等空操作，之后不可再写条目。
    /// </summary>
    public void Finish()
    {
        if (_finished)
        {
            return;
        }

        _finished = true;
        Array.Clear(_block);
        _stream.Write(_block);
        _stream.Write(_block);
    }

    private void EnsureNotFinished()
    {
        if (_finished)
        {
            throw new HdcException("TarWriter 已调用 Finish，不能再写入条目");
        }
    }

    private static string NormalizeEntryName(string entryName)
    {
        if (string.IsNullOrWhiteSpace(entryName))
        {
            throw new HdcException("tar 条目名不能为空");
        }

        // tar 条目名按 POSIX 惯例恒用 '/' 分隔（daemon 端按 UTF-8 路径处理），Windows 分隔符须归一
        return entryName.Replace('\\', '/');
    }

    private static long GetContentLength(Stream content)
    {
        if (!content.CanSeek)
        {
            throw new HdcException("tar 条目头须预知大小：content 流必须可定位（CanSeek）");
        }

        return content.Length - content.Position;
    }

    private void CopyExactly(Stream content, long size)
    {
        long copied = 0;
        while (copied < size)
        {
            int read = content.Read(_copyBuffer, 0, (int)Math.Min(_copyBuffer.Length, size - copied));
            if (read <= 0)
            {
                throw new HdcException($"tar 条目内容流提前结束：应为 {size} 字节，实际仅得 {copied} 字节");
            }

            _stream.Write(_copyBuffer, 0, read);
            copied += read;
        }
    }

    private void WritePadding(long size)
    {
        int padding = (int)(TarHeader.BlockSize - size % TarHeader.BlockSize) % TarHeader.BlockSize;
        if (padding == 0)
        {
            return;
        }

        Array.Clear(_block, 0, padding);
        _stream.Write(_block, 0, padding);
    }
}
