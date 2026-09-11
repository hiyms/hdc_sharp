namespace HdcSharp;

/// <summary>
/// 设备断开事件参数，由 <see cref="HdcHost.DeviceDisconnected"/> 触发。
/// </summary>
public sealed class DeviceDisconnectedEventArgs : EventArgs
{
    /// <summary>创建事件参数。</summary>
    /// <param name="key">连接键（ip:port）。</param>
    public DeviceDisconnectedEventArgs(string key)
    {
        Key = key;
    }

    /// <summary>连接键（ip:port）。</summary>
    public string Key { get; }
}
