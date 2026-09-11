using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;
using HdcSharp.Protocol;
using HdcSharp.Transport;

namespace HdcSharp.Operations;

/// <summary>
/// shell 类操作的共享实现（Task 14）：通道生命周期管理（注册/注销、取消时发 CHANNEL_CLOSE[0]）、
/// 一次性命令输出聚合、流式产出与交互式 shell 建立。
/// 输出帧为 KERNEL_ECHO_RAW（daemon 将 stdout/stderr 合并于此），完成以 CHANNEL_CLOSE 为准。
/// </summary>
internal static class ShellOperation
{
    /// <summary>执行一次性命令并返回聚合后的 UTF-8 文本；daemon 的 Fail 级 ECHO 记入异常。</summary>
    /// <param name="device">目标设备。</param>
    /// <param name="command">命令字（UnityExecute 或 UnityExecuteEx）。</param>
    /// <param name="payload">命令载荷。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>聚合输出文本；退出码不在线缆上（spec §4.11），需要时可用 <c>echo $?</c> 变通。</returns>
    internal static async Task<string> ExecuteAsync(HdcDevice device, HdcCommand command, byte[] payload, CancellationToken ct)
    {
        uint channelId = device.NewChannelId();
        device.Connection.RegisterChannel(channelId);
        ChannelContext context = device.Dispatcher.Open(channelId);
        var output = new ArrayBufferWriter<byte>();
        List<string> errors = [];
        bool finished = false;
        try
        {
            await device.Connection.SendAsync(channelId, command, payload, ct).ConfigureAwait(false);
            await foreach (Frame frame in context.Frames.ReadAllAsync(ct).ConfigureAwait(false))
            {
                switch (frame.Command)
                {
                    case HdcCommand.KernelEchoRaw:
                        // 按字节聚合、结束时整体解码，避免多字节 UTF-8 字符被分块截断
                        output.Write(frame.Payload);
                        break;
                    case HdcCommand.KernelEcho when frame.Payload.Length > 0 && frame.Payload[0] == (byte)MessageLevel.Fail:
                        errors.Add(Encoding.UTF8.GetString(frame.Payload.AsSpan(1)));
                        break;
                    default:
                        break;
                }
            }

            finished = true;
        }
        finally
        {
            await FinalizeAsync(device, channelId, context, finished).ConfigureAwait(false);
        }

        ThrowIfTerminated(device, context);
        if (errors.Count > 0)
        {
            throw new HdcException(string.Join(Environment.NewLine, errors));
        }

        return Encoding.UTF8.GetString(output.WrittenSpan);
    }

    /// <summary>流式执行一次性命令：逐块产出 ECHO_RAW 原始字节。</summary>
    /// <param name="device">目标设备。</param>
    /// <param name="command">命令字（UnityExecute 或 UnityExecuteEx）。</param>
    /// <param name="payload">命令载荷。</param>
    /// <param name="ct">取消令牌；取消或消费方提前退出时发 CHANNEL_CLOSE[0] 清理通道。</param>
    /// <returns>daemon 输出分块序列。</returns>
    internal static async IAsyncEnumerable<byte[]> StreamAsync(
        HdcDevice device, HdcCommand command, byte[] payload, [EnumeratorCancellation] CancellationToken ct)
    {
        uint channelId = device.NewChannelId();
        device.Connection.RegisterChannel(channelId);
        ChannelContext context = device.Dispatcher.Open(channelId);
        bool finished = false;
        try
        {
            await device.Connection.SendAsync(channelId, command, payload, ct).ConfigureAwait(false);
            await foreach (Frame frame in context.Frames.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (frame.Command == HdcCommand.KernelEchoRaw)
                {
                    yield return frame.Payload;
                }
            }

            finished = true;
        }
        finally
        {
            await FinalizeAsync(device, channelId, context, finished).ConfigureAwait(false);
        }

        ThrowIfTerminated(device, context);
    }

    /// <summary>建立交互式 shell；SHELL_INIT 载荷按世代对齐官方 host（C++ 空、Rust [0]）。</summary>
    /// <param name="device">目标设备。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>交互式 shell 会话。</returns>
    internal static async Task<IInteractiveShell> OpenInteractiveAsync(HdcDevice device, CancellationToken ct)
    {
        uint channelId = device.NewChannelId();
        device.Connection.RegisterChannel(channelId);
        ChannelContext context = device.Dispatcher.Open(channelId);
        try
        {
            byte[] payload = device.Generation == DaemonGeneration.Cpp ? [] : [0];
            await device.Connection.SendAsync(channelId, HdcCommand.ShellInit, payload, ct).ConfigureAwait(false);
            return new InteractiveShell(device, channelId, context);
        }
        catch
        {
            device.Connection.UnregisterChannel(channelId);
            device.Dispatcher.Close(channelId);
            context.Complete();
            throw;
        }
    }

    /// <summary>尽力发送 CHANNEL_CLOSE[0] 终结 daemon 侧任务；连接已断开时静默忽略。</summary>
    /// <param name="connection">连接。</param>
    /// <param name="channelId">通道号。</param>
    internal static async Task TryCloseAsync(HdcConnection connection, uint channelId)
    {
        try
        {
            await connection.CloseChannelAsync(channelId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HdcException or ObjectDisposedException)
        {
            // 清理是尽力而为，不得掩盖调用方原始取消或异常
        }
    }

    private static async Task FinalizeAsync(HdcDevice device, uint channelId, ChannelContext context, bool finished)
    {
        // finished 为 false 表示消费方取消或提前退出，需主动终结 daemon 侧任务（spec §6）
        if (!finished && !context.ClosedByPeer)
        {
            await TryCloseAsync(device.Connection, channelId).ConfigureAwait(false);
        }

        device.Connection.UnregisterChannel(channelId);
        device.Dispatcher.Close(channelId);
    }

    private static void ThrowIfTerminated(HdcDevice device, ChannelContext context)
    {
        if (context.IsTerminated || device.Connection.IsClosed)
        {
            throw new HdcException("连接已断开");
        }
    }
}
