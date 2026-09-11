using System.Security.Cryptography;
using HdcSharp.Protocol;

namespace HdcSharp.Tests.RealDevice;

/// <summary>
/// 真机集成测试共享支撑：端点/连接选项、设备端与本地唯一临时路径、sha256 比对、清理。
/// 每个用例自建连接与唯一路径，互不依赖执行顺序。
/// </summary>
internal static class RealDeviceHarness
{
    /// <summary>真机端点（<c>HDC_TEST_TARGET</c>）；用例只在门控特性放行后访问。</summary>
    internal static string Target => Environment.GetEnvironmentVariable(RealDeviceGate.TargetEnvironmentVariable)!;

    /// <summary>真机连接选项：认证预算 60 秒（含设备端授权确认等待）。</summary>
    internal static ConnectOptions CreateConnectOptions() => new()
    {
        AuthTimeout = TimeSpan.FromSeconds(60),
        OperationTimeout = TimeSpan.FromSeconds(30),
    };

    /// <summary>连接真机端点并返回认证完成后的在线设备。</summary>
    /// <param name="host">宿主实例。</param>
    /// <param name="ct">取消令牌。</param>
    internal static Task<HdcDevice> ConnectAsync(HdcHost host, CancellationToken ct) =>
        host.ConnectAsync(Target, CreateConnectOptions(), ct);

    /// <summary>设备端唯一临时路径（仅构造，不创建）。</summary>
    internal static string NewRemotePath(string label) =>
        $"/data/local/tmp/hdcsharp_it_{label}_{Guid.NewGuid().ToString("N")[..8]}";

    /// <summary>本地唯一临时目录（已创建）。</summary>
    internal static string NewLocalTempDir(string label)
    {
        string path = Path.Combine(Path.GetTempPath(), $"hdcsharp_it_{label}_{Guid.NewGuid().ToString("N")[..8]}");
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>字节内容的 sha256（小写十六进制）。</summary>
    internal static string Sha256Hex(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    /// <summary>本地文件的 sha256（小写十六进制）。</summary>
    internal static string Sha256HexOfFile(string path) => Sha256Hex(File.ReadAllBytes(path));

    /// <summary>写入指定尺寸的随机内容文件并返回内容字节。</summary>
    internal static byte[] WriteRandomFile(string path, int size)
    {
        byte[] data = RandomNumberGenerator.GetBytes(size);
        File.WriteAllBytes(path, data);
        return data;
    }

    /// <summary>读取设备端文件的 sha256；文件缺失或输出不可解析时返回 null。</summary>
    internal static async Task<string?> TryDeviceSha256Async(HdcDevice device, string remotePath, CancellationToken ct)
    {
        string output = (await device.ExecuteShellAsync($"sha256sum {remotePath} 2>/dev/null", ct)).Trim();
        if (output.Length < 64)
        {
            return null;
        }

        string hash = output[..64];
        return hash.All(Uri.IsHexDigit) ? hash.ToLowerInvariant() : null;
    }

    /// <summary>设备端路径是否存在（toybox 环境无 grep/expr，故用 sh 内建 test）。</summary>
    internal static async Task<bool> DevicePathExistsAsync(HdcDevice device, string remotePath, CancellationToken ct) =>
        (await device.ExecuteShellAsync($"test -e {remotePath} && echo YES || echo NO", ct))
            .Contains("YES", StringComparison.Ordinal);

    /// <summary>删除设备端临时路径（尽力而为：finally 中抛出的异常会替换用例本身的失败）。</summary>
    internal static async Task TryCleanupRemoteAsync(HdcDevice device, string remotePath)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            await device.ExecuteShellAsync($"rm -rf {remotePath}", cts.Token);
        }
        catch (Exception)
        {
        }
    }

    /// <summary>删除本地临时目录（尽力而为）。</summary>
    internal static void TryDeleteLocalDir(string path)
    {
        try
        {
            Directory.Delete(path, true);
        }
        catch (Exception)
        {
        }
    }
}

/// <summary>
/// 单个真机用例的会话作用域：一个 <see cref="HdcHost"/>、一台在线设备与预算取消令牌。
/// 预算耗尽即取消在途操作（用例不会无限期悬挂）；释放时先断开设备再释放宿主。
/// </summary>
internal sealed class RealDeviceSession : IAsyncDisposable
{
    private readonly HdcHost _host;
    private readonly CancellationTokenSource _cts;

    private RealDeviceSession(HdcHost host, HdcDevice device, CancellationTokenSource cts)
    {
        _host = host;
        Device = device;
        _cts = cts;
    }

    /// <summary>本用例的宿主实例。</summary>
    internal HdcHost Host => _host;

    /// <summary>已认证的真机设备。</summary>
    internal HdcDevice Device { get; }

    /// <summary>用例预算令牌。</summary>
    internal CancellationToken Token => _cts.Token;

    /// <summary>连接真机并开启用例作用域。</summary>
    /// <param name="budget">用例预算；超时后所有带本令牌的操作被取消。</param>
    /// <param name="configureHost">连接前配置宿主（订阅事件等），以便观察完整的状态迁移序列。</param>
    internal static async Task<RealDeviceSession> OpenAsync(TimeSpan budget, Action<HdcHost>? configureHost = null)
    {
        var cts = new CancellationTokenSource(budget);
        var host = new HdcHost();
        try
        {
            configureHost?.Invoke(host);
            HdcDevice device = await RealDeviceHarness.ConnectAsync(host, cts.Token);
            return new RealDeviceSession(host, device, cts);
        }
        catch
        {
            cts.Dispose();
            await host.DisposeAsync();
            throw;
        }
    }

    /// <summary>断开设备并释放宿主与预算令牌。</summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            await _host.DisconnectAsync(RealDeviceHarness.Target);
        }
        catch (Exception)
        {
        }

        await _host.DisposeAsync();
        _cts.Dispose();
    }
}
