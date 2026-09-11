using System.Threading.Channels;
using HdcSharp.Protocol;

namespace HdcSharp.Operations;

/// <summary>
/// 单个已注册通道的入站帧邮箱：由 <see cref="Transport.HdcConnection.FrameReceived"/> 的单一订阅者按 channelId 投递，
/// 终结（对端 CHANNEL_CLOSE、连接断开或本地释放）时完结队列并唤醒等待者。
/// </summary>
internal sealed class ChannelContext
{
    private readonly Channel<Frame> _frames = Channel.CreateUnbounded<Frame>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _closedByPeer;
    private int _terminated;

    /// <summary>创建指定通道号的上下文。</summary>
    /// <param name="channelId">通道号。</param>
    internal ChannelContext(uint channelId) => ChannelId = channelId;

    /// <summary>通道号。</summary>
    internal uint ChannelId { get; }

    /// <summary>入站帧读取端；通道终结后队列完结（已写入的帧仍可读完）。</summary>
    internal ChannelReader<Frame> Frames => _frames.Reader;

    /// <summary>通道终结任务：对端 CLOSE、连接断开或本地释放时完成。</summary>
    internal Task Closed => _closed.Task;

    /// <summary>是否收到过对端的 CHANNEL_CLOSE。</summary>
    internal bool ClosedByPeer => Volatile.Read(ref _closedByPeer) != 0;

    /// <summary>连接是否已断开。</summary>
    internal bool IsTerminated => Volatile.Read(ref _terminated) != 0;

    /// <summary>投递一帧；CLOSE 帧照常入队后立即完结（让读取方能看到终结帧）。</summary>
    /// <param name="frame">到达的帧。</param>
    internal void Accept(Frame frame)
    {
        bool close = frame.Command == HdcCommand.KernelChannelClose;
        if (close)
        {
            Volatile.Write(ref _closedByPeer, 1);
        }

        _frames.Writer.TryWrite(frame);
        if (close)
        {
            _frames.Writer.TryComplete();
            _closed.TrySetResult();
        }
    }

    /// <summary>连接断开时由分发器调用：完结队列并唤醒等待者。</summary>
    internal void OnTerminated()
    {
        Volatile.Write(ref _terminated, 1);
        _frames.Writer.TryComplete();
        _closed.TrySetResult();
    }

    /// <summary>本地释放（如交互式 shell Dispose）：不再期待入站帧，等待终结的操作立即返回。</summary>
    internal void Complete()
    {
        _frames.Writer.TryComplete();
        _closed.TrySetResult();
    }
}
