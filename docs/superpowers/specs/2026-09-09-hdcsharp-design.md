# HdcSharp 设计文档

- 日期：2026-09-09
- 状态：待终审
- 协议参考：`D:\work\developtools_hdc`（C++ 世代 `src\`，Rust 世代 `hdc_rust\`），本文件所有 file:line 引用均指该仓库
- 上游研究报告：帧格式/认证/命令流/Rust 差异四份（对话存档），关键结论已浓缩至 §4

---

## 1. 概述与目标

HdcSharp 是一个纯 C# 实现的 OpenHarmony HDC（HarmonyOS Device Connector）**宿主侧**协议库：让任何 .NET 程序通过 WiFi（TCP）连接、认证并操控 OpenHarmony 设备（shell 执行、文件收发、应用安装、端口转发、hilog/bugreport 流等）。

**它不是什么**：

- 不是 `hdc.exe` 等价替代品（无 CLI）
- 不实现设备端 daemon
- 不含任何 C++/Rust 源码（协议为逆向自公开源码参考的重新实现）
- 不做独立 C/S 架构：原版把 host 拆为 hdc client + hdc server 两个进程是历史产物，本库把"server 逻辑"做成进程内组件
- 不做 USB/UART 传输（接口预留 `ITransport`，一期仅 TCP）
- 不做 mDNS/UDP 发现（一期仅手动 connect）

**成功标准**：

1. 对真机 `192.168.2.161:44221`（follow 真机策略：以该设备实测行为为准）完成连接、认证、shell、文件往返、安装冒烟
2. 全部公共 API 有中文 XML 文档注释；`dotnet publish -r win-x64 -p:PublishAot=true` 的消费端可编译（库本体 AOT/Trimming 注解完备）
3. 协议原语层有 byte-exact 黄金向量测试；核心操作有 FakeDaemon 回环测试
4. 跨平台：Windows/Linux/macOS 均可运行（纯 BCL + 指定依赖）

## 2. 工程规范（硬性约束）

### 2.1 注释三分法（用户要求，强制）

| 类型 | 语言 | 规则 |
|---|---|---|
| **文档注释**（`///` XML doc） | 中文 | **所有公共 API 必须有**（public/protected 成员、接口、枚举、参数、返回值、异常）。含 `///` 而非 `//`，保证 IDE/文档生成可见 |
| **平台注释** | 中文 | 按需**少量**添加，必须解释**为什么**而非做了什么（例：`// 大端序：原版用 htons/htonl 写帧长，不可改用本机序`）。禁止流水账式"做了什么" |
| **其它注释** | — | **一律禁止**（含 TODO/FIXME 等散注；待办进任务系统或 spec 开放项） |

### 2.2 跨平台与 AOT

- 目标框架 `net8.0`（LTS；本机 .NET 10 SDK 可构建，消费端 8/9/10 运行时皆可用）
- 禁用：Windows 专有 API（注册表/WMI）、`Environment.NewLine` 之外的换行假设、路径大小写假设
- AOT/Trimming：无反射依赖用户类型、无 `dynamic`/`Emit`；csproj 启用 `IsAotCompatible` 与 `EnableTrimAnalyzer`；`SkipLocalsInit` 不强制
- 依赖白名单：`System.IO.Pipelines`、`System.Threading.Channels`（微软官方，AOT 安全）。其余一律不引入（LZ4、TLS-PSK 均自研或后置）

### 2.3 命名与风格

- 库名/命名空间/包 ID：`HdcSharp`
- 公共类型 PascalCase；协议字段名保留原版语义（如 `PayloadProtect`、`TransferConfig`），便于对照上游
- 异步方法以 `Async` 结尾，全链路 `CancellationToken`（最后一个参数，默认值 `default`）
- 文件作用域命名空间 + `readonly struct` 优先（协议原语大量使用）

## 3. 总体架构（方案 A：连接为中心 + 薄注册表）

```
┌────────────────────────── HdcHost（进程内注册表/门面）──────────────────────────┐
│  ConnectAsync / DisconnectAsync / ConnectedDevices / DeviceStateChanged 事件    │
└──────────────────────────────────────┬───────────────────────────────────────┘
                                       │ 1..*
┌──────────────────────────────── HdcDevice ─────────────────────────────────────┐
│  每设备一条 TCP 连接 = 一个 Session（sessionId 为 host 随机 u32，daemon 采纳） │
│  ExecuteShellAsync / StreamShellOutputAsync / OpenInteractiveShellAsync        │
│  SendFileAsync / ReceiveFileAsync / InstallAsync / UninstallAsync              │
│  ForwardTcpAsync / ReverseTcpAsync / StreamHilogAsync / CaptureBugReportAsync  │
│  RebootAsync / RemountAsync / SetRunModeAsync / DeviceInfo（握手所得）          │
└───────┬───────────────────────────────────────────────────────────────────────┘
        │ 拥有
┌───────▼────────────── HdcConnection（传输层）──────────────────────────────────┐
│  TcpClient + Pipe 帧泵（读循环/写锁）· 帧编解码 · 通道多路复用（channelId 路由）│
│  握手/认证状态机 · 心跳定时器（能力协商）· 能力指纹（DaemonGeneration）        │
└───────┬───────────────────────────────────────────────────────────────────────┘
        │ 使用
┌───────▼────────────────────── Protocol（线缆层，无 IO）────────────────────────┐
│  PayloadHead · PayloadProtect · SerialStructReader/Writer（泛用 varint TLV）   │
│  Tlv16（16B 空格填充块）· Tlv32（u32 tag+len，shell 选项专用）· TarHeader       │
│  各消息结构体（SessionHandShake/TransferConfig/TransferPayload/FileMode/…）    │
└───────────────────────────────────────────────────────────────────────────────┘
        │
┌───────▼────────────────────── Security ───────────────────────────────────────┐
│  HostKeyStore（~/.harmony/hdckey(.pub)，与官方 hdc 共享）· AuthHandler         │
│  RsaPkcs1PrivateEncrypt（BigInteger CRT 原语，见 §7.3）· TlsPskChannel（二期） │
└───────────────────────────────────────────────────────────────────────────────┘
```

**目录结构**

```
src\HdcSharp\
├── HdcHost.cs / HdcDevice.cs / HdcChannel.cs / HdcDeviceState.cs / ConnectOptions.cs
├── Protocol\（PayloadHead.cs PayloadProtect.cs SerialStruct\*.cs HdcCommand.cs
│             Tlv16.cs Tlv32.cs TarHeader.cs Messages\*.cs Constants.cs）
├── Transport\（HdcConnection.cs FramePump.cs HeartbeatTimer.cs DaemonCapabilities.cs
│             ITransport.cs ITransportFactory.cs）
├── Security\（HostKeyStore.cs AuthHandler.cs RsaRaw.cs TlsPsk\*.cs[二期]）
└── Operations\（ShellOperation.cs FileOperation.cs AppOperation.cs ForwardOperation.cs
              UnityOperation.cs Forward\*.cs InteractiveShell.cs）
tests\HdcSharp.Tests\（Protocol 黄金向量 · FakeDaemon · Operations 回环 · RealDevice 集成）
```

**数据流（以 ExecuteShellAsync 为例）**

1. `HdcDevice` 从连接分配空闲 channelId（随机 u32，与原版 `GetChannelPseudoUid` 同源）
2. `HdcConnection.Send(channelId, CMD_UNITY_EXECUTE=1001, "param get ...")` → 帧泵加帧头写出
3. 读循环收 `CMD_KERNEL_ECHO_RAW(10)`（同 channelId）→ 写入该通道的 `Channel<Chunk>` 队列
4. daemon 完成后发 `CMD_KERNEL_CHANNEL_CLOSE(2)`，载荷 `[1]`：连接层把通道标记为"远端已关闭"，队列入尾哨兵
5. `ExecuteShellAsync` 汇聚全部 Chunk 为 string 返回；`StreamShellOutputAsync` 则以 `IAsyncEnumerable<byte[]>` 逐块产出

**并发模型**：每连接 1 个读循环（Pipe.Reader）+ 1 个写信号量（`SemaphoreSlim` 串行化写帧）；每通道 1 个 `Channel<T>`（有界，背压）+ 每操作一个 `CancellationTokenSource` 联动连接级取消。事件回调在专用调度上下文触发，异常隔离（不炸连接）。

## 4. 协议参考（线缆层，实现以此为准）

### 4.1 帧格式（session 层，host↔daemon，两世代字节级一致）

```
┌─────────────── PayloadHead（11 字节，紧凑）───────────────┐
│ off 0  2B  flag       = 'H''W' (0x48 0x57)                │
│ off 2  2B  reserve    = 00 00                             │
│ off 4  1B  protocolVer= 0x01                              │
│ off 5  2B  headSize   = uint16 大端：Protect 序列化长度    │
│ off 7  4B  dataSize   = uint32 大端：payload 长度         │
├───────────────────────────────────────────────────────────┤
│ headSize 字节：PayloadProtect（serial_struct 编码）        │
│ dataSize 字节：命令负载                                    │
└───────────────────────────────────────────────────────────┘
```

- 引用：`src\common\session.h:160-166`（结构）、`src\common\session.cpp:1010-1079`（Send）、`session.cpp:1109-1139`（OnRead）、`hdc_rust\src\serializer\pack_assemble.rs:32-61`
- 接收规则：`flag=="HW"` 否则连接作废；帧必须**完整缓冲后**再解析；`vCode` 必须 `0x09`（`session.cpp:1089-1092`）
- 单帧上限：按接收缓冲 1MiB-1KiB 设计（原版 `HDC_SOCKETPAIR_SIZE`）；文件数据块用 48KiB（Rust 世代 `FILE_PACKAGE_PAYLOAD_SIZE=49152`）

### 4.2 serial_struct 编码（proto 风格，自研读写器）

- tag = `(fieldNumber << 3) | wireType`，以 varint 输出；wireType：0=VARINT、2=LEN
- varint = LEB128（7bit 小端组）；uint32 ≤5B、uint64 ≤10B；**无 zigzag/fixed 需求**（本项目涉及的消息全是 varint+LEN）
- string = tag + varint 长度 + 原始字节
- **默认值也必须输出**：所有标量（含 0）、所有 string（含空串）一律写出；嵌套消息仅在序列化长度为 0 时省略
- 解析端**不容忍未知 tag**（原版静默跳过但不消费值，等价于必须共享精确 tag 表）
- 引用：`src\common\serial_struct_define.h:168-262`（tag/varint）、`serial_struct.h:78-105`（描述符）、C++ 与 Rust 两份实现 diff 仅拼写差异（字节一致）

**黄金向量**（必须进单测）：

| 输入 | 期望字节 |
|---|---|
| PayloadProtect{channelId=42, cmd=9, checkSum=0, vCode=9} | `08 2A 10 09 18 00 20 09` |
| SessionHandShake{banner="OHOS HDC", authType=0, sessionId=1, connectKey="", buf="", version=""} | `0A 08 4F 48 4F 53 20 48 44 43 10 00 18 01 22 00 2A 00 32 00` |
| HeartbeatMsg{count=0, reserved=""} | `08 00 12 00` |

### 4.3 消息 tag 表

| 消息 | 字段（tag: 名称: 类型） |
|---|---|
| PayloadProtect | 1: channelId: varint · 2: commandFlag: varint · 3: checkSum: varint(恒0) · 4: vCode: varint(恒9) |
| SessionHandShake | 1: banner: string · 2: authType: varint · 3: sessionId: varint · 4: connectKey: string · 5: buf: string · 6: version: string |
| HeartbeatMsg | 1: heartbeatCount: varint(u64) · 2: reserved: string(恒空) |
| TransferConfig | 1: fileSize: varint(u64) · 2: atime: varint(u64,ns) · 3: mtime: varint(u64,ns) · 4: options: string · 5: path: string · 6: optionalName: string · 7: updateIfNew: varint(bool) · 8: compressType: varint · 9: holdTimestamp: varint(bool) · 10: functionName: string · 11: clientCwd: string · 12: reserve1: string · 13: reserve2: string |
| TransferPayload | 1: index: varint(u64,文件内绝对偏移) · 2: compressType: varint · 3: compressSize: varint(u32) · 4: uncompressSize: varint(u32) |
| FileMode | 1: perm: varint · 2: uId: varint · 3: gId: varint · 4: context: string · 5: fullName: string |

### 4.4 Tlv16（握手 `buf` 专用，与 4.9 Tlv32 是两套不同的东西）

```
[ tag：16 字节 ASCII，空格(0x20)右填充 ][ valueLen：10 进制 ASCII，空格右填充至 16 字节 ][ value 原始字节 ]
```

- 解析要求整块 ≥32 字节起；tag 与 len 均去除尾部空格；len 为十进制
- 引用：`src\common\base.cpp:2821-2869`；tag 常量 `src\common\base.h:276-284`
- 已知 tag：`devname` `hostname` `pubkey` `emgmsg` `token` `daemonauthstatus` `authtype` `1200` `supportfeatures`

### 4.5 命令字（HdcCommand，本项目用到的子集）

| 值 | 名称 | 方向 | 用途 |
|---|---|---|---|
| 1 | KERNEL_HANDSHAKE | 双向 | 握手/认证全部消息 |
| 2 | KERNEL_CHANNEL_CLOSE | 双向 | 通道关闭，载荷=递减跳数计数（1 字节） |
| 9 | KERNEL_ECHO | D→H | `[level u8][text]`，0=Fail 1=Info 2=Ok |
| 10 | KERNEL_ECHO_RAW | D→H | 原始输出字节流（shell/hilog 等） |
| 12 | KERNEL_WAKEUP_SLAVETASK | D→H | 空载荷，文件/应用任务预备信号，**收到即忽略** |
| 1001 | UNITY_EXECUTE | H→D | 一次性 shell，载荷=命令字符串 |
| 1002/1003/1004/1005/1007 | REMOUNT/REBOOT/RUNMODE/HILOG/ROOTRUN | H→D | 载荷见 §4.10 |
| 1011/1012 | BUGREPORT_INIT/DATA | H→D / D→H | bugreport 流 |
| 2000/2001 | SHELL_INIT/SHELL_DATA | H→D | 交互 shell（PTY） |
| 2500-2509 | FORWARD_* | 双向 | 端口转发（§4.8） |
| 3000-3007 | FILE_*/FILE_MODE/DIR_MODE | 双向 | 文件传输（§4.7） |
| 3500-3505 | APP_*/APP_UNINSTALL | 双向 | 应用安装（§4.7.4） |
| 5000 | HEARTBEAT_MSG | 双向 | 仅 C++ 世代且协商成功（§4.6） |

**禁发清单（对 Rust 世代 daemon）**：1200（UNITY_EXECUTE_EX）、20（SSL_HANDSHAKE）、5000（心跳）、17/18（两世代含义冲突：C++ SERVICE_START/TARGET_RECONNECT vs Rust ClientVersion/ClientKeyGenerate）。能力指纹见 §4.6。

### 4.6 握手、能力指纹与心跳

**流程**（引用：`src\common\session.cpp:1288-1380`、`src\host\server.cpp:556-829`、`src\daemon\daemon.cpp:544-1065`、`hdc_rust\src\host\auth.rs:36-45`、`hdc_rust\src\daemon_lib\auth.rs`）：

1. TCP 连接建立后 **host 先发** `CMD_KERNEL_HANDSHAKE(1)`，载荷=SessionHandShake：
   - `banner="OHOS HDC"` · `authType=0` · `sessionId=随机u32` · `connectKey="ip:port"`
   - `buf` = Tlv16{ `authtype`="1" } + Tlv16{ `supportfeatures`="Ver: 3.2.0f,TCP,<os>[,heartbeat][,encrypt_tcp]" }
     - 一期固定不声明 `encrypt_tcp`；`heartbeat` 是否声明由 ConnectOptions 决定（默认声明）
   - `version` = `"Ver: 3.2.0f"` + 构建哈希（哈希部分我们用固定 16 个 `0` 占位；原版为源码结构哈希，daemon 仅记录不校验）
2. daemon 校验 banner；**采纳 host 的 sessionId**；按自身能力回复：
   - auth 未启用 → 直接 `AUTH_OK(4)`（buf 含 Tlv16{devname, daemonauthstatus="SUCCESS", emgmsg=""}；Rust 世代仅这三项，C++ 世代另含 `1200`="enable"、`supportfeatures`）
   - auth 启用 → `AUTH_PUBLICKEY(3)`（C++ daemon 的 buf 含 Tlv16{authtype}；Rust daemon 无）
3. host 回 `AUTH_PUBLICKEY(3)`：`buf = hostname + 0x0C + PEM公钥`（625 字节 SPKI PEM，见 §7.1）
4. daemon 检查已知主机（`/data/service/el1/public/hdc/hdc_keys`）；陌生则设备端弹窗（约 3 分钟超时），允许后继续；拒绝/超时 → `AUTH_OK` 携带 `emgmsg`=`[E000002]/[E000003]` + `daemonauthstatus="DAEMON_UNAUTH"` → **连接结束**
5. daemon 发 `AUTH_SIGNATURE(2)`，buf=挑战 token：
   - **C++ daemon：20 字符（20 随机字节逐字节小写 hex）**；host 若在步骤 2 看到 `authtype="1"` → 用 PSS+SHA512 签名
   - **Rust daemon：64 字符（32 随机字节大写 hex）**；无 authtype TLV → 用 PKCS1 私钥运算（见 §7.3）
6. host 回 `AUTH_SIGNATURE(2)`，`buf=Base64(签名)`
7. daemon 校验 → `AUTH_OK(4)`（TLV 同步骤 2）+ 紧随 `CMD_KERNEL_CHANNEL_CLOSE` channelId=0 载荷 `[1]`（握手通道拆除：host 将载荷递减至 `[0]` 并回发一次后终结，对齐 `src\host\server.cpp:908-918`）
8. 任意阶段 banner 变为 `HS FAILED` 或版本串 < `"Ver: 3.0.0b"`（纯字符串比较）→ 连接作废

**能力指纹（DaemonGeneration）**：

| 信号 | Rust 世代 | C++ 世代 |
|---|---|---|
| version 串 | `Ver: 3.0.0*` | `Ver: 3.2.0*` |
| AUTH_PUBLICKEY 回复 buf | 裸 token（64 字符 hex） | TLV{`authtype`="1"}（当 host 声明支持 RSA_3072_SHA512 时，host 恒声明） |
| AUTH_OK buf TLV | 仅 devname/daemonauthstatus/emgmsg | 另含 `1200`/`supportfeatures`（设备未启用纯 daemon 侧连接校验时才附加） |
| 握手时 supportfeatures 回显 | 无 | 有（含 heartbeat/encrypt_tcp 与否） |

指纹在 AUTH_OK 时确定，此后决定：心跳开关、可否使用 1200、文件传输 FeatureFlags 容忍度。

> **version 字段的回显语义（实测 + 源码验证，见 `docs/verification/2026-09-11-real-device-connect.md`）**：C++ daemon 的握手回复（`AUTH_PUBLICKEY`/`AUTH_OK`）均为**就地修改收到的 `SessionHandShake` 后重发**（`src/daemon/daemon.cpp:544-566, 924-949, 1455-1490`），只改 `authType`/`buf` 并显式清零 `sessionId`、清空 `connectKey`，**从不设置 `version`** → 回复中的 version 恒为 **host 自己发出的串**。Rust daemon 相反，显式 `version: get_version()`（自身版本）。
>
> 因 host 恒声明 `Ver: 3.2.0f`（`HdcConstants.HostVersion`），前缀判定在两种世代下均正确：C++ 回显得 `Ver: 3.2.*` → Cpp；Rust 自报 `Ver: 3.0.0e`（`HDC_VERSION_NUMBER=0x30000400`）→ Rust。C++ 自身版本常量 `0x30200500` 亦为 `Ver: 3.2.0f`（`src/common/define.h:130`），与 host 声明一致。局限：若 host 改声明 `3.0.x`，与 C++ daemon 通信将被误判为 Rust——故**实现以 `authtype` TLV（定论性 C++ 信号）+ version 前缀组合判定**。

**心跳**：仅当双方声明 `heartbeat`（C++ 世代）才启用：`HdcConnection` 每 5s 发 `CMD_HEARTBEAT_MSG(5000)` 载荷=HeartbeatMsg（count 递增）；接收端仅刷新时间戳。daemon 侧 1 小时无任何入帧才断连，host 不主动超时。Rust 世代 daemon 收到 5000 会解析失败——**指纹为 Rust 时连支持特性声明都省略，从根源避免**。

### 4.7 文件传输与应用安装

引用：`src\common\transfer.h`、`src\common\file.cpp`、`src\common\header.cpp`、`hdc_rust\src\common\hdcfile.rs`、`hdctransfer.rs`、`tar\*.rs`、`src\host\host_app.cpp`、`hdc_rust\src\host\host_app.rs`

**压缩**：枚举存在 LZ4 等，但一期**恒 `compressType=0`**（不请求压缩即不会收到压缩块；接收端遇非 0 视为协议错误）。接口预留。

#### 4.7.1 发送单文件（host→device）

1. H→D `FILE_INIT(3000)`，载荷=ASCII 参数串 `"send <opts> <local> <remote>"`（-opts：`-a` 保留时间戳/`-sync` 仅当较新/`-cwd <dir>`；一期不实现 `-m` 模式同步与 `-b` 沙箱）
   - daemon 回 `WAKEUP_SLAVETASK(12)` 空载荷 → 忽略
2. H→D `FILE_CHECK(3001)`，载荷=TransferConfig{fileSize, path=remote, optionalName=本地文件名, updateIfNew, holdTimestamp, clientCwd, 其余空/0}
3. D→H `FILE_BEGIN(3002)`：**载荷可能为空（Rust）或 8 字节 FeatureFlags（C++，bit0=hugeBuf）**——两者都接受
4. H→D `FILE_DATA(3003)` 循环：载荷 = **64 字节定长槽**（TransferPayload 序列化后左对齐填入 64B，余下补 0）+ 数据块（≤48KiB，`index`=块内绝对偏移）
5. 完成后 H→D `FILE_FINISH(3004)` 载荷 `[1]`；D 回 `FILE_FINISH` 载荷 `[0]` 携带汇总文本（经 KERNEL_ECHO Ok 级）；随后通道关闭

#### 4.7.2 接收单文件（device→host）

镜像流程：H→D `FILE_INIT` 载荷 `"recv <remote> <local>"` → daemon 成为主端，D→H `FILE_CHECK`（TransferConfig，host 侧按 path/optionalName 落盘）→ H→D `FILE_BEGIN`（**host 作为 slave 发空载荷**）→ D→H `FILE_DATA` → `FILE_FINISH[1]`/`[0]` 同上。

#### 4.7.3 目录

发送目录：host 递归枚举，逐文件走 4.7.1（`optionalName` 携带相对路径，daemon 端自动逐级建目录）；每文件一个 `FILE_FINISH[1]` 推进队列，最后一个后总 `FILE_FINISH[0]`。接收目录：镜像。`FILE_MODE(3006)/DIR_MODE(3007)` 一期不实现（Rust 世代不使用；C++ 世代仅在 `-m` 时出现）。

#### 4.7.4 应用安装/卸载

- 安装：H→D `APP_INIT(3500)` 载荷=`"<opts> <paths>"`（opts 原样透传给设备端 `bm install`；一期支持 `-r`）→ 逐包：`APP_CHECK(3501)` 载荷=TransferConfig{functionName="install", options=opts, optionalName=**9位随机串+原扩展名**, fileSize, clientCwd} → `APP_BEGIN(3502)` → `APP_DATA(3503)`（同 64B 槽格式）→ 完成时 daemon 回 `APP_FINISH(3504)` 载荷=`[mode u8][success u8][bm 输出文本]`（mode: 1=install 2=uninstall；success: 0/1）
- **目录安装**：host 先打包为 tar（512 字节 ustar 头，见下）再按单文件流发送，`optionalName` 扩展名 `.tar`
- 卸载：H→D `APP_UNINSTALL(3505)` 载荷=`"<opts> <package>"`（opts 透传；无 `-n` 时由 daemon 补）→ `APP_FINISH(mode=2)`
- tar 头：512B，`name[100] mode[8八进制] size[12八进制+NUL] chksum[8八进制] typeflag[1]`（'0'=文件 '5'=目录）`magic[6]="ustar "` `version[2]={0x20,0x00}` `prefix[155]`；校验和=除 chksum 外全字节和 +256；负载按 512 对齐补零。引用：`src\common\header.h:24-90`、`hdc_rust\src\tar\header.rs:212-325`

### 4.8 端口转发（fport/rport）

引用：`src\common\forward.cpp`、`src\host\host_forward.cpp`、`hdc_rust\src\common\forward.rs`

- 节点串：仅支持 `tcp:<port>`（一期）；`localabstract:` 等 unix 域族不做（Windows 无对应物，接口预留）
- 建立（以 fport `tcp:8080 tcp:9999` 为例，host 监听本地 8080，daemon 连其本机 9999）：
  1. H→D `FORWARD_INIT(2500)` 载荷=完整命令串
  2. H→D `FORWARD_CHECK(2501)` 载荷=`[8 字节 0][远端节点串]`；D→H `FORWARD_CHECK_RESULT(2502)` 载荷=`[1=可达]`
  3. H 本地 listen；每个入站连接分配新 cid（u32 大端）：H→D `FORWARD_ACTIVE_SLAVE(2503)` 载荷=`[cid u32 BE][8 字节 0][远端节点串]`；D→H `FORWARD_ACTIVE_MASTER(2504)` 空载荷
  4. 数据全双工：`FORWARD_DATA(2505)` 载荷=`[cid u32 BE][原始字节]`
  5. 任一端关闭：`FORWARD_FREE_CONTEXT(2506)` 载荷=`[cid u32 BE]`
- rport 镜像（daemon 侧监听，方向从命令串 `rport` 前缀推断；Rust 世代另有 2510-2512 专属 ID 但**从不上线**，host 仍发 2500）
- `FORWARD_SUCCESS(2509)` 载荷=`"<'1'fport|'0'rport>|<原命令串>"`：原版由 server 记账，本库仅用于校验/事件
- `fport ls/rm` 为原版 server 本地记账，本库以 `IForwardSession` 对象生命周期表达

### 4.9 Tlv32（仅 shell 选项 1200 用，C++ 世代专属）

```
[ tag u32 本机小端 ][ len u32 本机小端 ][ value ]
```

- `TAG_SHELL_CMD=0`（命令文本）、`TAG_SHELL_BUNDLE=1`（应用包名）；按 tag 升序输出
- 仅当 `DaemonGeneration==Cpp` 且用户显式使用 `shell -b` 类 API 时启用；引用：`src\host\host_shell_option.cpp:27-167`、`src\common\tlv.cpp:195-220`

### 4.10 Unity 命令载荷

| API | 命令 | 载荷 |
|---|---|---|
| ExecuteShellAsync(cmd) | 1001 | cmd 原文 |
| RemountAsync | 1002 | 空 |
| RebootAsync(mode) | 1003 | mode 串（`bootloader` 等；去前导 `-`） |
| SetRunModeAsync(port) | 1004 | `"port <n>"` / `"port close"` / `"usb"` |
| StreamHilogAsync | 1005 | 空（`-h` 帮助传 `"h"`） |
| RootRunAsync(unroot) | 1007 | `""` / `"r"` |
| CaptureBugReportAsync | 1011 | 空载荷发往 daemon；输出经 1012 分块回流（host 打开文件落盘由库完成） |

- hilog/bugreport 均为**长命命令**：输出经 10（ECHO_RAW）或 1012 流式返回，完成以通道关闭为准，天然适配 `IAsyncEnumerable`
- bugreport 的 1012 分块 host 侧收下后以字节流产出（落盘或转发由调用方决定）

### 4.11 通道关闭语义（完成信号，全命令通用）

- 跳数语义（`src\daemon\daemon.cpp:1251-1260`）：daemon 收到 CHANNEL_CLOSE 时**无论载荷值均先清任务**，载荷非 0 才递减回传
- daemon 主动关闭发 `[1]` → host 递减为 `[0]` 回发一次并终结通道（`src\host\server.cpp:916-918`）
- host 主动取消发 `[0]` → daemon 清任务且不回传
- shell 一次性命令完成判定 = 通道关闭（**退出码不上线**，两世代皆然——库如实不提供，文档注明可用 `echo $?` 变通）
- 未知 channelId 上收到命令 → 回 `CHANNEL_CLOSE[0]`（对齐原版 `server.cpp:875-899`）

## 5. 公共 API（最终签名）

```csharp
namespace HdcSharp;

public sealed class HdcHost : IAsyncDisposable
{
    public event EventHandler<DeviceStateChangedEventArgs>? DeviceStateChanged;   // 状态迁移（Connecting/Authorizing/Online/Offline）
    public event EventHandler<string>? AuthorizationRequested;                    // 设备端弹窗授权待人工确认（参数=connectKey）
    public event EventHandler<DeviceDisconnectedEventArgs>? DeviceDisconnected;  // 连接断开（含异常断开），设备已从注册表移除
    public IReadOnlyList<HdcDevice> ConnectedDevices { get; }
    public HdcDevice? FindDevice(string connectKey);
    public Task<HdcDevice> ConnectAsync(string endpoint, ConnectOptions? options = null, CancellationToken ct = default);
    public Task<bool> DisconnectAsync(string connectKey, CancellationToken ct = default);
    public ValueTask DisposeAsync();
}

public sealed class ConnectOptions
{
    public bool Heartbeat { get; init; } = true;          // 仅 C++ 世代生效
    public bool EnableEncryption { get; init; } = false;  // 二期 TLS-PSK
    public TimeSpan AuthTimeout { get; init; } = TimeSpan.FromMinutes(3.5);
    public TimeSpan OperationTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public IHostKeyStore? KeyStore { get; init; } = null; // 默认 ~/.harmony/hdckey
}

public sealed class HdcDevice
{
    public string ConnectKey { get; }                 // "ip:port"
    public string Endpoint { get; }                   // ConnectKey 的语义别名
    public string DeviceName { get; }                 // 握手 AUTH_OK 所得 devname
    public uint SessionId { get; }                    // 本次会话的随机 sessionId
    public HdcDeviceState State { get; }
    public DaemonGeneration Generation { get; }       // 能力指纹：决定心跳/命令可用性
    public Task<string> ExecuteShellAsync(string command, CancellationToken ct = default);
    public IAsyncEnumerable<byte[]> StreamShellOutputAsync(string command, CancellationToken ct = default);
    public Task<IInteractiveShell> OpenInteractiveShellAsync(CancellationToken ct = default);
    public Task SendFileAsync(string localPath, string remotePath, IProgress<FileProgress>? progress = null, CancellationToken ct = default);
    public Task ReceiveFileAsync(string remotePath, string localPath, IProgress<FileProgress>? progress = null, CancellationToken ct = default);
    public Task SendDirectoryAsync(string localDir, string remoteDir, IProgress<FileProgress>? progress = null, CancellationToken ct = default);
    public Task ReceiveDirectoryAsync(string remoteDir, string localDir, IProgress<FileProgress>? progress = null, CancellationToken ct = default);
    public Task<string> InstallAsync(string packagePath, InstallOptions? options = null, CancellationToken ct = default);   // 返回 bm 输出
    public Task<string> UninstallAsync(string packageName, UninstallOptions? options = null, CancellationToken ct = default);
    public Task<IForwardSession> ForwardTcpAsync(int localPort, int remotePort, CancellationToken ct = default);
    public Task<IForwardSession> ReverseTcpAsync(int remotePort, int localPort, CancellationToken ct = default);
    public IAsyncEnumerable<string> StreamHilogAsync(CancellationToken ct = default);
    public IAsyncEnumerable<byte[]> StreamBugReportAsync(CancellationToken ct = default);
    public Task RemountAsync(CancellationToken ct = default);
    public Task RebootAsync(RebootMode mode = RebootMode.Default, CancellationToken ct = default);
    public Task SetRunModeAsync(RunMode mode, CancellationToken ct = default);      // tcp端口/usb/关闭
    public Task RootRunAsync(bool unroot = false, CancellationToken ct = default);
    public event EventHandler<DeviceStateChangedEventArgs>? StateChanged;
}

public interface IInteractiveShell : IAsyncDisposable
{
    Stream Input { get; }                     // 写入=键入；0x03=SIGINT、0x04=退出（daemon 语义）
    ChannelReader<byte[]> Output { get; }     // PTY 原始字节（含回显/ANSI）
    Task WaitUntilClosedAsync(CancellationToken ct = default);
}

public interface IForwardSession : IAsyncDisposable
{
    int ListenPort { get; }
    ForwardDirection Direction { get; }
    bool IsActive { get; }
    event EventHandler? Closed;
}

public sealed class DeviceDisconnectedEventArgs : EventArgs
{
    public string Key { get; }                        // connectKey
}
```

其余支撑类型（`FileProgress{BytesTransferred,TotalBytes,FileName}`、`InstallOptions{Replace,Downgrade,Shared,...}`、`RebootMode`、`RunMode`、`HdcException{ErrorCode,Message,Level}`、`DaemonGeneration{Rust,Cpp,Unknown}`、`HdcDeviceState{Connecting,Authorizing,Online,Offline}`）随实现细化，但**公共签名以本节为冻结基线**。

## 6. 错误处理与取消

- 连接级失败（握手/banner/版本/认证拒绝）→ `ConnectAsync` 抛 `HdcException`（含 `[E....]` 原文与 Level）
- 运行期连接断开 → 所有活跃操作以 `HdcException(State=Offline)` 收尾；`HdcDevice.State` 转 Offline 并广播
- 操作级失败（daemon 报错回显 `[E003003]` 等）→ 该操作抛 `HdcException`，连接不受影响
- 取消：CT 贯穿；主动取消 shell/传输时 host 发 `CHANNEL_CLOSE[0]` 后清理通道
- 设备端弹窗授权等待 = `AuthorizationRequested` 事件（HdcHost 级）+ `ConnectOptions.AuthTimeout`；超时按认证失败处理
- 帧解析错误/vCode 不符/未知世代行为 → 连接立即关闭（对齐原版 ErrBufCheck 路径）

## 7. 认证与安全

### 7.1 密钥库

- 默认路径 `~/.harmony/hdckey`（PKCS#8 PEM 私钥）+ `hdckey.pub`（SPKI PEM 公钥），**与官方 hdc 完全共享**——已授权过的设备免二次弹窗
- 不存在时自动生成 RSA-3072（e=65537），PEM 写入；文件权限按平台尽量收紧（Windows：ACL 仅当前用户；Unix：0750/0600）
- `IHostKeyStore` 允许消费端替换（内存/自定义目录）

### 7.2 签名方案选择（自动）

- 握手 daemon 回包 `buf` 含 Tlv16 `authtype="1"` → **PSS+SHA512**：`RSA.SignData(token, SHA512, RSASignaturePadding.Pss)`（daemon 端 saltlen=auto 校验，兼容）
- 否则 → **PKCS1 私钥运算**（§7.3）
- Base64 编码后放 `buf`，authType=`AUTH_SIGNATURE(2)`

### 7.3 RsaPkcs1PrivateEncrypt（纯 C#，AOT 安全）

.NET RSA 类不暴露"私钥加密"原语（PKCS1 块类型 1 + 原始私钥幂运算），自实现：

1. `RSA.ExportParameters(true)` 取 P/Q/D/DP/DQ/InverseQ/N
2. 手工构造 PKCS1 v1.5 块类型 1：`0x00 0x01 PS(0xFF×k) 0x00 || token`（PS 长度=模长-3-|token|，token 最长 64B，充裕）
3. `BigInteger.ModPow` CRT 加速：`m1=blk^DP mod P`、`m2=blk^DQ mod Q`、`h=(InverseQ*(m1-m2)) mod P`、`sig=m2+h*Q`
4. 自校验：`BigInteger.ModPow(sig, e, N)` 应还原块（单测覆盖）

引用：`src\common\auth.cpp:921-954`（原版 RSA_private_encrypt PKCS1）、`hdc_rust\src\host\auth.rs:216-227`（rust-crate 同语义）。

### 7.4 TLS-PSK（二期，实验性）

- 触发条件：`ConnectOptions.EnableEncryption=true` → 握手 supportfeatures 声明 `encrypt_tcp` → C++ daemon 回 `AUTH_SSL_TLS_PSK(6)`，`buf`=Base64(RSA-OAEP-SHA1 加密的 32B PSK)；host `RSA.Decrypt(...,OaepSHA1)` 得 PSK
- 纯 C# 手写 TLS 1.3 PSK 客户端：仅 `TLS_AES_128_GCM_SHA256`，identity=`"Client_identity"`；实现 `psk_ke`（纯 PSK）与 `psk_dhe_ke`（PSK+ECDHE X25519）两种模式 + HKDF 调度（RFC 8446）；TLS 记录封装在 `HdcConnection` 帧泵两侧挂载
- daemon 不支持时（Rust 世代/未开 env）仍回 AUTH_SIGNATURE——库自动降级明文并发出 Info 事件
- 一期不做；接口占位于 `Security\TlsPsk\`

## 8. 测试策略

| 层 | 手段 | 工具 |
|---|---|---|
| 协议原语 | 黄金向量（§4.2 表 + TLV16/Tlv32/TarHeader 用例）+ 往返属性测试 | xUnit，零依赖 |
| 传输/帧 | 帧切碎重组（半帧/粘帧/跨读）、vCode 错误、大帧 | 内存流 Twin Piper |
| 认证 | FakeDaemon 实现 PKCS1/PSS 双校验 + 已知主机/弹窗超时路径 | 内嵌 TCP listener |
| 操作回环 | FakeDaemon 按 §4.7/4.8 剧本应答：单文件/目录/安装/转发/hilog 流 | 同上 |
| 真机集成 | `HDC_TEST_TARGET=ip:port` 环境变量启用（默认跳过）：connect→shell→param get→文件往返→hilog 截断流→install 冒烟 | Trait("RealDevice") |
| AOT/Trimming | 消费样例工程 `PublishAot=true` 编译门禁（CI 本地脚本） | dotnet publish |

FakeDaemon 放 `tests\HdcSharp.Tests\TestDoubles\`，**可配置世代（Rust/Cpp）**以覆盖指纹分支；不复用任何上游代码。

## 9. 里程碑（Ralph loop 迭代单位，每项含测试交付）

1. **脚手架**：解决方案、src/tests 工程、csproj 规范落地（AOT 分析器启用）、.gitignore、CI 脚本占位
2. **协议原语**：SerialStruct 读写器 + PayloadHead/PayloadProtect + 全消息结构 + Tlv16/Tlv32/TarHeader + 黄金向量测试
3. **传输层**：HdcConnection 帧泵（半帧/粘帧处理）、通道复用、CHANNEL_CLOSE 语义、日志钩子
4. **握手与认证**：HostKeyStore、AuthHandler（PSS/PKCS1/免认证/弹窗超时）、能力指纹 → **真机连通里程碑**
5. **Shell**：ExecuteShell/StreamShellOutput/InteractiveShell（含 Ctrl-C/Ctrl-D）
6. **文件**：send/recv 单文件与目录、进度、FILE_FINISH 队列推进
7. **应用**：install/uninstall、tar 打包器、bm 输出回传
8. **转发**：fport/rport + IForwardSession 生命周期
9. **Unity**：hilog/bugreport 流、reboot/remount/runmode/rootrun
10. **二期 TLS-PSK**（实验性，独立分支式推进）
11. **收尾**：XML 文档全覆盖检查、README、NuGet 包（`dotnet pack`）、样例工程

## 10. 风险与待真机验证项

| # | 事项 | 处置 |
|---|---|---|
| R1 | FILE_INIT 载荷首词格式（`"send ..."` 带不带前缀，两份报告表述有出入） | 里程碑 6 首日真机抓包验证；实现按 daemon 实测回退 |
| R2 | 真机 daemon 世代未知（端口 44221 非 10187，倾向 Rust） | 里程碑 4 用指纹实测；两世代路径均实现并测试 |
| R3 | 真机 auth 是否启用/是否弹窗 | 里程碑 4 验证；弹窗路径已设计 |
| R4 | 一次性 shell 输出分块大小与编码（UTF-8） | FakeDaemon + 真机双重覆盖 |
| R5 | rport 的 SUCCESS 载荷方向字节 | 里程碑 8 真机验证 |
| R6 | TLS-PSK 无 C++ daemon 真机可测 | 二期以自环测试为主，文档标注实验性 |
| R7 | `version` 串被 daemon 记录但可能参与日志/审计 | 固定 `Ver: 3.2.0f`+零哈希，风险可接受 |

## 11. 明确不做（Non-Goals）

USB/UART/蓝牙传输 · mDNS/UDP 发现 · flashd/jdwp/ark（仅命令字占位） · `-b` 沙箱 shell 与 `-m` 模式同步 · unix 域转发节点 · LZ4 压缩 · 加密私钥存储（huks/credential 服务端概念） · CLI 工具
