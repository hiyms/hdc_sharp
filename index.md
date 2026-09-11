# HdcSharp

纯 C# 实现的 **OpenHarmony HDC 宿主侧/客户端协议库**（非 daemon、非 `hdc.exe` 替代品）。供 C# 程序经 WiFi/TCP 控制 OpenHarmony 设备：shell、文件与目录收发、应用安装卸载、端口转发、hilog/bugreport 等。

API 文档由源码中的中文 XML 文档注释生成；功能范围、快速开始与已知限制见 [README](README.md)。

## 功能一览

| 能力 | 入口 | 真机状态 |
|---|---|---|
| 连接与认证（RSA-3072 + PSS/PKCS1 自动选择，共享 `~/.harmony/hdckey`） | `HdcHost.ConnectAsync` | ✅ 已实测 |
| 一次性 / 流式 / 交互式 shell（PTY、TLV32 沙箱） | `HdcDevice.ExecuteShellAsync`、`StreamShellOutputAsync`、`OpenInteractiveShellAsync` | ✅ 已实测 |
| 文件与目录收发（64B 槽分片、进度回调） | `HdcDevice.SendFileAsync`、`ReceiveFileAsync`、`SendDirectoryAsync`、`ReceiveDirectoryAsync` | ✅ 已实测 |
| 应用安装 / 卸载（含目录 tar 打包、bm 错误提取） | `HdcDevice.InstallAsync`、`UninstallAsync` | ✅ 真实签名 hap 闭环已实测 |
| 端口转发 fport / rport | `HdcDevice.ForwardTcpAsync`、`ReverseTcpAsync` | ✅ 已实测 |
| hilog / bugreport / reboot / remount / runmode / rootrun | `HdcDevice.StreamHilogAsync`、`StreamBugReportAsync`、`RebootAsync`、`RemountAsync`、`SetRunModeAsync`、`RootRunAsync` | 只读项已实测；写操作仅单测覆盖 |

## 文档导航

| 文档 | 内容 |
|---|---|
| [API 参考](xref:HdcSharp) | 按命名空间组织的公共 API（含低层协议原语与传输层） |
| [设计规格](docs/superpowers/specs/2026-09-09-hdcsharp-design.md) | 协议细节、架构、公共 API 冻结基线、错误处理、安全 |
| [实施计划](docs/superpowers/plans/2026-09-09-hdcsharp-implementation.md) | 20 个 TDD 任务的拆分与验收标准 |
| [真机验证记录](docs/verification/2026-09-11-real-device-connect.md) | C++ 世代真机（`192.168.2.161:44221`）逐项实测证据与复现命令 |
| [README](README.md) | 快速开始、依赖与目标框架、AOT 说明、测试与验证、已知限制 |

## 目标框架与依赖

- `net8.0`，启用 AOT/Trim 分析器与原生 AOT 发布（`IsAotCompatible`），无反射/动态代码
- 唯一外部依赖：`System.IO.Pipelines`
- 公共 API 必须带中文 XML 文档注释（`CS1591` 在库工程为编译错误，由构建强制）

## 许可

Apache-2.0（与上游 OpenHarmony `developtools_hdc` 一致）。本库为协议独立重新实现，不含上游源码。
