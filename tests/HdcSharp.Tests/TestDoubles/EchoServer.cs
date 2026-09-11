using System.Net;
using System.Net.Sockets;

namespace HdcSharp.Tests.TestDoubles;

/// <summary>
/// 回环回声服务：模拟 <c>hdc fport tcp:&lt;local&gt; tcp:&lt;remote&gt;</c> 里设备侧真正要连的本地目标服务。
/// 每个连接把收到的字节原样回写；可选在收到指定字节时直接断开（用于验证「远端关闭单条连接」）。
/// </summary>
internal sealed class EchoServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _connections = [];
    private readonly object _gate = new();
    private int _disposed;

    /// <summary>在回环随机端口上启动回声服务。</summary>
    /// <param name="closeOn">收到该字节时断开该连接且不回写；null 时纯回声。</param>
    internal EchoServer(byte? closeOn = null)
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(() => AcceptLoopAsync(closeOn));
    }

    /// <summary>监听端口。</summary>
    internal int Port { get; }

    /// <summary>停止监听并断开全部连接。</summary>
    public void Dispose()
    {
        GC.SuppressFinalize(this);
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _cts.Cancel();
        _listener.Stop();
        Task[] pending;
        lock (_gate)
        {
            pending = [.. _connections];
        }

        try
        {
            Task.WaitAll(pending, TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // 测试替身后台异常不影响释放语义
        }

        _cts.Dispose();
    }

    private async Task AcceptLoopAsync(byte? closeOn)
    {
        try
        {
            while (true)
            {
                TcpClient client = await _listener.AcceptTcpClientAsync(_cts.Token);
                Task task = Task.Run(() => EchoAsync(client, closeOn));
                lock (_gate)
                {
                    _connections.Add(task);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (SocketException)
        {
        }
    }

    private async Task EchoAsync(TcpClient client, byte? closeOn)
    {
        client.NoDelay = true;
        try
        {
            NetworkStream stream = client.GetStream();
            byte[] buffer = new byte[32 * 1024];
            while (true)
            {
                int read = await stream.ReadAsync(buffer, _cts.Token);
                if (read <= 0)
                {
                    break;
                }

                if (closeOn is { } marker && Array.IndexOf(buffer, marker, 0, read) >= 0)
                {
                    break;
                }

                await stream.WriteAsync(buffer.AsMemory(0, read), _cts.Token);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
        }
        finally
        {
            client.Dispose();
        }
    }
}
