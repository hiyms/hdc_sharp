using System.Net;
using System.Net.Sockets;
using HdcSharp.Protocol;

namespace HdcSharp.Operations;

/// <summary>
/// 端口转发操作的建立入口（Task 17，spec §4.8）。
/// fport 由宿主自己扮演主端（本地监听 + 下发 CHECK）；rport 把 FORWARD_INIT 交给设备侧主端，宿主扮演从端。
/// 上游 fport 的 FORWARD_INIT 只在本机 server 内部分发、从不上设备线（src/host/server_for_client.cpp:1086-1126），
/// 设备侧任务由 WAKEUP_SLAVETASK(12) 预建（src/common/task.cpp:30-33、hdc_rust/src/host/task.rs:108-152）。
/// </summary>
internal static class ForwardOperation
{
    /// <summary>建立正向转发（fport）：本机监听并等待设备侧校验。</summary>
    /// <param name="device">目标设备。</param>
    /// <param name="localPort">本机监听端口；0 表示自动分配。</param>
    /// <param name="remotePort">设备侧目标端口。</param>
    /// <param name="ct">取消令牌；取消与会话释放同义。</param>
    /// <returns>已建立的转发会话。</returns>
    internal static async Task<IForwardSession> ForwardTcpAsync(
        HdcDevice device, int localPort, int remotePort, CancellationToken ct)
    {
        ValidatePort(localPort, nameof(localPort), allowZero: true);
        ValidatePort(remotePort, nameof(remotePort), allowZero: false);
        ct.ThrowIfCancellationRequested();

        uint channelId = device.NewChannelId();
        device.Connection.RegisterChannel(channelId);
        ChannelContext context = device.Dispatcher.Open(channelId);
        TcpListener? listener = null;
        ForwardSession? session = null;
        try
        {
            listener = new TcpListener(IPAddress.Loopback, localPort);
            try
            {
                listener.Start();
            }
            catch (SocketException ex)
            {
                throw new HdcException($"本地端口 {localPort} 监听失败：{ex.Message}");
            }

            int boundPort = ((IPEndPoint)listener.LocalEndpoint).Port;
            session = new ForwardSession(
                device, ForwardDirection.Forward, boundPort, listener, $"tcp:{remotePort}", null, 0, channelId, context, ct);
            await session.EstablishAsync(ct).ConfigureAwait(false);
            return session;
        }
        catch
        {
            if (session is not null)
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                listener?.Stop();
                device.Connection.UnregisterChannel(channelId);
                device.Dispatcher.Close(channelId);
                context.Complete();
            }

            throw;
        }
    }

    /// <summary>建立反向转发（rport）：设备侧监听，等待设备侧确认后返回。</summary>
    /// <param name="device">目标设备。</param>
    /// <param name="remotePort">设备侧监听端口。</param>
    /// <param name="localPort">本机目标服务端口。</param>
    /// <param name="ct">取消令牌；取消与会话释放同义。</param>
    /// <returns>已建立的转发会话。</returns>
    internal static async Task<IForwardSession> ReverseTcpAsync(
        HdcDevice device, int remotePort, int localPort, CancellationToken ct)
    {
        ValidatePort(remotePort, nameof(remotePort), allowZero: false);
        ValidatePort(localPort, nameof(localPort), allowZero: false);
        ct.ThrowIfCancellationRequested();

        uint channelId = device.NewChannelId();
        device.Connection.RegisterChannel(channelId);
        ChannelContext context = device.Dispatcher.Open(channelId);
        ForwardSession? session = null;
        try
        {
            // 上游 host 把 rport 命令串剥掉 "rport " 前缀后原样发给 daemon（hdc_rust/src/host/task.rs:113-115）
            string init = $"tcp:{remotePort} tcp:{localPort}";
            session = new ForwardSession(
                device, ForwardDirection.Reverse, remotePort, null, $"tcp:{localPort}", init, localPort, channelId, context, ct);
            await session.EstablishAsync(ct).ConfigureAwait(false);
            return session;
        }
        catch
        {
            if (session is not null)
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                device.Connection.UnregisterChannel(channelId);
                device.Dispatcher.Close(channelId);
                context.Complete();
            }

            throw;
        }
    }

    private static void ValidatePort(int port, string name, bool allowZero)
    {
        int minimum = allowZero ? 0 : 1;
        if (port < minimum || port > 65535)
        {
            throw new ArgumentOutOfRangeException(
                name, port, allowZero ? "端口须在 0-65535 之间（0 表示自动分配）" : "端口须在 1-65535 之间");
        }
    }
}
