namespace HdcSharp;

/// <summary>
/// 设备状态变化事件参数，由 <see cref="HdcDevice.StateChanged"/> 与
/// <see cref="HdcHost.DeviceStateChanged"/> 共用。
/// </summary>
public sealed class DeviceStateChangedEventArgs : EventArgs
{
    /// <summary>创建事件参数。</summary>
    /// <param name="key">连接键（ip:port）。</param>
    /// <param name="oldState">变化前状态。</param>
    /// <param name="newState">变化后状态。</param>
    public DeviceStateChangedEventArgs(string key, HdcDeviceState oldState, HdcDeviceState newState)
    {
        Key = key;
        OldState = oldState;
        NewState = newState;
    }

    /// <summary>连接键（ip:port）。</summary>
    public string Key { get; }

    /// <summary>变化前状态。</summary>
    public HdcDeviceState OldState { get; }

    /// <summary>变化后状态。</summary>
    public HdcDeviceState NewState { get; }
}
