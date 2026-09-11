using System.Text;
using HdcSharp.Operations;
using HdcSharp.Protocol;
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
    private readonly ChannelDispatcher _dispatcher;
    private int _state = (int)HdcDeviceState.Connecting;
    private string _deviceName = "";
    private DaemonGeneration _generation = DaemonGeneration.Unknown;

    internal HdcDevice(string connectKey, HdcConnection connection)
    {
        ConnectKey = connectKey;
        Endpoint = connectKey;
        Connection = connection;
        SessionId = connection.SessionId;
        _dispatcher = new ChannelDispatcher(connection);
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

    /// <summary>按 channelId 分发入站帧的设备级分发器（Task 14 起各操作共用）。</summary>
    internal ChannelDispatcher Dispatcher => _dispatcher;

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

    /// <summary>
    /// 执行一次性 shell 命令（UNITY_EXECUTE 1001），等待 daemon 关闭通道后返回聚合输出。
    /// stdout 与 stderr 合并为同一 UTF-8 文本流（daemon 均以 ECHO_RAW 下发）；退出码不上线（spec §4.11），
    /// 需要时可在命令中追加 <c>echo $?</c>。
    /// </summary>
    /// <param name="command">命令原文，原样发送给设备 shell。</param>
    /// <param name="ct">取消令牌；取消时发送 CHANNEL_CLOSE[0] 后清理通道。</param>
    /// <returns>聚合输出文本（按字节收集后整体 UTF-8 解码）。</returns>
    /// <exception cref="HdcException">daemon 回显 Fail 级错误、命令执行失败或连接断开。</exception>
    public Task<string> ExecuteShellAsync(string command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return ShellOperation.ExecuteAsync(this, HdcCommand.UnityExecute, Encoding.UTF8.GetBytes(command), ct);
    }

    /// <summary>
    /// 执行一次性 shell 命令并以 daemon 原始分块流式产出输出（不聚合、不做 UTF-8 解码）。
    /// 适合 hilog、大输出或需要边收边处理的场景。
    /// </summary>
    /// <param name="command">命令原文，原样发送给设备 shell。</param>
    /// <param name="ct">取消令牌；取消或消费方提前退出时发送 CHANNEL_CLOSE[0] 后清理通道。</param>
    /// <returns>daemon 输出分块序列。</returns>
    /// <exception cref="HdcException">连接断开。</exception>
    public IAsyncEnumerable<byte[]> StreamShellOutputAsync(string command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return ShellOperation.StreamAsync(this, HdcCommand.UnityExecute, Encoding.UTF8.GetBytes(command), ct);
    }

    /// <summary>
    /// 打开交互式 shell（PTY，SHELL_INIT 2000）：<see cref="IInteractiveShell.Input"/> 写入即发送 SHELL_DATA，
    /// <see cref="IInteractiveShell.Output"/> 读取 daemon 原始输出，控制字节 0x03/0x04 由 daemon 解释、库原样转发。
    /// </summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>交互式 shell 会话；须释放以终结通道。</returns>
    /// <exception cref="HdcException">发送 SHELL_INIT 失败或连接断开。</exception>
    public Task<IInteractiveShell> OpenInteractiveShellAsync(CancellationToken ct = default)
    {
        return ShellOperation.OpenInteractiveAsync(this, ct);
    }

    /// <summary>
    /// 以沙箱包名执行一次性 shell（UNITY_EXECUTE_EX 1200 + Tlv32，spec §4.9），仅 C++ 世代 daemon 支持。
    /// 载荷同时携带命令与包名两个标签（上游 daemon 缺包名会回绝为 [E003004]）。
    /// </summary>
    /// <param name="command">命令原文。</param>
    /// <param name="options">必须提供 <see cref="ShellOptions.BundleName"/>。</param>
    /// <param name="ct">取消令牌；取消时发送 CHANNEL_CLOSE[0] 后清理通道。</param>
    /// <returns>聚合输出文本。</returns>
    /// <exception cref="HdcException">daemon 世代不是 C++，或 daemon 回显 Fail 级错误、连接断开。</exception>
    /// <exception cref="ArgumentException">未提供沙箱包名。</exception>
    public Task<string> ExecuteUnityAsync(string command, ShellOptions? options = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (Generation != DaemonGeneration.Cpp)
        {
            throw new HdcException($"沙箱 shell（1200/Tlv32）仅 C++ 世代 daemon 支持，当前世代为 {Generation}");
        }

        string bundleName = options?.BundleName ?? "";
        if (bundleName.Length == 0)
        {
            throw new ArgumentException("沙箱 shell 必须提供应用包名（上游 daemon 同时要求命令与包名的 Tlv32 标签）", nameof(options));
        }

        Dictionary<uint, byte[]> entries = new()
        {
            [Tlv32.TagShellCmd] = Encoding.UTF8.GetBytes(command),
            [Tlv32.TagShellBundle] = Encoding.UTF8.GetBytes(bundleName),
        };
        return ShellOperation.ExecuteAsync(this, HdcCommand.UnityExecuteEx, Tlv32.Serialize(entries), ct);
    }

    /// <summary>
    /// 向设备发送单个文件（WAKEUP_SLAVETASK + FILE_CHECK/BEGIN/DATA/FINISH，spec §4.7.1）：
    /// 源文件缺失或不可读时在发出任何文件命令前抛出，不影响会话；
    /// <paramref name="remotePath"/> 可以为已存在的设备端目录，daemon 会拼接本地文件名。
    /// </summary>
    /// <param name="localPath">本地源文件路径。</param>
    /// <param name="remotePath">设备端目标路径。</param>
    /// <param name="progress">进度回调，每发送一个数据块（≤48KiB）调用一次。<see cref="Progress{T}"/> 会回到同步上下文，高频场景建议自备轻量实现。</param>
    /// <param name="ct">取消令牌；取消时发送 CHANNEL_CLOSE[0] 并清理通道。</param>
    /// <returns>传输完成的任务；daemon 报错时以 <see cref="HdcException"/> 结束。</returns>
    /// <exception cref="ArgumentException">路径为空。</exception>
    /// <exception cref="System.IO.FileNotFoundException">本地源文件不存在。</exception>
    /// <exception cref="HdcException">daemon 拒绝或连接断开。</exception>
    public Task SendFileAsync(
        string localPath, string remotePath, IProgress<FileProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(localPath);
        ArgumentException.ThrowIfNullOrEmpty(remotePath);
        return FileOperation.SendAsync(this, localPath, remotePath, progress, ct);
    }

    /// <summary>
    /// 从设备接收单个文件（FILE_INIT/CHECK/BEGIN/DATA/FINISH，spec §4.7.2）：daemon 作为主端读取设备文件并推送，
    /// 本库作为从端落盘；本地父目录不存在时逐级创建，同名文件被截断。
    /// </summary>
    /// <param name="remotePath">设备端源文件路径。</param>
    /// <param name="localPath">本地目标路径；传目录（或以目录分隔符结尾）时使用设备端文件名。</param>
    /// <param name="progress">进度回调，每收到一个数据块调用一次。</param>
    /// <param name="ct">取消令牌；取消时发送 CHANNEL_CLOSE[0] 并清理通道。</param>
    /// <returns>传输完成的任务；设备文件不存在等 daemon 报错时以 <see cref="HdcException"/> 结束。</returns>
    /// <exception cref="ArgumentException">路径为空。</exception>
    /// <exception cref="HdcException">daemon 拒绝、连接断开或协议字段非法。</exception>
    public Task ReceiveFileAsync(
        string remotePath, string localPath, IProgress<FileProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(remotePath);
        ArgumentException.ThrowIfNullOrEmpty(localPath);
        return FileOperation.ReceiveAsync(this, remotePath, localPath, progress, ct);
    }
}
