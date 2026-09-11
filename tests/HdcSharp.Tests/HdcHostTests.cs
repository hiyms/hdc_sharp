using HdcSharp.Protocol;
using HdcSharp.Security;
using HdcSharp.Tests.TestDoubles;
using HdcSharp.Transport;
using Xunit;

namespace HdcSharp.Tests;

public class HdcHostTests
{
    private static readonly TimeSpan TestBudget = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan HeartbeatWait = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Connect_NoAuthRustDaemon_DeviceOnlineWithDeviceName()
    {
        using var daemon = new FakeDaemon(new FakeDaemonOptions { Generation = DaemonGeneration.Rust, RequireAuth = false });
        using var keys = new TempKeyStore();
        await using var host = new HdcHost();
        string endpoint = $"127.0.0.1:{daemon.Port}";

        HdcDevice device = await WithTimeoutAsync(host.ConnectAsync(endpoint, CreateOptions(keys)), TestBudget);

        Assert.Equal(HdcDeviceState.Online, device.State);
        Assert.Equal("fake-dev", device.DeviceName);
        Assert.Equal(DaemonGeneration.Rust, device.Generation);
        Assert.Equal(endpoint, device.ConnectKey);
        Assert.Equal(endpoint, device.Endpoint);
        Assert.NotEqual(0u, device.SessionId);
        Assert.Same(device, host.FindDevice(endpoint));
        Assert.Same(device, Assert.Single(host.ConnectedDevices));
    }

    [Fact]
    public async Task Connect_AuthRequiredRustDaemon_Pkcs1Accepted()
    {
        using var daemon = new FakeDaemon(new FakeDaemonOptions { Generation = DaemonGeneration.Rust, RequireAuth = true });
        using var keys = new TempKeyStore();
        await using var host = new HdcHost();
        string endpoint = $"127.0.0.1:{daemon.Port}";

        HdcDevice device = await WithTimeoutAsync(host.ConnectAsync(endpoint, CreateOptions(keys)), TestBudget);

        Assert.Equal(HdcDeviceState.Online, device.State);
        Assert.Equal("fake-dev", device.DeviceName);
        Assert.Equal(DaemonGeneration.Rust, device.Generation);
        Assert.True(daemon.SignatureVerified);
        Assert.NotNull(daemon.RecordedHostPublicKeyPem);
    }

    [Fact]
    public async Task Connect_AuthRequiredCppDaemon_PssAccepted_HeartbeatSent()
    {
        using var daemon = new FakeDaemon(new FakeDaemonOptions
        {
            Generation = DaemonGeneration.Cpp,
            RequireAuth = true,
            DeviceName = "fake-dev-cpp",
        });
        using var keys = new TempKeyStore();
        await using var host = new HdcHost(TimeSpan.FromMilliseconds(50));
        string endpoint = $"127.0.0.1:{daemon.Port}";

        HdcDevice device = await WithTimeoutAsync(host.ConnectAsync(endpoint, CreateOptions(keys)), TestBudget);

        Assert.Equal(HdcDeviceState.Online, device.State);
        Assert.Equal("fake-dev-cpp", device.DeviceName);
        Assert.Equal(DaemonGeneration.Cpp, device.Generation);
        Assert.True(device.Capabilities.Heartbeat);
        Assert.True(daemon.SignatureVerified);
        Assert.True(await WaitForCommandAsync(daemon, HdcCommand.HeartbeatMsg, HeartbeatWait), "C++ 世代连接成功后应发送心跳帧");
    }

    [Fact]
    public async Task Connect_BadBanner_ThrowsAndNoDeviceRegistered()
    {
        using var daemon = new FakeDaemon(new FakeDaemonOptions { SendBadBanner = true });
        using var keys = new TempKeyStore();
        await using var host = new HdcHost();
        string endpoint = $"127.0.0.1:{daemon.Port}";

        HdcException ex = await WithTimeoutAsync(
            Assert.ThrowsAsync<HdcException>(() => host.ConnectAsync(endpoint, CreateOptions(keys))),
            TestBudget);

        Assert.NotEmpty(ex.Message);
        Assert.Empty(host.ConnectedDevices);
        Assert.Null(host.FindDevice(endpoint));
    }

    [Fact]
    public async Task Connect_Duplicate_Throws()
    {
        using var daemon = new FakeDaemon(new FakeDaemonOptions { RequireAuth = false });
        using var keys = new TempKeyStore();
        await using var host = new HdcHost();
        string endpoint = $"127.0.0.1:{daemon.Port}";
        HdcDevice first = await WithTimeoutAsync(host.ConnectAsync(endpoint, CreateOptions(keys)), TestBudget);

        HdcException ex = await Assert.ThrowsAsync<HdcException>(() => host.ConnectAsync(endpoint, CreateOptions(keys)));

        Assert.Contains("Target is connected", ex.Message);
        Assert.Same(first, host.FindDevice(endpoint));
        Assert.Single(host.ConnectedDevices);
    }

    [Fact]
    public async Task AuthorizationRequested_FiresWhenAuthRequired()
    {
        using var daemon = new FakeDaemon(new FakeDaemonOptions { Generation = DaemonGeneration.Rust, RequireAuth = true });
        using var keys = new TempKeyStore();
        await using var host = new HdcHost();
        string endpoint = $"127.0.0.1:{daemon.Port}";
        var requested = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.AuthorizationRequested += (_, key) => requested.TrySetResult(key);

        HdcDevice device = await WithTimeoutAsync(host.ConnectAsync(endpoint, CreateOptions(keys)), TestBudget);

        Assert.Equal(endpoint, await requested.Task.WaitAsync(TestBudget));
        Assert.Equal(HdcDeviceState.Online, device.State);
    }

    [Fact]
    public async Task Disconnect_ReturnsTrueAndRemovesDevice()
    {
        using var daemon = new FakeDaemon(new FakeDaemonOptions { RequireAuth = false });
        using var keys = new TempKeyStore();
        await using var host = new HdcHost();
        string endpoint = $"127.0.0.1:{daemon.Port}";
        HdcDevice device = await WithTimeoutAsync(host.ConnectAsync(endpoint, CreateOptions(keys)), TestBudget);
        var disconnected = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var offline = new TaskCompletionSource<DeviceStateChangedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.DeviceDisconnected += (_, e) => disconnected.TrySetResult(e.Key);
        host.DeviceStateChanged += (_, e) =>
        {
            if (e.NewState == HdcDeviceState.Offline)
            {
                offline.TrySetResult(e);
            }
        };

        bool result = await WithTimeoutAsync(host.DisconnectAsync(endpoint), TestBudget);

        Assert.True(result);
        Assert.Equal(HdcDeviceState.Offline, device.State);
        Assert.Empty(host.ConnectedDevices);
        Assert.Equal(endpoint, await disconnected.Task.WaitAsync(TestBudget));
        DeviceStateChangedEventArgs state = await offline.Task.WaitAsync(TestBudget);
        Assert.Equal(endpoint, state.Key);
        Assert.Equal(HdcDeviceState.Online, state.OldState);
        Assert.Equal(HdcDeviceState.Offline, state.NewState);
        Assert.False(await WithTimeoutAsync(host.DisconnectAsync(endpoint), TestBudget));
    }

    [Fact]
    public async Task Connect_EndpointWithoutPort_ThrowsArgumentException()
    {
        await using var host = new HdcHost();

        ArgumentException ex = await Assert.ThrowsAsync<ArgumentException>(() => host.ConnectAsync("127.0.0.1"));

        Assert.Equal("endpoint", ex.ParamName);
        await Assert.ThrowsAsync<ArgumentException>(() => host.ConnectAsync("127.0.0.1:"));
        await Assert.ThrowsAsync<ArgumentException>(() => host.ConnectAsync("127.0.0.1:not-a-port"));
    }

    [Fact]
    public async Task DaemonDisconnect_MarksDeviceOffline()
    {
        using var daemon = new FakeDaemon(new FakeDaemonOptions { RequireAuth = false });
        using var keys = new TempKeyStore();
        await using var host = new HdcHost();
        string endpoint = $"127.0.0.1:{daemon.Port}";
        HdcDevice device = await WithTimeoutAsync(host.ConnectAsync(endpoint, CreateOptions(keys)), TestBudget);
        var disconnected = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.DeviceDisconnected += (_, e) => disconnected.TrySetResult(e.Key);

        daemon.Dispose();

        Assert.Equal(endpoint, await disconnected.Task.WaitAsync(HeartbeatWait));
        Assert.Equal(HdcDeviceState.Offline, device.State);
        Assert.Empty(host.ConnectedDevices);
    }

    private static ConnectOptions CreateOptions(TempKeyStore keys)
    {
        return new ConnectOptions { KeyStore = keys.Store, AuthTimeout = TestBudget };
    }

    private static async Task<bool> WaitForCommandAsync(FakeDaemon daemon, HdcCommand command, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (daemon.ReceivedCommands.Any(entry => entry.Cmd == command))
            {
                return true;
            }

            await Task.Delay(25);
        }

        return daemon.ReceivedCommands.Any(entry => entry.Cmd == command);
    }

    private static async Task<T> WithTimeoutAsync<T>(Task<T> task, TimeSpan timeout)
    {
        if (await Task.WhenAny(task, Task.Delay(timeout)) != task)
        {
            throw new TimeoutException($"操作未在 {timeout} 内完成");
        }

        return await task;
    }

    private sealed class TempKeyStore : IDisposable
    {
        private readonly string _directory;

        public TempKeyStore()
        {
            _directory = Path.Combine(Path.GetTempPath(), "hdc-sharp-host-" + Guid.NewGuid().ToString("N"));
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
