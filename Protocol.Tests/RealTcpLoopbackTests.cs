using Core.Models;
using Core.Models.DTO;
using Core.Protocol;
using Chat_App.Infrastructure.Networking;
using Chat_App.Infrastructure.Serialization;
using System.Buffers;
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
    /// 断线排空验证（真实 socket + 生产池化发送路径，即 ChatSessionClient 实际使用的
    /// SendAsync(IMemoryOwner{byte})）：
    /// 确定性构造——第一帧 10MB 让发送循环的写操作占用 ≥2ms（实测 10MB 回环写 ~2.4ms，
    /// NetworkStream 在 Windows 上不支持取消在途写），测试线程在 µs 级立即调 Disconnect：
    /// - 第 2、3 帧必然仍停留在发送 Channel（消费者忙于第一帧），必须被 DrainSendChannel
    ///   置为失败，不允许静默成功，也不允许挂起
    /// - 在写的第一帧：写完成则成功、否则失败，两种结局都接受；其 owner 必须归还
    /// - 所有池化 owner 归还（成功/失败路径都不泄漏）
    /// </summary>
    [Fact]
    public async Task RealSocket_Disconnect_PendingSend_Fails()
    {
        using var server = await StartEchoServerAsync();
        using var client = new TcpClientExample();
        var serializer = new JsonPacketBodySerializer();

        await client.ConnectAsync(new ServerEndpoint { ServerIpAddress = "127.0.0.1", ServerPort = server.Port });

        var pool = System.Buffers.MemoryPool<byte>.Shared;
        var bigBody = new string('x', 10 * 1024 * 1024);
        var frame = BuildFrame(serializer, new ChatMessageDto { MessageId = "big", TargetUserId = 1, Content = bigBody });

        var owners = new TrackingOwner[3];
        for (var i = 0; i < owners.Length; i++)
        {
            owners[i] = new TrackingOwner(pool.Rent(frame.Length));
            frame.CopyTo(owners[i].Memory.Span);
        }

        // 入队后不等待，立即断开：消费者此刻必然在写第一帧（≥2ms），其余帧仍在队列
        var sendTasks = new List<Task>(owners.Length);
        foreach (var owner in owners)
            sendTasks.Add(client.SendAsync(owner));
        client.Disconnect("simulated drop");

        // 所有 pending send 必须在合理时间内完成（成功或失败），不能挂起
        var allTask = Task.WhenAll(sendTasks);
        var winner = await Task.WhenAny(allTask, Task.Delay(TimeSpan.FromSeconds(3)));
        Assert.True(winner == allTask, "pending SendAsync 在断线后 3 秒内未完成（排空路径必须结束所有调用方）");

        // 排队中的第 2、3 帧必须失败：消费者不可能在 µs 级完成第一帧前取走它们
        Assert.True(sendTasks[1].IsFaulted && sendTasks[2].IsFaulted,
            "断线时仍停留在队列中的帧必须被排空失败（不允许静默成功）");
        Assert.True(sendTasks[0].IsFaulted || sendTasks[0].IsCompletedSuccessfully,
            "在写的第一帧要么完成要么失败，不得挂起");

        // 无论成功还是失败，所有池化 owner 都必须归还
        Assert.All(owners, o => Assert.True(o.IsDisposed, "断线后 owner 应被 Dispose 归还池"));
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

    /// <summary>
    /// 生产池化发送路径：ChatSessionClient 实际使用 SendAsync(IMemoryOwner{byte}) 零拷贝发送
    /// （见 ChatSessionClient.SendRequestAsync），真实 socket 下验证：
    /// - 服务端完整收到全部帧
    /// - 发送完成后 owner 被发送循环 Dispose 归还池（成功路径）
    /// </summary>
    [Fact]
    public async Task RealSocket_PooledSend_Frames_Intact_And_Owners_Returned()
    {
        using var server = await StartEchoServerAsync();
        using var client = new TcpClientExample();
        var serializer = new JsonPacketBodySerializer();

        await client.ConnectAsync(new ServerEndpoint { ServerIpAddress = "127.0.0.1", ServerPort = server.Port });

        var pool = System.Buffers.MemoryPool<byte>.Shared;
        var owners = new List<TrackingOwner>();
        var expectedContents = new List<string>();
        for (var i = 0; i < 20; i++)
        {
            var content = $"pooled-{i}-{Guid.NewGuid():N}";
            var frame = BuildFrame(serializer, new ChatMessageDto { MessageId = content, TargetUserId = 1, Content = content });
            var owner = new TrackingOwner(pool.Rent(frame.Length));
            frame.CopyTo(owner.Memory.Span);
            owners.Add(owner);
            expectedContents.Add(content);
            await client.SendAsync(owner);
        }

        await Task.Delay(200);
        client.Disconnect("test done");

        // 成功发送后每个 owner 都必须已归还（不得泄漏池化内存）
        Assert.All(owners, o => Assert.True(o.IsDisposed, "成功发送后 owner 应被 Dispose 归还池"));

        var received = server.GetReceivedBytes();
        var codec = new MessagePacketCodec();
        codec.Append(received);
        var decoded = new List<string>();
        while (codec.TryRead(out var pkt))
        {
            var dto = serializer.Deserialize<ChatMessageDto>(pkt.Body);
            Assert.NotNull(dto);
            Assert.NotNull(dto.MessageId);
            decoded.Add(dto.MessageId);
        }
        Assert.Equal(expectedContents.Count, decoded.Count);
        Assert.All(expectedContents, c => Assert.Contains(c, decoded));
    }

    /// <summary>包装池化内存并记录 Dispose 次数，用于验证发送路径正确归还。</summary>
    private sealed class TrackingOwner(IMemoryOwner<byte> inner) : IMemoryOwner<byte>
    {
        private int _disposed;

        public Memory<byte> Memory => inner.Memory;

        public bool IsDisposed => Volatile.Read(ref _disposed) == 1;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                inner.Dispose();
        }
    }

    /// <summary>序列化消息为完整网络帧（header + body）。</summary>
    private static byte[] BuildFrame(JsonPacketBodySerializer serializer, ChatMessageDto dto)
    {
        var writer = new System.Buffers.ArrayBufferWriter<byte>(MessagePacket.HeaderSize + 64);
        serializer.Serialize(writer, dto);
        var bodyLen = writer.WrittenCount;
        var packet = new MessagePacket(PacketCommand.ChatMessage,
            bodyLen == 0 ? System.Buffers.ReadOnlySequence<byte>.Empty : new System.Buffers.ReadOnlySequence<byte>(writer.WrittenSpan.ToArray()));
        var frameWriter = new System.Buffers.ArrayBufferWriter<byte>(MessagePacket.HeaderSize + bodyLen);
        new MessagePacketCodec().TryWrite(packet, frameWriter, out _);
        return frameWriter.WrittenMemory.ToArray();
    }

    /// <summary>
    /// S0 验收：旧连接迟到异常不能关闭新连接。
    /// 旧服务端强制断开（RST）后立即重连新服务端——旧接收循环随后抛异常
    /// （迟到的 session-scoped 异常），必须只作用于旧会话，新连接保持连通可收发。
    /// </summary>
    [Fact]
    public async Task RealSocket_Old_Session_Late_Error_Does_Not_Kill_New_Connection()
    {
        var server1 = await StartEchoServerAsync();
        using var client = new TcpClientExample();
        var serializer = new JsonPacketBodySerializer();

        await client.ConnectAsync(new ServerEndpoint { ServerIpAddress = "127.0.0.1", ServerPort = server1.Port });
        Assert.True(client.IsConnected);
        await SendOneAsync(client, serializer, "old-session");

        // 旧服务端强制关闭：旧连接的接收循环将收到 RST/EOF（迟到的异常源）
        server1.Dispose();

        // 立即重连新服务端（旧循环可能尚未退出）
        using var server2 = await StartEchoServerAsync();
        await client.ConnectAsync(new ServerEndpoint { ServerIpAddress = "127.0.0.1", ServerPort = server2.Port });
        Assert.True(client.IsConnected);

        // 给旧接收循环时间抛出迟到异常（旧会话 RST 处理）
        await Task.Delay(300);

        // 新连接必须仍然连通且可正常收发
        Assert.True(client.IsConnected);
        await SendOneAsync(client, serializer, "new-session");
        await Task.Delay(200);
        client.Disconnect("done");

        var received = server2.GetReceivedBytes();
        var codec = new MessagePacketCodec();
        codec.Append(received);
        var count = 0;
        string? lastContent = null;
        while (codec.TryRead(out var pkt))
        {
            var dto = serializer.Deserialize<ChatMessageDto>(pkt.Body);
            lastContent = dto!.Content;
            count++;
        }
        Assert.Equal(1, count);
        Assert.Equal("new-session", lastContent);
    }

    private static async Task SendOneAsync(TcpClientExample client, JsonPacketBodySerializer serializer, string content)
        => await client.SendAsync(BuildFrame(serializer, new ChatMessageDto { MessageId = content, TargetUserId = 1, Content = content }));
}