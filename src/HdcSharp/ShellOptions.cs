namespace HdcSharp;

/// <summary>
/// 带沙箱包名的 shell 选项，对应上游 <c>hdc shell -b &lt;bundlename&gt; &lt;command&gt;</c>（spec §4.9）。
/// 上游 daemon 处理 1200 时同时要求命令与包名两个 Tlv32 标签（daemon_unity.cpp 的 ExecuteShellExtend），
/// 缺少包名会回绝为 [E003004]。
/// </summary>
public sealed class ShellOptions
{
    /// <summary>应用包名，daemon 侧据此挂载沙箱路径。</summary>
    public string? BundleName { get; init; }
}
