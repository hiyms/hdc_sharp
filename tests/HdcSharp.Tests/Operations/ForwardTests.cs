using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using HdcSharp.Protocol;
using HdcSharp.Security;
using HdcSharp.Tests.TestDoubles;
using Xunit;

namespace HdcSharp.Tests.Operations;

/// <summary>
/// Task 17 端口转发测试。线缆细节以 D:\work\developtools_hdc 源码为准
/// （src/common/forward.cpp、src/host/host_forward.cpp、hdc_rust/src/common/forward.rs）：
/// fport：WAKEUP(12) + FORWARD_CHECK([cid BE][8B 0][远端节点][NUL]) → CHECK_RESULT；
/// 本地每 accept 一条连接分配新 cid → ACTIVE_SLAVE([cid BE][8B 0][远端节点]) → ACTIVE_MASTER([cid BE]) → DATA([cid BE][字节])。
/// rport：FORWARD_INIT(载荷 "tcp:&lt;远端&gt; tcp:&lt;本地&gt;") 发往 daemon，daemon 侧监听后推 CHECK/ACTIVE_SLAVE。
/// </summary>
public class ForwardTests
{
    private static readonly TimeSpan TestBudget = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task ForwardTcp_LocalClientConnects_DataRoundTrips()
    {
        using var echo = new EchoServer();
        using var daemon = new FakeDaemon(new FakeDaemonOptions { ForwardEnabled = true });
        await using var host = new HdcHost();
        HdcDevice device = await ConnectAsync(host, daemon);

        await using IForwardSession session = await WithTimeoutAsync(
            device.ForwardTcpAsync(localPort: 0, remotePort: echo.Port), TestBudget);

        Assert.Equal(ForwardDirection.Forward, session.Direction);
        Assert.True(session.IsActive);
        Assert.True(session.ListenPort > 0);

        byte[] payload = "ping-through-forward"u8.ToArray();
        Assert.Equal(payload, await RoundTripAsync(session.ListenPort, payload));

        Frame[] forwardFrames = daemon.ReceivedFrames
            .Where(f => f.Command is HdcCommand.KernelWakeupSlavetask or HdcCommand.ForwardCheck)
            .ToArray();
        Assert.Equal(HdcCommand.KernelWakeupSlavetask, forwardFrames[0].Command);
        Assert.Empty(forwardFrames[0].Payload);
        Frame check = forwardFrames[1];
        Assert.True(check.Payload.Length >= 13);
        Assert.Equal(new byte[8], check.Payload[4..12]);
        string node = Encoding.UTF8.GetString(check.Payload, 12, check.Payload.Length - 12);
        Assert.Equal($"tcp:{echo.Port}", node.TrimEnd('\0'));

        Frame activeSlave = Assert.Single(daemon.ReceivedFrames, f => f.Command == HdcCommand.ForwardActiveSlave);
        Assert.Equal(check.ChannelId, activeSlave.ChannelId);
        string activeNode = Encoding.UTF8.GetString(activeSlave.Payload, 12, activeSlave.Payload.Length - 12);
        Assert.Equal($"tcp:{echo.Port}", activeNode.TrimEnd('\0'));
    }

    [Fact]
    public async Task ForwardTcp_CheckResultZeroFlag_StillAccepted()
    {
        // 上游 C++/Rust 两世代 daemon 对 TCP 节点都回结果字节 0；宿主必须以「载荷存在」判定可达，
        // 否则真机 fport 全部失败（src/common/forward.cpp:974、hdc_rust/src/common/forward.rs:1121-1130）
        using var echo = new EchoServer();
        using var daemon = new FakeDaemon(new FakeDaemonOptions { ForwardEnabled = true, ForwardCheckResultFlag = 0 });
        await using var host = new HdcHost();
        HdcDevice device = await ConnectAsync(host, daemon);

        await using IForwardSession session = await WithTimeoutAsync(
            device.ForwardTcpAsync(localPort: 0, remotePort: echo.Port), TestBudget);

        byte[] payload = "zero-flag"u8.ToArray();
        Assert.Equal(payload, await RoundTripAsync(session.ListenPort, payload));
    }

    [Fact]
    public async Task ForwardTcp_MultipleConcurrentConnections_IsolatedByCid()
    {
        using var echo = new EchoServer();
        using var daemon = new FakeDaemon(new FakeDaemonOptions { ForwardEnabled = true });
        await using var host = new HdcHost();
        HdcDevice device = await ConnectAsync(host, daemon);
        await using IForwardSession session = await WithTimeoutAsync(
            device.ForwardTcpAsync(localPort: 0, remotePort: echo.Port), TestBudget);

        byte[] first = Enumerable.Repeat((byte)0x41, 64 * 1024).ToArray();
        byte[] second = Enumerable.Repeat((byte)0x42, 96 * 1024).ToArray();
        byte[][] results = await WithTimeoutAsync(
            Task.WhenAll(RoundTripAsync(session.ListenPort, first), RoundTripAsync(session.ListenPort, second)), TestBudget);

        Assert.Equal(first, results[0]);
        Assert.Equal(second, results[1]);
        Assert.Equal(2, daemon.ReceivedFrames.Count(f => f.Command == HdcCommand.ForwardActiveSlave));
    }

    [Fact]
    public async Task ForwardTcp_BinaryIntegrity_LargePayload()
    {
        using var echo = new EchoServer();
        using var daemon = new FakeDaemon(new FakeDaemonOptions { ForwardEnabled = true });
        await using var host = new HdcHost();
        HdcDevice device = await ConnectAsync(host, daemon);
        await using IForwardSession session = await WithTimeoutAsync(
            device.ForwardTcpAsync(localPort: 0, remotePort: echo.Port), TestBudget);

        byte[] payload = RandomNumberGenerator.GetBytes(1_500_000);
        byte[] echoed = await WithTimeoutAsync(RoundTripAsync(session.ListenPort, payload), TestBudget);

        Assert.Equal(payload.Length, echoed.Length);
        Assert.Equal(payload, echoed);
    }

    [Fact]
    public async Task ForwardTcp_Dispose_StopsListeningAndClosesConnections()
    {
        using var echo = new EchoServer();
        using var daemon = new FakeDaemon(new FakeDaemonOptions { ForwardEnabled = true });
        await using var host = new HdcHost();
        HdcDevice device = await ConnectAsync(host, daemon);
        IForwardSession session = await WithTimeoutAsync(
            device.ForwardTcpAsync(localPort: 0, remotePort: echo.Port), TestBudget);

        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Closed += (_, _) => closed.TrySetResult();
        int port = session.ListenPort;
        uint channelId = Assert.Single(daemon.ReceivedFrames, f => f.Command == HdcCommand.ForwardCheck).ChannelId;

        await WithTimeoutAsync(session.DisposeAsync().AsTask(), TestBudget);

        Assert.False(session.IsActive);
        await WithTimeoutAsync(closed.Task, TestBudget);
        await Assert.ThrowsAnyAsync<SocketException>(async () =>
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
        });
        await WaitForAsync(
            () => daemon.ReceivedFrames.Any(f => f.Command == HdcCommand.KernelChannelClose && f.ChannelId == channelId),
            TestBudget);
    }

    [Fact]
    public async Task ForwardTcp_RemoteRejects_ThrowsHdcException()
    {
        using var echo = new EchoServer();
        using var daemon = new FakeDaemon(new FakeDaemonOptions { ForwardEnabled = true, ForwardRejectCheck = true });
        await using var host = new HdcHost();
        HdcDevice device = await ConnectAsync(host, daemon);

        // 上游把「CHECK_RESULT 载荷为空（仅 cid）」视为非法/失败（src/common/forward.cpp:987-990）
        await Assert.ThrowsAsync<HdcException>(
            () => WithTimeoutAsync(device.ForwardTcpAsync(localPort: 0, remotePort: echo.Port), TestBudget));
    }

    [Fact]
    public async Task ReverseTcp_LocalServiceReached_DataRoundTrips()
    {
        using var localService = new EchoServer();
        using var daemon = new FakeDaemon(new FakeDaemonOptions { ForwardReverseEnabled = true });
        await using var host = new HdcHost();
        HdcDevice device = await ConnectAsync(host, daemon);
        int devicePort = GetFreePort();

        await using IForwardSession session = await WithTimeoutAsync(
            device.ReverseTcpAsync(remotePort: devicePort, localPort: localService.Port), TestBudget);

        Assert.Equal(ForwardDirection.Reverse, session.Direction);
        Assert.Equal(devicePort, session.ListenPort);
        Assert.True(session.IsActive);

        byte[] payload = "reverse-through-forward"u8.ToArray();
        Assert.Equal(payload, await RoundTripAsync(devicePort, payload));

        Frame init = Assert.Single(daemon.ReceivedFrames, f => f.Command == HdcCommand.ForwardInit);
        Assert.Equal($"tcp:{devicePort} tcp:{localService.Port}", Encoding.UTF8.GetString(init.Payload));
    }

    [Fact]
    public async Task ForwardTcp_RemoteClosesSingleConnection_OtherConnectionsSurvive()
    {
        using var echo = new EchoServer(closeOn: 0xFF);
        using var daemon = new FakeDaemon(new FakeDaemonOptions { ForwardEnabled = true });
        await using var host = new HdcHost();
        HdcDevice device = await ConnectAsync(host, daemon);
        await using IForwardSession session = await WithTimeoutAsync(
            device.ForwardTcpAsync(localPort: 0, remotePort: echo.Port), TestBudget);

        using var firstClient = new TcpClient();
        await firstClient.ConnectAsync(IPAddress.Loopback, session.ListenPort);
        using var secondClient = new TcpClient();
        await secondClient.ConnectAsync(IPAddress.Loopback, session.ListenPort);
        NetworkStream first = firstClient.GetStream();
        NetworkStream second = secondClient.GetStream();

        Assert.Equal("one"u8.ToArray(), await RoundTripAsync(first, "one"u8.ToArray()));
        Assert.Equal("two"u8.ToArray(), await RoundTripAsync(second, "two"u8.ToArray()));

        // 远端（回声服务）收到 0xFF 即断开 → daemon 推 FREE_CONTEXT → 宿主只关掉这一条本地连接
        await first.WriteAsync(new byte[] { 0xFF });
        Assert.True(await WithTimeoutAsync(WaitForClosedAsync(first), TestBudget));

        Assert.Equal("still-alive"u8.ToArray(), await RoundTripAsync(second, "still-alive"u8.ToArray()));
        Assert.True(session.IsActive);
    }

    [Fact]
    public async Task Forward_CancellationMidStream_CleansUpChannel()
    {
        using var echo = new EchoServer();
        using var daemon = new FakeDaemon(new FakeDaemonOptions { ForwardEnabled = true });
        await using var host = new HdcHost();
        HdcDevice device = await ConnectAsync(host, daemon);
        using var cts = new CancellationTokenSource();

        await using IForwardSession session = await WithTimeoutAsync(
            device.ForwardTcpAsync(localPort: 0, remotePort: echo.Port, cts.Token), TestBudget);
        uint channelId = Assert.Single(daemon.ReceivedFrames, f => f.Command == HdcCommand.ForwardCheck).ChannelId;

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, session.ListenPort);
        NetworkStream stream = client.GetStream();
        Assert.Equal("before-cancel"u8.ToArray(), await RoundTripAsync(stream, "before-cancel"u8.ToArray()));

        cts.Cancel();

        await WaitForAsync(() => !session.IsActive, TestBudget);
        await WaitForAsync(
            () => daemon.ReceivedFrames.Any(f => f.Command == HdcCommand.KernelChannelClose && f.ChannelId == channelId),
            TestBudget);
        await Assert.ThrowsAnyAsync<SocketException>(async () =>
        {
            using var probe = new TcpClient();
            await probe.ConnectAsync(IPAddress.Loopback, session.ListenPort);
        });
    }

    private static async Task<HdcDevice> ConnectAsync(HdcHost host, FakeDaemon daemon)
    {
        return await WithTimeoutAsync(
            host.ConnectAsync($"127.0.0.1:{daemon.Port}", new ConnectOptions
            {
                KeyStore = new UnusedKeyStore(),
                AuthTimeout = TestBudget,
            }),
            TestBudget);
    }

    private static async Task<byte[]> RoundTripAsync(int port, byte[] payload)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        return await RoundTripAsync(client.GetStream(), payload);
    }

    private static async Task<byte[]> RoundTripAsync(NetworkStream stream, byte[] payload)
    {
        using var cts = new CancellationTokenSource(TestBudget);
        Task<byte[]> read = ReadExactlyAsync(stream, payload.Length, cts.Token);
        await stream.WriteAsync(payload, cts.Token);
        return await read;
    }

    private static async Task<byte[]> ReadExactlyAsync(NetworkStream stream, int count, CancellationToken ct)
    {
        byte[] buffer = new byte[count];
        int read = 0;
        while (read < count)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(read), ct);
            if (n == 0)
            {
                throw new IOException($"连接在读满 {count} 字节前被对端关闭（已读 {read}）");
            }

            read += n;
        }

        return buffer;
    }

    private static async Task<bool> WaitForClosedAsync(NetworkStream stream)
    {
        byte[] buffer = new byte[1];
        try
        {
            return await stream.ReadAsync(buffer) == 0;
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            return true;
        }
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException("条件未在期限内满足");
    }

    private static async Task<T> WithTimeoutAsync<T>(Task<T> task, TimeSpan timeout)
    {
        if (await Task.WhenAny(task, Task.Delay(timeout)) != task)
        {
            throw new TimeoutException($"操作未在 {timeout} 内完成");
        }

        return await task;
    }

    private static async Task WithTimeoutAsync(Task task, TimeSpan timeout)
    {
        if (await Task.WhenAny(task, Task.Delay(timeout)) != task)
        {
            throw new TimeoutException($"操作未在 {timeout} 内完成");
        }

        await task;
    }

    private static int GetFreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private sealed class UnusedKeyStore : IHostKeyStore
    {
        public RSA GetPrivateKey() => throw new InvalidOperationException("测试未启用认证，不应访问密钥库");

        public string GetPublicKeyPem() => throw new InvalidOperationException("测试未启用认证，不应访问密钥库");
    }
}
