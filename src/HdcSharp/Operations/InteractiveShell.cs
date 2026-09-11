using System.Threading.Channels;
using HdcSharp.Protocol;

namespace HdcSharp.Operations;

/// <summary>
/// 交互式 shell 实现：后台泵把通道入站 ECHO_RAW 帧写入 <see cref="Output"/>，
/// <see cref="Input"/> 的写入映射为 SHELL_DATA 帧，释放时发 CHANNEL_CLOSE[0] 并注销通道。
/// </summary>
internal sealed class InteractiveShell : IInteractiveShell
{
    private readonly HdcDevice _device;
    private readonly ChannelContext _context;
    private readonly Channel<byte[]> _output = Channel.CreateUnbounded<byte[]>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly ShellInputStream _input;
    private readonly CancellationTokenSource _pumpCts = new();
    private readonly Task _pumpTask;
    private int _disposed;

    /// <summary>接管已发送 SHELL_INIT 的通道并启动输出泵。</summary>
    /// <param name="device">所属设备。</param>
    /// <param name="channelId">通道号。</param>
    /// <param name="context">通道上下文。</param>
    internal InteractiveShell(HdcDevice device, uint channelId, ChannelContext context)
    {
        _device = device;
        ChannelId = channelId;
        _context = context;
        _input = new ShellInputStream(this);
        _pumpTask = PumpAsync();
    }

    /// <summary>通道号。</summary>
    internal uint ChannelId { get; }

    /// <summary>PTY 原始输出（含回显/ANSI）。</summary>
    public ChannelReader<byte[]> Output => _output.Reader;

    /// <summary>只写输入流，写入即发送 SHELL_DATA。</summary>
    public Stream Input => _input;

    /// <summary>等待 daemon 关闭通道或连接断开；仅在连接断开时抛 <see cref="HdcException"/>。</summary>
    /// <param name="ct">取消令牌。</param>
    public async Task WaitUntilClosedAsync(CancellationToken ct = default)
    {
        await _context.Closed.WaitAsync(ct).ConfigureAwait(false);
        if (_context.IsTerminated || _device.Connection.IsClosed)
        {
            throw new HdcException("连接已断开");
        }
    }

    /// <summary>释放会话：发 CHANNEL_CLOSE[0]（对端尚未关闭时）、停止泵并注销通道；幂等。</summary>
    /// <returns>释放完成的 ValueTask。</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (!_context.ClosedByPeer && !_device.Connection.IsClosed)
        {
            await ShellOperation.TryCloseAsync(_device.Connection, ChannelId).ConfigureAwait(false);
        }

        _pumpCts.Cancel();
        try
        {
            await _pumpTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _device.Connection.UnregisterChannel(ChannelId);
        _device.Dispatcher.Close(ChannelId);
        _context.Complete();
        _output.Writer.TryComplete();
        _pumpCts.Dispose();
    }

    /// <summary>把写入字节作为一条 SHELL_DATA 帧发送；已释放时抛 <see cref="ObjectDisposedException"/>。</summary>
    /// <param name="data">输入字节（原样转发，包括 0x03/0x04）。</param>
    /// <param name="ct">取消令牌。</param>
    internal async Task WriteInputAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _device.Connection.SendAsync(ChannelId, HdcCommand.ShellData, data, ct).ConfigureAwait(false);
    }

    private async Task PumpAsync()
    {
        try
        {
            await foreach (Frame frame in _context.Frames.ReadAllAsync(_pumpCts.Token).ConfigureAwait(false))
            {
                if (frame.Command == HdcCommand.KernelEchoRaw)
                {
                    _output.Writer.TryWrite(frame.Payload);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _output.Writer.TryComplete(_context.IsTerminated || _device.Connection.IsClosed
                ? new HdcException("连接已断开")
                : null);
        }
    }
}
