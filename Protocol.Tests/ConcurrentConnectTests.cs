using Chat_App.Infrastructure.Networking;
using Core.Models;
using Core.Models.DTO;
using Core.Protocol;
using System.Buffers;
using System.Net;
using System.Net.Sockets;
using Xunit;

namespace Protocol.Tests;

/// <summary>
/// 并发 ConnectAsync 串行化测试（P0-4 整改）。
/// 验收场景：两个 ConnectAsync 同时发起时由互斥门串行执行——
/// 后到者等待前一次连接流程完全结束，再关闭刚建立的会话重连；
/// 最终只有最后建立的连接活跃，先建立的连接被静默关闭（服务端收到 EOF），
/// 不存在并发会话覆盖导致的 socket 泄漏或双活跃连接。
/// </summary>
public class ConcurrentConnectTests
{
    /// <summary>循环 accept 的本地服务端：记录每个连接的字节数与 EOF。</summary>
    private sealed class MultiAcceptServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _acceptTask;
        private readonly List<Socket> _sockets = new();
        private readonly List<int> _byteCounts = new();
        private readonly List<bool> _closed = new();
        private readonly object _lock = new();

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public IReadOnlyList<int> ByteCounts { get { lock (_lock) return _byteCounts.ToArray(); } }
        public IReadOnlyList<bool> Closed { get { lock (_lock) return _closed.ToArray(); } }
        public int ConnectionCount { get { lock (_lock) return _sockets.Count; } }

        public MultiAcceptServer()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            _acceptTask = AcceptLoopAsync();
            Task.Delay(50).GetAwaiter().GetResult();
        }

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var socket = await _listener.AcceptSocketAsync(_cts.Token);
                    lock (_lock)
                    {
                        _sockets.Add(socket);
                        _closed.Add(false);
                        _byteCounts.Add(0);
                    }
                    _ = ReadLoopAsync(socket);
                }
            }
            catch (OperationCanceledException) { }
            catch (SocketException) { }
        }

        private async Task ReadLoopAsync(Socket socket)
        {
            var buf = new byte[4096];
            var bytes = 0;
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var n = await socket.ReceiveAsync(buf, SocketFlags.None, _cts.Token);
                    if (n == 0)
                        break;
                    bytes += n;
                }
            }
            catch (SocketException) { }
            catch (OperationCanceledException) { }
            lock (_lock)
            {
                var i = _sockets.IndexOf(socket);
                if (i < 0)
                    return;
                _byteCounts[i] = bytes;
                _closed[i] = true;
            }
        }

        public void Dispose()
        {
            try { _cts.Cancel(); } catch (ObjectDisposedException) { }
            lock (_lock)
                foreach (var s in _sockets)
                    try { s.Dispose(); } catch { }
            _listener.Stop();
            _cts.Dispose();
        }
    }

    /// <summary>
    /// 两个并发 ConnectAsync：串行执行后最终只有最后建立的连接活跃；
    /// 先建立的连接被静默关闭（服务端读到此连接 EOF），无并发覆盖泄漏。
    /// </summary>
    [Fact]
    public async Task Concurrent_ConnectAsync_Is_Serialized_No_Session_Leak()
    {
        using var server = new MultiAcceptServer();
        using var client = new TcpClientExample();

        // 同一时刻发起两个连接（模拟自动重连与用户手动重连竞争）
        var task1 = client.ConnectAsync(new ServerEndpoint { ServerIpAddress = "127.0.0.1", ServerPort = server.Port });
        var task2 = client.ConnectAsync(new ServerEndpoint { ServerIpAddress = "127.0.0.1", ServerPort = server.Port });

        await Task.WhenAll(task1, task2).WaitAsync(TimeSpan.FromSeconds(10));

        // 最终状态：已连接
        Assert.True(client.IsConnected);

        // 等待服务端观察到两条连接与第一条的 EOF（串行关闭旧会话）
        await Task.Delay(300);

        // 服务端 accept 了两条连接
        Assert.Equal(2, server.ConnectionCount);
        // 第一条连接被静默关闭：服务端读到 EOF（Receive 返回 0 → Closed=true），且未发送任何数据
        Assert.True(server.Closed[0], "第一条连接应被静默关闭（服务端读到 EOF）");
        Assert.Equal(0, server.ByteCounts[0]);
        // 第二条连接仍活跃：未被关闭（服务端未读到 EOF）
        Assert.False(server.Closed[1], "第二条连接应保持活跃");
        Assert.True(server.ByteCounts[1] >= 0);
    }

    /// <summary>
    /// 串行化不破坏正常收发：并发连接完成后，活跃连接可以正常发送数据帧。
    /// </summary>
    [Fact]
    public async Task Concurrent_ConnectAsync_Active_Connection_Still_Sends()
    {
        using var server = new MultiAcceptServer();
        using var client = new TcpClientExample();
        var serializer = new Chat_App.Infrastructure.Serialization.JsonPacketBodySerializer();

        var task1 = client.ConnectAsync(new ServerEndpoint { ServerIpAddress = "127.0.0.1", ServerPort = server.Port });
        var task2 = client.ConnectAsync(new ServerEndpoint { ServerIpAddress = "127.0.0.1", ServerPort = server.Port });
        await Task.WhenAll(task1, task2).WaitAsync(TimeSpan.FromSeconds(10));

        // 活跃连接发送一帧
        var writer = new ArrayBufferWriter<byte>(MessagePacket.HeaderSize + 64);
        serializer.Serialize(writer, new ChatMessageDto { MessageId = "m1", TargetUserId = 1, Content = "hello" });
        var packet = new MessagePacket(PacketCommand.ChatMessage,
            new ReadOnlySequence<byte>(writer.WrittenSpan.ToArray()));
        var frameWriter = new ArrayBufferWriter<byte>(MessagePacket.HeaderSize + writer.WrittenCount);
        new MessagePacketCodec().TryWrite(packet, frameWriter, out _);
        await client.SendAsync(frameWriter.WrittenMemory).WaitAsync(TimeSpan.FromSeconds(5));

        await Task.Delay(200);
        client.Disconnect("done");

        // 等待服务端读到 EOF 并记录字节数
        await Task.Delay(300);

        // 最后一条连接收到了该帧
        Assert.Equal(2, server.ConnectionCount);
        var last = server.ByteCounts[1];
        Assert.True(last > 0, "活跃连接应收到发送的帧");
    }
}
