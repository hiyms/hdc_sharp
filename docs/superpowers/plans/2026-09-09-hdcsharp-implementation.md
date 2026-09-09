# HdcSharp 实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 纯 C# 的 OpenHarmony HDC 宿主侧协议库：通过 WiFi(TCP) 连接/认证/操控 OpenHarmony 设备（shell、文件、安装、转发、hilog 等）。

**Architecture:** 方案 A——连接为中心 + 薄注册表。每设备一条 TCP 连接（帧泵 + 通道多路复用），操作层在连接上开逻辑通道；`HdcHost` 仅做设备注册表与事件。协议层（无 IO）与传输层严格分离。

**Tech Stack:** .NET 8（LTS）、`System.IO.Pipelines`、`System.Threading.Channels`、xUnit。零其它依赖。

**Spec:** `docs/superpowers/specs/2026-09-09-hdcsharp-design.md`（本计划从 spec 出发，执行者必须同时读 spec；协议细节以 spec §4 为准，本计划只复述关键常量）

**范围说明:** spec 里程碑 10（TLS-PSK 加密通道）不在本计划内，届时另立计划。里程碑 1-9 + 11 对应本计划 Task 1-20。

## Global Constraints（每个任务隐含遵守）

- 目标框架 `net8.0`；依赖白名单仅 `System.IO.Pipelines 8.*` 与 `System.Threading.Channels`（后者 in-box，不显式引用）
- **注释三分法（强制）**：所有公共 API 必须有中文 `///` XML 文档注释；平台注释少量按需、只解释为什么；**其它注释一律禁止**
- AOT/Trimming：无反射/`dynamic`/`Emit`；主 csproj 设 `<IsAotCompatible>true</IsAotCompatible>`、`<EnableTrimAnalyzer>true</EnableTrimAnalyzer>`、`<EnableAotAnalyzer>true</EnableAotAnalyzer>`
- 全公共异步 API 以 `Async` 结尾且末参数 `CancellationToken ct = default`
- 协议常量以 spec §4 为准；两世代（Rust `Ver: 3.0.0*` / C++ `Ver: 3.2.0*`）行为差异必须按指纹分支
- 每个任务：先写失败测试 → 跑失败 → 最小实现 → 跑通过 → `git commit`（提交信息用中文，格式 `feat|test|chore: 描述`）
- 测试命名：`Method_Scenario_Expectation`（英文），文件名 `XxxTests.cs`
- 禁止复用上游任何 C++/Rust 代码；禁止引用 `D:\work\developtools_hdc` 运行时

## 协议速查卡（实现时对照，详细语义见 spec §4）

```
帧 = PayloadHead(11B) + Protect(serial_struct) + payload
PayloadHead: 'H''W' 00 00 01 | headSize u16 大端 | dataSize u32 大端
Protect 字段: 1 channelId varint / 2 commandFlag varint / 3 checkSum varint(恒0) / 4 vCode varint(恒0x09)
serial_struct: tag=(field<<3)|wireType 以 varint 输出; string=tag+varint长度+字节;
               所有标量(含0)与字符串(含空)一律输出; 解析不容忍未知 tag
Tlv16: tag 16B 空格右填充 + len 十进制 ASCII 16B 空格右填充 + value
Tlv32(仅1200): tag u32 LE + len u32 LE + value, 按 tag 升序
命令字: 1 HANDSHAKE 2 CHANNEL_CLOSE 9 ECHO 10 ECHO_RAW 12 WAKEUP(忽略)
       1001 UNITY_EXECUTE 1002 REMOUNT 1003 REBOOT 1004 RUNMODE 1005 HILOG 1007 ROOTRUN
       1011 BUGREPORT_INIT 1012 BUGREPORT_DATA 2000 SHELL_INIT 2001 SHELL_DATA
       2500-2509 FORWARD_* 3000-3007 FILE_* 3500-3505 APP_* 5000 HEARTBEAT(仅C++世代)
通道关闭: daemon发[1]→host减为[0]回发一次并终结; host主动取消发[0](daemon无论值均清任务)
心跳: 仅Cpp世代且双方声明heartbeat, 5s, HeartbeatMsg{1:count u64, 2:""}
握手: host先发(1) SessionHandShake{banner"OHOS HDC",authType0,sessionId rand,connectKey"ip:port",
      buf=Tlv16(authtype"1")+Tlv16(supportfeatures"Ver: 3.2.0f,TCP,<os>[,heartbeat]"), version"Ver: 3.2.0f"+16×'0'}
认证: daemon→3(PUBLICKEY)[buf可能含Tlv16 authtype] → host→3 buf=hostname\x0C+PEM
      → daemon→2 buf=token(Cpp:20字符/Rust:64字符) → host→2 buf=Base64(签名)
      [daemon有authtype=1→PSS+SHA512; 无→PKCS1私钥运算] → daemon→4(OK) TLV{devname,daemonauthstatus,...}
文件: INIT(ASCII"send <opts> <local> <remote>"/"recv <remote> <local>")→CHECK(TransferConfig)
      →BEGIN(空或8B)→DATA(64B槽:TransferPayload变长序列化后补0到64B + 数据≤48KiB)→FINISH[1]每文件/[0]总
安装: INIT"<opts> <paths>"→CHECK(TransferConfig{functionName="install",optionalName=随机9+原扩展名})
      →BEGIN→DATA→FINISH[mode u8][success u8][bm输出]
转发: INIT(命令串)→CHECK[8B0][远端节点]→CHECK_RESULT[1]→(每连接)ACTIVE_SLAVE[cid BE][8B0][节点]
      →ACTIVE_MASTER→DATA[cid BE][字节]⇄→FREE_CONTEXT[cid BE]
unity: REBOOT=mode串(去'-') / RUNMODE="port n"|"port close"|"usb" / ROOTRUN=""|"r" / HILOG=""|"h"
```

---

### Task 1: 解决方案脚手架

**Files:**
- Create: `HdcSharp.sln`、`src/HdcSharp/HdcSharp.csproj`、`tests/HdcSharp.Tests/HdcSharp.Tests.csproj`、`.gitignore`、`Directory.Build.props`
- Test: 编译 + 空测试运行

**Interfaces:**
- Produces: 可构建的解决方案；后续所有任务的容器

- [ ] **Step 1: 创建 .gitignore**

```gitignore
bin/
obj/
*.user
.vs/
artifacts/
*.binlog
```

- [ ] **Step 2: 创建 Directory.Build.props**

```xml
<Project>
  <PropertyGroup>
    <LangVersion>12.0</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <AnalysisLevel>latest-recommended</AnalysisLevel>
    <GenerateDocumentationFile>true</GenerateDocumentationFile>
    <NoWarn>$(NoWarn);CS1591</NoWarn>
  </PropertyGroup>
</Project>
```

- [ ] **Step 3: 创建 src/HdcSharp/HdcSharp.csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <RootNamespace>HdcSharp</RootNamespace>
    <IsAotCompatible>true</IsAotCompatible>
    <EnableAotAnalyzer>true</EnableAotAnalyzer>
    <EnableTrimAnalyzer>true</EnableTrimAnalyzer>
    <Version>0.1.0</Version>
    <PackageId>HdcSharp</PackageId>
    <Description>纯 C# 实现的 OpenHarmony HDC 宿主侧协议库（WiFi/TCP）。</Description>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="System.IO.Pipelines" Version="8.0.0" />
  </ItemGroup>
</Project>
```

- [ ] **Step 4: 创建 tests/HdcSharp.Tests/HdcSharp.Tests.csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" PrivateAssets="all" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\HdcSharp\HdcSharp.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 5: 生成解决方案并验证**

```bash
cd /d/work/hdc_sharp
dotnet new sln -n HdcSharp
dotnet sln add src/HdcSharp tests/HdcSharp.Tests
dotnet build
```
预期：Build succeeded（测试项目暂无测试文件时加一个占位 `SmokeTests.cs`，断言 `true`）

- [ ] **Step 6: Commit**

```bash
git add -A && git commit -m "chore: 解决方案脚手架（net8.0/AOT 分析器/xUnit）"
```

---

### Task 2: 协议常量与命令枚举

**Files:**
- Create: `src/HdcSharp/Protocol/HdcCommand.cs`、`src/HdcSharp/Protocol/HdcConstants.cs`、`src/HdcSharp/Protocol/HdcException.cs`
- Test: `tests/HdcSharp.Tests/Protocol/HdcConstantsTests.cs`

**Interfaces:**
- Produces: `enum HdcCommand : uint`（spec §4.5 全部值）；`static class HdcConstants`（下表常量）；`sealed class HdcException : Exception`（`string? ErrorCode`、`MessageLevel Level` 属性）；`enum MessageLevel { Fail, Info, Ok }`

- [ ] **Step 1: 写失败测试**

```csharp
using HdcSharp.Protocol;
using Xunit;

namespace HdcSharp.Tests.Protocol;

public class HdcConstantsTests
{
    [Theory]
    [InlineData(HdcCommand.KernelHandshake, 1u)]
    [InlineData(HdcCommand.KernelChannelClose, 2u)]
    [InlineData(HdcCommand.KernelEcho, 9u)]
    [InlineData(HdcCommand.KernelEchoRaw, 10u)]
    [InlineData(HdcCommand.KernelWakeupSlavetask, 12u)]
    [InlineData(HdcCommand.UnityExecute, 1001u)]
    [InlineData(HdcCommand.UnityRemount, 1002u)]
    [InlineData(HdcCommand.UnityReboot, 1003u)]
    [InlineData(HdcCommand.UnityRunmode, 1004u)]
    [InlineData(HdcCommand.UnityHilog, 1005u)]
    [InlineData(HdcCommand.UnityRootrun, 1007u)]
    [InlineData(HdcCommand.UnityBugreportInit, 1011u)]
    [InlineData(HdcCommand.UnityBugreportData, 1012u)]
    [InlineData(HdcCommand.ShellInit, 2000u)]
    [InlineData(HdcCommand.ShellData, 2001u)]
    [InlineData(HdcCommand.ForwardInit, 2500u)]
    [InlineData(HdcCommand.ForwardCheck, 2501u)]
    [InlineData(HdcCommand.ForwardCheckResult, 2502u)]
    [InlineData(HdcCommand.ForwardActiveSlave, 2503u)]
    [InlineData(HdcCommand.ForwardActiveMaster, 2504u)]
    [InlineData(HdcCommand.ForwardData, 2505u)]
    [InlineData(HdcCommand.ForwardFreeContext, 2506u)]
    [InlineData(HdcCommand.ForwardList, 2507u)]
    [InlineData(HdcCommand.ForwardRemove, 2508u)]
    [InlineData(HdcCommand.ForwardSuccess, 2509u)]
    [InlineData(HdcCommand.FileInit, 3000u)]
    [InlineData(HdcCommand.FileCheck, 3001u)]
    [InlineData(HdcCommand.FileBegin, 3002u)]
    [InlineData(HdcCommand.FileData, 3003u)]
    [InlineData(HdcCommand.FileFinish, 3004u)]
    [InlineData(HdcCommand.FileMode, 3006u)]
    [InlineData(HdcCommand.DirMode, 3007u)]
    [InlineData(HdcCommand.AppInit, 3500u)]
    [InlineData(HdcCommand.AppCheck, 3501u)]
    [InlineData(HdcCommand.AppBegin, 3502u)]
    [InlineData(HdcCommand.AppData, 3503u)]
    [InlineData(HdcCommand.AppFinish, 3504u)]
    [InlineData(HdcCommand.AppUninstall, 3505u)]
    [InlineData(HdcCommand.HeartbeatMsg, 5000u)]
    public void CommandValues_MatchWireProtocol(HdcCommand cmd, uint expected) =>
        Assert.Equal(expected, (uint)cmd);

    [Fact]
    public void Constants_MatchSpecValues()
    {
        Assert.Equal("HW", HdcConstants.PacketFlag);
        Assert.Equal(0x01, HdcConstants.ProtocolVer);
        Assert.Equal(0x09, HdcConstants.PayloadVCode);
        Assert.Equal("OHOS HDC", HdcConstants.HandshakeMessage);
        Assert.Equal("HS FAILED", HdcConstants.HandshakeFailed);
        Assert.Equal("Ver: 3.2.0f", HdcConstants.HostVersion);
        Assert.Equal("Ver: 3.0.0b", HdcConstants.MinDaemonVersion);
        Assert.Equal(0x0C, HdcConstants.HostDaemonBufSeparator);
        Assert.Equal(11, HdcConstants.PayloadHeadSize);
        Assert.Equal(64, HdcConstants.TransferSlotSize);
        Assert.Equal(49152, HdcConstants.MaxFileChunkSize);
        Assert.Equal(5, HdcConstants.HeartbeatIntervalSeconds);
    }
}
```

- [ ] **Step 2: 跑失败**

Run: `dotnet test tests/HdcSharp.Tests`
Expected: 编译错误（类型不存在）

- [ ] **Step 3: 实现**

`HdcCommand`：`public enum HdcCommand : uint`，成员名与值如上测试（每个成员带中文 `///`）。`HdcConstants`：`const string PacketFlag = "HW"`、`const byte ProtocolVer = 0x01`、`const byte PayloadVCode = 0x09`、`const string HandshakeMessage = "OHOS HDC"`、`const string HandshakeFailed = "HS FAILED"`、`const string HostVersion = "Ver: 3.2.0f"`、`const string MinDaemonVersion = "Ver: 3.0.0b"`、`const byte HostDaemonBufSeparator = 0x0C`、`const int PayloadHeadSize = 11`、`const int TransferSlotSize = 64`、`const int MaxFileChunkSize = 49152`、`const int HeartbeatIntervalSeconds = 5`、`const string TlvAuthType = "authtype"`、`const string TlvSupportFeatures = "supportfeatures"`、`const string TlvDevName = "devname"`、`const string TlvDaemonAuthStatus = "daemonauthstatus"`、`const string TlvEmgMsg = "emgmsg"`、`const string AuthStatusSuccess = "SUCCESS"`、`const string AuthStatusUnauth = "DAEMON_UNAUTH"`。`HdcException(string message, string? errorCode = null, MessageLevel level = MessageLevel.Fail)`。

- [ ] **Step 4: 跑通过** `dotnet test tests/HdcSharp.Tests` → PASS
- [ ] **Step 5: Commit** `git add -A && git commit -m "feat: 协议常量与命令枚举"`

---

### Task 3: serial_struct 读写器

**Files:**
- Create: `src/HdcSharp/Protocol/SerialStruct/SerialWriter.cs`、`src/HdcSharp/Protocol/SerialStruct/SerialReader.cs`
- Test: `tests/HdcSharp.Tests/Protocol/SerialStructTests.cs`

**Interfaces:**
- Produces:
  - `SerialWriter`：`void WriteVarint(ulong value)`；`void WriteTag(int field, byte wireType)`；`void WriteStringField(int field, string value)`（空串也写）；`void WriteVarintField(int field, ulong value)`（0 也写）；`byte[] ToArray()`
  - `SerialReader`（构造参数 `ReadOnlySpan<byte> data`）：`bool ReadTag(out int field, out byte wireType)`；`ulong ReadVarint()`；`string ReadString()`；`int Field { get; }`（当前游标所在字段号，无更多时 -1）
  - wireType 常量：`WireType.Varint=0`、`WireType.Len=2`

- [ ] **Step 1: 写失败测试**

```csharp
using HdcSharp.Protocol.SerialStruct;
using Xunit;

namespace HdcSharp.Tests.Protocol;

public class SerialStructTests
{
    [Fact]
    public void Varint_SmallValue_SingleByte()
    {
        var w = new SerialWriter();
        w.WriteVarint(42);
        Assert.Equal(new byte[] { 0x2A }, w.ToArray());
    }

    [Fact]
    public void Varint_MultiByte_Leb128()
    {
        var w = new SerialWriter();
        w.WriteVarint(300);
        Assert.Equal(new byte[] { 0xAC, 0x02 }, w.ToArray());
    }

    [Fact]
    public void Varint_Zero_EmitsByte()
    {
        var w = new SerialWriter();
        w.WriteVarint(0);
        Assert.Equal(new byte[] { 0x00 }, w.ToArray());
    }

    [Fact]
    public void StringField_Empty_StillEmitted()
    {
        var w = new SerialWriter();
        w.WriteStringField(5, "");
        Assert.Equal(new byte[] { 0x2A, 0x00 }, w.ToArray());
    }

    [Fact]
    public void VarintField_Zero_StillEmitted()
    {
        var w = new SerialWriter();
        w.WriteVarintField(2, 0);
        Assert.Equal(new byte[] { 0x10, 0x00 }, w.ToArray());
    }

    [Fact]
    public void PayloadProtect_GoldenBytes() // spec §4.2 黄金向量
    {
        var w = new SerialWriter();
        w.WriteVarintField(1, 42).WriteVarintField(2, 9).WriteVarintField(3, 0).WriteVarintField(4, 9);
        Assert.Equal(new byte[] { 0x08, 0x2A, 0x10, 0x09, 0x18, 0x00, 0x20, 0x09 }, w.ToArray());
    }

    [Fact]
    public void Reader_RoundTrip_TagsAndValues()
    {
        var w = new SerialWriter();
        w.WriteVarintField(1, 42).WriteStringField(5, "OHOS HDC").WriteVarintField(3, 0);
        var r = new SerialReader(w.ToArray());
        Assert.True(r.ReadTag(out var f1, out var t1));
        Assert.Equal((1, WireType.Varint), (f1, t1));
        Assert.Equal(42UL, r.ReadVarint());
        Assert.True(r.ReadTag(out var f2, out var t2));
        Assert.Equal((5, WireType.Len), (f2, t2));
        Assert.Equal("OHOS HDC", r.ReadString());
        Assert.True(r.ReadTag(out var f3, out _));
        Assert.Equal(3, f3);
        Assert.Equal(0UL, r.ReadVarint());
        Assert.False(r.ReadTag(out _, out _));
    }
}
```

- [ ] **Step 2: 跑失败** → 编译错误
- [ ] **Step 3: 实现**

`SerialWriter` 内部 `List<byte>`（或 ArrayBufferWriter）；varint 循环：`while (v >= 0x80) { emit((byte)(v | 0x80)); v >>= 7; } emit((byte)v);`。`WriteTag(field, wt)` = `WriteVarint((uint)((field << 3) | wt))`。`WriteStringField` = tag + `WriteVarint((ulong)utf8Len)` + UTF-8 字节。所有方法返回 `SerialWriter`（链式）。`SerialReader` 顺序解析：`ReadTag` 读 varint 得 key，`field = key >> 3`，`wireType = key & 7`；`ReadString` 读 varint 长度再截取 UTF-8 解码。**解析不容忍未知 tag**：调用方按字段号分发，`ReadString`/`ReadVarint` 按当前 wireType 消费（错误 wireType 抛 `HdcException`）。

- [ ] **Step 4: 跑通过** → PASS
- [ ] **Step 5: Commit** `git add -A && git commit -m "feat: serial_struct 协议读写器（含黄金向量）"`

---

### Task 4: 线缆消息结构体

**Files:**
- Create: `src/HdcSharp/Protocol/Messages/PayloadProtect.cs`、`SessionHandShake.cs`、`HeartbeatMsg.cs`、`TransferConfig.cs`、`TransferPayload.cs`、`FileMode.cs`（全部 `internal sealed class`，含 `WriteTo(SerialWriter)` 与静态 `Parse(ReadOnlySpan<byte>)`）
- Test: `tests/HdcSharp.Tests/Protocol/MessagesTests.cs`

**Interfaces:**
- Consumes: Task 3 读写器
- Produces（tag 表严格按 spec §4.3）:
  - `PayloadProtect(uint ChannelId, HdcCommand Command)` → 字节；`PayloadProtect.Parse`（忽略 checkSum/vCode 之外校验 vCode 由帧层做）
  - `SessionHandShake`：`string Banner`、`byte AuthType`、`uint SessionId`、`string ConnectKey`、`string Buf`、`string Version`，字段号 1-6
  - `HeartbeatMsg`：`ulong Count`，字段 1-2（2 恒空串）
  - `TransferConfig`：13 字段 `ulong FileSize, Atime, Mtime` / `string Options, Path, OptionalName, FunctionName, ClientCwd, Reserve1, Reserve2` / `bool UpdateIfNew, HoldTimestamp` / `byte CompressType`
  - `TransferPayload`：`ulong Index; uint CompressSize, UncompressSize; byte CompressType`
  - `FileMode`：`ulong Perm, Uid, Gid; string Context, FullName`

- [ ] **Step 1: 写失败测试**

```csharp
using HdcSharp.Protocol.Messages;
using Xunit;

namespace HdcSharp.Tests.Protocol;

public class MessagesTests
{
    [Fact]
    public void SessionHandShake_GoldenBytes() // spec §4.2 向量2
    {
        var m = new SessionHandShake { Banner = "OHOS HDC", AuthType = 0, SessionId = 1, ConnectKey = "", Buf = "", Version = "" };
        var expected = new byte[]
        {
            0x0A, 0x08, (byte)'O', (byte)'H', (byte)'O', (byte)'S', (byte)' ', (byte)'H', (byte)'D', (byte)'C',
            0x10, 0x00, 0x18, 0x01, 0x22, 0x00, 0x2A, 0x00, 0x32, 0x00
        };
        Assert.Equal(expected, m.Serialize());
    }

    [Fact]
    public void HeartbeatMsg_GoldenBytes() // spec §4.2 向量3
    {
        Assert.Equal(new byte[] { 0x08, 0x00, 0x12, 0x00 }, new HeartbeatMsg { Count = 0 }.Serialize());
    }

    [Fact]
    public void TransferConfig_RoundTrip_AllFields()
    {
        var c = new TransferConfig { FileSize = 12345, Path = "/data/x", OptionalName = "y.log", UpdateIfNew = true, CompressType = 0, FunctionName = "install" };
        var parsed = TransferConfig.Parse(c.Serialize());
        Assert.Equal(c.FileSize, parsed.FileSize);
        Assert.Equal(c.Path, parsed.Path);
        Assert.Equal(c.OptionalName, parsed.OptionalName);
        Assert.Equal(c.UpdateIfNew, parsed.UpdateIfNew);
        Assert.Equal(c.FunctionName, parsed.FunctionName);
    }

    [Fact]
    public void TransferPayload_RoundTrip()
    {
        var p = new TransferPayload { Index = 98304, CompressType = 0, CompressSize = 1024, UncompressSize = 1024 };
        var parsed = TransferPayload.Parse(p.Serialize());
        Assert.Equal(98304UL, parsed.Index);
        Assert.Equal(1024u, parsed.CompressSize);
    }
}
```

- [ ] **Step 2: 跑失败** → 编译错误
- [ ] **Step 3: 实现** 每结构体 `Serialize()` 返回 `byte[]`（内部 SerialWriter 按字段号 1..N 顺序写，全部字段恒写）；`Parse` 按 spec §4.3 tag 分发（未出现字段保持默认值）
- [ ] **Step 4: 跑通过** → PASS
- [ ] **Step 5: Commit** `git add -A && git commit -m "feat: 线缆消息结构体（握手/心跳/传输配置/负载/文件模式）"`

---

### Task 5: PayloadHead 与帧编解码器

**Files:**
- Create: `src/HdcSharp/Protocol/FrameCodec.cs`
- Test: `tests/HdcSharp.Tests/Protocol/FrameCodecTests.cs`

**Interfaces:**
- Consumes: Task 2 常量、Task 4 PayloadProtect
- Produces:
  - `static class FrameCodec`：
    - `byte[] Encode(uint channelId, HdcCommand cmd, ReadOnlySpan<byte> payload)`（返回完整帧）
    - `void Encode(uint channelId, HdcCommand cmd, ReadOnlySpan<byte> payload, IBufferWriter<byte> sink)`
    - `FrameDecoder`（类）：`void Append(ReadOnlySpan<byte> data)`（喂入任意切分的 TCP 字节流）；`bool TryRead(out Frame frame)`（`record struct Frame(uint ChannelId, HdcCommand Command, byte[] Payload)`）；帧不完整时返回 false 保留缓冲
  - 语义：flag/protocolVer/vCode 校验失败抛 `HdcException`（连接必须终止）；单帧总长 > 1MiB-1KiB 抛 `HdcException`

- [ ] **Step 1: 写失败测试**

```csharp
using HdcSharp.Protocol;
using Xunit;

namespace HdcSharp.Tests.Protocol;

public class FrameCodecTests
{
    [Fact]
    public void Encode_WorkedExample_MatchesSpec() // spec §2.3：channelId=42, ECHO(9), payload "hi"
    {
        var frame = FrameCodec.Encode(42, HdcCommand.KernelEcho, "hi"u8);
        var expected = new byte[]
        {
            0x48, 0x57, 0x00, 0x00, 0x01, 0x00, 0x08, 0x00, 0x00, 0x00, 0x02,
            0x08, 0x2A, 0x10, 0x09, 0x18, 0x00, 0x20, 0x09,
            (byte)'h', (byte)'i'
        };
        Assert.Equal(expected, frame);
    }

    [Fact]
    public void Decoder_ReassemblesSplitFrames()
    {
        var frame = FrameCodec.Encode(42, HdcCommand.KernelEcho, "hi"u8);
        var dec = new FrameDecoder();
        dec.Append(frame.AsSpan(0, 7));                 // 半帧
        Assert.False(dec.TryRead(out _));
        dec.Append(frame.AsSpan(7));                    // 剩余
        Assert.True(dec.TryRead(out var f));
        Assert.Equal(42u, f.ChannelId);
        Assert.Equal(HdcCommand.KernelEcho, f.Command);
        Assert.Equal("hi"u8.ToArray(), f.Payload);
        Assert.False(dec.TryRead(out _));
    }

    [Fact]
    public void Decoder_HandlesCoalescedFrames()
    {
        var a = FrameCodec.Encode(1, HdcCommand.KernelEcho, "a"u8);
        var b = FrameCodec.Encode(2, HdcCommand.KernelEchoRaw, "bb"u8);
        var dec = new FrameDecoder();
        dec.Append(a.AsSpan().ToArray().Concat(b.AsSpan().ToArray()).ToArray());
        Assert.True(dec.TryRead(out var f1) && f1.ChannelId == 1);
        Assert.True(dec.TryRead(out var f2) && f2.ChannelId == 2 && f2.Payload.Length == 2);
    }

    [Fact]
    public void Decoder_BadVCode_Throws()
    {
        var frame = FrameCodec.Encode(42, HdcCommand.KernelEcho, "hi"u8);
        frame[17] = 0x00;                               // 破坏 vCode（偏移 11+6）
        var dec = new FrameDecoder();
        dec.Append(frame);
        Assert.Throws<HdcException>(() => dec.TryRead(out _));
    }
}
```

- [ ] **Step 2: 跑失败** → 编译错误
- [ ] **Step 3: 实现** `Encode`：写 11B 头（headSize=8 恒定因 Protect 恒 4 字段 varint——**注意** channelId > 2^28 时 Protect 变长，实现必须先序列化 Protect 再填 headSize，不要硬编码 8）；Decoder 内部 `PipeWriter`/`List<byte>` 累积，`TryRead` 按头部长度判断完整性，成功后移除已消费字节
- [ ] **Step 4: 跑通过** → PASS
- [ ] **Step 5: Commit** `git add -A && git commit -m "feat: 帧编解码器（半帧重组/粘帧/校验）"`

---

### Task 6: Tlv16（握手 buf 编解码）

**Files:**
- Create: `src/HdcSharp/Protocol/Tlv16.cs`
- Test: `tests/HdcSharp.Tests/Protocol/Tlv16Tests.cs`

**Interfaces:**
- Produces: `static class Tlv16`：`void Append(IBufferWriter<byte>/List<byte> sink, string tag, string value)`；`Dictionary<string, string> Parse(string buf)`；`string Serialize(IEnumerable<(string Tag, string Value)> items)`
  - 语义：tag/len 均 16 字节 ASCII 空格右填充；len=十进制；超长 tag/值>1024 抛 `HdcException`

- [ ] **Step 1: 写失败测试**

```csharp
using HdcSharp.Protocol;
using Xunit;

namespace HdcSharp.Tests.Protocol;

public class Tlv16Tests
{
    [Fact]
    public void Append_AuthType_MatchesSpecExample() // spec §4.4：33 字节
    {
        var bytes = Tlv16.Encode(("authtype", "1"));
        Assert.Equal(33, bytes.Length);
        Assert.Equal("authtype        ", System.Text.Encoding.ASCII.GetString(bytes, 0, 16));
        Assert.Equal("1               ", System.Text.Encoding.ASCII.GetString(bytes, 16, 16));
        Assert.Equal("1", System.Text.Encoding.ASCII.GetString(bytes, 32, 1));
    }

    [Fact]
    public void Parse_RoundTrip_MultipleEntries()
    {
        var buf = Tlv16.Serialize(new[] { ("authtype", "1"), ("supportfeatures", "Ver: 3.2.0f,TCP,win,heartbeat") });
        var map = Tlv16.Parse(buf);
        Assert.Equal("1", map["authtype"]);
        Assert.Equal("Ver: 3.2.0f,TCP,win,heartbeat", map["supportfeatures"]);
    }
}
```

- [ ] **Step 2: 跑失败** → 编译错误
- [ ] **Step 3: 实现**：`PadRight(s, 16)`（tag 超 16 字节抛异常）；Parse 循环 `while (pos + 32 <= buf.Length)`，`int.Parse(len.Trim())`
- [ ] **Step 4: 跑通过** → PASS
- [ ] **Step 5: Commit** `git add -A && git commit -m "feat: Tlv16 编解码"`

---

### Task 7: Tlv32（shell 选项，C++ 世代）

**Files:**
- Create: `src/HdcSharp/Protocol/Tlv32.cs`
- Test: `tests/HdcSharp.Tests/Protocol/Tlv32Tests.cs`

**Interfaces:**
- Produces: `static class Tlv32`：`byte[] Serialize(IReadOnlyDictionary<uint, byte[]> entries)`（按 key 升序）；`Dictionary<uint, byte[]> Parse(ReadOnlySpan<byte> data)`
  - 常量：`TagShellCmd = 0`、`TagShellBundle = 1`；布局 `[tag u32 LE][len u32 LE][value]`

- [ ] **Step 1: 写失败测试**

```csharp
using HdcSharp.Protocol;
using Xunit;

namespace HdcSharp.Tests.Protocol;

public class Tlv32Tests
{
    [Fact]
    public void Serialize_AscendingTagOrder_LeLayout()
    {
        var data = Tlv32.Serialize(new Dictionary<uint, byte[]> { [1] = "com.demo"u8.ToArray(), [0] = "ls"u8.ToArray() });
        // tag0 在前：0,0,0,0 | 2,0,0,0 | 'l','s' | 1,0,0,0 | 8,0,0,0 | "com.demo"
        var expected = new byte[] { 0,0,0,0, 2,0,0,0, (byte)'l', (byte)'s', 1,0,0,0, 8,0,0,0,
            (byte)'c',(byte)'o',(byte)'m',(byte)'.',(byte)'d',(byte)'e',(byte)'m',(byte)'o' };
        Assert.Equal(expected, data);
    }
}
```

- [ ] **Step 2: 跑失败** → 编译错误
- [ ] **Step 3: 实现**（`BinaryPrimitives.WriteUInt32LittleEndian`）
- [ ] **Step 4: 跑通过** → PASS
- [ ] **Step 5: Commit** `git add -A && git commit -m "feat: Tlv32 编解码（shell 选项）"`

---

### Task 8: Tar 打包/解包（目录安装用）

**Files:**
- Create: `src/HdcSharp/Protocol/Tar/TarHeader.cs`、`src/HdcSharp/Protocol/Tar/TarWriter.cs`、`src/HdcSharp/Protocol/Tar/TarReader.cs`
- Test: `tests/HdcSharp.Tests/Protocol/TarTests.cs`

**Interfaces:**
- Consumes: Task 2 常量
- Produces:
  - `TarHeader`：`static void Write(IBufferWriter<byte> sink, string name, long size, TarEntryType type)`；`static TarHeaderInfo Parse(ReadOnlySpan<byte> block)`；`enum TarEntryType { NormalFile = '0', Directory = '5' }`；`readonly record struct TarHeaderInfo(string Name, long Size, TarEntryType Type)`
  - `TarWriter`：`void AddFile(string entryName, Stream content)`、`void AddDirectory(string entryName)`、`void Finish(Stream output)`（写两个 512B 零块）
  - `TarReader`：`bool TryReadEntry(out TarHeaderInfo info)`、`int ReadContent(Span<byte> buffer)`、`void SkipToNextEntry()`
  - 布局：`name[100] mode[8] uid[8] gid[8] size[12 八进制+NUL] mtime[12] chksum[8] typeflag[1] linkname[100] magic[6]="ustar " version[2]={0x20,0x00} uname[32] gname[32] devmajor[8] devminor[8] prefix[155] pad[12]`；校验和=全 512B（chksum 字段按 8 空格计）和 + 256，八进制写入；文件名 >100B 时拆 name/prefix（prefix/ 分隔）

- [ ] **Step 1: 写失败测试**

```csharp
using HdcSharp.Protocol.Tar;
using Xunit;

namespace HdcSharp.Tests.Protocol;

public class TarTests
{
    [Fact]
    public void Header_ChecksumIsSumPlus256_Octal()
    {
        var block = new byte[512];
        TarHeader.WriteTo(block, "a.hap", 5, TarEntryType.NormalFile);
        int sum = 0;
        for (int i = 148; i < 156; i++) { sum += 0x20; }   // chksum 字段按空格计
        for (int i = 0; i < 512; i++) if (i < 148 || i >= 156) sum += block[i];
        var chksumStr = System.Text.Encoding.ASCII.GetString(block, 148, 8).TrimEnd('\0', ' ');
        Assert.Equal(Convert.ToString(sum + 256, 8), chksumStr);
    }

    [Fact]
    public void WriterReader_RoundTrip()
    {
        using var ms = new MemoryStream();
        var w = new TarWriter(ms);
        w.AddDirectory("dir");
        w.AddFile("dir/a.txt", new MemoryStream("hello"u8.ToArray()));
        w.Finish();
        ms.Position = 0;
        var r = new TarReader(ms);
        Assert.True(r.TryReadEntry(out var e1));
        Assert.Equal(TarEntryType.Directory, e1.Type);
        Assert.True(r.TryReadEntry(out var e2));
        var buf = new byte[16];
        Assert.Equal(5, r.ReadContent(buf));
        Assert.Equal("hello"u8.ToArray(), buf.AsSpan(0, 5).ToArray());
        Assert.False(r.TryReadEntry(out _));
    }
}
```

- [ ] **Step 2: 跑失败** → 编译错误
- [ ] **Step 3: 实现**：size 字段格式 `Convert.ToString(size, 8).PadLeft(11, '0') + "\0"`；内容 512 对齐补零；解包容忍GNU 风格 magic `ustar `（0x20 结尾）与 `ustar\0`
- [ ] **Step 4: 跑通过** → PASS
- [ ] **Step 5: Commit** `git add -A && git commit -m "feat: ustar tar 打包/解包"`

---

### Task 9: 主机密钥库

**Files:**
- Create: `src/HdcSharp/Security/IHostKeyStore.cs`、`src/HdcSharp/Security/FileHostKeyStore.cs`
- Test: `tests/HdcSharp.Tests/Security/FileHostKeyStoreTests.cs`

**Interfaces:**
- Produces:
  - `interface IHostKeyStore`：`System.Security.Cryptography.RSA GetPrivateKey()`；`string GetPublicKeyPem()`（SPKI `-----BEGIN PUBLIC KEY-----` 块，RSA-3072 时恒 625 字符）
  - `sealed class FileHostKeyStore : IHostKeyStore`：构造 `FileHostKeyStore(string? baseDir = null)`（null → `Environment.GetFolderPath(UserProfile)/.harmony`）；无文件时自动生成 RSA-3072（`RSA.Create(3072)`），写 `hdckey`（PKCS#8 PEM）与 `hdckey.pub`（SPKI PEM）；已存在则加载；目录不存在则创建（Unix 0750、文件 0600——用 `File.SetUnixFileMode` 于非 Windows，平台注释注明原因）

- [ ] **Step 1: 写失败测试**

```csharp
using HdcSharp.Security;
using Xunit;

namespace HdcSharp.Tests.Security;

public class FileHostKeyStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hdcsharp-tests-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    [Fact]
    public void GeneratesAndReloadsKeyPair()
    {
        string pem1;
        using (var ks = new FileHostKeyStore(_dir))
        {
            pem1 = ks.GetPublicKeyPem();
            Assert.StartsWith("-----BEGIN PUBLIC KEY-----", pem1);
            Assert.Equal(625, pem1.Length);                       // RSA-3072 SPKI PEM 恒 625 字符
        }
        using var ks2 = new FileHostKeyStore(_dir);               // 重载，不重新生成
        Assert.Equal(pem1, ks2.GetPublicKeyPem());
        Assert.True(File.Exists(Path.Combine(_dir, "hdckey")));
        Assert.True(File.Exists(Path.Combine(_dir, "hdckey.pub")));
    }
}
```

- [ ] **Step 2: 跑失败** → 编译错误
- [ ] **Step 3: 实现**（`rsa.ExportPkcs8PrivateKeyPem()` / `ExportSubjectPublicKeyInfoPem()`；加载用 `RSA.ImportFromPem(File.ReadAllText(...))`）
- [ ] **Step 4: 跑通过** → PASS
- [ ] **Step 5: Commit** `git add -A && git commit -m "feat: 主机密钥库（与官方 hdc 共享 ~/.harmony/hdckey）"`

---

### Task 10: RSA 原语（PKCS1 私钥运算 + PSS 签名）

**Files:**
- Create: `src/HdcSharp/Security/RsaRaw.cs`
- Test: `tests/HdcSharp.Tests/Security/RsaRawTests.cs`

**Interfaces:**
- Consumes: Task 9 密钥库（测试中直接用 `RSA.Create(3072)`）
- Produces: `static class RsaRaw`：
  - `byte[] Pkcs1PrivateEncrypt(RSA rsa, ReadOnlySpan<byte> data)`：块类型 1（`00 01 FF..FF 00 || data`），经 CRT 幂运算返回模长签名；`data` 长度须 ≤ 模长-11
  - `byte[] PssSign(RSA rsa, ReadOnlySpan<byte> data)`：`rsa.SignData(data.ToArray(), HashAlgorithmName.SHA512, RSASignaturePadding.Pss)`

- [ ] **Step 1: 写失败测试**

```csharp
using System.Numerics;
using HdcSharp.Security;
using Xunit;

namespace HdcSharp.Tests.Security;

public class RsaRawTests
{
    [Fact]
    public void Pkcs1PrivateEncrypt_PublicOpRestoresBlock()
    {
        using var rsa = RSA.Create(2048);                     // 测试用小模长提速
        var p = rsa.ExportParameters(true);
        var token = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"u8.ToArray();
        var sig = RsaRaw.Pkcs1PrivateEncrypt(rsa, token);
        // 公钥幂运算还原块：sig^e mod n == 00 01 FF..FF 00 || token
        var n = ToBigInteger(p.Modulus!, true);
        var e = ToBigInteger(p.Exponent!, false);
        var m = BigInteger.ModPow(ToBigInteger(sig, true), e, n);
        var block = m.ToByteArray(isBigEndian: true);         // 需去前导 0
        var expected = BuildBlock(token, (rsa.KeySize + 7) / 8);
        Assert.Equal(expected, block);
    }

    [Fact]
    public void PssSign_VerifiesWithDotNet()
    {
        using var rsa = RSA.Create(2048);
        var sig = RsaRaw.PssSign(rsa, "token"u8);
        Assert.True(rsa.VerifyData("token"u8.ToArray(), sig, HashAlgorithmName.SHA512, RSASignaturePadding.Pss));
    }

    private static BigInteger ToBigInteger(byte[] be, bool unsigned) { /* 大端→BigInteger，unsigned 时补 0x00 */ throw new NotImplementedException(); }
    private static byte[] BuildBlock(byte[] data, int modulusLen) { /* 00 01 FF.. 00 data */ throw new NotImplementedException(); }
}
```

（测试辅助方法实现也属本任务交付物）

- [ ] **Step 2: 跑失败** → 编译错误
- [ ] **Step 3: 实现** 核心 CRT（引用 spec §7.3）：

```csharp
var p = rsa.ExportParameters(true);
BigInteger n = FromBe(p.Modulus), d = FromBe(p.D), dp = FromBe(p.DP), dq = FromBe(p.DQ),
           pp = FromBe(p.P), qq = FromBe(p.Q), qinv = FromBe(p.InverseQ);
BigInteger c = FromBe(block);
BigInteger m1 = BigInteger.ModPow(c, dp, pp);
BigInteger m2 = BigInteger.ModPow(c, dq, qq);
BigInteger h = (qinv * (m1 - m2)) % pp; if (h < 0) h += pp;
BigInteger sig = m2 + h * qq;
byte[] outBytes = sig.ToByteArray();          // 小端
Array.Reverse(outBytes);
// 左填充 0x00 至模长
```

`FromBe(byte[] be)`：反转 + 尾补 0x00（保证正数）。

- [ ] **Step 4: 跑通过** → PASS
- [ ] **Step 5: Commit** `git add -A && git commit -m "feat: RSA 原语（PKCS1 块类型1 私钥运算/PSS 签名）"`

---

### Task 11: 握手/认证消息构建与解析（纯函数部分）

**Files:**
- Create: `src/HdcSharp/Security/AuthMessages.cs`、`src/HdcSharp/Transport/DaemonCapabilities.cs`
- Test: `tests/HdcSharp.Tests/Security/AuthMessagesTests.cs`

**Interfaces:**
- Consumes: Task 4 SessionHandShake、Task 6 Tlv16、Task 2 常量
- Produces:
  - `static class AuthMessages`：
    - `SessionHandShake BuildInitialHandshake(uint sessionId, string connectKey, bool heartbeat)`（buf=Tlv16(authtype"1")+Tlv16(supportfeatures "Ver: 3.2.0f,TCP,<os>[,heartbeat]")，os=`Windows|Linux|macOS` 按 `OperatingSystem` 映射为 `win|linux|mac`，version=HostVersion+16×'0'）
    - `byte[] BuildPublicKeyResponse(string hostName, string publicKeyPem)`（`hostName + 0x0C + pem`）
    - `byte[] BuildSignatureResponse(byte[] token, AuthScheme scheme, RSA key)`（scheme=PsSha512→RsaRaw.PssSign；Pkcs1→RsaRaw.Pkcs1PrivateEncrypt；再 Base64）
    - `AuthPhase ParseDaemonHandshake(SessionHandShake msg, out DaemonCapabilities caps, out byte[] token, out string errorText)`；`enum AuthPhase { AuthRequired, AuthOk, AuthFailed, BannerInvalid }`
  - `DaemonCapabilities`：`DaemonGeneration Generation`（`enum { Rust, Cpp, Unknown }`：版本串以 "Ver: 3.0."开头→Rust；"Ver: 3.2."→Cpp）、`bool Heartbeat`（AUTH_OK TLV supportfeatures 含 "heartbeat" 或握手回显含）、`string DeviceName`、`bool Authenticated`（daemonauthstatus=="SUCCESS"）、`string? ErrorMessage`（emgmsg）

- [ ] **Step 1: 写失败测试**

```csharp
using HdcSharp.Transport;
using Xunit;

namespace HdcSharp.Tests.Security;

public class AuthMessagesTests
{
    [Fact]
    public void InitialHandshake_ContainsTlvs()
    {
        var hs = AuthMessages.BuildInitialHandshake(sessionId: 7, connectKey: "192.168.2.161:44221", heartbeat: true);
        Assert.Equal("OHOS HDC", hs.Banner);
        Assert.Equal(0, hs.AuthType);
        Assert.Equal(7u, hs.SessionId);
        Assert.Equal("192.168.2.161:44221", hs.ConnectKey);
        Assert.StartsWith("authtype", hs.Buf);
        Assert.Contains("supportfeatures", hs.Buf);
        Assert.Contains("Ver: 3.2.0f", hs.Buf);
        Assert.Equal("Ver: 3.2.0f" + new string('0', 16), hs.Version);
    }

    [Fact]
    public void ParseAuthOk_CppStyle_FeaturesPresent()
    {
        var buf = HdcSharp.Protocol.Tlv16.Serialize(new[]
        {
            ("emgmsg", ""), ("devname", "my-dev"), ("daemonauthstatus", "SUCCESS"),
            ("1200", "enable"), ("supportfeatures", "heartbeat,encrypt_tcp")
        });
        var msg = new HdcSharp.Protocol.Messages.SessionHandShake { Banner = "OHOS HDC", AuthType = 4, Buf = buf, Version = "Ver: 3.2.0fabcdef0123456789" };
        var phase = AuthMessages.ParseDaemonHandshake(msg, out var caps, out var token, out var err);
        Assert.Equal(AuthPhase.AuthOk, phase);
        Assert.Equal(DaemonGeneration.Cpp, caps.Generation);
        Assert.True(caps.Authenticated);
        Assert.Equal("my-dev", caps.DeviceName);
        Assert.True(caps.Heartbeat);
    }

    [Fact]
    public void ParseAuthOk_RustStyle_SubsetTlvs()
    {
        var buf = HdcSharp.Protocol.Tlv16.Serialize(new[]
        {
            ("devname", "rust-dev"), ("daemonauthstatus", "SUCCESS"), ("emgmsg", "")
        });
        var msg = new HdcSharp.Protocol.Messages.SessionHandShake { Banner = "OHOS HDC", AuthType = 4, Buf = buf, Version = "Ver: 3.0.0e" };
        var phase = AuthMessages.ParseDaemonHandshake(msg, out var caps, out _, out _);
        Assert.Equal(AuthPhase.AuthOk, phase);
        Assert.Equal(DaemonGeneration.Rust, caps.Generation);
        Assert.False(caps.Heartbeat);                          // Rust 世代无心跳
    }

    [Fact]
    public void ParsePublicKeyRequest_SetsTokenPhase()
    {
        var msg = new HdcSharp.Protocol.Messages.SessionHandShake { Banner = "OHOS HDC", AuthType = 3, Buf = HdcSharp.Protocol.Tlv16.Serialize(new[] { ("authtype", "1") }) };
        var phase = AuthMessages.ParseDaemonHandshake(msg, out _, out var token, out _);
        Assert.Equal(AuthPhase.AuthRequired, phase);
        Assert.True(token.Length == 0);                        // token 在 AUTH_SIGNATURE 到达
    }

    [Fact]
    public void ParseSignatureChallenge_CppToken20Chars()
    {
        var msg = new HdcSharp.Protocol.Messages.SessionHandShake { Banner = "OHOS HDC", AuthType = 2, Buf = "0123456789abcdefghij" };
        var phase = AuthMessages.ParseDaemonHandshake(msg, out var caps, out var token, out _);
        Assert.Equal(AuthPhase.AuthRequired, phase);
        Assert.Equal(20, token.Length);
        Assert.Equal(AuthScheme.PssSha512, caps.Scheme);       // C++ 世代握手 buf 含 authtype=1
    }
}
```

- [ ] **Step 2: 跑失败** → 编译错误
- [ ] **Step 3: 实现** 状态机要点：daemon `AuthType==3` → AuthRequired（buf 中 Tlv16 含 authtype → caps.Scheme=PssSha512；否则 Scheme=Pkcs1）；`AuthType==2` → AuthRequired 且 token=buf（**C++ 20 字符、Rust 64 字符都原样用作签名输入**）；`AuthType==4` → AuthOk/按 daemonauthstatus；banner=="HS FAILED" → BannerInvalid；解析失败/版本 < "Ver: 3.0.0b" → AuthFailed(errorText)
  - 注意：`caps.Scheme` 需在 AUTH_PUBLICKEY 阶段就确定并保留到签名阶段——`ParseDaemonHandshake` 无状态，`DaemonCapabilities` 由调用方（Task 12 AuthHandler）跨阶段持有并更新
- [ ] **Step 4: 跑通过** → PASS
- [ ] **Step 5: Commit** `git add -A && git commit -m "feat: 握手/认证消息构建解析与能力指纹"`

---

### Task 12: HdcConnection（传输核心）

**Files:**
- Create: `src/HdcSharp/Transport/HdcConnection.cs`、`src/HdcSharp/Transport/FramePump.cs`、`src/HdcSharp/Transport/HeartbeatTimer.cs`
- Test: `tests/HdcSharp.Tests/Transport/HdcConnectionTests.cs`（用真实 `TcpListener` 回环）

**Interfaces:**
- Consumes: Task 5 FrameCodec、Task 4 消息、Task 2 常量
- Produces:
  - `sealed class HdcConnection : IAsyncDisposable`：
    - 构造 `HdcConnection(TcpClient client, HdcConnectionOptions opts)`（opts：`Action<LogLevel,string>? Logger`、`TimeSpan HeartbeatInterval`）
    - `uint SessionId { get; }`（构造时随机非零）
    - `Task SendAsync(uint channelId, HdcCommand cmd, ReadOnlyMemory<byte> payload, CancellationToken ct)`（写信号量串行化）
    - `event Action<Frame>? FrameReceived`（读循环线程触发）
    - `event Action<Frame>? ChannelClosed`（语义化转发：见下）
    - `Task RunAsync(CancellationToken ct)`：读循环直到断开；内部职责：帧解码→按 channelId 事件分发；`CHANNEL_CLOSE` 特殊处理（见 Step 3）；未知 channelId 收到命令时自动回发 `CHANNEL_CLOSE[0]`
    - `void StartHeartbeat()`（仅 Cpp 世代调用；`PeriodicTimer` 每 5s 发 `HeartbeatMsg`，Count 递增）
    - `Task CloseChannelAsync(uint channelId, CancellationToken ct)`：发 `CHANNEL_CLOSE[0]`
  - 语义（spec §4.11）：读循环收到 `CHANNEL_CLOSE` 载荷 `[n]`：本地通道终结（触发 ChannelClosed 事件）；若 n>0，发送 `CHANNEL_CLOSE[n-1]` 回发一次

- [ ] **Step 1: 写失败测试**

```csharp
using HdcSharp.Protocol;
using HdcSharp.Transport;
using Xunit;

namespace HdcSharp.Tests.Transport;

public class HdcConnectionTests
{
    private static async Task<(HdcConnection conn, TcpClient serverSide)> CreatePairAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        var serverSide = await listener.AcceptTcpClientAsync();
        listener.Stop();
        return (new HdcConnection(client, new HdcConnectionOptions()), serverSide);
    }

    [Fact]
    public async Task Send_ReceivesWellFormedFrameOnOtherEnd()
    {
        var (conn, server) = await CreatePairAsync();
        using var runCts = new CancellationTokenSource();
        var runTask = conn.RunAsync(runCts.Token);
        using var serverStream = server.GetStream();
        var buf = new byte[64];
        int n = await serverStream.ReadAsync(buf);
        var dec = new FrameDecoder();
        dec.Append(buf.AsSpan(0, n));
        Assert.True(dec.TryRead(out var f));
        Assert.Equal(HdcCommand.KernelHandshake, f.Command);  // 首帧应为占位（见 Step 3：连接建立即等待外层发握手，此处仅验证帧可达）
        runCts.Cancel();
    }

    [Fact]
    public async Task ChannelClose_One_DecrementsAndEchoes()
    {
        var (conn, server) = await CreatePairAsync();
        using var runCts = new CancellationTokenSource();
        var runTask = conn.RunAsync(runCts.Token);
        using var serverStream = server.GetStream();
        // 向连接写入 daemon 发出的 CHANNEL_CLOSE[1]
        await serverStream.WriteAsync(FrameCodec.Encode(9, HdcCommand.KernelChannelClose, new byte[] { 1 }));
        // 期望连接回发 CHANNEL_CLOSE[0]
        var buf = new byte[64];
        int n = await serverStream.ReadAsync(buf);
        var dec = new FrameDecoder(); dec.Append(buf.AsSpan(0, n));
        Assert.True(dec.TryRead(out var f));
        Assert.Equal(HdcCommand.KernelChannelClose, f.Command);
        Assert.Equal(new byte[] { 0 }, f.Payload);
        runCts.Cancel();
    }
}
```

（首测断言以 Step 3 实现为准调整：若连接不自动发帧，则改为 `conn.SendAsync` 后在服务端读帧验证字节含 `48 57` 头——测试意图：帧能双向到达且解码正确。）

- [ ] **Step 2: 跑失败** → 编译错误
- [ ] **Step 3: 实现**：`RunAsync` 用 `PipeReader.Create(networkStream)` 循环 `ReadAsync` → `FrameDecoder.Append` → `TryRead` 全部出队 → 分发；写路径 `SemaphoreSlim(1,1)` 保护 `FrameCodec.Encode` 后 `WriteAsync` + `FlushAsync`；`ChannelClosed` 事件在 `FrameReceived` 内部过滤 `CMD==KernelChannelClose` 后触发（含回发逻辑）；心跳 `PeriodicTimer` 独立 Task；断开时置 `IsClosed` 并以 `OperationCanceledException` 退出
- [ ] **Step 4: 跑通过** → PASS
- [ ] **Step 5: Commit** `git add -A && git commit -m "feat: HdcConnection 传输核心（帧泵/通道关闭语义/心跳）"`

---

### Task 13: AuthHandler + HdcDevice/HdcHost（连通真机的关键路径）

**Files:**
- Create: `src/HdcSharp/Security/AuthHandler.cs`、`src/HdcSharp/HdcDevice.cs`（本任务先建骨架：属性+状态机）、`src/HdcSharp/HdcHost.cs`、`src/HdcSharp/ConnectOptions.cs`、`src/HdcSharp/HdcDeviceState.cs`
- Test: `tests/HdcSharp.Tests/Security/AuthHandlerTests.cs`、`tests/HdcSharp.Tests/HdcHostTests.cs`

**Interfaces:**
- Consumes: Task 11 消息、Task 12 连接、Task 9/10 密钥
- Produces（**与 spec §5 冻结 API 一致**）:
  - `HdcHost`：`event EventHandler<DeviceStateChangedEventArgs>? DeviceStateChanged`；`IReadOnlyList<HdcDevice> ConnectedDevices`；`HdcDevice? FindDevice(string connectKey)`；`Task<HdcDevice> ConnectAsync(string endpoint, ConnectOptions? options = null, CancellationToken ct = default)`；`Task<bool> DisconnectAsync(string connectKey, CancellationToken ct = default)`；`event EventHandler<string>? AuthorizationRequested`；`IAsyncDisposable`
  - `ConnectOptions`：`bool Heartbeat=true`、`bool EnableEncryption=false`（一期 false 抛 `NotSupportedException` 当且仅当 daemon 要求 PSK——实际 daemon 不要求）、`TimeSpan AuthTimeout=3.5min`、`TimeSpan OperationTimeout=30s`、`IHostKeyStore? KeyStore=null`
  - `AuthHandler`（internal）：`Task<DaemonCapabilities> RunAsync(HdcConnection conn, string connectKey, IHostKeyStore keys, ConnectOptions opts, CancellationToken ct)`——发握手→处理 AUTH_PUBLICKEY（hostName=`Environment.MachineName`）→处理 AUTH_SIGNATURE（按 caps.Scheme 签名）→等待 AUTH_OK→等 daemon 的 `CHANNEL_CLOSE[1]` 并回 `[0]`→返回 caps；事件 `AuthorizationRequested` 在收到 AUTH_PUBLICKEY 后触发一次（提示用户设备可能弹窗）
  - `HdcDevice`（骨架）：`ConnectKey/DeviceName/State/Generation` 属性 + internal `HdcConnection Connection` + `internal uint NewChannelId()`（随机 u32，Interlocked 递增回绕时重摇）
  - `HdcDeviceState { Connecting, Authorizing, Online, Offline }`、`DeviceStateChangedEventArgs(string Key, HdcDeviceState OldState, HdcDeviceState NewState)`

- [ ] **Step 1: 写失败测试（FakeDaemon 首秀）**

创建 `tests/HdcSharp.Tests/TestDoubles/FakeDaemon.cs`：监听 `127.0.0.1:0` 的 `TcpListener`；`FakeDaemonOptions { DaemonGeneration Generation, bool RequireAuth, string DeviceName="fake-dev" }`；行为（按 spec §4.6 剧本，Rust/Cpp 两分支）：
1. 收到 `CMD_KERNEL_HANDSHAKE` 载荷 SessionHandShake → 校验 banner=="OHOS HDC"，否则回 `HS FAILED` 并断开
2. RequireAuth=false → 回 `AUTH_OK(4)`（Rust: TLV devname/daemonauthstatus=SUCCESS/emgmsg=""；Cpp: 另加 1200=enable、supportfeatures="heartbeat"）+ `CHANNEL_CLOSE[1]`(cid=0)
3. RequireAuth=true → 回 `AUTH_PUBLICKEY(3)`（Cpp 另附 TLV authtype="1"）→ 收到 `AUTH_PUBLICKEY(3)` 后回 `AUTH_SIGNATURE(2)` buf=token（Cpp 20 字符小写 hex/Rust 64 字符大写 hex）→ 收到 `AUTH_SIGNATURE(2)` 用 host 公钥验签（PSS 或 PKCS1 公钥幂+比对）→ 成功回 AUTH_OK 同 2，失败断开
4. 记录收到的全部帧到 `IReadOnlyList<Frame> Frames` 供断言

```csharp
[Fact]
public async Task ConnectAsync_NoAuth_OnlineWithDeviceName()
{
    using var daemon = new FakeDaemon(new FakeDaemonOptions { RequireAuth = false, Generation = DaemonGeneration.Rust });
    using var host = new HdcHost();
    var dev = await host.ConnectAsync($"127.0.0.1:{daemon.Port}");
    Assert.Equal(HdcDeviceState.Online, dev.State);
    Assert.Equal("fake-dev", dev.DeviceName);
    Assert.Equal(DaemonGeneration.Rust, dev.Generation);
}

[Fact]
public async Task ConnectAsync_RustDaemon_Pkcs1SignatureAccepted()
{
    using var daemon = new FakeDaemon(new FakeDaemonOptions { RequireAuth = true, Generation = DaemonGeneration.Rust });
    using var host = new HdcHost();
    var dev = await host.ConnectAsync($"127.0.0.1:{daemon.Port}");
    Assert.Equal(HdcDeviceState.Online, dev.State);
    // FakeDaemon 已用 host 公钥完成 PKCS1 公钥幂验证（内部断言），此处验证状态即可
}

[Fact]
public async Task ConnectAsync_CppDaemon_PssSignatureAccepted_HeartbeatNegotiated()
{
    using var daemon = new FakeDaemon(new FakeDaemonOptions { RequireAuth = true, Generation = DaemonGeneration.Cpp });
    using var host = new HdcHost();
    var dev = await host.ConnectAsync($"127.0.0.1:{daemon.Port}");
    Assert.Equal(HdcDeviceState.Online, dev.State);
    Assert.Equal(DaemonGeneration.Cpp, dev.Generation);
}

[Fact]
public async Task ConnectAsync_BadBanner_Fails()
{
    using var daemon = new FakeDaemon(new FakeDaemonOptions { SendBadBanner = true });
    using var host = new HdcHost();
    await Assert.ThrowsAsync<HdcException>(() => host.ConnectAsync($"127.0.0.1:{daemon.Port}"));
}
```

- [ ] **Step 2: 跑失败** → 编译错误
- [ ] **Step 3: 实现**：`ConnectAsync` 解析 endpoint（`ip:port` 必含 ':'，否则抛 `ArgumentException`）→ TcpClient 连接（超时 OperationTimeout）→ HdcConnection → AuthHandler.RunAsync（带 AuthTimeout）→ 组装 HdcDevice 注册表 → Online 事件。断开/异常路径置 Offline 并广播。`DisconnectAsync`：`CloseChannelAsync` 所有活跃通道后关 TCP
- [ ] **Step 4: 跑通过** → PASS（四个用例全绿）
- [ ] **Step 5: Commit** `git add -A && git commit -m "feat: 握手认证处理器与 HdcHost/HdcDevice（FakeDaemon 全回环）"`

---

### Task 14: Shell 操作

**Files:**
- Create: `src/HdcSharp/Operations/ShellOperation.cs`、`src/HdcSharp/Operations/InteractiveShell.cs`
- Modify: `src/HdcSharp/HdcDevice.cs`（新增三个方法）
- Test: `tests/HdcSharp.Tests/Operations/ShellTests.cs`

**Interfaces:**
- Consumes: Task 13 的 HdcDevice/连接
- Produces（spec §5 冻结签名）:
  - `Task<string> ExecuteShellAsync(string command, CancellationToken ct = default)`：开通道→发 `UNITY_EXECUTE(1001)` 载荷=command→收集 `ECHO_RAW` 与 `ECHO` 文本（ECHO 按 `[level]text` 语义记入异常而非输出）→`CHANNEL_CLOSE` 终结→返回聚合 UTF-8 串
  - `IAsyncEnumerable<byte[]> StreamShellOutputAsync(string command, CancellationToken ct = default)`：同上但逐块 yield（`System.Threading.Channels` 桥接）
  - `Task<IInteractiveShell> OpenInteractiveShellAsync(CancellationToken ct = default)`：发 `SHELL_INIT(2000)`（Cpp 空载荷/Rust `[0]`，按 Generation）→ `IInteractiveShell`（`Stream Input`：写入即发 `SHELL_DATA`；`ChannelReader<byte[]> Output`；`WaitUntilClosedAsync`）——`0x03/0x04` 特殊字节由 daemon 解释，**库原样转发**（平台注释：daemon 侧拦截 0x03 转 SIGINT，库不可吞掉）

- [ ] **Step 1: 写失败测试**（FakeDaemon 扩展：收到 1001 → 回两帧 ECHO_RAW + CHANNEL_CLOSE[1]；收到 2000 → 回 ECHO_RAW 提示符 + 对 2001 回显 + 收到 0x04 时 CHANNEL_CLOSE[1]）

```csharp
[Fact]
public async Task ExecuteShell_AggregatesOutputUntilClose()
{
    // daemon 剧本：ECHO_RAW "hello " + ECHO_RAW "world" + CHANNEL_CLOSE[1]
    using var daemon = FakeDaemon.WithShellScript("hello ", "world");
    using var host = new HdcHost();
    var dev = await host.ConnectAsync($"127.0.0.1:{daemon.Port}");
    Assert.Equal("hello world", await dev.ExecuteShellAsync("echo hello world"));
}

[Fact]
public async Task StreamShell_YieldsChunks()
{
    using var daemon = FakeDaemon.WithShellScript("a", "b", "c");
    using var host = new HdcHost();
    var dev = await host.ConnectAsync($"127.0.0.1:{daemon.Port}");
    var chunks = new List<byte[]>();
    await foreach (var c in dev.StreamShellOutputAsync("abc")) chunks.Add(c);
    Assert.Equal(3, chunks.Count);
}

[Fact]
public async Task InteractiveShell_InputReachesDaemon_CloseOnEot()
{
    using var daemon = FakeDaemon.WithInteractiveEcho();
    using var host = new HdcHost();
    var dev = await host.ConnectAsync($"127.0.0.1:{daemon.Port}");
    var shell = await dev.OpenInteractiveShellAsync();
    shell.Input.Write("ls\n"u8.ToArray());
    var line = await shell.Output.ReadAsync();             // 回显
    Assert.Equal("ls\n"u8.ToArray(), line);
    shell.Input.Write(new byte[] { 0x04 });                // Ctrl-D
    await shell.WaitUntilClosedAsync();
}
```

- [ ] **Step 2: 跑失败** → 编译错误
- [ ] **Step 3: 实现** 通道生命周期管理：`HdcDevice` 内部 `ChannelRegistry`（`ConcurrentDictionary<uint, ChannelContext>`；ChannelContext 持 `Channel<byte[]> Queue` + `TaskCompletionSource Closed` + CTS）；FrameReceived 按 channelId 投递；WAKEUP(12) 全局忽略
- [ ] **Step 4: 跑通过** → PASS
- [ ] **Step 5: Commit** `git add -A && git commit -m "feat: shell 一次性/流式/交互（含 PTY 字节透传）"`

---

### Task 15: 文件传输

**Files:**
- Create: `src/HdcSharp/Operations/FileOperation.cs`、`src/HdcSharp/FileProgress.cs`
- Modify: `src/HdcSharp/HdcDevice.cs`（四方法）、`tests/TestDoubles/FakeDaemon.cs`（文件剧本）
- Test: `tests/HdcSharp.Tests/Operations/FileTransferTests.cs`

**Interfaces:**
- Consumes: Task 4 TransferConfig/TransferPayload、Task 2 常量
- Produces（spec §5）: `SendFileAsync/ReceiveFileAsync/SendDirectoryAsync/ReceiveDirectoryAsync`；`FileProgress(long BytesTransferred, long? TotalBytes, string FileName)`
  - 线上语义（spec §4.7）：INIT 载荷 `"send <local> <remote>"`/`"recv <remote> <local>"`（**R1 注**：常量 `FileInitVerb` internal 可调，默认带首词，真机验证后如需调整只改一处）；CHECK=TransferConfig（send: path=remote, optionalName=本地文件名, fileSize, clientCwd=""）；BEGIN 载荷空或 8B 都接受；DATA=`TransferPayload` 序列化后补零到 64B 槽 + ≤48KiB 数据（index=累计偏移）；FINISH `[1]` 每文件/`[0]` 总结；recv 时 host 作为 slave 回 BEGIN 空载荷；收 `FILE_CHECK` 未请求过视为协议错误
- 目录：递归枚举（跳过符号链接目录），`optionalName`=相对路径（`/` 分隔），逐文件推进；进度回调每块一次

- [ ] **Step 1: 写失败测试**（FakeDaemon 文件剧本：接收 send 剧本——INIT→回 WAKEUP(12)→CHECK→回 BEGIN(空)→收 DATA 按 index 拼接→收 FINISH[1] 回 FINISH[0]+ECHO(Ok,"FileTransfer finish")；recv 剧本镜像主动推数据）

```csharp
[Fact]
public async Task SendFile_RoundTrip_FakeDaemonReceivesIdenticalBytes()
{
    var src = Path.Combine(_dir, "a.bin");
    var payload = RandomNumberGenerator.GetBytes(200_000);     // >2 块，测分片
    await File.WriteAllBytesAsync(src, payload);
    using var daemon = FakeDaemon.WithFileRecvSink(_dst);
    using var host = new HdcHost();
    var dev = await host.ConnectAsync($"127.0.0.1:{daemon.Port}");
    await dev.SendFileAsync(src, "/data/local/tmp/a.bin");
    Assert.Equal(payload, await File.ReadAllBytesAsync(Path.Combine(_dst, "a.bin")));
}

[Fact]
public async Task SendDirectory_QueuesAllFiles()
{
    // 3 个子目录文件 + 1 个根文件；FakeDaemon 断言收到 4 次 CHECK 且 optionalName 为相对路径
}

[Fact]
public async Task ReceiveFile_FakeDaemonPushes_ContentMatches()
{
    // FakeDaemon 按 recv 剧本推 TransferConfig + DATA×n + FINISH；断言落盘内容一致
}
```

- [ ] **Step 2: 跑失败** → 编译错误
- [ ] **Step 3: 实现**（写循环 `MemoryStream` 复用；`updateIfNew`/`holdTimestamp` 默认 false；进度经 `IProgress<T>`）
- [ ] **Step 4: 跑通过** → PASS
- [ ] **Step 5: Commit** `git add -A && git commit -m "feat: 文件收发（单文件/目录/进度/64B 槽分片）"`

---

### Task 16: 应用安装/卸载

**Files:**
- Create: `src/HdcSharp/Operations/AppOperation.cs`、`src/HdcSharp/InstallOptions.cs`、`src/HdcSharp/UninstallOptions.cs`
- Modify: `src/HdcSharp/HdcDevice.cs`、FakeDaemon（安装剧本）
- Test: `tests/HdcSharp.Tests/Operations/AppTests.cs`

**Interfaces:**
- Consumes: Task 8 Tar、Task 15 传输内件
- Produces（spec §5）: `Task<string> InstallAsync(string packagePath, InstallOptions? options = null, CancellationToken ct = default)`（返回 bm 输出文本）；`InstallOptions { bool Replace=true, bool Downgrade=false, bool Shared=false }` → 线上 options 串 `-r`/`-d`/`-s` 空格拼接；`Task<string> UninstallAsync(string packageName, UninstallOptions? options = null, ...)`；`UninstallOptions { bool KeepData=false }` → `-k`
  - 线上（spec §4.7.4）：INIT 载荷 `"<opts> <path>"`；CHECK TransferConfig{functionName="install", options=opts, optionalName=`Random.Shared.Next` 9 位数字+原扩展名, fileSize}；目录时先 TarWriter 打包（entryName=相对路径）扩展名 `.tar`；FINISH 载荷 `[mode][success][msg]`：mode==1/2，success==0 → `HdcException(msg)`，==1 → 返回 msg

- [ ] **Step 1: 写失败测试**

```csharp
[Fact]
public async Task InstallHap_ReportsBmOutput()
{
    using var daemon = FakeDaemon.WithInstallScript(success: true, output: "Success");
    using var host = new HdcHost();
    var dev = await host.ConnectAsync($"127.0.0.1:{daemon.Port}");
    var msg = await dev.InstallAsync(_dummyHapPath, new InstallOptions { Replace = true });
    Assert.Equal("Success", msg);
    // 断言 daemon 收到的 CHECK optionalName 以 .hap 结尾、functionName=="install"
}

[Fact]
public async Task InstallFailure_ThrowsWithDaemonMessage()
{
    // success=false → Assert.ThrowsAsync<HdcException> 且 ErrorCode 含 daemon 文本
}

[Fact]
public async Task InstallDirectory_PacksTar()
{
    // 目录含 2 个 hap；断言 daemon 收到的包为合法 tar（TarReader 可解出 2 条目）且 optionalName 以 .tar 结尾
}
```

- [ ] **Step 2: 跑失败** → 编译错误
- [ ] **Step 3: 实现**（复用 Task 15 的单包传输内部件 `TransferOneAsync(config, stream, ct)`）
- [ ] **Step 4: 跑通过** → PASS
- [ ] **Step 5: Commit** `git add -A && git commit -m "feat: 应用安装/卸载（tar 目录打包/bm 输出回传）"`

---

### Task 17: 端口转发（fport/rport）

**Files:**
- Create: `src/HdcSharp/Operations/Forward/ForwardOperation.cs`、`src/HdcSharp/Operations/Forward/TcpForwardSession.cs`、`src/HdcSharp/IForwardSession.cs`
- Modify: `src/HdcSharp/HdcDevice.cs`、FakeDaemon（转发剧本）
- Test: `tests/HdcSharp.Tests/Operations/ForwardTests.cs`

**Interfaces:**
- Consumes: Task 2 FORWARD_* 命令、Task 13 连接
- Produces（spec §5）: `Task<IForwardSession> ForwardTcpAsync(int localPort, int remotePort, CancellationToken ct = default)`；`Task<IForwardSession> ReverseTcpAsync(int remotePort, int localPort, ...)`；`IForwardSession { int ListenPort; ForwardDirection Direction; bool IsActive; event EventHandler? Closed; IAsyncDisposable }`
  - 线上（spec §4.8）：fport：INIT 载荷 `"fport ln tcp:<local> tcp:<remote>"`（host 本地 listen）；CHECK `[8B 0][远端节点]`；CHECK_RESULT `[1]`；本地每接受一条连接→随机 cid→ACTIVE_SLAVE `[cid BE][8B 0][远端节点]`→等 ACTIVE_MASTER→双向 DATA `[cid BE][bytes]`；任一端关闭→FREE_CONTEXT `[cid BE]`。rport 镜像：INIT 载荷 `"rport rn tcp:<remote> tcp:<local>"`，host 侧不做本地 listen，daemon 接受连接后推 ACTIVE_SLAVE，host 连接 `127.0.0.1:<local>` 后回 ACTIVE_MASTER
- 取消/释放：`DisposeAsync` → FREE_CONTEXT 全部 cid + CLOSE 通道 + 停 listener

- [ ] **Step 1: 写失败测试**（FakeDaemon 中继剧本：对 CHECK 回 CHECK_RESULT[1]；对 ACTIVE_SLAVE 回 ACTIVE_MASTER 并把 DATA 转发到 daemon 本地的回声服务器；完整链路 = host client → host listener → 帧通道 → FakeDaemon → 回声服务器 → 原路返回）

```csharp
[Fact]
public async Task ForwardTcp_EchoThroughChannel()
{
    using var echo = FakeDaemon.StartEchoServer(out int echoPort);
    using var daemon = FakeDaemon.WithForward(echoPort);
    using var host = new HdcHost();
    var dev = await host.ConnectAsync($"127.0.0.1:{daemon.Port}");
    using var fwd = await dev.ForwardTcpAsync(localPort: 0, remotePort: echoPort);   // 0=自动分配
    using var client = new TcpClient();
    await client.ConnectAsync(IPAddress.Loopback, fwd.ListenPort);
    var s = client.GetStream();
    var payload = "ping-through-forward"u8.ToArray();
    await s.WriteAsync(payload);
    var buf = new byte[64]; int n = await s.ReadAsync(buf);
    Assert.Equal(payload, buf.AsSpan(0, n).ToArray());
}
```

- [ ] **Step 2: 跑失败** → 编译错误
- [ ] **Step 3: 实现**（cid=`Random` u32；`ConcurrentDictionary<uint, TcpClient>`；DATA 收发双 Task per cid）
- [ ] **Step 4: 跑通过** → PASS
- [ ] **Step 5: Commit** `git add -A && git commit -m "feat: fport/rport 转发（TCP 节点/会话生命周期）"`

---

### Task 18: Unity 命令（hilog/bugreport/reboot/remount/runmode/rootrun）

**Files:**
- Create: `src/HdcSharp/Operations/UnityOperation.cs`、`src/HdcSharp/RebootMode.cs`、`src/HdcSharp/RunMode.cs`
- Modify: `src/HdcSharp/HdcDevice.cs`、FakeDaemon
- Test: `tests/HdcSharp.Tests/Operations/UnityTests.cs`

**Interfaces:**
- Consumes: Task 14 通道机制
- Produces（spec §5 + §4.10 载荷）:
  - `IAsyncEnumerable<string> StreamHilogAsync(CancellationToken ct = default)`：1005 空载荷→ECHO_RAW 按行切分（`\n`）产出；连接断开/通道关闭终结
  - `IAsyncEnumerable<byte[]> StreamBugReportAsync(CancellationToken ct = default)`：1011 空载荷→1012 块产出
  - `Task RemountAsync(ct)`：1002 空载荷，等 ECHO(Ok) 或通道关闭
  - `Task RebootAsync(RebootMode mode = Default, ct)`：1003 载荷=`bootloader|recovery|""`（枚举映射，不带 `-`）
  - `Task SetRunModeAsync(RunMode mode, ct)`：1004 载荷：`RunMode.TcpPort(int)`→`"port <n>"`、`RunMode.TcpClose`→`"port close"`、`RunMode.Usb`→`"usb"`（readonly record struct）
  - `Task RootRunAsync(bool unroot = false, ct)`：1007 载荷 `""|"r"`

- [ ] **Step 1: 写失败测试**

```csharp
[Fact]
public async Task Hilog_StreamsLinesUntilClose()
{
    using var daemon = FakeDaemon.WithStreamScript(HdcCommand.UnityHilog, "line1\n", "line2\n");
    using var host = new HdcHost();
    var dev = await host.ConnectAsync($"127.0.0.1:{daemon.Port}");
    var lines = new List<string>();
    await foreach (var l in dev.StreamHilogAsync().WithCancellation(TimeSpan.FromSeconds(5))) lines.Add(l);
    Assert.Equal(new[] { "line1", "line2" }, lines);
}

[Fact]
public async Task Reboot_PayloadStripsPrefix()
{
    using var daemon = FakeDaemon.WithUnityCapture();
    using var host = new HdcHost();
    var dev = await host.ConnectAsync($"127.0.0.1:{daemon.Port}");
    await dev.RebootAsync(RebootMode.Bootloader);
    var sent = daemon.CapturedSingle(dev.ConnectKey);       // 断言最后一帧
    Assert.Equal(HdcCommand.UnityReboot, sent.Command);
    Assert.Equal("bootloader"u8.ToArray(), sent.Payload);
}
```

- [ ] **Step 2: 跑失败** → 编译错误
- [ ] **Step 3: 实现**（复用 ShellOperation 的流式通道件）
- [ ] **Step 4: 跑通过** → PASS
- [ ] **Step 5: Commit** `git add -A && git commit -m "feat: unity 命令（hilog/bugreport 流式与 reboot/remount/runmode）"`

---

### Task 19: 真机集成测试（R1/R2/R3 验证）

**Files:**
- Create: `tests/HdcSharp.Tests/RealDevice/RealDeviceTests.cs`、`scripts/run-realdevice-tests.ps1`
- Test: `HDC_TEST_TARGET` 环境变量门控（默认全部 Skip）

**Interfaces:**
- Consumes: 全部已完成 API
- Produces: 真机验证结论（写入 `docs/realdevice-notes.md`）：R1 FileInit 首词格式实测、R2 世代指纹、R3 auth 行为

- [ ] **Step 1: 写测试**

```csharp
[Trait("Category", "RealDevice")]
public class RealDeviceTests
{
    private static string? Target => Environment.GetEnvironmentVariable("HDC_TEST_TARGET");
    private static bool Available => !string.IsNullOrEmpty(Target);

    [SkippableFact]
    public async Task Connect_Shell_ParamGet()
    {
        Skip.IfNot(Available);
        using var host = new HdcHost();
        var dev = await host.ConnectAsync(Target!);
        var outp = await dev.ExecuteShellAsync("param get const.product.name");
        Assert.False(string.IsNullOrWhiteSpace(outp));
    }

    [SkippableFact]
    public async Task FileRoundtrip() { /* 1MB 随机文件 send→recv→比对，路径 /data/local/tmp/hdcsharp_test */ }

    [SkippableFact]
    public async Task Hilog_Snippet() { /* StreamHilogAsync 取 3 行或 5s 超时即过 */ }

    [SkippableFact]
    public async Task InteractiveShell_Echo()
    { /* OpenInteractiveShellAsync→发 "echo ok\n"→断言输出含 "ok"→0x04 退出 */ }
}
```

（`SkippableFact` 需 ` Xunit.SkippableFact` 包 1.5.x——依赖白名单仅约束**库**，测试工程可加）

- [ ] **Step 2: 运行真机验证**

```bash
export HDC_TEST_TARGET=192.168.2.161:44221
dotnet test tests/HdcSharp.Tests --filter Category=RealDevice
```

- [ ] **Step 3: 按 R1 结论修正**：若设备拒绝 `"send "` 首词格式（connect 失败/无响应），把 `FileOperation.FileInitVerb` 改为实测格式并更新 spec 风险表；记录 daemon 指纹与 auth 行为到 `docs/realdevice-notes.md`
- [ ] **Step 4: 全部通过**（真机在场时）→ Commit：`git add -A && git commit -m "test: 真机集成测试与协议实测记录"`

---

### Task 20: 收尾——AOT 门禁、XML 文档检查、README、NuGet、样例

**Files:**
- Create: `samples/AotConsumer/AotConsumer.csproj`（`PublishAot=true` 的消费样例）、`README.md`、`scripts/pack.ps1`
- Modify: `src/HdcSharp/HdcSharp.csproj`（pack 元数据补全）、按检查结果修补 XML 注释缺口

**Interfaces:**
- Consumes: 全部
- Produces: `artifacts/HdcSharp.0.1.0.nupkg`；AOT 编译门禁通过

- [ ] **Step 1: XML 文档覆盖检查**：运行 `dotnet build` 确认无 CS1591 警告外的缺口（`GenerateDocumentationFile=true` 下公共 API 缺注释会报 CS1591；Directory.Build.props 已把它从 NoWarn 移除——检查方式：`dotnet build src/HdcSharp 2>&1 | grep CS1591`，预期 0 条）
- [ ] **Step 2: AOT 门禁**

```bash
cd samples/AotConsumer && dotnet publish -r win-x64 -c Release -p:PublishAot=true
```
预期：IL 编译成功无 AOT 警告（样例内容：ConnectAsync + ExecuteShellAsync + 控制台输出，20 行）
- [ ] **Step 3: README**（中文）：简介、支持矩阵（命令/世代）、快速上手代码、密钥共享说明、真机测试开关、限制（无退出码、无 LZ4、无加密通道一期）
- [ ] **Step 4: pack**

```bash
dotnet pack src/HdcSharp -c Release -o artifacts
```
- [ ] **Step 5: Commit** `git add -A && git commit -m "chore: AOT 门禁/文档/NuGet 打包/样例"`

---

## Self-Review 记录

1. **Spec 覆盖**：§1 目标→Task 1-20；§2 规范→Global Constraints+Task 1；§3 架构→Task 12/13/15 通道注册表；§4 协议全节→Task 2-8、11、14-18；§5 API→Task 13-18 签名逐条核对；§6 错误→Task 12/13（HdcException/事件）；§7 认证→Task 9/10/11/13；§8 测试→各任务+Task 19；§9 里程碑 1-9/11→Task 1-20（里程碑 10 TLS-PSK 按约定另立计划）；§10 风险 R1-R5→Task 19，R6/R7 文档化（README/realdevice-notes）
2. **占位符扫描**：Task 14-18 测试块中中文说明注释为剧本描述，实现剧本代码属该任务交付（FakeDaemon 剧本已在 Task 13 建立模式）；无 TBD/TODO
3. **类型一致性**：`Frame(uint ChannelId, HdcCommand Command, byte[] Payload)`（Task 5 定义，Task 12/13 复用）；`DaemonCapabilities.Scheme`（Task 11 定义 Task 13 消费）；`AuthPhase/AuthScheme`（Task 11）→Task 13；`FileProgress`/`IForwardSession` 签名与 spec §5 一致
