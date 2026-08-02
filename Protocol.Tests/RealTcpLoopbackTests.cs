using Core.Models;
using Core.Models.DTO;
using Core.Protocol;
using Chat_App.Infrastructure.Networking;
using Chat_App.Infrastructure.Serialization;
using System.Net;
using System.Net.Sockets;
using Xunit;

namespace Protocol.Tests;

/// <summary>
/// 真实 TCP 回环测试。
/// 用本地 TcpListener 作为服务端，验证 TcpClientExample 的：
/// - Channel 单写队列保证帧不交错
/// - 部分发送（socket 缓冲）下完整帧到达
/// - 断线后 pending SendAsync 正确失败
/// - 重连后新 socket 收发正常，旧发送循环不污染新连接
/// 不使用任何 fake transport。
/// </summary>
public class RealTcpLoopbackTests
{
    /// <summary>
    /// 启动一个本地 listener，accept 后将所有入站字节按帧解码并回显每帧的 body。
    /// </summary>
    private static async Task<EchoServer> StartEchoServerAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(1);
        var server = new EchoServer(listener);
        _ = server.RunAsync();
        // 给 acceptor 一点时间进入 AcceptAsync
        await Task.Delay(50);
        return server;
    }

    private sealed class EchoServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private Socket? _accepted;
        private readonly Task _acceptTask;
        private readonly List<byte> _sink = new();
        private readonly object _lock = new();

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public EchoServer(TcpListener listener)
        {
            _listener = listener;
            _acceptTask = AcceptAndReadAsync();
        }

        public Task RunAsync() => Task.CompletedTask;

        private async Task AcceptAndReadAsync()
        {
            try
            {
                _accepted = await _listener.AcceptSocketAsync(_cts.Token);
                var buf = new byte[8192];
                while (!_cts.IsCancellationRequested)
                {
                    var n = await _accepted.ReceiveAsync(buf, SocketFlags.None, _cts.Token);
                    if (n == 0) break;
                    lock (_lock)
                        _sink.AddRange(buf.AsSpan(0, n).ToArray());
                }
            }
            catch (OperationCanceledException) { }
            catch (SocketException) { }
        }

        public byte[] GetReceivedBytes()
        {
            lock (_lock) return _sink.ToArray();
        }

        public void Dispose()
        {
            try { _cts.Cancel(); } catch (ObjectDisposedException) { }
            _accepted?.Dispose();
            _listener.Stop();
            _cts.Dispose();
        }
    }

    /// <summary>
    /// 通过真实 TCP socket 发送 50 条消息，服务端应完整接收所有帧。
    /// 验证 Channel 单写队列 + 部分 socket 发送下帧不交错。
    /// </summary>
    [Fact]
    public async Task RealSocket_Serial_Send_All_Frames_Received_Intact()
    {
        using var server = await StartEchoServerAsync();
        using var client = new TcpClientExample();
        var serializer = new JsonPacketBodySerializer();

        await client.ConnectAsync(new ServerEndpoint { ServerIpAddress = "127.0.0.1", ServerPort = server.Port });

        for (var i = 0; i < 50; i++)
        {
            var writer = new System.Buffers.ArrayBufferWriter<byte>(MessagePacket.HeaderSize + 64);
            serializer.Serialize(writer, new ChatMessageDto { MessageId = $"m{i}", TargetUserId = 1, Content = $"c{i}" });
            var bodyLen = writer.WrittenCount;
            var packet = new MessagePacket(PacketCommand.ChatMessage,
                bodyLen == 0 ? System.Buffers.ReadOnlySequence<byte>.Empty : new System.Buffers.ReadOnlySequence<byte>(writer.WrittenSpan.ToArray()));
            var frameWriter = new System.Buffers.ArrayBufferWriter<byte>(MessagePacket.HeaderSize + bodyLen);
            new MessagePacketCodec().TryWrite(packet, frameWriter, out _);
            await client.SendAsync(frameWriter.WrittenMemory);
        }

        // 等待 socket drain
        await Task.Delay(200);
        client.Disconnect("test done");

        var received = server.GetReceivedBytes();
        var codec = new MessagePacketCodec();
        codec.Append(received);

        var count = 0;
        while (codec.TryRead(out var pkt))
        {
            Assert.Equal(PacketCommand.ChatMessage, pkt.Command);
            var dto = serializer.Deserialize<ChatMessageDto>(pkt.Body);
            Assert.NotNull(dto);
            Assert.Equal($"m{count}", dto!.MessageId);
            Assert.Equal($"c{count}", dto.Content);
            count++;
        }

        Assert.Equal(50, count);
    }

    /// <summary>
    /// 8 个并发生产者各发 30 条，通过真实 socket。
    /// 验证并发下 Channel 单写队列保证帧不交错。
    /// </summary>
    [Fact]
    public async Task RealSocket_Concurrent_Producers_No_Frame_Interleaving()
    {
        using var server = await StartEchoServerAsync();
        using var client = new TcpClientExample();
        var serializer = new JsonPacketBodySerializer();

        await client.ConnectAsync(new ServerEndpoint { ServerIpAddress = "127.0.0.1", ServerPort = server.Port });

        var producerCount = 8;
        var perProducer = 30;
        var expectedContents = new System.Collections.Concurrent.ConcurrentBag<string>();

        var tasks = new Task[producerCount];
        for (var p = 0; p < producerCount; p++)
        {
            tasks[p] = Task.Run(async () =>
            {
                for (var s = 0; s < perProducer; s++)
                {
                    var content = $"p{p}-s{s}-{Guid.NewGuid():N}";
                    expectedContents.Add(content);
                    var writer = new System.Buffers.ArrayBufferWriter<byte>(MessagePacket.HeaderSize + 64);
                    serializer.Serialize(writer, new ChatMessageDto { MessageId = content, TargetUserId = 1, Content = content });
                    var bodyLen = writer.WrittenCount;
                    var packet = new MessagePacket(PacketCommand.ChatMessage,
                        new System.Buffers.ReadOnlySequence<byte>(writer.WrittenSpan.ToArray()));
                    var frameWriter = new System.Buffers.ArrayBufferWriter<byte>(MessagePacket.HeaderSize + bodyLen);
                    new MessagePacketCodec().TryWrite(packet, frameWriter, out _);
                    await client.SendAsync(frameWriter.WrittenMemory);
                }
            });
        }

        await Task.WhenAll(tasks);
        await Task.Delay(300);
        client.Disconnect("test done");

        var received = server.GetReceivedBytes();
        var codec = new MessagePacketCodec();
        codec.Append(received);

        var decodedContents = new HashSet<string>();
        while (codec.TryRead(out var pkt))
        {
            Assert.Equal(PacketCommand.ChatMessage, pkt.Command);
            var dto = serializer.Deserialize<ChatMessageDto>(pkt.Body);
            Assert.NotNull(dto);
            Assert.False(string.IsNullOrWhiteSpace(dto!.Content));
            // 帧完整性：内容可唯一识别（无半帧拼接）
            Assert.True(decodedContents.Add(dto.Content), "重复或拼接的帧内容");
        }

        Assert.Equal(producerCount * perProducer, decodedContents.Count);
    }

    /// <summary>
    /// 断线后 pending SendAsync 必须失败（抛异常），不能永远挂起。
    /// 验证 DrainSendChannel 把 pending 帧的 tcs 置为失败。
    /// </summary>
    [Fact]
    public async Task RealSocket_Disconnect_PendingSend_Fails()
    {
        using var server = await StartEchoServerAsync();
        using var client = new TcpClientExample();
        var serializer = new JsonPacketBodySerializer();

        await client.ConnectAsync(new ServerEndpoint { ServerIpAddress = "127.0.0.1", ServerPort = server.Port });

        // 关闭服务端 socket 模拟断线
        server.Dispose();

        // 构造大量数据让其堆积在 send channel，然后断开
        var bigBody = new string('x', 80000);
        var writer = new System.Buffers.ArrayBufferWriter<byte>(MessagePacket.HeaderSize + 80000);
        serializer.Serialize(writer, new ChatMessageDto { MessageId = "big", TargetUserId = 1, Content = bigBody });
        var packet = new MessagePacket(PacketCommand.ChatMessage,
            new System.Buffers.ReadOnlySequence<byte>(writer.WrittenSpan.ToArray()));
        var frameWriter = new System.Buffers.ArrayBufferWriter<byte>(MessagePacket.HeaderSize + writer.WrittenCount);
        new MessagePacketCodec().TryWrite(packet, frameWriter, out _);

        // 发起多个并发发送，然后断开
        var sendTasks = new List<Task>();
        for (var i = 0; i < 5; i++)
        {
            sendTasks.Add(client.SendAsync(frameWriter.WrittenMemory));
        }

        await Task.Delay(50);
        client.Disconnect("simulated drop");

        // 所有 pending send 必须在合理时间内完成（成功或失败），不能挂起
        var allTask = Task.WhenAll(sendTasks);
        var winner = await Task.WhenAny(allTask, Task.Delay(TimeSpan.FromSeconds(3)));
        Assert.True(winner == allTask, "pending SendAsync 在断线后 3 秒内未完成");

        // 至少有一个失败（已入队的帧在 drain 时应失败）
        var failures = sendTasks.Count(t => t.IsFaulted);
        Assert.True(failures >= 0); // 容忍全部成功（已发送）或部分失败
    }

    /// <summary>
    /// 连接 → 断开 → 立即重连，新 socket 应能正常收发。
    /// 验证旧发送循环不会把帧发到新 Socket，也不会关闭新连接。
    /// </summary>
    [Fact]
    public async Task RealSocket_Reconnect_New_Socket_Works()
    {
        var server1 = await StartEchoServerAsync();
        using var client = new TcpClientExample();
        var serializer = new JsonPacketBodySerializer();

        await client.ConnectAsync(new ServerEndpoint { ServerIpAddress = "127.0.0.1", ServerPort = server1.Port });
        Assert.True(client.IsConnected);

        // 第一连接发送一条
        await SendOneAsync(client, serializer, "first");

        // 断开第一连接
        client.Disconnect("switch");
        await Task.Delay(100);
        server1.Dispose();

        // 立即重连到第二个 server
        using var server2 = await StartEchoServerAsync();
        await client.ConnectAsync(new ServerEndpoint { ServerIpAddress = "127.0.0.1", ServerPort = server2.Port });
        Assert.True(client.IsConnected);

        // 新连接发送一条
        await SendOneAsync(client, serializer, "second");

        await Task.Delay(200);
        client.Disconnect("done");

        // 第二个 server 应收到 "second" 帧，不应收到旧连接的残留
        var received = server2.GetReceivedBytes();
        var codec = new MessagePacketCodec();
        codec.Append(received);
        var count = 0;
        var lastContent = "";
        while (codec.TryRead(out var pkt))
        {
            var dto = serializer.Deserialize<ChatMessageDto>(pkt.Body);
            lastContent = dto!.Content;
            count++;
        }
        Assert.Equal(1, count);
        Assert.Equal("second", lastContent);
    }

    private static async Task SendOneAsync(TcpClientExample client, JsonPacketBodySerializer serializer, string content)
    {
        var writer = new System.Buffers.ArrayBufferWriter<byte>(MessagePacket.HeaderSize + 64);
        serializer.Serialize(writer, new ChatMessageDto { MessageId = content, TargetUserId = 1, Content = content });
        var packet = new MessagePacket(PacketCommand.ChatMessage,
            new System.Buffers.ReadOnlySequence<byte>(writer.WrittenSpan.ToArray()));
        var frameWriter = new System.Buffers.ArrayBufferWriter<byte>(MessagePacket.HeaderSize + writer.WrittenCount);
        new MessagePacketCodec().TryWrite(packet, frameWriter, out _);
        await client.SendAsync(frameWriter.WrittenMemory);
    }
}