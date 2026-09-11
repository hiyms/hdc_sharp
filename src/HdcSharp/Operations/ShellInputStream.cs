namespace HdcSharp.Operations;

/// <summary>
/// 交互式 shell 的只写输入流：同步/异步写入均映射为一条 SHELL_DATA 帧；读与定位一律不支持。
/// </summary>
internal sealed class ShellInputStream : Stream
{
    private readonly InteractiveShell _owner;

    /// <summary>创建绑定到指定交互式 shell 的输入流。</summary>
    /// <param name="owner">所属交互式 shell。</param>
    internal ShellInputStream(InteractiveShell owner) => _owner = owner;

    /// <summary>本流不可读。</summary>
    public override bool CanRead => false;

    /// <summary>本流不可定位。</summary>
    public override bool CanSeek => false;

    /// <summary>本流可写。</summary>
    public override bool CanWrite => true;

    /// <summary>不支持长度查询。</summary>
    public override long Length => throw new NotSupportedException("交互式 shell 输入流不支持长度查询");

    /// <summary>不支持定位。</summary>
    public override long Position
    {
        get => throw new NotSupportedException("交互式 shell 输入流不支持定位");
        set => throw new NotSupportedException("交互式 shell 输入流不支持定位");
    }

    /// <summary>无缓冲，空操作。</summary>
    public override void Flush()
    {
    }

    /// <summary>无缓冲，立即完成。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>本流不可读。</summary>
    public override int Read(byte[] buffer, int offset, int count)
        => throw new NotSupportedException("交互式 shell 输入流不可读");

    /// <summary>不支持定位。</summary>
    public override long Seek(long offset, SeekOrigin origin)
        => throw new NotSupportedException("交互式 shell 输入流不支持定位");

    /// <summary>不支持长度设置。</summary>
    public override void SetLength(long value)
        => throw new NotSupportedException("交互式 shell 输入流不支持长度设置");

    /// <summary>同步写入并阻塞至发送完成（Stream 契约要求；建议优先使用 <c>WriteAsync</c>）。</summary>
    public override void Write(byte[] buffer, int offset, int count)
        => _owner.WriteInputAsync(buffer.AsMemory(offset, count), CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>同步写入并阻塞至发送完成（Stream 契约要求；建议优先使用 <c>WriteAsync</c>）。</summary>
    public override void Write(ReadOnlySpan<byte> buffer)
        => _owner.WriteInputAsync(buffer.ToArray(), CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>异步写入，映射为一条 SHELL_DATA 帧。</summary>
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => _owner.WriteInputAsync(buffer.AsMemory(offset, count), cancellationToken);

    /// <summary>异步写入，映射为一条 SHELL_DATA 帧。</summary>
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        => new(_owner.WriteInputAsync(buffer, cancellationToken));
}
