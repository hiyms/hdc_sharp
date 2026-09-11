namespace HdcSharp;

/// <summary>
/// 设备重启目标（UNITY_REBOOT 1003 的载荷取值，spec §4.10）。
/// 载荷为去掉前导 <c>-</c> 的模式串：上游 host 把 <c>target boot -bootloader</c> 归一为 <c>bootloader</c>
/// （src/host/translate.cpp:462-471）。C++ daemon 拼成 <c>reboot,&lt;mode&gt;</c> 交给系统电源服务
/// （src/daemon/system_depend.cpp:78-87），Rust daemon 写 <c>ohos.startup.powerctrl</c>
/// （hdc_rust/src/daemon_lib/daemon_unity.rs:126-144）。
/// </summary>
public enum RebootMode
{
    /// <summary>普通重启（载荷为空）。</summary>
    Default,

    /// <summary>重启进入 bootloader（载荷 <c>bootloader</c>，对应 CLI 的 <c>target boot -bootloader</c>）。</summary>
    Bootloader,

    /// <summary>重启进入 recovery（载荷 <c>recovery</c>，对应 CLI 的 <c>target boot -recovery</c>）。</summary>
    Recovery,

    /// <summary>重启进入 flashd（载荷 <c>flashd</c>，对应 CLI 的 <c>target boot -flashd</c>）。</summary>
    Flashd,
}
