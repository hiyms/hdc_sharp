using System.Net;
using System.Net.Sockets;
using HdcSharp.Protocol;
using HdcSharp.Protocol.Messages;
using HdcSharp.Transport;
using Xunit;

namespace HdcSharp.Tests.Transport;

public class HdcConnectionTests
{
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan NoFrameWindow = TimeSpan.FromMilliseconds(300);

    private static readonly byte[] ZeroPayload = [0];

    private static readonly byte[] OnePayload = [1];

    private static async Task<Pair> CreatePairAsync(HdcConnectionOptions? options = null)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            TcpClient server = await listener.AcceptTcpClientAsync();
            return new Pair(new HdcConnection(client, options ?? new HdcConnectionOptions()), server);
        }
        finally
        {
            listener.Stop();
        }
    }

    private sealed class Pair : IAsyncDisposable
    {
        public Pair(HdcConnection conn, TcpClient server)
        {
            Conn = conn;
            Server = server;
            ServerStream = server.GetStream();
        }

        public HdcConnection Conn { get; }

        public TcpClient Server { get; }

        public NetworkStream ServerStream { get; }

        public CancellationTokenSource RunCts { get; } = new();

        public Task RunTask { get; private set; } = Task.CompletedTask;

        public void StartRun()
        {
            RunTask = Conn.RunAsync(RunCts.Token);
        }

        public async ValueTask DisposeAsync()
        {
            RunCts.Cancel();
            try
            {
                await RunTask;
            }
            catch (HdcException)
            {
            }
            catch (OperationCanceledException)
            {
            }

            await Conn.DisposeAsync();
            Server.Dispose();
            RunCts.Dispose();
        }
    }

    private sealed class FrameReader
    {
        private readonly NetworkStream _stream;
        private readonly FrameDecoder _decoder = new();
        private readonly byte[] _buffer = new byte[4096];

        public FrameReader(NetworkStream stream)
        {
            _stream = stream;
        }

        /// <summary>读取下一帧；跨调用保留未消费字节，支持一次到达多帧（TCP 粘帧）不被丢弃。</summary>
        /// <param name="timeout">单帧超时。</param>
        public async Task<Frame> ReadAsync(TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            while (true)
            {
                if (_decoder.TryRead(out Frame frame))
                {
                    return frame;
                }

                int n = await _stream.ReadAsync(_buffer, cts.Token);
                Assert.True(n > 0, "对端在超时内未发送完整帧");
                _decoder.Append(_buffer.AsSpan(0, n));
            }
        }
    }

    private static async Task<byte[]> ReadOneFrameRawAsync(NetworkStream stream)
    {
        var decoder = new FrameDecoder();
        var buffer = new byte[4096];
        using var received = new MemoryStream();
        using var timeout = new CancellationTokenSource(FrameTimeout);
        while (!decoder.TryRead(out _))
        {
            int n = await stream.ReadAsync(buffer, timeout.Token);
            Assert.True(n > 0, "对端在超时内未发送完整帧");
            received.Write(buffer, 0, n);
            decoder.Append(buffer.AsSpan(0, n));
        }

        return received.ToArray();
    }

    private static Frame DecodeOne(byte[] raw)
    {
        var decoder = new FrameDecoder();
        decoder.Append(raw);
        Assert.True(decoder.TryRead(out Frame frame), "收到的字节无法解出完整帧");
        return frame;
    }

    private static async Task AssertNoFrameAsync(NetworkStream stream)
    {
        var buffer = new byte[4096];
        using var window = new CancellationTokenSource(NoFrameWindow);
        try
        {
            int n = await stream.ReadAsync(buffer, window.Token);
            Assert.True(n == 0, $"窗口内意外收到 {n} 字节");
        }
        catch (OperationCanceledException)
        {
            // 窗口内没有任何数据到达，符合预期
        }
    }

    private static TaskCompletionSource<Frame> CreateFrameSource()
    {
        return new TaskCompletionSource<Frame>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    [Fact]
    public async Task SessionId_IsNonZero()
    {
        await using Pair pair = await CreatePairAsync();
        Assert.NotEqual(0u, pair.Conn.SessionId);
    }

    [Fact]
    public async Task SendAsync_FirstFrame_RawBytesContainHwHeader()
    {
        await using Pair pair = await CreatePairAsync();
        await pair.Conn.SendAsync(42, HdcCommand.KernelEcho, "hi"u8.ToArray());
        byte[] raw = await ReadOneFrameRawAsync(pair.ServerStream);
        Assert.Equal(0x48, raw[0]);
        Assert.Equal(0x57, raw[1]);
        Frame frame = DecodeOne(raw);
        Assert.Equal(42u, frame.ChannelId);
        Assert.Equal(HdcCommand.KernelEcho, frame.Command);
        Assert.Equal("hi"u8.ToArray(), frame.Payload);
    }

    [Fact]
    public async Task SendAsync_ConcurrentCalls_AllFramesIntact()
    {
        await using Pair pair = await CreatePairAsync();
        const int count = 32;
        var payloads = new Dictionary<uint, byte[]>();
        var sends = new List<Task>();
        for (uint i = 0; i < count; i++)
        {
            var payload = new byte[64];
            Random.Shared.NextBytes(payload);
            payload[0] = (byte)i;
            payloads[100 + i] = payload;
            sends.Add(pair.Conn.SendAsync(100 + i, HdcCommand.KernelEchoRaw, payload));
        }

        await Task.WhenAll(sends);
        var reader = new FrameReader(pair.ServerStream);
        for (int i = 0; i < count; i++)
        {
            Frame frame = await reader.ReadAsync(FrameTimeout);
            Assert.Equal(HdcCommand.KernelEchoRaw, frame.Command);
            Assert.True(payloads.TryGetValue(frame.ChannelId, out byte[]? expected), $"未预期的通道号 {frame.ChannelId}");
            Assert.NotNull(expected);
            Assert.Equal(expected, frame.Payload);
        }
    }

    [Fact]
    public async Task RunAsync_DeliversFrameForRegisteredChannel()
    {
        await using Pair pair = await CreatePairAsync();
        pair.Conn.RegisterChannel(7);
        var received = CreateFrameSource();
        pair.Conn.FrameReceived += f => received.TrySetResult(f);
        pair.StartRun();
        await pair.ServerStream.WriteAsync(FrameCodec.Encode(7, HdcCommand.KernelEcho, "abc"u8.ToArray()));
        Frame frame = await received.Task.WaitAsync(FrameTimeout);
        Assert.Equal(7u, frame.ChannelId);
        Assert.Equal(HdcCommand.KernelEcho, frame.Command);
        Assert.Equal("abc"u8.ToArray(), frame.Payload);
        await AssertNoFrameAsync(pair.ServerStream);
    }

    [Fact]
    public async Task ChannelClose_One_FiresEventAndEchoesDecrement()
    {
        await using Pair pair = await CreatePairAsync();
        pair.Conn.RegisterChannel(9);
        var closed = CreateFrameSource();
        var received = CreateFrameSource();
        pair.Conn.ChannelClosed += f => closed.TrySetResult(f);
        pair.Conn.FrameReceived += f => received.TrySetResult(f);
        pair.StartRun();
        await pair.ServerStream.WriteAsync(FrameCodec.Encode(9, HdcCommand.KernelChannelClose, OnePayload));
        Frame closedFrame = await closed.Task.WaitAsync(FrameTimeout);
        Assert.Equal(9u, closedFrame.ChannelId);
        Assert.Equal(HdcCommand.KernelChannelClose, closedFrame.Command);
        Assert.Equal(OnePayload, closedFrame.Payload);
        var echoReader = new FrameReader(pair.ServerStream);
        Frame echo = await echoReader.ReadAsync(FrameTimeout);
        Assert.Equal(9u, echo.ChannelId);
        Assert.Equal(HdcCommand.KernelChannelClose, echo.Command);
        Assert.Equal(ZeroPayload, echo.Payload);
        Frame receivedFrame = await received.Task.WaitAsync(FrameTimeout);
        Assert.Equal(HdcCommand.KernelChannelClose, receivedFrame.Command);
    }

    [Fact]
    public async Task ChannelClose_Zero_FiresEventWithoutEcho()
    {
        await using Pair pair = await CreatePairAsync();
        pair.Conn.RegisterChannel(9);
        var closed = CreateFrameSource();
        pair.Conn.ChannelClosed += f => closed.TrySetResult(f);
        pair.StartRun();
        await pair.ServerStream.WriteAsync(FrameCodec.Encode(9, HdcCommand.KernelChannelClose, ZeroPayload));
        Frame closedFrame = await closed.Task.WaitAsync(FrameTimeout);
        Assert.Equal(9u, closedFrame.ChannelId);
        Assert.Equal(HdcCommand.KernelChannelClose, closedFrame.Command);
        Assert.Equal(ZeroPayload, closedFrame.Payload);
        await AssertNoFrameAsync(pair.ServerStream);
    }

    [Fact]
    public async Task UnknownChannel_Command_EchoesCloseZeroAndStillDelivers()
    {
        await using Pair pair = await CreatePairAsync();
        var received = CreateFrameSource();
        pair.Conn.FrameReceived += f => received.TrySetResult(f);
        pair.StartRun();
        await pair.ServerStream.WriteAsync(FrameCodec.Encode(123, HdcCommand.UnityExecute, "param get"u8.ToArray()));
        Frame echo = await new FrameReader(pair.ServerStream).ReadAsync(FrameTimeout);
        Assert.Equal(123u, echo.ChannelId);
        Assert.Equal(HdcCommand.KernelChannelClose, echo.Command);
        Assert.Equal(ZeroPayload, echo.Payload);
        Frame frame = await received.Task.WaitAsync(FrameTimeout);
        Assert.Equal(HdcCommand.UnityExecute, frame.Command);
    }

    [Fact]
    public async Task UnknownChannel_ControlCommands_DoNotEcho()
    {
        // 原版 server.cpp:859-869 在通道查找前处理握手与心跳，不应触发未知通道回发
        await using Pair pair = await CreatePairAsync();
        pair.StartRun();
        await pair.ServerStream.WriteAsync(FrameCodec.Encode(0, HdcCommand.KernelHandshake, "OHOS HDC"u8.ToArray()));
        var heartbeat = new HeartbeatMsg { Count = 1 };
        await pair.ServerStream.WriteAsync(FrameCodec.Encode(0, HdcCommand.HeartbeatMsg, heartbeat.Serialize()));
        await AssertNoFrameAsync(pair.ServerStream);
    }

    [Fact]
    public async Task BadFrame_FailsRunWithHdcExceptionAndCloses()
    {
        await using Pair pair = await CreatePairAsync();
        pair.StartRun();
        await pair.ServerStream.WriteAsync(new byte[16]);
        await Assert.ThrowsAsync<HdcException>(() => pair.RunTask);
        Assert.True(pair.Conn.IsClosed);
    }

    [Fact]
    public async Task RemoteReset_FailsRunWithDisconnectedException()
    {
        await using Pair pair = await CreatePairAsync();
        pair.StartRun();
        pair.Server.LingerState = new LingerOption(true, 0);
        pair.Server.Close();
        HdcException ex = await Assert.ThrowsAsync<HdcException>(() => pair.RunTask);
        Assert.Contains("连接已断开", ex.Message);
        Assert.True(pair.Conn.IsClosed);
    }

    [Fact]
    public async Task RemoteGracefulClose_FailsRunWithDisconnectedException()
    {
        // Windows 回环上对端 RST 同样呈现为 0 字节读，与优雅 FIN 从读路径不可区分（SO_ERROR
        // 与写探测亦无法区分），故两种关闭统一按断开语义上报
        await using Pair pair = await CreatePairAsync();
        pair.StartRun();
        pair.Server.Close();
        HdcException ex = await Assert.ThrowsAsync<HdcException>(() => pair.RunTask);
        Assert.Contains("连接已断开", ex.Message);
        Assert.True(pair.Conn.IsClosed);
    }

    [Fact]
    public async Task CancelRun_CompletesGracefully()
    {
        await using Pair pair = await CreatePairAsync();
        pair.StartRun();
        pair.RunCts.Cancel();
        await pair.RunTask;
        Assert.True(pair.Conn.IsClosed);
    }

    [Fact]
    public async Task Heartbeat_SendsIncrementingCounts()
    {
        var options = new HdcConnectionOptions { HeartbeatInterval = TimeSpan.FromMilliseconds(80) };
        await using Pair pair = await CreatePairAsync(options);
        pair.Conn.StartHeartbeat();
        var reader = new FrameReader(pair.ServerStream);
        for (ulong expected = 1; expected <= 3; expected++)
        {
            Frame frame = await reader.ReadAsync(FrameTimeout);
            Assert.Equal(0u, frame.ChannelId);
            Assert.Equal(HdcCommand.HeartbeatMsg, frame.Command);
            Assert.Equal(expected, HeartbeatMsg.Parse(frame.Payload).Count);
        }
    }

    [Fact]
    public async Task Heartbeat_StopsAfterDispose()
    {
        var options = new HdcConnectionOptions { HeartbeatInterval = TimeSpan.FromMilliseconds(80) };
        await using Pair pair = await CreatePairAsync(options);
        pair.Conn.StartHeartbeat();
        var reader = new FrameReader(pair.ServerStream);
        for (int i = 0; i < 3; i++)
        {
            await reader.ReadAsync(FrameTimeout);
        }

        await Task.Delay(40);
        await pair.Conn.DisposeAsync();
        await AssertNoFrameAsync(pair.ServerStream);
        Assert.True(pair.Conn.IsClosed);
    }

    [Fact]
    public async Task CloseChannelAsync_SendsCloseWithZeroPayload()
    {
        await using Pair pair = await CreatePairAsync();
        await pair.Conn.CloseChannelAsync(7);
        Frame frame = await new FrameReader(pair.ServerStream).ReadAsync(FrameTimeout);
        Assert.Equal(7u, frame.ChannelId);
        Assert.Equal(HdcCommand.KernelChannelClose, frame.Command);
        Assert.Equal(ZeroPayload, frame.Payload);
    }

    [Fact]
    public async Task SendAsync_AfterDispose_Throws()
    {
        await using Pair pair = await CreatePairAsync();
        await pair.Conn.DisposeAsync();
        Assert.True(pair.Conn.IsClosed);
        await Assert.ThrowsAsync<HdcException>(
            () => pair.Conn.SendAsync(1, HdcCommand.KernelEcho, "x"u8.ToArray()));
    }

    [Fact]
    public async Task RunAsync_CalledTwice_Throws()
    {
        await using Pair pair = await CreatePairAsync();
        pair.StartRun();
        await Assert.ThrowsAsync<InvalidOperationException>(() => pair.Conn.RunAsync(pair.RunCts.Token));
        pair.RunCts.Cancel();
        await pair.RunTask;
    }

    [Fact]
    public async Task FrameReceived_HandlerException_DoesNotKillConnection()
    {
        await using Pair pair = await CreatePairAsync();
        pair.Conn.RegisterChannel(3);
        bool thrown = false;
        var second = CreateFrameSource();
        pair.Conn.FrameReceived += f =>
        {
            if (!thrown)
            {
                thrown = true;
                throw new InvalidOperationException("处理器故意抛错");
            }

            second.TrySetResult(f);
        };
        pair.StartRun();
        await pair.ServerStream.WriteAsync(FrameCodec.Encode(3, HdcCommand.KernelEcho, "a"u8.ToArray()));
        await pair.ServerStream.WriteAsync(FrameCodec.Encode(3, HdcCommand.KernelEcho, "b"u8.ToArray()));
        Frame frame = await second.Task.WaitAsync(FrameTimeout);
        Assert.Equal("b"u8.ToArray(), frame.Payload);
        Assert.False(pair.Conn.IsClosed);
    }
}
