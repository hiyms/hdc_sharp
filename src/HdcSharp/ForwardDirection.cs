namespace HdcSharp;

/// <summary>
/// 端口转发方向（spec §4.8）。
/// </summary>
public enum ForwardDirection
{
    /// <summary>正向转发（fport）：宿主监听本地端口，设备侧连接远端节点。</summary>
    Forward,

    /// <summary>反向转发（rport）：设备监听远端端口，宿主连接本地节点。</summary>
    Reverse,
}
