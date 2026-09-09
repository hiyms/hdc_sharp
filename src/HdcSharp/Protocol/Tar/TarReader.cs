namespace HdcSharp.Protocol.Tar;

/// <summary>
/// ustar tar 归档解包器：逐条读取条目头，按剩余大小读取或跳过负载（含 512 对齐补零）。
/// 不接管底层 <see cref="Stream"/> 的所有权，由调用方负责关闭。
/// </summary>
public sealed class TarReader
{
    private const int DiscardBufferSize = 64 * 1024;

    private readonly Stream _stream;
    private readonly byte[] _header = new byte[TarHeader.BlockSize];
    private readonly byte[] _discardBuffer = new byte[DiscardBufferSize];
    private long _remaining;
    private long _pendingPadding;

    /// <summary>
    /// 以源流创建解包器。
    /// </summary>
    /// <param name="stream">归档字节来源。</param>
    public TarReader(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _stream = stream;
    }

    /// <summary>
    /// 读取下一个条目头；上一条目有未消费的负载/补零时自动跳过。
    /// </summary>
    /// <param name="info">成功时为条目头信息。</param>
    /// <returns>读到条目返回 true；遇到全零块（归档结束标记）或流尾返回 false。</returns>
    /// <exception cref="HdcException">头块被截断、magic/typeflag/size 非法，或跳过残留数据时流提前结束。</exception>
    public bool TryReadEntry(out TarHeaderInfo info)
    {
        SkipPending();
        int total = 0;
        while (total < TarHeader.BlockSize)
        {
            int read = _stream.Read(_header, total, TarHeader.BlockSize - total);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        if (total == 0)
        {
            info = default;
            return false;
        }

        if (total < TarHeader.BlockSize)
        {
            throw new HdcException($"tar 头被截断：仅读到 {total}/{TarHeader.BlockSize} 字节");
        }

        if (TarHeader.IsZeroBlock(_header))
        {
            info = default;
            return false;
        }

        info = TarHeader.Parse(_header);
        _remaining = info.Size;
        _pendingPadding = (TarHeader.BlockSize - info.Size % TarHeader.BlockSize) % TarHeader.BlockSize;
        return true;
    }

    /// <summary>
    /// 读取当前条目的负载字节，至多消费到该条目剩余大小，不会读入对齐补零。
    /// </summary>
    /// <param name="buffer">读取目标。</param>
    /// <returns>实际读取的字节数；无当前条目、无剩余负载或 <paramref name="buffer"/> 为空时返回 0。</returns>
    public int ReadContent(Span<byte> buffer)
    {
        if (_remaining == 0 || buffer.Length == 0)
        {
            return 0;
        }

        int max = (int)Math.Min(buffer.Length, _remaining);
        int total = 0;
        while (total < max)
        {
            int read = _stream.Read(buffer.Slice(total, max - total));
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        _remaining -= total;
        return total;
    }

    /// <summary>
    /// 跳过当前条目未消费的负载与 512 对齐补零，定位到下一条目头。
    /// </summary>
    /// <exception cref="HdcException">流提前结束时抛出。</exception>
    public void SkipToNextEntry()
    {
        SkipPending();
    }

    private void SkipPending()
    {
        long skip = _remaining + _pendingPadding;
        while (skip > 0)
        {
            int read = _stream.Read(_discardBuffer, 0, (int)Math.Min(_discardBuffer.Length, skip));
            if (read == 0)
            {
                throw new HdcException($"tar 数据被截断：还需跳过 {skip} 字节但流已结束");
            }

            skip -= read;
        }

        _remaining = 0;
        _pendingPadding = 0;
    }
}
