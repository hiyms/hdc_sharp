namespace HdcSharp;

/// <summary>
/// 设备运行模式（UNITY_RUNMODE 1004 的载荷取值，spec §4.10；对应上游 CLI 的 <c>tmode</c>）。
/// 载荷由上游 host 按 <c>tmode</c> 后面的原文拼出（src/host/translate.cpp:431-460），
/// C++ daemon 按 <c>usb</c> / <c>port</c> / <c>port &lt;n&gt;</c> / <c>port close</c> 分派
/// （src/daemon/daemon_unity.cpp:322-375），Rust daemon 只识别 <c>usb</c> 与 <c>port &lt;n&gt;</c> 前缀
/// （hdc_rust/src/daemon_lib/daemon_unity.rs:169-206，无法识别时回 ECHO Fail "Unknown command"）。
/// 本类型以值相等比较，等价于载荷相等。
/// </summary>
public readonly record struct RunMode
{
    private RunMode(string payload) => Payload = payload;

    /// <summary>发往 daemon 的载荷原文。</summary>
    internal string Payload { get; }

    /// <summary>USB 调试模式（载荷 <c>usb</c>）：C++ daemon 回 ECHO Fail 提示改在设备设置界面开启，Rust daemon 直接写系统参数。</summary>
    public static RunMode Usb { get; } = new("usb");

    /// <summary>以默认端口开启 TCP 监听（载荷 <c>port</c>）：仅 C++ daemon 支持，Rust daemon 会回 ECHO Fail "Unknown command"。</summary>
    public static RunMode Tcp { get; } = new("port");

    /// <summary>关闭 TCP 监听（载荷 <c>port close</c>）：对齐 C++ daemon 的 <c>persist.hdc.port=0</c> 分支。</summary>
    public static RunMode TcpClose { get; } = new("port close");

    /// <summary>以指定端口开启 TCP 监听（载荷 <c>port &lt;n&gt;</c>）。</summary>
    /// <param name="port">监听端口，取值范围 1-65535（上游 CLI 的合法性判定，translate.cpp:450-454）。</param>
    /// <returns>对应的运行模式。</returns>
    /// <exception cref="ArgumentOutOfRangeException">端口超出 1-65535。</exception>
    public static RunMode TcpPort(int port)
    {
        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), port, "端口须在 1-65535 之间");
        }

        return new RunMode($"port {port}");
    }

    /// <summary>返回载荷原文，便于日志与诊断。</summary>
    /// <returns>发往 daemon 的载荷原文。</returns>
    public override string ToString() => Payload;
}
