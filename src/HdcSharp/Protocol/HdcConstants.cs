namespace HdcSharp.Protocol;

/// <summary>
/// HDC 线缆协议常量（spec §4），取值与官方 hdc 宿主/守护实现一致。
/// </summary>
public static class HdcConstants
{
    /// <summary>帧头魔数，PayloadHead 前两字节 'H''W'。</summary>
    public const string PacketFlag = "HW";

    /// <summary>帧头协议版本字节，恒为 0x01。</summary>
    public const byte ProtocolVer = 0x01;

    /// <summary>PayloadProtect 的 vCode 校验字节，恒为 0x09。</summary>
    public const byte PayloadVCode = 0x09;

    /// <summary>握手 banner 字符串。</summary>
    public const string HandshakeMessage = "OHOS HDC";

    /// <summary>握手失败时 daemon 回复的 banner 字符串。</summary>
    public const string HandshakeFailed = "HS FAILED";

    /// <summary>宿主版本串（C++ 世代指纹）。</summary>
    public const string HostVersion = "Ver: 3.2.0f";

    /// <summary>可接受的最低 daemon 版本串，纯字符串比较，低于它连接作废。</summary>
    public const string MinDaemonVersion = "Ver: 3.0.0b";

    /// <summary>公钥认证时 hostname 与 PEM 公钥之间的分隔字节（form feed）。</summary>
    public const byte HostDaemonBufSeparator = 0x0C;

    /// <summary>PayloadHead 固定长度 11 字节。</summary>
    public const int PayloadHeadSize = 11;

    /// <summary>文件传输 DATA 命令槽位大小 64 字节。</summary>
    public const int TransferSlotSize = 64;

    /// <summary>单个文件数据块最大 49152 字节（48KiB）。</summary>
    public const int MaxFileChunkSize = 49152;

    /// <summary>FILE_FINISH 载荷：单文件完成。</summary>
    internal const byte FileFinishOneFile = 1;

    /// <summary>FILE_FINISH 载荷：整单完成（收到即整次传输结束）。</summary>
    internal const byte FileFinishAll = 0;

    /// <summary>FILE_INIT 参数串分隔符（空格）。</summary>
    internal const string FileInitArgumentSeparator = " ";

    /// <summary>
    /// 宿主 CLI 的 file send 首词。仅存在于 client→本地 server 的命令串；上游 host 发往设备前剥离
    /// （server_for_client.cpp:1081-1094），故设备侧 FILE_INIT 载荷不含该首词。
    /// </summary>
    internal const string FileInitVerbSend = "send";

    /// <summary>宿主 CLI 的 file recv 首词；同样不进入发往设备的 FILE_INIT 载荷（hdc_rust/src/host/task.rs:100-103）。</summary>
    internal const string FileInitVerbRecv = "recv";

    /// <summary>心跳间隔秒数（仅 C++ 世代且双方声明 heartbeat 时启用）。</summary>
    public const int HeartbeatIntervalSeconds = 5;

    /// <summary>Tlv16 认证类型 tag。</summary>
    public const string TlvAuthType = "authtype";

    /// <summary>Tlv16 支持特性 tag。</summary>
    public const string TlvSupportFeatures = "supportfeatures";

    /// <summary>Tlv16 shell 扩展选项 tag（数值即命令号 1200，仅 C++ 世代 daemon 会附加，故兼作世代指纹）。</summary>
    public const string TlvShellOpt = "1200";

    /// <summary>Tlv16 设备名 tag。</summary>
    public const string TlvDevName = "devname";

    /// <summary>Tlv16 daemon 认证状态 tag。</summary>
    public const string TlvDaemonAuthStatus = "daemonauthstatus";

    /// <summary>Tlv16 紧急错误消息 tag。</summary>
    public const string TlvEmgMsg = "emgmsg";

    /// <summary>daemon 认证状态值：认证成功。</summary>
    public const string AuthStatusSuccess = "SUCCESS";

    /// <summary>daemon 认证状态值：设备端未授权（用户拒绝或弹窗超时）。</summary>
    public const string AuthStatusUnauth = "DAEMON_UNAUTH";
}
