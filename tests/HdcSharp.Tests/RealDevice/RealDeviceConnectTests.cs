using System.Collections.Concurrent;
using HdcSharp.Protocol;
using HdcSharp.Security;
using HdcSharp.Transport;
using Xunit;

namespace HdcSharp.Tests.RealDevice;

/// <summary>
/// 真机连接、认证与断开用例：固化 2026-09-11 探针结论。
/// 依据 docs/verification/2026-09-11-real-device-connect.md §1（世代指纹 R2）与 §2（端到端连通、状态机与事件）。
/// 设备要求：C++ 世代 daemon（握手回复含 authtype TLV）；本机 ~/.harmony/hdckey 已被设备授权，故免弹窗。
/// </summary>
[Collection(RealDeviceCollectionDefinition.Name)]
[Trait("RealDevice", "true")]
public class RealDeviceConnectTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromMinutes(2);

    [RealDeviceFact]
    public async Task Connect_EstablishesOnlineSessionWithDeviceIdentity()
    {
        await using var session = await RealDeviceSession.OpenAsync(Budget);
        HdcDevice device = session.Device;

        Assert.Equal(HdcDeviceState.Online, device.State);
        Assert.Equal(DaemonGeneration.Cpp, device.Generation);
        Assert.False(string.IsNullOrWhiteSpace(device.DeviceName));
        Assert.NotEqual(0u, device.SessionId);
        Assert.Equal(RealDeviceHarness.Target, device.ConnectKey);
        Assert.Equal(RealDeviceHarness.Target, device.Endpoint);
        Assert.Same(device, session.Host.FindDevice(RealDeviceHarness.Target));
        Assert.Same(device, Assert.Single(session.Host.ConnectedDevices));
    }

    [RealDeviceFact]
    public async Task Connect_RaisesStateTransitionsConnectingAuthorizingOnline()
    {
        var transitions = new ConcurrentQueue<(HdcDeviceState Old, HdcDeviceState New)>();
        await using var session = await RealDeviceSession.OpenAsync(
            Budget,
            host => host.DeviceStateChanged += (_, e) => transitions.Enqueue((e.OldState, e.NewState)));

        Assert.Contains((HdcDeviceState.Connecting, HdcDeviceState.Authorizing), transitions);
        Assert.Contains((HdcDeviceState.Authorizing, HdcDeviceState.Online), transitions);
        Assert.Equal(HdcDeviceState.Online, transitions.Last().New);
        Assert.Equal(HdcDeviceState.Online, session.Device.State);
    }

    [RealDeviceFact]
    public async Task Connect_RaisesAuthorizationRequestedOnceBeforeOnline()
    {
        var markers = new ConcurrentQueue<string>();
        await using var session = await RealDeviceSession.OpenAsync(Budget, host =>
        {
            host.AuthorizationRequested += (_, key) => markers.Enqueue($"auth:{key}");
            host.DeviceStateChanged += (_, e) => markers.Enqueue($"state:{e.NewState}");
        });

        // AUTH_PUBLICKEY 阶段无条件触发一次（本机密钥已授权，无需人工确认），且必然早于 Online
        string authorized = Assert.Single(markers, m => m.StartsWith("auth:", StringComparison.Ordinal));
        Assert.Equal($"auth:{RealDeviceHarness.Target}", authorized);
        string[] sequence = [.. markers];
        Assert.True(
            Array.IndexOf(sequence, authorized) < Array.IndexOf(sequence, "state:Online"),
            $"AuthorizationRequested 应早于 Online，实际序列：{string.Join(" → ", sequence)}");
        Assert.Equal(HdcDeviceState.Online, session.Device.State);
    }

    [RealDeviceFact]
    public async Task Disconnect_MarksOfflineAndRaisesDeviceDisconnected()
    {
        await using var session = await RealDeviceSession.OpenAsync(Budget);
        HdcDevice device = session.Device;
        var states = new List<HdcDeviceState>();
        device.StateChanged += (_, e) => states.Add(e.NewState);
        string? disconnectedKey = null;
        session.Host.DeviceDisconnected += (_, e) => disconnectedKey = e.Key;

        bool removed = await session.Host.DisconnectAsync(RealDeviceHarness.Target, session.Token);

        Assert.True(removed);
        Assert.Equal(HdcDeviceState.Offline, device.State);
        Assert.Equal(HdcDeviceState.Offline, Assert.Single(states));
        Assert.Equal(RealDeviceHarness.Target, disconnectedKey);
        Assert.Null(session.Host.FindDevice(RealDeviceHarness.Target));
        Assert.Empty(session.Host.ConnectedDevices);
        // 已移除的端点再次断开返回 false，不抛异常
        Assert.False(await session.Host.DisconnectAsync(RealDeviceHarness.Target, session.Token));
    }

    [RealDeviceFact]
    public async Task Connect_SameEndpointTwice_IsRejectedAsDuplicate()
    {
        await using var session = await RealDeviceSession.OpenAsync(Budget);

        var error = await Assert.ThrowsAsync<HdcException>(
            () => session.Host.ConnectAsync(RealDeviceHarness.Target, RealDeviceHarness.CreateConnectOptions(), session.Token));

        Assert.Contains("Target is connected", error.Message, StringComparison.Ordinal);
    }

    [RealDeviceFact]
    public async Task Connect_SharedHostKeyStoreAlreadyAuthorized_ConnectsWithoutPrompt()
    {
        // 免弹窗前提：官方 hdc 的已授权密钥存在（verification §2）；缺失时本库会生成新密钥并等待设备确认
        string keyPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".harmony", "hdckey");
        Assert.True(File.Exists(keyPath), $"真机免弹窗前提不满足：{keyPath} 不存在");

        using var store = new FileHostKeyStore();
        Assert.StartsWith("-----BEGIN PUBLIC KEY-----", store.GetPublicKeyPem(), StringComparison.Ordinal);

        await using var host = new HdcHost();
        using var cts = new CancellationTokenSource(Budget);
        HdcDevice device = await host.ConnectAsync(
            RealDeviceHarness.Target,
            new ConnectOptions { KeyStore = store, AuthTimeout = TimeSpan.FromSeconds(60) },
            cts.Token);

        Assert.Equal(HdcDeviceState.Online, device.State);
        await host.DisconnectAsync(RealDeviceHarness.Target);
    }
}
