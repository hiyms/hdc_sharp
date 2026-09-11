using System.Net;
using System.Net.Sockets;
using System.Text;
using HdcSharp.Protocol;
using Xunit;

namespace HdcSharp.Tests.RealDevice;

/// <summary>
/// 真机端口转发用例（spec §4.8）。
/// 依据 docs/verification/2026-09-11-real-device-connect.md §9：
/// fport 经隧道跑完整 HDC 会话（握手+认证+shell+40KB 载荷）、释放后本地监听端口不可连；
/// rport 由设备侧监听，设备内 ftpput 主动连本机 FTP 响应服务，双向断言。
/// </summary>
[Collection(RealDeviceCollectionDefinition.Name)]
[Trait("RealDevice", "true")]
public class RealDeviceForwardTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromMinutes(3);

    /// <summary>设备 44221 即 hdcd 本体，故经 fport 隧道可对设备自身再跑一次完整 HDC 会话。</summary>
    private const int DeviceHdcdPort = 44221;

    [RealDeviceFact]
    public async Task ForwardTcp_TunnelsFullHdcSessionAndLargePayload()
    {
        await using var session = await RealDeviceSession.OpenAsync(Budget);
        await using IForwardSession forward = await session.Device.ForwardTcpAsync(0, DeviceHdcdPort, session.Token);

        Assert.Equal(ForwardDirection.Forward, forward.Direction);
        Assert.True(forward.ListenPort > 0);
        Assert.True(forward.IsActive);

        string tunnelEndpoint = $"127.0.0.1:{forward.ListenPort}";
        await using (var tunnelHost = new HdcHost())
        {
            HdcDevice tunneled = await tunnelHost.ConnectAsync(
                tunnelEndpoint, RealDeviceHarness.CreateConnectOptions(), session.Token);
            try
            {
                Assert.Equal(HdcDeviceState.Online, tunneled.State);
                Assert.False(string.IsNullOrWhiteSpace(tunneled.DeviceName));

                string echo = await tunneled.ExecuteShellAsync("echo TUNNELED-SHELL-OK", session.Token);
                Assert.Equal("TUNNELED-SHELL-OK", echo.Trim());

                // 40KB 命令载荷（含认证往返）经隧道双向无损
                string payload = new string('X', 40_000);
                string size = await tunneled.ExecuteShellAsync($"echo -n {payload} | wc -c", session.Token);
                Assert.Equal("40000", size.Trim());
            }
            finally
            {
                await tunnelHost.DisconnectAsync(tunnelEndpoint, session.Token);
            }
        }

        await forward.DisposeAsync();
        Assert.False(forward.IsActive);
        await AssertLocalPortClosedAsync(forward.ListenPort);
    }

    [RealDeviceFact]
    public async Task ReverseTcp_DeviceListenerBridgesToLocalServiceBothDirections()
    {
        await using var session = await RealDeviceSession.OpenAsync(Budget);
        await using var responder = LocalFtpResponder.Start();

        await using IForwardSession reverse = await OpenReverseSessionAsync(session, responder.Port);
        int remotePort = reverse.ListenPort;
        Assert.Equal(ForwardDirection.Reverse, reverse.Direction);
        Assert.True(remotePort is > 0 and <= 65535);
        Assert.True(reverse.IsActive);

        // 设备侧确实在监听（设备无 grep，过滤在 C# 侧完成）；监听器出现可能滞后于 session 返回
        Assert.True(
            await WaitUntilAsync(
                async () => NetstatListsPort(await session.Device.ExecuteShellAsync("netstat -lnt", session.Token), remotePort),
                TimeSpan.FromSeconds(5),
                session.Token),
            $"设备侧未监听 {remotePort}");

        // 设备内 ftpput 经设备监听端口主动连本机服务
        string ftpputOutput = await session.Device.ExecuteShellAsync(
            $"ftpput -v -p {remotePort} 127.0.0.1 /dev/null probe.bin 2>&1", session.Token);

        bool loggedIn = await responder.WaitForAsync(
            commands => commands.Any(c => c.StartsWith("USER", StringComparison.Ordinal))
                        && commands.Any(c => c.StartsWith("PASS", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(15),
            session.Token);

        // 设备 → 主机：本机服务收到设备侧 FTP 命令
        Assert.True(loggedIn, $"本机服务未收到 USER/PASS，实际收到：{string.Join(" | ", responder.Commands)}");
        // 主机 → 设备：本机服务的问候与应答回到了设备侧 ftpput 的输出
        Assert.Contains("220", ftpputOutput, StringComparison.Ordinal);
        Assert.Contains("331", ftpputOutput, StringComparison.Ordinal);

        await reverse.DisposeAsync();
        Assert.False(reverse.IsActive);

        // 释放后设备侧监听端口必须消失（轮询容忍异步关闭）
        string lastNetstat = "";
        bool released = await WaitUntilAsync(
            async () =>
            {
                lastNetstat = await session.Device.ExecuteShellAsync("netstat -lnt", session.Token);
                return !NetstatListsPort(lastNetstat, remotePort);
            },
            TimeSpan.FromSeconds(10),
            session.Token);
        Assert.True(released, $"释放后设备侧仍监听 {remotePort}，netstat：{lastNetstat.Trim()}");
    }

    [RealDeviceFact]
    public async Task ReverseTcp_ZeroPort_ThrowsWithoutAutoAssignment()
    {
        await using var session = await RealDeviceSession.OpenAsync(Budget);

        // 设备侧监听端口无「自动分配」语义（verification §9.2）：0 必须在发帧前被拒绝
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => session.Device.ReverseTcpAsync(0, 12345, session.Token));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => session.Device.ReverseTcpAsync(12345, 0, session.Token));
    }

    /// <summary>释放转发后本地监听端口必须拒绝连接（多次重试以容忍监听器的异步关闭）。</summary>
    private static async Task AssertLocalPortClosedAsync(int port)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (true)
        {
            try
            {
                using var probe = new TcpClient();
                await probe.ConnectAsync(IPAddress.Loopback, port).WaitAsync(TimeSpan.FromSeconds(2));
                Assert.True(DateTime.UtcNow < deadline, $"释放后本地端口 {port} 仍可连接");
                await Task.Delay(200);
            }
            catch (Exception ex) when (ex is SocketException or TimeoutException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// 建立 rport：设备侧随机端口重试（netstat 看不到的占用状态只有 daemon 能定论，以它接受为准）。
    /// </summary>
    private static async Task<IForwardSession> OpenReverseSessionAsync(RealDeviceSession session, int localPort)
    {
        HdcException? lastError = null;
        for (int attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                return await session.Device.ReverseTcpAsync(
                    Random.Shared.Next(30000, 45000), localPort, session.Token);
            }
            catch (HdcException ex)
            {
                lastError = ex;
            }
        }

        throw lastError!;
    }

    /// <summary>netstat -lnt 中是否存在以 <c>:port</c> 结尾的 LISTEN 本地地址（toybox 输出为六列：Proto/Recv-Q/Send-Q/Local/Foreign/State）。</summary>
    private static bool NetstatListsPort(string netstat, int port)
    {
        string suffix = $":{port}";
        foreach (string line in netstat.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 6
                && tokens[5].Equals("LISTEN", StringComparison.OrdinalIgnoreCase)
                && tokens[3].EndsWith(suffix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>轮询条件直到成立或超时。</summary>
    private static async Task<bool> WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout, CancellationToken ct)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return true;
            }

            await Task.Delay(200, ct);
        }

        return await condition();
    }
}

/// <summary>
/// 本地最小 FTP 响应服务，作为 rport 的转发目标：发送 220 问候、按命令回 331/230/200，
/// 并记录设备侧发来的每一条命令（用于双向断言）。
/// </summary>
file sealed class LocalFtpResponder : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<string> _commands = [];
    private readonly Task _acceptLoop;

    private LocalFtpResponder(TcpListener listener)
    {
        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    /// <summary>本机监听端口（rport 的转发目标）。</summary>
    internal int Port { get; }

    /// <summary>已收到的设备侧命令快照。</summary>
    internal IReadOnlyList<string> Commands
    {
        get
        {
            lock (_commands)
            {
                return _commands.ToArray();
            }
        }
    }

    /// <summary>在 127.0.0.1 上随机端口启动服务。</summary>
    internal static LocalFtpResponder Start()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return new LocalFtpResponder(listener);
    }

    /// <summary>轮询等待已收到的命令满足条件；超时返回 false。</summary>
    internal async Task<bool> WaitForAsync(
        Func<IReadOnlyList<string>, bool> predicate, TimeSpan timeout, CancellationToken ct)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate(Commands))
            {
                return true;
            }

            await Task.Delay(100, ct);
        }

        return predicate(Commands);
    }

    /// <summary>释放服务：停止监听、取消在途连接并等待循环退出。</summary>
    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _listener.Stop();
        try
        {
            await _acceptLoop;
        }
        catch (Exception)
        {
        }

        _cts.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                using TcpClient client = await _listener.AcceptTcpClientAsync(_cts.Token);
                await ServeAsync(client, _cts.Token);
            }
        }
        catch (Exception)
        {
            // 释放导致的监听/连接终结
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken ct)
    {
        NetworkStream stream = client.GetStream();
        await stream.WriteAsync("220 hdcsharp-fake-ftp\r\n"u8.ToArray(), ct);
        byte[] buffer = new byte[4096];
        while (!ct.IsCancellationRequested)
        {
            int read = await stream.ReadAsync(buffer, ct);
            if (read == 0)
            {
                return;
            }

            string command = Encoding.ASCII.GetString(buffer, 0, read).Trim();
            lock (_commands)
            {
                _commands.Add(command);
            }

            string reply = command.StartsWith("USER", StringComparison.Ordinal) ? "331 need password\r\n"
                : command.StartsWith("PASS", StringComparison.Ordinal) ? "230 logged in\r\n"
                : command.StartsWith("QUIT", StringComparison.Ordinal) ? "221 bye\r\n"
                : "200 ok\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(reply), ct);
            if (command.StartsWith("QUIT", StringComparison.Ordinal))
            {
                return;
            }
        }
    }
}
