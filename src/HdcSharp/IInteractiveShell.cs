using System.Threading.Channels;
using HdcSharp.Protocol;

namespace HdcSharp;

/// <summary>
/// 交互式 shell（PTY）会话：写入 <see cref="Input"/> 的字节原样封装为 SHELL_DATA 帧发往 daemon，
/// daemon 的原始输出（含回显与 ANSI 转义）经 <see cref="Output"/> 逐块产出，
/// 通道/连接终结由 <see cref="WaitUntilClosedAsync"/> 观察（spec §5）。
/// </summary>
public interface IInteractiveShell : IAsyncDisposable
{
    /// <summary>
    /// 只写输入流：写入即发送 SHELL_DATA。0x03（SIGINT）与 0x04（退出）等控制字节由 daemon 侧解释，
    /// 库原样转发不做拦截。
    /// </summary>
    Stream Input { get; }

    /// <summary>PTY 原始输出（含回显与 ANSI 转义），daemon 每帧一个分块；通道关闭或连接断开时完结。</summary>
    ChannelReader<byte[]> Output { get; }

    /// <summary>
    /// 等待 daemon 关闭通道（PTY 结束）或连接断开；仅在连接断开时抛 <see cref="HdcException"/>。
    /// 本地释放后立即返回。
    /// </summary>
    /// <param name="ct">取消令牌。</param>
    Task WaitUntilClosedAsync(CancellationToken ct = default);
}
