# 真机集成测试（RealDevice）

把 2026-09-11 手工探针固化为仓库内、可复跑的真机用例。规格来源与全部已实测行为见
[`docs/verification/2026-09-11-real-device-connect.md`](../../../docs/verification/2026-09-11-real-device-connect.md)。

## 启用方式

未设置环境变量 `HDC_TEST_TARGET` 时，全部用例被标记为 Skip：默认 `dotnet test` 与 CI 不依赖真机、零副作用。

```bash
# 默认路径（全部跳过）
dotnet test tests/HdcSharp.Tests

# 真机路径
HDC_TEST_TARGET=192.168.2.161:44221 dotnet test tests/HdcSharp.Tests --filter "RealDevice=true"
```

PowerShell：

```powershell
$env:HDC_TEST_TARGET = '192.168.2.161:44221'
dotnet test tests/HdcSharp.Tests --filter "RealDevice=true"
```

或使用脚本（带挂起保护）：

```powershell
scripts/run-realdevice-tests.ps1 -Target 192.168.2.161:44221
```

## 设备要求

- daemon **C++ 世代**（握手回复含 `authtype` TLV；`Ver: 3.2.0f`）。Rust 世代的 `1200/Tlv32` 沙箱 shell 不可用，相关用例会失败。
- 设备 shell 为 **toybox**：用例依赖 `sha256sum`、`test`、`netstat`、`ftpput`、`echo`、`sh`；**不依赖** `nc/grep/tr/expr`（过滤一律在 C# 侧完成）。
- 宿主 `~/.harmony/hdckey` 存在且已被设备授权（与官方 hdc 共享密钥），否则认证会等待设备端弹窗确认而超时。用例会先断言该文件存在。
- 设备为真机（非模拟器）且未被其他 hdc 会话独占。

## 设计约定

- **门控**：`RealDeviceFactAttribute` / `RealDeviceTheoryAttribute`（`HDC_TEST_TARGET` 缺失即 Skip）；测试类标注 `[Trait("RealDevice", "true")]` 供 `--filter` 筛选。
- **禁止并行**：`[CollectionDefinition("RealDevice", DisableParallelization = true)]`——设备是共享物理资源，全部真机用例串行执行。
- **超时保护**：每个用例持有预算 `CancellationTokenSource`（连接 2 分钟、文件/目录/转发 3 分钟），预算耗尽即取消在途操作，不会无限悬挂。脚本另外使用 `--blame-hang-timeout` 兜底。
- **独立可跑**：每个用例自建连接与唯一临时路径（本地 `%TEMP%/hdcsharp_it_*`、设备侧 `/data/local/tmp/hdcsharp_it_*`），不依赖执行顺序；临时资源在 `finally` 中清理（设备侧 `rm -rf`）。
- **只读原则**：不执行会改变设备状态的命令。

## 覆盖矩阵

| 主题 | 场景 | 文件 |
|---|---|---|
| 连接 | Online/世代 Cpp/DeviceName/SessionId/ConnectKey、状态迁移序列、`AuthorizationRequested` 早于 Online 且仅一次、断线置 Offline + `DeviceDisconnected`、重复连接拒绝、共享密钥库免弹窗 | `RealDeviceConnectTests.cs` |
| shell | `echo` 回显、多字节 UTF-8、stderr 合流（`ls /nonexistent`）、退出码不上线（空输出）、流式分块顺序、交互式 PTY 算术求值 42、`DisposeAsync` 后写入抛 `ObjectDisposedException`、1200/Tlv32 未知包名报 `E003001` | `RealDeviceShellTests.cs` |
| 文件 | 尺寸 `0/1/49152/49153/98304/98305/147457/500000`（含块整数倍与「整数倍+1」）发送后设备侧 `sha256sum` 比对；同尺寸接收后本地 sha256 比对 | `RealDeviceFileTests.cs` |
| 目录 | 多文件 + 3 层嵌套 + 空目录：发送后按相对路径逐个比对设备侧 sha256、空目录不落盘、再接收回来做往返闭环；空目录源直接成功 | `RealDeviceDirectoryTests.cs` |
| 转发 | fport（本地 → 设备 44221）经隧道跑完整握手/认证/shell + 40KB 载荷，释放后本地端口不可连；rport 设备侧监听 + 设备内 `ftpput` 连本机 FTP 响应服务（双向断言），释放后设备侧监听消失；`reverse(0,…)` 无自动分配 | `RealDeviceForwardTests.cs` |
| Unity | `StreamHilogAsync` 取 5 行后主动退出、`StreamBugReportAsync` 分块采到 ≥20KB，二者之后会话仍可用 | `RealDeviceUnityTests.cs` |
| 应用 | 卸载不存在包 → bm 原文错误；安装缺失本地文件 → `FileNotFoundException`（未发帧）；安装 1KB 垃圾 hap → bm `no signature` 拒绝（APP 链路已到达设备端 bm） | `RealDeviceAppTests.cs` |

## 已知未覆盖项

| 未覆盖 | 原因 |
|---|---|
| `RebootAsync` / `RemountAsync` / `RootRunAsync` / `SetRunModeAsync` | 会重启 daemon、重新挂载分区或改写 `persist.hdc.*` 系统参数，改变设备状态（仅由 FakeDaemon 单元测试覆盖） |
| 真实应用包的安装成功路径 | 需要可用的 `.hap` / 已签名应用包；夹具仅有垃圾内容与不存在路径 |
| 目录安装真实 hap（tar 打包） | 同上：tar 帧路径已由单元测试覆盖，设备端 `bm` 需真实包结构 |
| Rust 世代 daemon 的 `1200/Tlv32` | 上游仅 C++ 世代实现；本套件针对 C++ 世代设备 |
| TLS-PSK 加密通道（里程碑 10） | 一期不实现 |
| 应用安装的进度回调与并发多设备 | 非本次固化的探针行为 |
