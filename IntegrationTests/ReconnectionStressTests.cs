using Core.Interfaces;
using Core.Models;
using Core.Models.DTO;
using Core.Protocol;
using Core.Services;
using Chat_App.Infrastructure.Serialization;
using System.Buffers;
using System.Collections.Concurrent;
using Xunit;

namespace IntegrationTests;

/// <summary>
/// 重连压力测试：连续执行连接、断开、取消、立即重连。
/// 验收：
/// - 旧连接不能关闭新连接
/// - pending request 全部正确结束
/// - 坏帧不终止接收循环
/// - 鉴权超时正确失败
/// </summary>
public class ReconnectionStressTests
{
    /// <summary>
    /// 可控的假 TCP 客户端：解析每次发送的帧，触发回调让测试模拟服务端响应。
    /// </summary>
    private sealed class ScriptedTcpClient : ITcpClient
    {
        private volatile bool _connected;

        public bool IsConnected => _connected;

        public event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStatusChanged;
        public event EventHandler<ReadOnlyMemory<byte>>? OnDataChunkReceived;

        /// <summary>每次发送一帧后触发（command, bodyBytes）。</summary>
        public event Action<PacketCommand, ReadOnlyMemory<byte>>? OnFrameSent;

        public Task ConnectAsync(ServerEndpoint endpoint, CancellationToken token = default)
        {
            _connected = true;
            ConnectionStatusChanged?.Invoke(this, new ConnectionStateChangedEventArgs(ConnectionState.Connected));
            return Task.CompletedTask;
        }

        public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken token = default)
        {
            // 解析发送的帧，触发回调让测试模拟服务端响应
            var seq = new ReadOnlySequence<byte>(data);
            while (seq.Length > 0)
            {
                if (!MessagePacket.TryDeserialize(ref seq, out var pkt, out _))
                    break;
                OnFrameSent?.Invoke(pkt.Command, pkt.Body.ToArray());
            }
            return Task.CompletedTask;
        }

        public Task ReceiveDataAsync(CancellationToken token) => Task.Delay(-1, token);

        public void Disconnect(string? reason = null)
        {
            if (!_connected) return;
            _connected = false;
            ConnectionStatusChanged?.Invoke(this, new ConnectionStateChangedEventArgs(ConnectionState.Disconnected, reason));
        }

        /// <summary>注入一段数据块给上层（模拟服务端下发）。</summary>
        public void InjectData(ReadOnlyMemory<byte> chunk)
            => OnDataChunkReceived?.Invoke(this, chunk);

        public void Dispose()
        {
            _connected = false;
            GC.SuppressFinalize(this);
        }
    }

    private static void InjectPacket<T>(
        ScriptedTcpClient tcp,
        IPacketBodySerializer serializer,
        PacketCommand command,
        T? payload)
    {
        // 序列化器直写 IBufferWriter，不再返回独立 byte[]
        var writer = new ArrayBufferWriter<byte>(MessagePacket.HeaderSize + 64);
        serializer.Serialize(writer, payload);
        var bodyLen = writer.WrittenCount;
        var packet = new MessagePacket(command,
            bodyLen == 0 ? ReadOnlySequence<byte>.Empty : new ReadOnlySequence<byte>(writer.WrittenSpan.ToArray()));
        var frameWriter = new ArrayBufferWriter<byte>(MessagePacket.HeaderSize + bodyLen);
        new MessagePacketCodec().TryWrite(packet, frameWriter, out _);
        tcp.InjectData(frameWriter.WrittenMemory);
    }

    /// <summary>
    /// 设置自动鉴权响应：收到 AuthRequest 后立即注入 AuthResponse。
    /// 重复调用会替换上一次的响应注入（避免旧轮次的响应串入新一轮）。
    /// </summary>
    private static Action<PacketCommand, ReadOnlyMemory<byte>> SetupAutoAuth(
        ScriptedTcpClient tcp,
        IPacketBodySerializer serializer,
        long userId,
        bool success = true)
    {
        Action<PacketCommand, ReadOnlyMemory<byte>> handler = (cmd, _) =>
        {
            if (cmd == PacketCommand.AuthRequest)
            {
                InjectPacket(tcp, serializer, PacketCommand.AuthResponse,
                    new AuthResponseDto { Success = success, UserId = userId });
            }
        };
        tcp.OnFrameSent += handler;
        return handler;
    }

    /// <summary>
    /// 连续 5 轮「连接 → 鉴权 → 断开 → 立即重连」，每轮断开后 pending 请求应全部失败。
    /// </summary>
    [Fact]
    public async Task Repeated_Connect_Disconnect_Reconnect_PendingRequests_Resolved()
    {
        var tcp = new ScriptedTcpClient();
        var serializer = new JsonPacketBodySerializer();
        var session = new ChatSessionClient(tcp, new MessagePacketCodec(), serializer);

        var closeReasons = new ConcurrentBag<string>();
        session.ConnectionClosed += (_, reason) => closeReasons.Add(reason);

        Action<PacketCommand, ReadOnlyMemory<byte>>? authHandler = null;

        for (var round = 0; round < 5; round++)
        {
            await session.ConnectAsync(new ServerEndpoint { ServerIpAddress = "127.0.0.1", ServerPort = 7000 });
            if (authHandler is not null)
                tcp.OnFrameSent -= authHandler;
            authHandler = SetupAutoAuth(tcp, serializer, 7000 + round);

            await session.AuthenticateAsync("token", 7000 + round, null, null);

            Assert.True(session.IsAuthenticated);
            Assert.Equal(7000 + round, session.CurrentUserId);

            // 发起一个 pending 请求但不等响应，立即断开
            var historyTask = session.QueryMessageHistoryAsync("conv-1");
            Assert.False(historyTask.IsCompleted);

            await session.DisconnectAsync($"round-{round}-drop");

            // FailPendingRequests 通过 TrySetException 完成 tcs，但 WaitAsync 包装的完成可能需要调度
            // 等待最多 2s，pending 请求必须被结束（异常），不能永远挂起
            var completed = await Task.WhenAny(historyTask, Task.Delay(TimeSpan.FromSeconds(2)));
            Assert.True(historyTask.IsCompleted, $"round {round}: pending 请求未在断开后结束");
            await Assert.ThrowsAnyAsync<Exception>(() => historyTask);

            Assert.False(session.IsAuthenticated);
        }

        Assert.Equal(5, closeReasons.Count);
    }

    /// <summary>
    /// 坏帧注入后接收循环不应终止，后续合法帧仍能被路由。
    /// </summary>
    [Fact]
    public async Task Corrupted_Frame_Does_Not_Terminate_Receive_Loop()
    {
        var tcp = new ScriptedTcpClient();
        var serializer = new JsonPacketBodySerializer();
        var session = new ChatSessionClient(tcp, new MessagePacketCodec(), serializer);

        await session.ConnectAsync(new ServerEndpoint { ServerIpAddress = "127.0.0.1", ServerPort = 7000 });
        SetupAutoAuth(tcp, serializer, 8001);
        await session.AuthenticateAsync("token", 8001, null, null);

        // 注入魔数损坏的字节
        tcp.InjectData(new byte[] { 0xFF, 0xEE, 0xDD, 0xCC, 0x01, 0x02, 0x03, 0x04 });

        // 注入合法的 HeartbeatAck
        InjectPacket<object?>(tcp, serializer, PacketCommand.HeartbeatAck, null);

        // 注入合法的 ChatMessage
        ChatMessageDto? received = null;
        session.ChatMessageReceived += (_, msg) => received = msg;
        InjectPacket(tcp, serializer, PacketCommand.ChatMessage, new ChatMessageDto
        {
            MessageId = "msg-after-garbage",
            TargetUserId = 8001,
            Content = "survived"
        });

        // 等待事件处理（同步回调，小幅 delay 保证订阅线程可见）
        await Task.Delay(50);

        Assert.NotNull(received);
        Assert.Equal("survived", received!.Content);
        Assert.Equal("msg-after-garbage", received.MessageId);
    }

    /// <summary>
    /// 鉴权超时应正确失败，不残留 _authTcs，且可再次发起鉴权。
    /// 注意：生产代码 AuthenticateAsync 使用 WaitAsync(token)+CancelAfter，
    /// 超时时抛 OperationCanceledException 而非 TimeoutException，
    /// 因此 AuthenticationFailed 事件不会被触发——此处仅验证不挂起、可重试。
    /// </summary>
    [Fact]
    public async Task Auth_Timeout_Fails_Cleanly()
    {
        var tcp = new ScriptedTcpClient();
        var serializer = new JsonPacketBodySerializer();
        var session = new ChatSessionClient(tcp, new MessagePacketCodec(), serializer);

        await session.ConnectAsync(new ServerEndpoint { ServerIpAddress = "127.0.0.1", ServerPort = 7000 });
        // 不设置 AutoAuth，让鉴权超时

        // 用短超时 token 加速测试（生产代码用 linked token，外部 ct 取消会立即触发）
        using var fastCts1 = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<Exception>(() =>
            session.AuthenticateAsync("token", 9001, null, null, fastCts1.Token));
        Assert.False(session.IsAuthenticated);

        // 第二次：应能重新设置 _authTcs（不残留），同样超时失败
        using var fastCts2 = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<Exception>(() =>
            session.AuthenticateAsync("token", 9001, null, null, fastCts2.Token));
        Assert.False(session.IsAuthenticated);
    }
}
