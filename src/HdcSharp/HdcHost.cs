using System.Net;
using System.Net.Sockets;
using HdcSharp.Protocol;
using HdcSharp.Security;
using HdcSharp.Transport;

namespace HdcSharp;

/// <summary>
/// HDC 宿主门面：按 endpoint 建立并管理到各 daemon 的 TCP 连接（每连接一个 <see cref="HdcDevice"/>），
/// 提供连接/断开、设备查找与状态事件。实例线程安全，可并发连接多个设备。
/// </summary>
public sealed class HdcHost : IAsyncDisposable
{
    private readonly Dictionary<string, HdcDevice> _devices = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly TimeSpan _heartbeatInterval;
    private readonly Action<HdcLogLevel, string>? _logger;
    private int _disposed;

    /// <summary>创建 HdcHost：心跳间隔 5 秒，不输出日志。</summary>
    public HdcHost()
        : this(TimeSpan.FromSeconds(HdcConstants.HeartbeatIntervalSeconds), null)
    {
    }

    /// <summary>测试用构造：覆盖心跳间隔并可注入日志回调。</summary>
    internal HdcHost(TimeSpan heartbeatInterval, Action<HdcLogLevel, string>? logger = null)
    {
        _heartbeatInterval = heartbeatInterval;
        _logger = logger;
    }

    /// <summary>任一设备状态变化时触发（含连接/认证过程中的中间状态与断开）。</summary>
    public event EventHandler<DeviceStateChangedEventArgs>? DeviceStateChanged;

    /// <summary>设备从注册表移除（主动断开或连接断开）时触发，订阅者可据此清理该设备上的操作。</summary>
    public event EventHandler<DeviceDisconnectedEventArgs>? DeviceDisconnected;

    /// <summary>
    /// 认证需要设备端用户确认（daemon 弹出授权对话框）时触发一次，参数为连接键（ip:port）。
    /// </summary>
    public event EventHandler<string>? AuthorizationRequested;

    /// <summary>当前已注册设备的快照（含正在连接/认证中的设备）；修改返回值不影响内部注册表。</summary>
    public IReadOnlyList<HdcDevice> ConnectedDevices
    {
        get
        {
            lock (_gate)
            {
                return _devices.Values.ToArray();
            }
        }
    }

    /// <summary>按连接键查找设备。</summary>
    /// <param name="connectKey">连接键（ip:port），即 ConnectAsync 的 endpoint。</param>
    /// <returns>找到的设备；不存在时为 null。</returns>
    public HdcDevice? FindDevice(string connectKey)
    {
        ArgumentNullException.ThrowIfNull(connectKey);
        lock (_gate)
        {
            return _devices.GetValueOrDefault(connectKey);
        }
    }

    /// <summary>
    /// 连接到 endpoint 指定的 daemon：建立 TCP 连接、完成握手认证，C++ 世代协商成功后启动心跳，
    /// 返回 State=Online 的设备。失败时设备不进入注册表。
    /// </summary>
    /// <param name="endpoint">目标端点，必须为 ip:port 形式。</param>
    /// <param name="options">连接选项；null 时使用默认值。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>认证成功后的设备。</returns>
    /// <exception cref="ArgumentException">endpoint 不是 ip:port 形式。</exception>
    /// <exception cref="HdcException">连接失败、目标已被连接，或握手/认证失败。</exception>
    /// <exception cref="ObjectDisposedException">HdcHost 已释放。</exception>
    public async Task<HdcDevice> ConnectAsync(string endpoint, ConnectOptions? options = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!endpoint.Contains(':', StringComparison.Ordinal) || !IPEndPoint.TryParse(endpoint, out IPEndPoint? remote))
        {
            throw new ArgumentException("endpoint 必须为 ip:port 形式", nameof(endpoint));
        }

        options ??= new ConnectOptions();
        if (FindDevice(endpoint) is not null)
        {
            throw new HdcException("Target is connected, repeat operation");
        }

        if (options.EnableEncryption)
        {
            Log(HdcLogLevel.Warn, "ConnectOptions.EnableEncryption 属二期 TLS-PSK 能力，当前按明文连接处理");
        }

        var client = new TcpClient();
        HdcConnection? connection = null;
        HdcDevice? device = null;
        try
        {
            using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                connectCts.CancelAfter(options.OperationTimeout);
                try
                {
                    await client.ConnectAsync(remote.Address, remote.Port, connectCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (ex is SocketException or OperationCanceledException or IOException)
                {
                    throw new HdcException($"连接 {endpoint} 失败：{ex.Message}", ex);
                }
            }

            connection = new HdcConnection(client, new HdcConnectionOptions
            {
                Logger = _logger,
                HeartbeatInterval = _heartbeatInterval,
            });
            HdcDevice createdDevice = new(endpoint, connection);
            device = createdDevice;
            RegisterDevice(createdDevice);
            connection.Terminated += () => RemoveDevice(createdDevice);
            _ = ObserveRunAsync(connection.RunAsync(_cts.Token));
            createdDevice.SetState(HdcDeviceState.Authorizing);

            IHostKeyStore keys = options.KeyStore ?? new FileHostKeyStore();
            bool ownsKeys = options.KeyStore is null;
            try
            {
                DaemonCapabilities caps = await AuthHandler.RunAsync(
                    connection,
                    endpoint,
                    keys,
                    options.AuthTimeout,
                    options.Heartbeat,
                    () => RaiseAuthorizationRequested(endpoint),
                    ct).ConfigureAwait(false);

                createdDevice.SetCapabilities(caps);
                if (caps.Generation == DaemonGeneration.Cpp && options.Heartbeat)
                {
                    connection.StartHeartbeat();
                }

                createdDevice.SetState(HdcDeviceState.Online);
                return createdDevice;
            }
            finally
            {
                if (ownsKeys && keys is IDisposable disposableKeys)
                {
                    disposableKeys.Dispose();
                }
            }
        }
        catch
        {
            if (device is not null)
            {
                RemoveDevice(device);
            }

            if (connection is not null)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                client.Dispose();
            }

            throw;
        }
    }

    /// <summary>断开指定设备：将其移出注册表并关闭连接；不存在时返回 false。</summary>
    /// <param name="connectKey">连接键（ip:port）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>是否找到并断开了设备。</returns>
    public async Task<bool> DisconnectAsync(string connectKey, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connectKey);
        ct.ThrowIfCancellationRequested();
        HdcDevice? device = FindDevice(connectKey);
        if (device is null)
        {
            return false;
        }

        RemoveDevice(device);
        await device.Connection.DisposeAsync().ConfigureAwait(false);
        return true;
    }

    /// <summary>释放宿主：取消内部令牌、断开并释放全部设备连接。幂等。</summary>
    /// <returns>释放完成的 ValueTask。</returns>
    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _cts.Cancel();
        HdcDevice[] devices;
        lock (_gate)
        {
            devices = _devices.Values.ToArray();
        }

        foreach (HdcDevice device in devices)
        {
            RemoveDevice(device);
        }

        foreach (HdcDevice device in devices)
        {
            await device.Connection.DisposeAsync().ConfigureAwait(false);
        }

        _cts.Dispose();
    }

    private void RegisterDevice(HdcDevice device)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_devices.ContainsKey(device.ConnectKey))
            {
                throw new HdcException("Target is connected, repeat operation");
            }

            _devices.Add(device.ConnectKey, device);
            device.StateChanged += OnDeviceStateChanged;
        }
    }

    private void RemoveDevice(HdcDevice device)
    {
        bool removed;
        lock (_gate)
        {
            removed = _devices.TryGetValue(device.ConnectKey, out HdcDevice? current) && ReferenceEquals(current, device);
            if (removed)
            {
                _devices.Remove(device.ConnectKey);
            }
        }

        if (!removed)
        {
            return;
        }

        device.SetState(HdcDeviceState.Offline);
        RaiseDeviceDisconnected(device.ConnectKey);
        device.StateChanged -= OnDeviceStateChanged;
    }

    private void OnDeviceStateChanged(object? sender, DeviceStateChangedEventArgs e)
    {
        RaiseDeviceStateChanged(e);
    }

    private void RaiseDeviceStateChanged(DeviceStateChangedEventArgs e)
    {
        EventHandler<DeviceStateChangedEventArgs>? handler = DeviceStateChanged;
        if (handler is null)
        {
            return;
        }

        try
        {
            handler(this, e);
        }
        catch (Exception ex)
        {
            Log(HdcLogLevel.Error, $"DeviceStateChanged 事件处理器异常：{ex.Message}");
        }
    }

    private void RaiseDeviceDisconnected(string connectKey)
    {
        EventHandler<DeviceDisconnectedEventArgs>? handler = DeviceDisconnected;
        if (handler is null)
        {
            return;
        }

        try
        {
            handler(this, new DeviceDisconnectedEventArgs(connectKey));
        }
        catch (Exception ex)
        {
            Log(HdcLogLevel.Error, $"DeviceDisconnected 事件处理器异常：{ex.Message}");
        }
    }

    private void RaiseAuthorizationRequested(string connectKey)
    {
        EventHandler<string>? handler = AuthorizationRequested;
        if (handler is null)
        {
            return;
        }

        try
        {
            handler(this, connectKey);
        }
        catch (Exception ex)
        {
            Log(HdcLogLevel.Error, $"AuthorizationRequested 事件处理器异常：{ex.Message}");
        }
    }

    private static async Task ObserveRunAsync(Task runTask)
    {
        try
        {
            await runTask.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HdcException or OperationCanceledException)
        {
            // 读循环结束异常由 Terminated/DisposeAsync 路径统一处理，此处仅避免未观察的任务异常
        }
    }

    private void Log(HdcLogLevel level, string message)
    {
        _logger?.Invoke(level, message);
    }
}
