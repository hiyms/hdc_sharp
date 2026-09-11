namespace HdcSharp.Protocol;

/// <summary>
/// HDC 线缆协议命令字（spec §4.5），对应 PayloadProtect 的 commandFlag 字段。
/// </summary>
public enum HdcCommand : uint
{
    /// <summary>握手/认证全部消息（双向）。</summary>
    KernelHandshake = 1,

    /// <summary>通道关闭（双向），载荷为 1 字节递减跳数计数。</summary>
    KernelChannelClose = 2,

    /// <summary>daemon→宿主状态回显，载荷为 [level u8][text]，0=Fail、1=Info、2=Ok。</summary>
    KernelEcho = 9,

    /// <summary>daemon→宿主原始输出字节流（shell/hilog 等）。</summary>
    KernelEchoRaw = 10,

    /// <summary>daemon→宿主文件/应用任务预备信号，空载荷，收到即忽略。</summary>
    KernelWakeupSlavetask = 12,

    /// <summary>宿主→daemon 一次性 shell，载荷为命令字符串。</summary>
    UnityExecute = 1001,

    /// <summary>宿主→daemon 带选项的一次性 shell（Tlv32 载荷同时携带命令与沙箱包名），仅 C++ 世代 daemon 支持。</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "对齐上游命令字 CMD_UNITY_EXECUTE_EX")]
    UnityExecuteEx = 1200,

    /// <summary>宿主→daemon 系统分区重挂载请求。</summary>
    UnityRemount = 1002,

    /// <summary>宿主→daemon 设备重启请求，载荷为模式串（去除 '-'）。</summary>
    UnityReboot = 1003,

    /// <summary>宿主→daemon 运行模式切换，载荷为 "port n"、"port close" 或 "usb"。</summary>
    UnityRunmode = 1004,

    /// <summary>宿主→daemon hilog 日志流控制，载荷为 "" 或 "h"。</summary>
    UnityHilog = 1005,

    /// <summary>宿主→daemon root 权限执行切换，载荷为 "" 或 "r"。</summary>
    UnityRootrun = 1007,

    /// <summary>宿主→daemon bugreport 采集初始化。</summary>
    UnityBugreportInit = 1011,

    /// <summary>daemon→宿主 bugreport 数据流。</summary>
    UnityBugreportData = 1012,

    /// <summary>宿主→daemon 交互 shell（PTY）初始化。</summary>
    ShellInit = 2000,

    /// <summary>宿主→daemon 交互 shell 数据传输。</summary>
    ShellData = 2001,

    /// <summary>端口转发初始化，载荷为转发命令串。</summary>
    ForwardInit = 2500,

    /// <summary>端口转发配置校验。</summary>
    ForwardCheck = 2501,

    /// <summary>端口转发校验结果应答。</summary>
    ForwardCheckResult = 2502,

    /// <summary>转发从连接激活（每用户连接一条，载荷含 channelId 与远端节点）。</summary>
    ForwardActiveSlave = 2503,

    /// <summary>转发主连接激活应答。</summary>
    ForwardActiveMaster = 2504,

    /// <summary>转发数据传输，载荷含 channelId 与字节流。</summary>
    ForwardData = 2505,

    /// <summary>释放转发上下文，载荷含 channelId。</summary>
    ForwardFreeContext = 2506,

    /// <summary>查询当前转发规则列表。</summary>
    ForwardList = 2507,

    /// <summary>移除指定转发规则。</summary>
    ForwardRemove = 2508,

    /// <summary>转发操作成功通知。</summary>
    ForwardSuccess = 2509,

    /// <summary>文件传输初始化，载荷为 ASCII 命令串（send/recv 及选项）。</summary>
    FileInit = 3000,

    /// <summary>文件传输配置校验，载荷为 TransferConfig。</summary>
    FileCheck = 3001,

    /// <summary>文件传输开始，载荷为空或 8 字节文件大小。</summary>
    FileBegin = 3002,

    /// <summary>文件数据块（64 字节槽位布局）。</summary>
    FileData = 3003,

    /// <summary>文件传输结束，载荷区分单文件结束与整体结束。</summary>
    FileFinish = 3004,

    /// <summary>设置文件权限（载荷为 FileMode）。</summary>
    FileMode = 3006,

    /// <summary>设置目录权限（载荷为 FileMode）。</summary>
    DirMode = 3007,

    /// <summary>应用安装初始化，载荷为选项与包路径命令串。</summary>
    AppInit = 3500,

    /// <summary>应用安装配置校验，载荷为 TransferConfig。</summary>
    AppCheck = 3501,

    /// <summary>应用安装开始。</summary>
    AppBegin = 3502,

    /// <summary>应用包数据块。</summary>
    AppData = 3503,

    /// <summary>应用安装结束，载荷含安装结果与 bm 工具输出。</summary>
    AppFinish = 3504,

    /// <summary>应用卸载请求。</summary>
    AppUninstall = 3505,

    /// <summary>心跳消息，仅 C++ 世代且双方协商成功后使用。</summary>
    HeartbeatMsg = 5000,
}
