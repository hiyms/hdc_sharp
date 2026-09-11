# 真机验证记录：2026-09-11 库级连通（里程碑 4）

目标设备：`192.168.2.161:44221`（WiFi/TCP，官方 OpenHarmony 设备）

## 1. 原始协议探针（不含库实现，手工构造握手帧）

探针脚本：`D:\work\hdc-probe\connect-probe.cs`（file-based app，仅用 `FrameCodec`/`Tlv16` 公开 API + 手工 serial_struct）

发送（169 字节帧，channelId=0，cmd=1 `CMD_KERNEL_HANDSHAKE`）：

```
banner="OHOS HDC", authType=0, sessionId=1, connectKey="192.168.2.161:44221",
buf=Tlv16{authtype="1", supportfeatures="Ver: 3.2.0f,TCP,win"},
version="Ver: 3.2.0f0000000000000000"
```

真机回复（80 字节 payload，`channelId=0`，`cmd=1`）：

```
field1 (len=8)  = "OHOS HDC"                 ← banner 正确
field2 (varint) = 3                          ← AUTH_PUBLICKEY（要求认证）
field3 (varint) = 0                          ← daemon 显式清零 sessionId
field4 (len=0)  = ""                         ← daemon 显式清空 connectKey
field5 (len=33) = "authtype        1               1"   ← TLV{authtype="1"} = RSA_3072_SHA512
field6 (len=27) = "Ver: 3.2.0f0000000000000000"        ← 回显 host 的 version
```

### 结论

| 项 | 结果 | 依据 |
|---|---|---|
| daemon 世代（风险 R2） | **C++ 世代** | 回复含 `authtype` TLV（`HandDaemonAuthInit` 独有行为，`src/daemon/daemon.cpp:553-558`）；Rust 的 `handshake_init_new` 在 buf 放裸 token 而非 TLV |
| 认证方案 | **PSS+SHA512**（`AuthVerifyType::RSA_3072_SHA512 = 1`） | 同上；`src/common/define_enum.h:56` |
| 心跳 | **支持**（C++ 世代） | 世代判定 |
| 1200/TLV32 shell 选项 | **可用**（C++ 世代） | 世代判定 |

## 2. 库级端到端连通（公共 API `HdcHost`）

探针脚本：`D:\work\hdc-probe\host-connect-probe.cs`
命令：`dotnet host-connect-probe.cs`（工作目录 `D:\work\hdc-probe`，需真机可达）

```
=== HdcSharp 库级真机连通探测 → 192.168.2.161:44221 ===
[事件] 状态迁移 192.168.2.161:44221: Connecting → Authorizing
[事件] ⚠ 需要设备端授权确认（弹窗）：192.168.2.161:44221
[事件] 状态迁移 192.168.2.161:44221: Authorizing → Online

✅ 连接成功，用时 0.6s
   ConnectKey  = 192.168.2.161:44221
   DeviceName  = localhost
   Generation  = Cpp
   SessionId   = 0x92D2622F
   State       = Online
   ConnectedDevices.Count = 1

   1.5s 后 State = Online（心跳在线）
[事件] 状态迁移 192.168.2.161:44221: Online → Offline
[事件] 连接断开：192.168.2.161:44221
断开结果 = True，State = Offline
总用时 2.1s
```

### 已验证的能力（端到端，真机）

1. 帧编解码（11 字节头 + PayloadProtect + 载荷）双向正确
2. Tlv16 握手 buf 编解码被 daemon 接受
3. 握手时序：host `CMD_KERNEL_HANDSHAKE` → daemon `AUTH_PUBLICKEY(3)` → host 回 PEM → daemon `AUTH_SIGNATURE(2)` 挑战 → host 签名回包 → daemon `AUTH_OK(4)`
4. PSS+SHA512 签名方案自动选择（由 daemon 的 `authtype="1"` TLV 触发）
5. `CHANNEL_CLOSE[1]` → host 回 `[0]` → 通道终结（`HdcConnection` 关闭语义）
6. 能力指纹：世代 C++ 判定正确；心跳协商成功且连接保持 Online
7. 状态机与事件：`Connecting → Authorizing → Online → Offline`、`DeviceDisconnected`
8. 清理：`DisconnectAsync` 正确移除设备

### 未触发弹窗的原因

本机已存在官方 hdc 于 2025-06 生成并被设备授权的密钥（`~/.harmony/hdckey`，625 字节 SPKI PEM），故设备直接放行。
`AuthorizationRequested` 事件在 `AUTH_PUBLICKEY` 阶段无条件触发（语义：可能需要在设备上确认），此行为正确。

## 3. 新发现的协议事实（已回写 spec §4.6）

**C++ daemon 回显 host 的 version 字段**：`DaemonSessionHandshakeInit`（`src/daemon/daemon.cpp:924-949`）与 `HandDaemonAuthInit`（`:544-566`）、`SendAuthOkMsg`（`:1455-1490`）均为**就地修改收到的 `SessionHandShake` 对象**后重发，只改 `authType`/`buf`/`sessionId`/`connectKey`，**从不设置 `version`**。因此 C++ daemon 的所有握手回复中 `version` 均为 host 自己发出的串。

Rust daemon 相反：`handshake_init_new` / `make_ok_message` 都显式 `version: get_version()`（`hdc_rust/src/daemon_lib/auth.rs:380-392, 290-330`）。

两者自报版本分别为：

| 世代 | `HDC_VERSION_NUMBER` | 自报版本串 |
|---|---|---|
| C++ | `0x30200500`（`src/common/define.h:130`） | `Ver: 3.2.0f` |
| Rust | `0x30000400`（`hdc_rust/src/config.rs:375`） | `Ver: 3.0.0e` |

**世代判定启发式成立性**：host 恒声明 `Ver: 3.2.0f`（`HdcConstants.HostVersion`），故
- C++ daemon：回复版本 = host 版本 = `Ver: 3.2.0f...` → 前缀 `Ver: 3.2.` → 判 Cpp ✓
- Rust daemon：回复版本 = 自身 `Ver: 3.0.0e` → 前缀 `Ver: 3.0.` → 判 Rust ✓

局限：若 host 声明的版本串本身为 `3.0.x`，与 C++ daemon 通信会被误判为 Rust。当前实现恒声明 `3.2.0f`，不触发该局限（已在 spec 注明）。

## 4. 复现命令

```bash
# 1) 原始协议探针（不含库）
cd D:/work/hdc-probe && dotnet connect-probe.cs 192.168.2.161 44221

# 2) 库级连通
cd D:/work/hdc-probe && dotnet host-connect-probe.cs 192.168.2.161:44221

# 3) 单元/集成测试（FakeDaemon 回环，不需要真机）
cd D:/work/hdc_sharp && dotnet test tests/HdcSharp.Tests
```

---

## 5. 真机 Shell 验证（Task 14，同日追加）

探针脚本：`D:\work\hdc-probe\shell-probe.cs`、`D:\work\hdc-probe\interactive-probe.cs`

### 一次性执行（`ExecuteShellAsync`，走 1001）

| 命令 | 结果 | 耗时 |
|---|---|---|
| `echo hello-hdcsharp` | `hello-hdcsharp` | 151ms |
| `uname -a` | `HarmonyOS localhost HongMeng Kernel 1.12.0 #1 SMP Mon Jul  6 13:40:02 UTC 2026 aarch64 Toybox` | 119ms |
| `echo 你好，HDC` | `你好，HDC`（多字节 UTF-8 跨块聚合正确） | 104ms |
| `ls /nonexistent-path-xyz` | `ls: /nonexistent-path-xyz: No such file or directory`（stderr 合并入输出） | 149ms |
| `sh -c 'exit 42'` | 空输出（**退出码不上线**，与 spec §4.11 一致） | 75ms |
| `ls /system \| head -20` | `ls: /system: Permission denied` | 153ms |

### 流式输出（`StreamShellOutputAsync`）

`for i in 1 2 3; do echo line-$i; sleep 0.2; done` → 收到 **3 个块**（daemon 按行 flush），累计 21 字符，顺序正确。

### 交互式 shell（`OpenInteractiveShellAsync`，走 2000/2001）

```
[chunk 2B] "$ "                                    ← PTY 提示符
[send] echo ARITH=$((6*7))
[chunk 3B] ech / [4B] o AR / [2B] IT / ...         ← 逐键回显（真实 PTY 行为）
[chunk 12B] ARITH=42\r\n$ "                        ← 命令执行结果（算术求值证明非回显）
```
- Ctrl-C（0x03）发送成功（daemon 侧转 SIGINT，`src/daemon/shell.cpp:91-107`）
- `DisposeAsync` 后写入 → `ObjectDisposedException` ✓

### unity 1200 / TLV32（`ExecuteUnityAsync`）

`ExecuteUnityAsync("echo unity-1200", new ShellOptions { BundleName = "com.example.sandbox" })`
→ 设备回 `[E003001] Invalid bundle name: com.example.sandbox`

**结论**：TLV32 载荷被 daemon 正确解析、`1200` 路径可达（错误仅因该沙箱包名在设备上不存在）。
印证上游 `src/daemon/daemon_unity.cpp:138-172`「1200 强制要求命令 + 包名两个 tag」。

### 新确认的协议事实

**shell 载荷无任何包装结构**：全仓检索 `RawDataProtocol|raw_data_protocol` 命中数为 0（`developtools_hdc/src/` + `hdc_rust/src/`）——一次性 shell（1001）与交互式输入（2001）的载荷均为**原始字节**；输出经 `CMD_KERNEL_ECHO_RAW(10)` 下发；完成信号是 `CMD_KERNEL_CHANNEL_CLOSE` 载荷 `[1]`，无 `CMD_SHELL_EXIT` 命令（`src/common/task.cpp:49-58`）。

---

## 6. 真机文件传输验证与丢尾缺陷修复（Task 15a，同日追加）

### 6.1 发现：文件尾部间歇丢失

真机发送 500KB 文件时，设备侧偶发只落 491520B（丢最后一块）；98305B 偶发只落 98304B。

**定位过程**：

| 步骤 | 手段 | 结论 |
|---|---|---|
| 1 | 发件前 `rm -f` + 内容按尺寸区分 | 排除残留文件污染（首次测量有 3 例是污染） |
| 2 | 同尺寸重复 4 轮 | 失败位置漂移 → **竞态**，非固定 off-by-one |
| 3 | 出帧跟踪（`HdcConnection.SendAsync` 临时钩子） | **我们发出了全部 11 个 DATA 帧**（10×49152 + 8480 = 500000），发送侧无缺陷 |
| 4 | 只发数据、**完全不发 FINISH**、等 3 秒 | **全部尺寸 100% 完整** → 丢尾由 FINISH 处理触发 |
| 5 | 上游源码 | `src/common/file.cpp:647-668`：daemon 收到 `FILE_FINISH[1]` 立即 `CloseCtxFd`，不等挂起的 `uv_fs_write` 完成，回 `[0]` 且**不走 `TaskFinish`**（故也无 ECHO） |
| 6 | 上游源码 | `src/common/transfer.cpp:291-302`：`closeNotify` **仅在写路径**设置 → **只有写端（从端）会在自身 IO 完成后主动发 `[1]`**；注释明示 *"you can't make Finish first, because Slave may not end"* |
| 7 | 上游源码 | `src/common/file.cpp:647-668` `void HdcFile::WhenTransferFinish` 恒发 `[1]`；主端读取路径（`ProcressFileIORead`）完成时**不发** `[1]` |

### 6.2 修复（`FileOperation`）

1. **主端不再抢先发 `FILE_FINISH[1]`**：发完数据后等从端的 `[1]`，收到后回 `[0]`，再把从端的 `KERNEL_CHANNEL_CLOSE` 视为正常终结（新增 `AwaitSlaveFinishAsync`）
2. **空文件补零长度 DATA 帧**：对齐上游主端"读到 0 字节仍发一次 `SendIOPayload(index, buf, 0)`"，从端写 0 字节命中 `req->result == 0` 完成分支
3. 曾试验但**证伪**：追加零长度 EOF 标记到非空文件会让失败率上升（0 字节写触发 `req->result == 0` 提前完成分支）

### 6.3 验证结果（真机）

| 方向 | 尺寸 | 次数 | 结果 |
|---|---|---|---|
| 发送 | 49152 / 98304 / 49153 / 98305 / 147457 / 500000 | 6 尺寸 × 6 轮 = 36 | **0 失败**（sha256 逐一比对） |
| 接收 | 0 / 1 / 49152 / 49153 / 98305 / 500000 / 1000000 | 7 尺寸 × 2 轮 = 14 | **0 失败**（sha256 逐一比对） |

修复前的对照基线：同样 6 尺寸 × 4 轮 = 24 次中失败 8 次（约 33%）。

### 6.4 回归保护

`FakeDaemon` 的 sink 剧本改为忠实建模真机语义：
- 写完成**延迟 20ms**（模拟 `uv_fs_write` 异步滞后），维护 `Written`（对应 daemon 的 `indexIO`）
- 仅当 `Written >= fileSize` 时才主动发 `FILE_FINISH[1]`（对应 `ProcressFileIOWrite`）
- 收到主端 `FILE_FINISH[1]` 且尚有挂起写 → **丢弃挂起块**并回 `[0]`（复现真机丢尾行为）

验证有效性：把客户端回退为"抢先发 `[1]`"后，`FileTransferTests` **3 个用例失败**（round-trip / 整除边界 / 通道清理）；修复版本 151/151 通过。
另更新 `SendFile_EmptyFile_Succeeds` 断言为空文件**应发且仅发一个零长度 DATA 帧**。

---

## 7. 真机目录传输验证（Task 15b，同日追加）

探针：`D:\work\hdc-probe\dir-probe.cs`

本地树：`root.txt` + `sub/a.bin`(120KB) + `sub/deep/b.txt` + `sub/deep/deeper/c.bin`(5KB) + 空目录 `emptydir`（协议不承载目录，空目录无条目 → 未创建）。

**发送**（`SendDirectoryAsync`，503ms，进度回调 6 次涉及 4 个文件）：

```
设备侧树：                      设备侧 sha256 校验：
/data/local/tmp/dirprobe        root.txt              ✅
/data/local/tmp/dirprobe/sub    sub/a.bin             ✅
  .../sub/deep                 sub/deep/b.txt        ✅
  .../sub/deep/deeper          sub/deep/deeper/c.bin ✅
```

**接收**（`ReceiveDirectoryAsync`）：4 个文件全部回收，sha256 与发送前一致 → **往返闭环一致**。

**结论**：目录结构、相对路径、多文件推进、进度累计均正确；`optionalName` 语义（目标不存在时按上游语义剥首层目录名）与设备实际落盘位置吻合。

### 顺带核实的上游事实（已写回 spec §4.7.3）

- 目录传输**仅 1 次 `WAKEUP`**（任务创建时），每个文件走完整 `FILE_CHECK`→`DATA`→`FINISH` 循环
- `FILE_FINISH` 多文件语义：每个文件由**写端→主端**发一个 `[1]`；主端收 `[1]` 时若有下一文件则推进（**不发 `[0]`**），仅最后一个文件把 `1` 递减为 `[0]` 回发一次
- `FILE_MODE(3006)`/`DIR_MODE(3007)` 仅在 `-m` 模式同步时使用（一期不实现）；`functionName` 在文件/目录传输中恒为空串（仅应用安装为 `"install"`）
- 空目录不产生任何条目，上游 CLI 对空源目录直接报错；本库选择直返成功（不发明帧）

---

## 8. 真机应用安装/卸载路径验证（Task 16，同日追加）

探针：`D:\work\hdc-probe\app-probe.cs`（无真实 .hap 包，故验证协议路径与错误提取，并以 bm 的真实响应反证帧路径已打通）

| # | 场景 | 真机响应 | 结论 |
|---|---|---|---|
| 1 | 卸载不存在的包 `com.hdcsharp.nonexistent.pkg` | `HdcException: error: failed to uninstall bundle. code:9568386 error: uninstall missing installed bundle.` | ✅ `APP_UNINSTALL` 帧到达 bm，bm 真实错误被正确提取 |
| 2 | 安装不存在的本地文件 | `FileNotFoundException: 待安装包不存在：…`（未发任何帧） | ✅ 客户端前置校验 |
| 3 | 安装 1KB 垃圾内容 `.hap` | `HdcException: error: failed to install bundle. code:9568320 error: no signature file.` | ✅ **完整 APP 路径打通**：WAKEUP→APP_CHECK→APP_BEGIN→APP_DATA→APP_FINISH 全部被 daemon 接受，文件已传递并在设备侧调用 bm 安装 |
| 4 | 安装目录（tar 打包） | `HdcException: error: failed to install bundle. code:9568269 error: install file path invalid.` | ⚠ 协议帧路径通（同上），但 tar 内容非真实 hap 结构，bm 拒绝；**需真实应用包才能进一步验证** |

### 顺带核实的上游事实

- 安装选项经 **`TransferConfig.options`** 传递（`src/host/host_app.cpp:99`，dash 前缀参数以空格连接），**不发 `APP_INIT` ASCII 参数串**；`functionName = "install"`
- 目录安装：`Dir2Tar` 打到临时文件 `<随机名>.tar` 后作为**单个普通文件**走 APP 流（`host_app.cpp:32-54,143-154`）；本库命名与之一致（`AppOperation` 用 `NewRandomName() + ".tar"`）
- 只有 `.hap/.hsp/.app` 后缀被当作包文件，其余路径一律尝试打包为 tar（`host_app.cpp:86-95`）
- `APP_FINISH` 载荷 `[mode][success][bm 文本]`；`success=0` 时以文本与首个 `[Exxxxxx]` 作 `HdcException` 抛出

**诚实标注**：真实应用包的安装成功路径与目录安装（真实 hap 目录）在本机缺少可用包的前提下**未验证**，已在计划 Task 19 的真机集成测试中标注为需真实包方可覆盖。

---

## 9. 真机端口转发验证（Task 17，同日追加）

探针：`D:\work\hdc-probe\fport-probe.cs`、`D:\work\hdc-probe\rport-probe.cs`

### 9.1 fport（本地端口 → 设备端口）

`ForwardTcpAsync(0, 44221)`（设备 44221 = hdcd 本体）→ 本地监听 61015。

**用最强验证**：再起一个 `HdcHost` 经隧道连接设备自身 hdcd，跑完整会话——

```
✅ 经隧道完成握手+认证：DeviceName=localhost Generation=Cpp State=Online
✅ 经隧道执行 shell：TUNNELED-SHELL-OK
✅ 经隧道传 40KB 载荷：设备回 40000（期望 40000）
已释放转发：IsActive=False
✅ 释放后连接被拒：SocketException
```

即隧道承载了完整握手、约 1KB 的认证往返、shell 命令与 40KB 数据，双向无损；释放后监听端口立即关闭。

### 9.2 rport（设备端口 → 本地端口）

`ReverseTcpAsync(28765, <本地端口>)`；本地起最小 FTP 响应服务作为被转发目标；在设备上以 `ftpput -p 28765 127.0.0.1 …` 主动发起连接。

```
rport 建立成功：设备侧监听端口=28765 Direction=Reverse IsActive=True
设备侧 netstat: tcp 0 0 127.0.0.1:28765 0.0.0.0:* LISTEN

设备侧 ftpput 输出：              本地服务日志：
220 hdcsharp-fake-ftp             [本地服务] 接受来自 127.0.0.1:61060 的连接（经 rport 隧道）
USER anonymous                    [本地服务] 已发送问候 220（23 字节）
331 need password                 [本地服务] 收到 16 字节: USER anonymous
PASS ftpget@                      [本地服务] 收到 14 字节: PASS ftpget@
230 logged in                     [本地服务] 收到 8 字节: TYPE I
TYPE I                            [本地服务] 收到 6 字节: PASV
200 ok
PASV
```

**结论**：双向数据流完全正确（设备→主机与应用层往返均验证）；`DisposeAsync` 后 `IsActive=False`。
`ReverseTcpAsync(0, …)` 会抛 `ArgumentOutOfRangeException`（设备侧监听端口必须显式指定，无"自动分配"语义）——符合设计预期。
