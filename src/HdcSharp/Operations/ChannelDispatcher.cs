using System.Collections.Concurrent;
using HdcSharp.Protocol;
using HdcSharp.Transport;

namespace HdcSharp.Operations;

/// <summary>
/// 设备级通道分发器：对 <see cref="HdcConnection.FrameReceived"/> 只订阅一次，按 channelId 投递到对应的
/// <see cref="ChannelContext"/>，供并发 shell/文件/转发等操作各自消费，避免逐操作订阅与退订竞态。
/// 生命周期与连接一致，不单独释放。
/// </summary>
internal sealed class ChannelDispatcher
{
    private readonly ConcurrentDictionary<uint, ChannelContext> _contexts = new();

    /// <summary>创建分发器并订阅连接的帧到达与终结事件。</summary>
    /// <param name="connection">所属连接。</param>
    internal ChannelDispatcher(HdcConnection connection)
    {
        connection.FrameReceived += OnFrameReceived;
        connection.Terminated += OnTerminated;
    }

    /// <summary>注册通道并返回其帧邮箱。</summary>
    /// <param name="channelId">通道号。</param>
    /// <returns>该通道的上下文。</returns>
    /// <exception cref="InvalidOperationException">通道号已注册。</exception>
    internal ChannelContext Open(uint channelId)
    {
        var context = new ChannelContext(channelId);
        if (!_contexts.TryAdd(channelId, context))
        {
            throw new InvalidOperationException($"通道 {channelId} 已注册");
        }

        return context;
    }

    /// <summary>查询通道上下文。</summary>
    /// <param name="channelId">通道号。</param>
    /// <returns>已注册的上下文；未注册时为 null。</returns>
    internal ChannelContext? Get(uint channelId)
    {
        return _contexts.TryGetValue(channelId, out ChannelContext? context) ? context : null;
    }

    /// <summary>注销通道（操作结束）；已入队的帧仍可由持有者读完。</summary>
    /// <param name="channelId">通道号。</param>
    internal void Close(uint channelId)
    {
        _contexts.TryRemove(channelId, out _);
    }

    private void OnFrameReceived(Frame frame)
    {
        if (_contexts.TryGetValue(frame.ChannelId, out ChannelContext? context))
        {
            context.Accept(frame);
        }
    }

    private void OnTerminated()
    {
        foreach (ChannelContext context in _contexts.Values)
        {
            context.OnTerminated();
        }
    }
}
