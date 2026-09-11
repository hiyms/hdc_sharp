using System.Net;
using System.Net.Sockets;
using HdcSharp.Protocol;
using HdcSharp.Security;
using HdcSharp.Tests.TestDoubles;
using HdcSharp.Transport;
using Xunit;

namespace HdcSharp.Tests.Security;

public class AuthHandlerTests
{
    private static readonly TimeSpan TestBudget = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan AuthTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task NoAuth_RustDaemon_ReturnsCapabilities()
    {
        using var daemon = new FakeDaemon(new FakeDaemonOptions { Generation = DaemonGeneration.Rust, RequireAuth = false });
        using var keys = new TempKeyStore();
        using var cts = new CancellationTokenSource(TestBudget);
        await using LoopbackSession session = await LoopbackSession.ConnectAsync(daemon.Port, cts.Token);

        DaemonCapabilities caps = await RunAuthAsync(session, daemon.Port, keys.Store, AuthTimeout, cts.Token);

        Assert.True(caps.Authenticated);
        Assert.Equal("fake-dev", caps.DeviceName);
        Assert.Equal(DaemonGeneration.Rust, caps.Generation);
        Assert.Equal(AuthScheme.Unknown, caps.Scheme);
        Assert.False(caps.Heartbeat);
        Assert.Null(caps.ErrorMessage);
        Assert.Null(daemon.RecordedHostPublicKeyPem);
    }

    [Fact]
    public async Task AuthRequired_RustDaemon_Pkcs1SignatureAccepted()
    {
        using var daemon = new FakeDaemon(new FakeDaemonOptions { Generation = DaemonGeneration.Rust, RequireAuth = true });
        using var keys = new TempKeyStore();
        using var cts = new CancellationTokenSource(TestBudget);
        await using LoopbackSession session = await LoopbackSession.ConnectAsync(daemon.Port, cts.Token);
        int authorizationRequests = 0;

        DaemonCapabilities caps = await RunAuthAsync(
            session, daemon.Port, keys.Store, AuthTimeout, cts.Token, () => Interlocked.Increment(ref authorizationRequests));

        Assert.True(caps.Authenticated);
        Assert.Equal(DaemonGeneration.Rust, caps.Generation);
        Assert.Equal(AuthScheme.Pkcs1, caps.Scheme);
        Assert.Equal("fake-dev", caps.DeviceName);
        Assert.True(daemon.SignatureVerified);
        Assert.Equal(1, authorizationRequests);
        Assert.NotNull(daemon.RecordedHostPublicKeyPem);
        Assert.Contains("BEGIN PUBLIC KEY", daemon.RecordedHostPublicKeyPem);
    }

    [Fact]
    public async Task AuthRequired_CppDaemon_PssSignatureAccepted()
    {
        using var daemon = new FakeDaemon(new FakeDaemonOptions { Generation = DaemonGeneration.Cpp, RequireAuth = true });
        using var keys = new TempKeyStore();
        using var cts = new CancellationTokenSource(TestBudget);
        await using LoopbackSession session = await LoopbackSession.ConnectAsync(daemon.Port, cts.Token);

        DaemonCapabilities caps = await RunAuthAsync(session, daemon.Port, keys.Store, AuthTimeout, cts.Token);

        Assert.True(caps.Authenticated);
        Assert.Equal(DaemonGeneration.Cpp, caps.Generation);
        Assert.Equal(AuthScheme.PssSha512, caps.Scheme);
        Assert.True(caps.Heartbeat);
        Assert.True(daemon.SignatureVerified);
    }

    [Fact]
    public async Task BadBanner_Throws()
    {
        using var daemon = new FakeDaemon(new FakeDaemonOptions { SendBadBanner = true });
        using var keys = new TempKeyStore();
        using var cts = new CancellationTokenSource(TestBudget);
        await using LoopbackSession session = await LoopbackSession.ConnectAsync(daemon.Port, cts.Token);

        HdcException ex = await Assert.ThrowsAsync<HdcException>(
            () => RunAuthAsync(session, daemon.Port, keys.Store, AuthTimeout, cts.Token));

        Assert.Contains("banner", ex.Message);
    }

    [Fact]
    public async Task AuthTimeout_Throws()
    {
        using var daemon = new FakeDaemon(new FakeDaemonOptions { RequireAuth = true, StallAfterPublicKey = true });
        using var keys = new TempKeyStore();
        using var cts = new CancellationTokenSource(TestBudget);
        await using LoopbackSession session = await LoopbackSession.ConnectAsync(daemon.Port, cts.Token);

        HdcException ex = await Assert.ThrowsAsync<HdcException>(
            () => RunAuthAsync(session, daemon.Port, keys.Store, TimeSpan.FromMilliseconds(500), cts.Token));

        Assert.Contains("认证超时", ex.Message);
    }

    [Fact]
    public async Task DisconnectDuringAuth_ThrowsDisconnected()
    {
        // 认证中途断开必须立即以“连接已断开”收尾（而不是等满 5s 认证超时），覆盖连接的终结事件
        using var daemon = new FakeDaemon(new FakeDaemonOptions { RequireAuth = true, StallAfterPublicKey = true });
        using var keys = new TempKeyStore();
        using var cts = new CancellationTokenSource(TestBudget);
        await using LoopbackSession session = await LoopbackSession.ConnectAsync(daemon.Port, cts.Token);
        var challengeSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<DaemonCapabilities> authTask = RunAuthAsync(
            session, daemon.Port, keys.Store, AuthTimeout, cts.Token, () => challengeSeen.TrySetResult());
        await challengeSeen.Task.WaitAsync(AuthTimeout, cts.Token);

        daemon.Dispose();

        HdcException ex = await Assert.ThrowsAsync<HdcException>(() => authTask);
        Assert.Contains("连接已断开", ex.Message);
    }

    private static Task<DaemonCapabilities> RunAuthAsync(
        LoopbackSession session, int port, IHostKeyStore keys, TimeSpan timeout, CancellationToken ct, Action? onAuthorizationRequested = null)
    {
        return AuthHandler.RunAsync(
            session.Connection, $"127.0.0.1:{port}", keys, timeout, requestHeartbeat: true, onAuthorizationRequested, ct);
    }

    private sealed class LoopbackSession : IAsyncDisposable
    {
        private readonly CancellationTokenSource _runCts;
        private readonly Task _runTask;

        private LoopbackSession(HdcConnection connection, CancellationTokenSource runCts, Task runTask)
        {
            Connection = connection;
            _runCts = runCts;
            _runTask = runTask;
        }

        public HdcConnection Connection { get; }

        public static async Task<LoopbackSession> ConnectAsync(int port, CancellationToken ct)
        {
            var client = new TcpClient();
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, port, ct);
                var connection = new HdcConnection(client);
                var runCts = new CancellationTokenSource();
                Task runTask = connection.RunAsync(runCts.Token);
                return new LoopbackSession(connection, runCts, runTask);
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            _runCts.Cancel();
            try
            {
                await _runTask;
            }
            catch (HdcException)
            {
            }
            catch (OperationCanceledException)
            {
            }

            await Connection.DisposeAsync();
            _runCts.Dispose();
        }
    }

    private sealed class TempKeyStore : IDisposable
    {
        private readonly string _directory;

        public TempKeyStore()
        {
            _directory = Path.Combine(Path.GetTempPath(), "hdc-sharp-auth-" + Guid.NewGuid().ToString("N"));
            Store = new FileHostKeyStore(_directory);
        }

        public FileHostKeyStore Store { get; }

        public void Dispose()
        {
            Store.Dispose();
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
    }
}
