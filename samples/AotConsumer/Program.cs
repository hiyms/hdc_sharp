// AOT 消费样例：演示在 Native AOT（PublishAot=true）下使用 HdcSharp 公共 API。
// 用途：作为库的 AOT/Trim 编译门禁——本文件只使用驱动式（非反射）代码，
// 命令行为：AotConsumer <ip:port> [shell 命令]
//   1) 连接并认证（含密钥库共享）
//   2) 执行一次性 shell 命令
//   3) 单文件往返（发送 → 拉回 → SHA-256 比对）
// 无参数或 --help 时打印用法并退出，不会硬编码任何真机地址。

using System.Security.Cryptography;
using System.Text;
using HdcSharp;

if (args.Length == 0 || args[0] is "--help" or "-h")
{
    Console.WriteLine("用法：AotConsumer <ip:port> [shell 命令]");
    Console.WriteLine("示例：AotConsumer <设备IP>:<端口> \"param get const.product.model\"");
    Console.WriteLine("说明：样例不内置任何默认地址，必须由命令行传入；认证使用默认密钥库 ~/.harmony/hdckey（与官方 hdc 共享，缺失时自动生成）。");
    return args.Length == 0 ? 2 : 0;
}

string endpoint = args[0];
string command = args.Length > 1 ? args[1] : "param get const.product.model";
int exitCode = 0;

HdcHost host = new();
host.DeviceStateChanged += (_, e) => Console.WriteLine($"[状态] {e.Key}: {e.OldState} -> {e.NewState}");
host.AuthorizationRequested += (_, key) => Console.WriteLine($"[授权] 设备 {key} 正在等待人工确认，请在设备端弹窗中允许调试");

try
{
    // 连接 + 握手认证：网络失败、认证拒绝、authTimeout 超时均以 HdcException 抛出
    HdcDevice device = await host.ConnectAsync(endpoint);
    Console.WriteLine($"[连接] {device.ConnectKey} 设备名={device.DeviceName} 世代={device.Generation} session=0x{device.SessionId:X8}");

    // 一次性 shell：返回聚合 stdout+stderr；退出码不上线（spec §4.11），需要时用 echo $? 变通
    Console.WriteLine($"[shell] $ {command}");
    string output = await device.ExecuteShellAsync(command);
    Console.WriteLine(output.TrimEnd());

    // 单文件往返：本地临时文件 → 设备 /data/local/tmp → 拉回本地再比对 SHA-256
    string localFile = Path.Combine(Path.GetTempPath(), "hdcsharp-aot-sample.txt");
    string remoteFile = "/data/local/tmp/hdcsharp-aot-sample.txt";
    string receivedFile = Path.Combine(Path.GetTempPath(), "hdcsharp-aot-sample.recv.txt");
    await File.WriteAllTextAsync(localFile, $"AotConsumer 往返校验载荷 {Guid.NewGuid():N}\n", Encoding.UTF8);

    var progress = new ProgressLogger();
    await device.SendFileAsync(localFile, remoteFile, progress);
    await device.ReceiveFileAsync(remoteFile, receivedFile, progress);

    string sentHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(localFile)));
    string receivedHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(receivedFile)));
    Console.WriteLine($"[文件] 发送 {sentHash[..16]}… 接收 {receivedHash[..16]}… 一致={sentHash == receivedHash}");
    if (sentHash != receivedHash)
    {
        exitCode = 3;
    }

    await host.DisconnectAsync(device.ConnectKey);
    Console.WriteLine("[断开] 完成");
}
catch (HdcException ex)
{
    // 连接级/操作级协议错误：ErrorCode 为设备端 [E....] 原文（无则为 null）
    Console.WriteLine($"[失败] {ex.Message}（错误码={ex.ErrorCode ?? "无"} 级别={ex.Level}）");
    exitCode = 1;
}

await host.DisposeAsync();
return exitCode;

// 进度回调实现：IProgress<T> 的驱动式用法，无需反射，AOT 安全
internal sealed class ProgressLogger : IProgress<FileProgress>
{
    private long _lastReported;

    public void Report(FileProgress value)
    {
        // 样例只打印整秒级进度，避免刷屏
        if (value.BytesTransferred - _lastReported < 1024 * 1024 && value.TotalBytes is not null && value.BytesTransferred < value.TotalBytes)
        {
            return;
        }

        _lastReported = value.BytesTransferred;
        Console.WriteLine($"[进度] {value.FileName} {value.BytesTransferred}/{value.TotalBytes?.ToString() ?? "?"}");
    }
}
