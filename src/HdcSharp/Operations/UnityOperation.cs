using System.Runtime.CompilerServices;
using System.Text;
using HdcSharp.Protocol;

namespace HdcSharp.Operations;

/// <summary>
/// Unity 命令的共享实现（Task 18，spec §4.10）：
/// 单帧命令（REMOUNT 1002 / REBOOT 1003 / RUNMODE 1004 / ROOTRUN 1007）、
/// hilog 流（HILOG 1005 → ECHO_RAW 10）、bugreport 流（BUGREPORT_INIT 1011 → BUGREPORT_DATA 1012）。
/// 完成信号统一为 CHANNEL_CLOSE（daemon 命令处理结束即 TaskFinish → CLOSE[1]，src/common/task.cpp:49-59）；
/// 单帧命令与两条流上的 Fail 级 ECHO 记入异常。
/// </summary>
internal static class UnityOperation
{
    /// <summary>发送单帧 Unity 命令并等到通道关闭；daemon 的 Fail 级回显转 <see cref="HdcException"/>。</summary>
    /// <param name="device">目标设备。</param>
    /// <param name="command">命令字。</param>
    /// <param name="payload">命令载荷。</param>
    /// <param name="ct">取消令牌；取消时发送 CHANNEL_CLOSE[0] 并清理通道。</param>
    internal static async Task SendSingleFrameAsync(HdcDevice device, HdcCommand command, byte[] payload, CancellationToken ct)
    {
        await foreach (Frame _ in StreamFramesAsync(device, command, payload, ct).ConfigureAwait(false))
        {
            // 单帧命令没有流式输出：ECHO Ok/Info 是回执（如 "Mount finish"），Fail 已由 StreamFramesAsync 收集后抛错
        }
    }

    /// <summary>hilog 流：ECHO_RAW 原始字节按 <c>\n</c> 切分为整行产出（解码前不做 UTF-8 截断）。</summary>
    /// <param name="device">目标设备。</param>
    /// <param name="ct">取消令牌；取消或消费方提前退出时发送 CHANNEL_CLOSE[0] 并清理通道。</param>
    /// <returns>行序列；行尾 <c>\n</c> 与 <c>\r</c> 不包含在结果中，末尾无换行的残留内容作为最后一行产出。</returns>
    internal static async IAsyncEnumerable<string> StreamHilogAsync(
        HdcDevice device, [EnumeratorCancellation] CancellationToken ct)
    {
        List<byte> pending = [];
        await foreach (Frame frame in StreamFramesAsync(device, HdcCommand.UnityHilog, [], ct).ConfigureAwait(false))
        {
            if (frame.Command != HdcCommand.KernelEchoRaw)
            {
                continue;
            }

            pending.AddRange(frame.Payload);
            int start = 0;
            for (int i = 0; i < pending.Count; i++)
            {
                if (pending[i] != (byte)'\n')
                {
                    continue;
                }

                yield return DecodeLine(pending, start, i - start);
                start = i + 1;
            }

            if (start > 0)
            {
                pending.RemoveRange(0, start);
            }
        }

        if (pending.Count > 0)
        {
            yield return DecodeLine(pending, 0, pending.Count);
        }
    }

    /// <summary>bugreport 流：daemon 对 hidumper 输出按 BUGREPORT_DATA(1012) 分块下发，原样产出。</summary>
    /// <param name="device">目标设备。</param>
    /// <param name="ct">取消令牌；取消或消费方提前退出时发送 CHANNEL_CLOSE[0] 并清理通道。</param>
    /// <returns>数据分块序列（空分块不产出）。</returns>
    internal static async IAsyncEnumerable<byte[]> StreamBugReportAsync(
        HdcDevice device, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (Frame frame in StreamFramesAsync(device, HdcCommand.UnityBugreportInit, [], ct).ConfigureAwait(false))
        {
            if (frame.Command == HdcCommand.UnityBugreportData && frame.Payload.Length > 0)
            {
                yield return frame.Payload;
            }
        }
    }

    /// <summary>把重启模式映射为线上载荷（上游 host 归一化后不带前导 <c>-</c>）。</summary>
    /// <param name="mode">重启模式。</param>
    /// <returns>载荷字节。</returns>
    /// <exception cref="ArgumentOutOfRangeException">枚举值非法。</exception>
    internal static byte[] RebootPayload(RebootMode mode)
    {
        string text = mode switch
        {
            RebootMode.Default => "",
            RebootMode.Bootloader => "bootloader",
            RebootMode.Recovery => "recovery",
            RebootMode.Flashd => "flashd",
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "未知的重启模式"),
        };
        return Encoding.UTF8.GetBytes(text);
    }

    private static string DecodeLine(List<byte> buffer, int start, int length)
    {
        if (length > 0 && buffer[start + length - 1] == (byte)'\r')
        {
            length--;
        }

        return Encoding.UTF8.GetString(buffer.GetRange(start, length).ToArray());
    }

    /// <summary>
    /// 通道生命周期与入站帧产出的公共实现：注册通道 → 发命令 → 逐帧产出（Fail 级 ECHO 记账）→
    /// 通道关闭后注销；未读完（取消或消费方提前退出）时补发 CHANNEL_CLOSE[0] 终结 daemon 侧任务。
    /// </summary>
    private static async IAsyncEnumerable<Frame> StreamFramesAsync(
        HdcDevice device, HdcCommand command, byte[] payload, [EnumeratorCancellation] CancellationToken ct)
    {
        uint channelId = device.NewChannelId();
        device.Connection.RegisterChannel(channelId);
        ChannelContext context = device.Dispatcher.Open(channelId);
        List<string> errors = [];
        bool finished = false;
        try
        {
            await device.Connection.SendAsync(channelId, command, payload, ct).ConfigureAwait(false);
            await foreach (Frame frame in context.Frames.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (frame.Command == HdcCommand.KernelEcho && IsFailLevel(frame.Payload))
                {
                    errors.Add(DecodeEchoText(frame.Payload));
                }

                yield return frame;
            }

            finished = true;
        }
        finally
        {
            if (!finished && !context.ClosedByPeer)
            {
                await ShellOperation.TryCloseAsync(device.Connection, channelId).ConfigureAwait(false);
            }

            device.Connection.UnregisterChannel(channelId);
            device.Dispatcher.Close(channelId);
        }

        ThrowIfErrors(errors);
        if (context.IsTerminated || device.Connection.IsClosed)
        {
            throw new HdcException("连接已断开");
        }
    }

    private static void ThrowIfErrors(List<string> errors)
    {
        if (errors.Count > 0)
        {
            throw new HdcException(string.Join(Environment.NewLine, errors));
        }
    }

    private static bool IsFailLevel(byte[] payload)
    {
        return payload.Length > 0 && payload[0] == (byte)MessageLevel.Fail;
    }

    private static string DecodeEchoText(byte[] payload)
    {
        return Encoding.UTF8.GetString(payload.AsSpan(1));
    }
}
