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

    /// <summary>
    /// 递归发送本地目录到设备（spec §4.7.3）：一次性枚举后逐文件沿单文件线缆流程推进，
    /// 整次传输共用一个通道、只发一次 WAKEUP_SLAVETASK；每个文件的 optionalName 为「源目录名/相对路径」
    /// （'/' 分隔，对齐上游 transfer.cpp:717），daemon 端自动逐级建目录。
    /// 空目录（或仅含空子目录）不产生任何线上帧并成功返回——协议只承载文件（上游 CLI 在同场景报错）。
    /// 符号链接（目录与文件）跳过，避免目录环与重复内容。
    /// </summary>
    /// <param name="localDir">本地源目录；不存在时抛出。</param>
    /// <param name="remoteDir">设备端目标路径：为已存在目录时在其下创建「源目录名」子树；不存在时该路径即重命名后的源目录（daemon 端语义）。</param>
    /// <param name="progress">进度回调，每发送一个数据块（≤48KiB）调用一次；<see cref="FileProgress.BytesTransferred"/> 跨文件累计，
    /// <see cref="FileProgress.FileName"/> 为相对源目录的路径（'/' 分隔），<see cref="FileProgress.TotalBytes"/> 为整目录字节数。</param>
    /// <param name="ct">取消令牌；取消时发送 CHANNEL_CLOSE[0] 并清理通道（已传输的部分文件留在设备端）。</param>
    /// <returns>传输完成的任务；daemon 报错时以 <see cref="HdcException"/> 结束。</returns>
    /// <exception cref="ArgumentException">路径为空，或 <paramref name="localDir"/> 为目录根（无法形成相对路径）。</exception>
    /// <exception cref="DirectoryNotFoundException">本地源目录不存在。</exception>
    /// <exception cref="HdcException">daemon 拒绝、连接断开或协议字段非法。</exception>
    public Task SendDirectoryAsync(
        string localDir, string remoteDir, IProgress<FileProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(localDir);
        ArgumentException.ThrowIfNullOrEmpty(remoteDir);
        return FileOperation.SendDirectoryAsync(this, localDir, remoteDir, progress, ct);
    }

    /// <summary>
    /// 从设备接收目录（spec §4.7.3）：daemon 作为主端递归枚举并逐文件推送 FILE_CHECK，
    /// 本库按 daemon 给出的 optionalName 重建目录树。本地目标已存在时保留 daemon 的顶层目录名；
    /// 不存在时创建该路径并剥掉首层（对齐上游 daemon 落盘语义，transfer.cpp:768-812、857-873）。
    /// </summary>
    /// <param name="remoteDir">设备端源目录。</param>
    /// <param name="localDir">本地目标根；父目录不存在时逐级创建。</param>
    /// <param name="progress">进度回调，每收到一个数据块调用一次；<see cref="FileProgress.BytesTransferred"/> 跨文件累计，
    /// <see cref="FileProgress.FileName"/> 为 daemon 提供的相对路径，<see cref="FileProgress.TotalBytes"/> 为已发现文件大小累计（结束时等于总字节数）。</param>
    /// <param name="ct">取消令牌；取消时发送 CHANNEL_CLOSE[0] 并清理通道。</param>
    /// <returns>传输完成的任务；设备端目录不存在等 daemon 报错时以 <see cref="HdcException"/> 结束。</returns>
    /// <exception cref="ArgumentException">路径为空。</exception>
    /// <exception cref="HdcException">daemon 拒绝、连接断开、协议字段非法或本地目标已被同名文件占用。</exception>
    public Task ReceiveDirectoryAsync(
        string remoteDir, string localDir, IProgress<FileProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(remoteDir);
        ArgumentException.ThrowIfNullOrEmpty(localDir);
        return FileOperation.ReceiveDirectoryAsync(this, remoteDir, localDir, progress, ct);
    }

    /// <summary>
    /// 安装应用到设备（WAKEUP_SLAVETASK + APP_CHECK/BEGIN/DATA + 设备端 APP_FINISH，spec §4.7.4）：
    /// 本地为 <c>.hap/.hsp/.app</c> 文件时直接传输；为目录时先在本机打成 ustar tar（条目名为相对路径，
    /// 不写结尾全零块，与上游打包器一致）再作为单个文件传输，设备端解包后交给 <c>bm install</c>。
    /// 线上 optionalName 为 9 位随机名加原扩展名，避免非法应用名导致设备端 pm 无法安装。
    /// 包缺失或目录为空在任何 APP 命令发出前抛出，不影响会话。
    /// </summary>
    /// <param name="packagePath">本地包文件或待安装目录。</param>
    /// <param name="options">安装选项；null 使用默认（<see cref="InstallOptions.Replace"/> 为 true，即 <c>-r</c>）。</param>
    /// <param name="ct">取消令牌；取消时发送 CHANNEL_CLOSE[0] 并清理通道。</param>
    /// <returns>设备端 <c>bm install</c> 输出文本（APP_FINISH 载荷自偏移 2 起）。</returns>
    /// <exception cref="ArgumentException">路径为空，或目录没有任何可打包条目。</exception>
    /// <exception cref="FileNotFoundException">本地包文件不存在。</exception>
    /// <exception cref="HdcException">安装失败（携带设备端 bm 输出与首个 [Exxxxxx] 错误码）、连接断开或协议字段非法。</exception>
    public Task<string> InstallAsync(
        string packagePath, InstallOptions? options = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(packagePath);
        return AppOperation.InstallAsync(this, packagePath, options, ct);
    }

    /// <summary>
    /// 按包名卸载设备端应用（单帧 APP_UNINSTALL，spec §4.7.4）：选项原样拼进载荷（<c>"&lt;opts&gt; &lt;package&gt;"</c>），
    /// 设备端补 <c>-n</c> 后执行 <c>bm uninstall</c>。上游 host 对卸载不发 WAKEUP/APP_CHECK，也不传包数据。
    /// </summary>
    /// <param name="packageName">应用包名（bundle name）。</param>
    /// <param name="options">卸载选项；null 表示无选项。</param>
    /// <param name="ct">取消令牌；取消时发送 CHANNEL_CLOSE[0] 并清理通道。</param>
    /// <returns>设备端 <c>bm uninstall</c> 输出文本（APP_FINISH 载荷自偏移 2 起）。</returns>
    /// <exception cref="ArgumentException">包名为空。</exception>
    /// <exception cref="HdcException">卸载失败（携带设备端 bm 输出与首个 [Exxxxxx] 错误码）或连接断开。</exception>
    public Task<string> UninstallAsync(
        string packageName, UninstallOptions? options = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(packageName);
        return AppOperation.UninstallAsync(this, packageName, options, ct);
    }

    /// <summary>
    /// 建立正向 TCP 端口转发（fport，spec §4.8）：本机监听 <paramref name="localPort"/>，
    /// 设备侧把每条入站连接转接到其本机的 <paramref name="remotePort"/>（一期仅支持 <c>tcp:</c> 节点）。
    /// 返回时会等待设备侧校验通过；取消令牌与会话同生命周期，取消即释放。
    /// </summary>
    /// <param name="localPort">本机监听端口；0 表示由系统自动分配（实际端口见 <see cref="IForwardSession.ListenPort"/>）。</param>
    /// <param name="remotePort">设备侧目标端口，须为 1-65535。</param>
    /// <param name="ct">取消令牌；取消会终结本次会话并清理通道。</param>
    /// <returns>已建立的转发会话；须释放以停止监听并向设备端注销规则。</returns>
    /// <exception cref="ArgumentOutOfRangeException">端口超出取值范围。</exception>
    /// <exception cref="HdcException">本地端口无法监听、设备侧拒绝校验或连接断开。</exception>
    public Task<IForwardSession> ForwardTcpAsync(int localPort, int remotePort, CancellationToken ct = default)
    {
        return ForwardOperation.ForwardTcpAsync(this, localPort, remotePort, ct);
    }

    /// <summary>
    /// 建立反向 TCP 端口转发（rport，spec §4.8）：设备侧监听 <paramref name="remotePort"/>，
    /// 每条入站连接由本机连接 <c>127.0.0.1:&lt;localPort&gt;</c>。
    /// 返回时会等待设备侧确认监听成功；取消令牌与会话同生命周期，取消即释放。
    /// </summary>
    /// <param name="remotePort">设备侧监听端口，须为 1-65535。</param>
    /// <param name="localPort">本机目标服务端口，须为 1-65535。</param>
    /// <param name="ct">取消令牌；取消会终结本次会话并清理通道。</param>
    /// <returns>已建立的转发会话；须释放以向设备端注销规则。</returns>
    /// <exception cref="ArgumentOutOfRangeException">端口超出取值范围。</exception>
    /// <exception cref="HdcException">设备侧拒绝（如端口被占用）或连接断开。</exception>
    public Task<IForwardSession> ReverseTcpAsync(int remotePort, int localPort, CancellationToken ct = default)
    {
        return ForwardOperation.ReverseTcpAsync(this, remotePort, localPort, ct);
    }

    /// <summary>
    /// 重启设备（UNITY_REBOOT 1003，spec §4.10）：C++ daemon 把载荷拼成 <c>reboot,&lt;mode&gt;</c> 交给系统电源服务且
    /// 成功不回显（src/daemon/daemon_unity.cpp:452-455、system_depend.cpp:78-87），Rust daemon 写
    /// <c>ohos.startup.powerctrl</c> 并回 ECHO Ok。两世代均以通道关闭表示命令已被 daemon 受理，故本方法在通道关闭后返回；
    /// 设备真正重启会随后断开连接，后续操作以 <see cref="HdcException"/> 收尾。
    /// </summary>
    /// <param name="mode">重启目标模式；默认普通重启（空载荷）。</param>
    /// <param name="ct">取消令牌；取消时发送 CHANNEL_CLOSE[0] 并清理通道。</param>
    /// <returns>命令受理的任务；daemon 回显 Fail 级错误时以 <see cref="HdcException"/> 结束。</returns>
    /// <exception cref="ArgumentOutOfRangeException">重启模式不是已知枚举值（发送前即抛出，不影响会话）。</exception>
    /// <exception cref="HdcException">daemon 拒绝或连接断开。</exception>
    public Task RebootAsync(RebootMode mode = RebootMode.Default, CancellationToken ct = default)
    {
        return UnityOperation.SendSingleFrameAsync(this, HdcCommand.UnityReboot, UnityOperation.RebootPayload(mode), ct);
    }

    /// <summary>
    /// 以可读写方式重新挂载设备分区（UNITY_REMOUNT 1002，空载荷，spec §4.10）。
    /// daemon 要求 <c>const.debuggable=1</c> 且自身以 root 运行，否则回 ECHO Fail（如 <c>[E007100]</c>）并结束。
    /// C++ daemon 成功后回 ECHO Ok <c>Mount finish</c>（src/daemon/daemon_unity.cpp:282-313），两世代均以通道关闭收尾。
    /// </summary>
    /// <param name="ct">取消令牌；取消时发送 CHANNEL_CLOSE[0] 并清理通道。</param>
    /// <returns>命令受理的任务；daemon 回显 Fail 级错误时以 <see cref="HdcException"/> 结束。</returns>
    /// <exception cref="HdcException">daemon 拒绝（非调试版本/非 root）或连接断开。</exception>
    public Task RemountAsync(CancellationToken ct = default)
    {
        return UnityOperation.SendSingleFrameAsync(this, HdcCommand.UnityRemount, [], ct);
    }

    /// <summary>
    /// 切换 daemon 的启动权限（UNITY_ROOTRUN 1007，spec §4.10；上游 CLI 的 <c>smode</c>）：
    /// 载荷为空表示以 root 运行（C++ daemon 写 <c>persist.hdc.root=1</c>），载荷 <c>r</c> 表示取消 root
    /// （写 <c>0</c>），对应 CLI 的 <c>-r</c>（src/host/translate.cpp:583-587、daemon_unity.cpp:466-486）。
    /// daemon 会重启自身，通道随即关闭；非调试版本回 ECHO Fail <c>Cannot set root run mode in undebuggable version.</c>。
    /// </summary>
    /// <param name="unroot">true 表示取消 root 权限（载荷 <c>r</c>）；默认 false 表示以 root 运行。</param>
    /// <param name="ct">取消令牌；取消时发送 CHANNEL_CLOSE[0] 并清理通道。</param>
    /// <returns>命令受理的任务；daemon 回显 Fail 级错误时以 <see cref="HdcException"/> 结束。</returns>
    /// <exception cref="HdcException">daemon 拒绝（非调试版本）或连接断开。</exception>
    public Task RootRunAsync(bool unroot = false, CancellationToken ct = default)
    {
        byte[] payload = unroot ? [(byte)'r'] : [];
        return UnityOperation.SendSingleFrameAsync(this, HdcCommand.UnityRootrun, payload, ct);
    }

    /// <summary>
    /// 设置 daemon 运行模式（UNITY_RUNMODE 1004，spec §4.10；上游 CLI 的 <c>tmode</c>）。
    /// 载荷由 <see cref="RunMode"/> 决定（<c>usb</c> / <c>port</c> / <c>port &lt;n&gt;</c> / <c>port close</c>），
    /// daemon 写系统参数后可能重启自身并关闭通道；无法识别的载荷由 daemon 回 ECHO Fail（如 Rust 世代对 <c>port</c>）。
    /// </summary>
    /// <param name="mode">目标运行模式。</param>
    /// <param name="ct">取消令牌；取消时发送 CHANNEL_CLOSE[0] 并清理通道。</param>
    /// <returns>命令受理的任务；daemon 回显 Fail 级错误时以 <see cref="HdcException"/> 结束。</returns>
    /// <exception cref="HdcException">daemon 拒绝（如 USB 模式提示改在设备设置界面开启）或连接断开。</exception>
    public Task SetRunModeAsync(RunMode mode, CancellationToken ct = default)
    {
        return UnityOperation.SendSingleFrameAsync(this, HdcCommand.UnityRunmode, Encoding.UTF8.GetBytes(mode.Payload), ct);
    }

    /// <summary>
    /// 流式读取设备 hilog（UNITY_HILOG 1005，空载荷，spec §4.10）：daemon 执行 <c>hilog</c> 并把输出以
    /// ECHO_RAW(10) 下发（src/daemon/daemon_unity.cpp:29、377-385），本方法按 <c>\n</c> 切分为整行产出。
    /// hilog 是长命命令，通常不会自行结束：取消令牌、消费方提前退出或连接断开是正常终止路径；若 daemon 命令结束，则以通道关闭终止。
    /// </summary>
    /// <param name="ct">取消令牌；取消或消费方提前退出时发送 CHANNEL_CLOSE[0] 后清理通道。</param>
    /// <returns>行序列（不含行尾换行；末尾无换行的残留内容作为最后一行产出）。</returns>
    /// <exception cref="HdcException">daemon 回显 Fail 级错误或连接断开。</exception>
    public IAsyncEnumerable<string> StreamHilogAsync(CancellationToken ct = default)
    {
        return UnityOperation.StreamHilogAsync(this, ct);
    }

    /// <summary>
    /// 流式采集设备 bugreport（BUGREPORT_INIT 1011 空载荷发往 daemon，输出经 BUGREPORT_DATA 1012 分块回流，spec §4.10）：
    /// daemon 侧执行 <c>hidumper</c>（src/daemon/daemon_unity.cpp:487-490、hdc_rust/src/daemon_lib/task.rs:258-268）。
    /// 分块原样产出、不落盘也不解压，写入文件或转发由调用方决定；daemon 输出结束即关闭通道。
    /// </summary>
    /// <param name="ct">取消令牌；取消或消费方提前退出时发送 CHANNEL_CLOSE[0] 后清理通道。</param>
    /// <returns>数据分块序列（空分块不产出）。</returns>
    /// <exception cref="HdcException">daemon 回显 Fail 级错误或连接断开。</exception>
    public IAsyncEnumerable<byte[]> StreamBugReportAsync(CancellationToken ct = default)
    {
        return UnityOperation.StreamBugReportAsync(this, ct);
    }
}
