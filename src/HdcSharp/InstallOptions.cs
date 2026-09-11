namespace HdcSharp;

/// <summary>
/// 应用安装选项，按固定顺序拼接为线上 options 串并原样透传给设备端 <c>bm install</c> 命令行
/// （对齐上游 host_app.cpp:60-79 的选项收集与 daemon_app.cpp:166-186 的 bm 拼装语义）。
/// </summary>
public sealed class InstallOptions
{
    /// <summary>覆盖安装已存在的同名应用（<c>-r</c>），默认启用；测试与 spec 均以 <c>-r</c> 为默认线上串。</summary>
    public bool Replace { get; set; } = true;

    /// <summary>允许降级安装（<c>-d</c>）。</summary>
    public bool Downgrade { get; set; }

    /// <summary>以共享包方式安装（<c>-s</c>），供多应用共用。</summary>
    public bool Shared { get; set; }

    /// <summary>安装时为应用授予权限（<c>-g</c>）。</summary>
    public bool GrantPermissions { get; set; }
}
