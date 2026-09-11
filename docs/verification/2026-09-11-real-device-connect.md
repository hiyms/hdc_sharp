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
