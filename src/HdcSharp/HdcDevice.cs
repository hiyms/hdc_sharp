using HdcSharp.Transport;

namespace HdcSharp;

/// <summary>
/// 一台已注册的 daemon 设备：持有本设备的 TCP 会话（<see cref="HdcConnection"/>）与握手所得的身份信息。
/// 由 <see cref="HdcHost.ConnectAsync"/> 创建，生命周期与连接一致。
/// </summary>
public sealed class HdcDevice
{
    private readonly HashSet<uint> _channelIds = [];
    private readonly object _channelIdGate = new();
    private int _state = (int)HdcDeviceState.Connecting;
    private string _deviceName = "";
    private DaemonGeneration _generation = DaemonGeneration.Unknown;

    internal HdcDevice(string connectKey, HdcConnection connection)
    {
        ConnectKey = connectKey;
        Endpoint = connectKey;
        Connection = connection;
        SessionId = connection.SessionId;
    }

    /// <summary>连接键（ip:port），即 <see cref="HdcHost.ConnectAsync"/> 的 endpoint，用于查找与断开。</summary>
    public string ConnectKey { get; }

    /// <summary>目标端点（ip:port），与 <see cref="ConnectKey"/> 相同，语义别名。</summary>
    public string Endpoint { get; }

    /// <summary>设备名（握手 AUTH_OK 的 devname）；认证完成前为空串。</summary>
    public string DeviceName => _deviceName;

    /// <summary>当前状态快照。</summary>
    public HdcDeviceState State => (HdcDeviceState)Volatile.Read(ref _state);

    /// <summary>daemon 世代指纹，认证完成前为 Unknown。</summary>
    public DaemonGeneration Generation => _generation;

    /// <summary>本连接的会话号，daemon 已在 AUTH_OK 中采纳。</summary>
    public uint SessionId { get; }

    /// <summary>状态变化时触发；状态未实际变化时不触发。事件在状态变更线程触发，处理器应尽快返回。</summary>
    public event EventHandler<DeviceStateChangedEventArgs>? StateChanged;

    internal HdcConnection Connection { get; }

    internal DaemonCapabilities Capabilities { get; private set; } = new();

    /// <summary>认证成功后写入能力快照与身份信息（DeviceName/Generation）。</summary>
    internal void SetCapabilities(DaemonCapabilities capabilities)
    {
        Capabilities = capabilities;
        _deviceName = capabilities.DeviceName;
        _generation = capabilities.Generation;
    }

    /// <summary>分配会话内唯一的非零随机通道号。</summary>
    internal uint NewChannelId()
    {
        lock (_channelIdGate)
        {
            uint id;
            do
            {
                id = ((uint)Random.Shared.Next(ushort.MaxValue + 1) << 16) | (uint)Random.Shared.Next(ushort.MaxValue + 1);
            }
            while (id == 0 || !_channelIds.Add(id));
            return id;
        }
    }

    /// <summary>更新状态并在变化时触发 <see cref="StateChanged"/>；同状态重复设置无效果。</summary>
    internal void SetState(HdcDeviceState newState)
    {
        int previous = Interlocked.Exchange(ref _state, (int)newState);
        if (previous == (int)newState)
        {
            return;
        }

        EventHandler<DeviceStateChangedEventArgs>? handler = StateChanged;
        if (handler is null)
        {
            return;
        }

        try
        {
            handler(this, new DeviceStateChangedEventArgs(ConnectKey, (HdcDeviceState)previous, newState));
        }
        catch (Exception)
        {
            // 用户事件处理器异常不得打断连接状态机
        }
    }
}
