namespace HdcSharp;

/// <summary>
/// 一条已建立的端口转发会话（spec §5）。由 <see cref="HdcDevice.ForwardTcpAsync"/>（fport）或
/// <see cref="HdcDevice.ReverseTcpAsync"/>（rport）创建，生命周期即转发规则的存活期；
/// 释放会话会停止本地监听、关闭全部已转发连接并向设备端注销该规则。
/// </summary>
public interface IForwardSession : IAsyncDisposable
{
    /// <summary>
    /// fport：本机实际监听的端口（请求端口为 0 时由系统分配）。
    /// rport：设备侧监听的远端端口（本机不监听）。
    /// </summary>
    int ListenPort { get; }

    /// <summary>转发方向。</summary>
    ForwardDirection Direction { get; }

    /// <summary>会话是否仍然存活；释放、连接断开或对端终结通道后为 false。</summary>
    bool IsActive { get; }

    /// <summary>会话终结（释放、连接断开、取消或对端关闭通道）时触发一次。</summary>
    event EventHandler? Closed;
}
