# HdcSharp

纯 C# 实现的 OpenHarmony HDC（HarmonyOS Device Connector）**宿主侧/客户端**协议库：让任何 .NET 程序通过 WiFi（TCP）连接、认证并操控 OpenHarmony 设备（shell 执行、文件收发、应用安装、端口转发、hilog/bugreport 流等）。

## 项目定位

**是什么**

- 一张"宿主侧协议库"：在原版 hdc 中属于 `hdc client + hdc server` 两个进程的职责，本库把它们做成进程内组件（方案 A：连接为中心 + 薄注册表）
- 面向 .NET 8+ 的可嵌入库：一个 `HdcHost` 门面 + 每设备一个 `HdcDevice`，全异步、全链路 `CancellationToken`
- 协议为参照公开源码的重新实现（C++ 世代 `src\`、Rust 世代 `hdc_rust\`），**未复用上游任何代码**

**不是什么**

- 不是 `hdc.exe` 替代品：**无 CLI**，不解析命令行，不提供 `hdc list targets` 之类的交互
- 不实现设备端 daemon：仅实现宿主侧协议
- 不做独立 C/S 架构、不做 USB/UART/蓝牙传输、不做 mDNS/UDP 自动发现（一期仅手动 `ip:port` 连接）
- 不含 LZ4 压缩、不含 TLS-PSK 加密通道（二期实验特性，见下）、不含 `-m` 模式同步/unix 域转发节点等 spec §11 Non-Goals

## 功能矩阵

| 能力 | 状态 | 真机验证 |
|---|---|---|
| TCP 连接、握手、RSA 认证（PSS-SHA512 / PKCS1 自动降级）、密钥库与官方 hdc 共享 | 可用 | ✅ C++ 世代真机 |
| 世代指纹（Rust `Ver: 3.0.0*` / C++ `Ver: 3.2.0*`）、心跳（仅 C++ 世代）、`AuthorizationRequested` 授权事件 | 可用 | ✅ C++ 世代真机 |
| shell：一次性 `ExecuteShellAsync`、流式 `StreamShellOutputAsync`、交互式 `OpenInteractiveShellAsync`（Ctrl-C/Ctrl-D） | 可用 | ✅ 真机（含 TLV32 `-b` 沙箱） |
| 文件：单文件 `SendFileAsync` / `ReceiveFileAsync`（进度回调、48KiB 分块） | 可用 | ✅ 真机（多尺寸双向 sha256，含 0/1/49152/49153/98304/98305/147457/500000 边界） |
| 目录：`SendDirectoryAsync` / `ReceiveDirectoryAsync`（递归、ustar 打包、跳过符号链接） | 可用 | ✅ 真机（3 层嵌套、往返闭环） |
| 应用：`InstallAsync`（含 tar 打包）/ `UninstallAsync`（bm 输出与 `[Exxxxxx]` 错误码提取） | 可用 | ⚠️ 仅"文件安装路径 + 卸载错误路径"；**真实 hap 安装成功路径未验证** |
| 端口转发：`ForwardTcpAsync`（fport）/ `ReverseTcpAsync`（rport）、`IForwardSession` 生命周期 | 可用 | ✅ 真机（隧道承载完整 HDC 会话 / 双向 FTP 交互） |
| unity：`StreamHilogAsync`（行流）、`StreamBugReportAsync`（分块）、`RebootAsync`、`RemountAsync`、`SetRunModeAsync`、`RootRunAsync` | 可用 | ⚠️ 仅 hilog/bugreport 只读真机验证；reboot/remount/runmode/rootrun **仅有单测** |
| TLS-PSK 加密通道（`ConnectOptions.EnableEncryption`） | **二期实验特性，未实现** | ❌ 置 true 时本连接按明文处理并记录警告 |
| flashd | 仅 `RebootMode.Flashd` 载荷预留，未验证 | ❌ |
| jdwp / ark | 未纳入（命令字未实现） | ❌ |
| 设备端 shell 退出码 | **不上线**（协议本身不下发，spec §4.11） | — |
| 传输介质 / 发现方式 | 仅 WiFi/TCP、仅手动 connect | — |

## 快速开始

完整可运行版本见 [`samples/AotConsumer/`](samples/AotConsumer/)（连接 → shell → 文件往返 sha256 比对，无参数或 `--help` 时优雅退出，不硬编码任何真机地址）。

下面的片段会被 `scripts/check-readme-snippets.ps1` 抽取并真实编译，文档不会与公共 API 漂移：

```csharp
using HdcSharp;

// HdcHost 是注册表/门面：可并发连接多台设备，也是事件的聚合点
await using HdcHost host = new();
host.DeviceStateChanged += (_, e) => Console.WriteLine($"[状态] {e.Key}: {e.OldState} -> {e.NewState}");
host.AuthorizationRequested += (_, key) => Console.WriteLine($"[授权] 请在设备端弹窗中允许 {key}");

// 连接 + 握手认证（默认使用 ~/.harmony/hdckey，与官方 hdc 共享密钥，已授权设备免二次弹窗）
HdcDevice device = await host.ConnectAsync("192.168.2.161:44221");
Console.WriteLine($"设备={device.DeviceName} 世代={device.Generation}");

// 一次性 shell：返回聚合 stdout+stderr；退出码不上线，需要时自行追加 echo $?
Console.WriteLine(await device.ExecuteShellAsync("param get const.product.model"));

// 单文件收发（进度回调可选）
await device.SendFileAsync(@"D:\tmp\a.txt", "/data/local/tmp/a.txt",
    new Progress<FileProgress>(p => Console.WriteLine($"[进度] {p.FileName} {p.BytesTransferred}/{p.TotalBytes?.ToString() ?? "?"}")));
await device.ReceiveFileAsync("/data/local/tmp/a.txt", @"D:\tmp\a.recv.txt");

// hilog 行流（取消即结束该通道）
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
await foreach (string line in device.StreamHilogAsync(cts.Token))
{
    Console.WriteLine(line);
}

// 交互式 shell：写入=键入，0x03=SIGINT、0x04=退出（daemon 语义）
await using (IInteractiveShell shell = await device.OpenInteractiveShellAsync())
{
    await shell.Input.WriteAsync("echo hi\n"u8.ToArray());
    Console.WriteLine(System.Text.Encoding.UTF8.GetString(await shell.Output.ReadAsync()));
}

await host.DisconnectAsync(device.ConnectKey);
```

## 兼容性矩阵

| daemon 世代 | 版本指纹 | 本库支持 | 真机状态 |
|---|---|---|---|
| C++ 世代（`src\`） | 握手回复 `Ver: 3.2.0f...`（daemon 回显 host 版本）、`AUTH_PUBLICKEY`/`AUTH_OK` 附 `authtype` TLV | 全功能（含心跳、TLV32 `-b` 沙箱 shell） | ✅ 已在 `192.168.2.161:44221` 实测通过（连接/认证/shell/文件/目录/安装路径/转发/hilog/bugreport） |
| Rust 世代（`hdc_rust\`） | 握手回复 `Ver: 3.0.0e`、`AUTH_PUBLICKEY` 的 buf 为裸 hex token（非 TLV） | 按源码实现（无心跳、`RunMode.Tcp`/`TcpClose` 不支持、`AUTH_PUBLICKEY` 回退 PKCS1） | ⚠️ **未在真机验证**（无 Rust 世代设备），仅由可配置世代的 FakeDaemon 单测覆盖两世代分支 |

世代指纹优先级（spec §4.6）：`authtype` TLV（定论）> `1200`/`supportfeatures` TLV > version 前缀（兜底）。

## 依赖与目标框架

- 目标框架：`net8.0`（LTS；消费端 8/9/10 运行时均可）
- 依赖：仅 [`System.IO.Pipelines`](https://www.nuget.org/packages/System.IO.Pipelines) 8.0.0（`System.Threading.Channels` 为 in-box，不显式引用）；测试工程另用 xUnit
- 跨平台：Windows / Linux / macOS（纯 BCL，无 Windows 专有 API、无路径大小写假设）
- 无反射 / 无 `dynamic` / 无 `Emit`：公共 API 面向 Native AOT 与裁剪设计

## AOT 与裁剪支持

库工程开启 `IsAotCompatible` + `EnableAotAnalyzer` + `EnableTrimAnalyzer` + `IsTrimmable`，并以此为门禁：

```bash
dotnet publish samples/AotConsumer -r win-x64 -c Release
```

期望：IL 编译成功、**0 警告 0 错误**（样例工程 `TreatWarningsAsErrors=true`，任何 IL2026/IL3050 等 AOT/裁剪告警都会直接失败）；产物 `samples/AotConsumer/bin/Release/net8.0/win-x64/publish/AotConsumer.exe` 可直接运行（`--help` 应打印用法）。

## 密钥库与授权

- 默认密钥库为 `~/.harmony/hdckey`（PKCS#8 PEM 私钥）+ `hdckey.pub`（SPKI PEM 公钥），**与官方 hdc 完全共享**：机器上已授权过的设备免二次弹窗；缺失时自动生成 RSA-3072 并落盘（目录 0750 / 私钥 0600，对齐官方实现）
- 自定义位置：`new ConnectOptions { KeyStore = new FileHostKeyStore(@"D:\my-keys") }`
- 设备端首次授权需要人工确认：daemon 弹出授权对话框时触发 `HdcHost.AuthorizationRequested`（参数为 `ip:port`），等待上限为 `ConnectOptions.AuthTimeout`（默认 3.5 分钟），超时按认证失败抛 `HdcException`
- 签名方案自动选择：daemon 声明 `authtype=1`（C++ 世代）用 PSS-SHA512；否则回退 PKCS1 私钥运算（Rust 世代）

## 测试与验证

测试分两层：默认路径全部基于 FakeDaemon 回环（**无真机依赖、零副作用**），真机路径由环境变量门控。

```bash
# 1) 构建（0 警告 0 错误）
dotnet build HdcSharp.sln -c Release

# 2) 单元/回环测试：通过 210、跳过 25（真机用例）、总计 235
dotnet test tests/HdcSharp.Tests -c Release

# 3) AOT 编译门禁（消费端样例）
dotnet publish samples/AotConsumer -r win-x64 -c Release

# 4) NuGet 打包（清单见包内容）
dotnet pack src/HdcSharp -c Release
```

`scripts/verify-all.ps1` 按顺序执行以上四条 + 三条质量门禁（公共 API 基线、注释三分法、README 代码块编译）并汇总结果，**这是本仓库唯一需要复跑的验收命令**：

```powershell
pwsh -File scripts/verify-all.ps1
```

真机集成测试（默认跳过）：

```powershell
# 方式一：直接指定端点
$env:HDC_TEST_TARGET = '192.168.2.161:44221'
dotnet test tests/HdcSharp.Tests -c Release --filter 'RealDevice=true'

# 方式二：带挂起保护的脚本
pwsh -File scripts/run-realdevice-tests.ps1 -Target 192.168.2.161:44221
```

真机用例覆盖：连接/认证/世代指纹、shell 一次性与流式（含多字节 UTF-8、stderr 合并、退出码不上线）、交互式 shell、TLV32 沙箱、文件双向传输与 R1 边界尺寸、目录嵌套往返、安装/卸载路径、fport/rport、hilog/bugreport 只读流。真机路径共 25 个测试方法（其中 2 个为 8 行数据驱动的边界尺寸用例，展开后 39 个用例结果），已在 `192.168.2.161:44221` 亲验 39/39 通过；默认未设 `HDC_TEST_TARGET` 时它们整体跳过（显示为 25 个跳过项）。

其余质量门禁：

```powershell
# XML 文档覆盖（库工程把 CS1591 视为错误，缺注释即编译失败；此处断言 0 条）
dotnet build src/HdcSharp -c Release | Select-String 'CS1591'

# 公共 API 冻结基线回归（比对公共 API 面与 scripts/public-api-baseline.txt）
pwsh -File scripts/check-public-api.ps1

# 注释三分法粗筛（报告 src/ 下全部非 /// 注释行供人工确认，并拦截 TODO/块注释/注释掉的代码）
pwsh -File scripts/check-comments.ps1

# README 代码块编译校验（防文档示例与公共 API 漂移）
pwsh -File scripts/check-readme-snippets.ps1
```

## 已知限制与未覆盖项

- **退出码不上线**：两世代 daemon 均不下发 shell 退出码，`ExecuteShellAsync` 只返回聚合输出；需要时在命令里追加 `echo $?`
- **真实 hap 安装成功路径未验证**：真机仅打通"文件安装路径（bm 返回 `no signature file`）"与卸载错误提取；需要真实签名 hap 才能覆盖成功分支
- **reboot / remount / runmode / rootrun 仅有单测**：会改变设备状态，未在真机上执行
- **Rust 世代未在真机验证**：`Ver: 3.0.0e` 路径按上游源码实现，缺失心跳、`RunMode.Tcp`/`TcpClose` 不支持
- **TLS-PSK 未实现**（二期实验特性）：`EnableEncryption=true` 当前按明文连接处理并记录警告
- **无 LZ4 压缩、无 USB/UART/蓝牙、无 mDNS/UDP 发现**（spec §11 Non-Goals）
- **flashd 仅载荷预留**（`RebootMode.Flashd`，未验证）；**jdwp / ark 命令字未实现**
- **单进程内多设备**：`HdcHost` 支持并发连接多台设备；原版的多客户端共享 daemon 进程语义不适用

## 参考文档

- 设计 spec（协议细节以 §4 为准，公共 API 冻结基线见 §5）：[`docs/superpowers/specs/2026-09-09-hdcsharp-design.md`](docs/superpowers/specs/2026-09-09-hdcsharp-design.md)
- 实施计划（20 个任务与验收口径）：[`docs/superpowers/plans/2026-09-09-hdcsharp-implementation.md`](docs/superpowers/plans/2026-09-09-hdcsharp-implementation.md)
- 真机验证记录（原始探针输出 + 复现命令 + 逐里程碑结论）：[`docs/verification/2026-09-11-real-device-connect.md`](docs/verification/2026-09-11-real-device-connect.md)

## 许可

包元数据声明 `PackageLicenseExpression = Apache-2.0`（与上游 OpenHarmony `developtools_hdc` 一致）；本库为协议重新实现，不含上游源码。仓库当前未附 LICENSE 文件，正式分发前请补齐（或改用用户指定的许可证）。
