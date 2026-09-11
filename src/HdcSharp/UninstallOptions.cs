namespace HdcSharp;

/// <summary>
/// 应用卸载选项，按固定顺序拼接为线上 options 串并原样透传给设备端 <c>bm uninstall</c> 命令行
/// （对齐上游 daemon_app.cpp:123-128 的 SplitCommand 与 daemon_app.rs:227-241 的 bm 拼装语义）。
/// </summary>
public sealed class UninstallOptions
{
    /// <summary>卸载时保留应用数据与缓存（<c>-k</c>）。</summary>
    public bool KeepData { get; set; }

    /// <summary>卸载共享包（<c>-s</c>）。</summary>
    public bool Shared { get; set; }
}
