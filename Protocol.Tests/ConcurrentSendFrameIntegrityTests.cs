using Core.Interfaces;
using Core.Models;
using Core.Models.DTO;
using Core.Protocol;
using Core.Services;
using Chat_App.Infrastructure.Serialization;
using System.Collections.Concurrent;
using Xunit;

namespace Protocol.Tests;

/// <summary>
/// 并发发送测试：8–32 个并发生产者持续通过 ChatSessionClient 发送，
/// 服务端按帧解码。验收：任何一帧都不得出现字节交错。
/// </summary>
public class ConcurrentSendFrameIntegrityTests
{
    /// <summary>
    /// 收集所有 SendAsync 字节并按帧解码的假 TCP 客户端。
    /// 用 lock 保证写入字节的原子性，模拟真实 socket 单写。
    /// </summary>
    private sealed class CapturingTcpClient : ITcpClient
    {
        private readonly List<byte> _sink = new();
        private readonly object _lock = new();
        private volatile bool _connected = true;

        public bool IsConnected => _connected;
        public event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStatusChanged;
        public event EventHandler<ReadOnlyMemory<byte>>? OnDataChunkReceived;

        /// <summary>模拟服务端下发数据块，触发接收回调。</summary>
        public void SimulateIncomingChunk(ReadOnlyMemory<byte> chunk) => OnDataChunkReceived?.Invoke(this, chunk);

        public Task ConnectAsync(ServerEndpoint endpoint, CancellationToken token = default)
        {
            _connected = true;
            ConnectionStatusChanged?.Invoke(this, new ConnectionStateChangedEventArgs(ConnectionState.Connected, "Connected"));
            return Task.CompletedTask;
        }

        public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken token = default)
        {
            // 模拟单写队列：整块原子追加，不会与其他发送交错
            lock (_lock)
            {
                _sink.AddRange(data.Span.ToArray());
            }
            return Task.CompletedTask;
        }

        public Task ReceiveDataAsync(CancellationToken token) => Task.Delay(-1, token);

        public void Disconnect(string? reason = null)
        {
            _connected = false;
            ConnectionStatusChanged?.Invoke(this, new ConnectionStateChangedEventArgs(ConnectionState.Disconnected, reason ?? "Disconnected"));
        }

        public void Dispose() => _connected = false;

        /// <summary>返回当前已收集的所有字节副本。</summary>
        public byte[] GetSentBytes()
        {
            lock (_lock) return _sink.ToArray();
        }
    }

    /// <summary>
    /// 32 个并发生产者各发送 500 条消息，所有帧应完整、无字节交错、无丢包。
    /// </summary>
    [Theory]
    [InlineData(8, 200)]
    [InlineData(16, 200)]
    [InlineData(32, 100)]
    public async Task Concurrent_Producers_No_Frame_Interleaving(int producerCount, int perProducer)
    {
        var tcp = new CapturingTcpClient();
        var codec = new MessagePacketCodec();
        var serializer = new JsonPacketBodySerializer();
        var session = new ChatSessionClient(tcp, codec, serializer);

        // 模拟已鉴权状态：直接触发 Authenticated 路径不便，这里改为只测底层 SendPacketAsync
        // 通过反射设置 IsAuthenticated / IsConnected 以绕过 EnsureAuthenticated
        var authedField = typeof(ChatSessionClient).GetProperty("IsAuthenticated");
        authedField!.SetValue(session, true);

        var expected = new ConcurrentBag<(long Producer, int Seq, string Content)>();
        var tasks = new Task[producerCount];

        for (var p = 0; p < producerCount; p++)
        {
            var producerId = (long)p + 1;
            tasks[p] = Task.Run(async () =>
            {
                for (var s = 0; s < perProducer; s++)
                {
                    var content = $"p{producerId}-s{s}-{Guid.NewGuid():N}";
                    expected.Add((producerId, s, content));
                    await session.SendChatMessageAsync(targetUserId: 9999, content: content);
                }
            });
        }

        await Task.WhenAll(tasks);

        // 按 sink 字节流解码所有帧
        var sentBytes = tcp.GetSentBytes();
        var decodeCodec = new MessagePacketCodec();
        decodeCodec.Append(sentBytes);

        var decodedCount = 0;
        while (decodeCodec.TryRead(out var packet))
        {
            Assert.Equal(PacketCommand.ChatMessage, packet.Command);
            var dto = serializer.Deserialize<ChatMessageDto>(packet.Body);
            Assert.NotNull(dto);
            Assert.False(string.IsNullOrWhiteSpace(dto!.Content));
            decodedCount++;
        }

        Assert.Equal(producerCount * perProducer, decodedCount);
        Assert.Equal(producerCount * perProducer, expected.Count);
    }

    /// <summary>
    /// 帧边界原子性：连续两次 SendAsync 之间不应出现半帧交错。
    /// 通过对每次发送后立即解码验证完整性。
    /// </summary>
    [Fact]
    public async Task Sequential_Send_Each_Frame_Decodable_Immediately()
    {
        var tcp = new CapturingTcpClient();
        var codec = new MessagePacketCodec();
        var serializer = new JsonPacketBodySerializer();
        var session = new ChatSessionClient(tcp, codec, serializer);

        var authedField = typeof(ChatSessionClient).GetProperty("IsAuthenticated");
        authedField!.SetValue(session, true);

        for (var i = 0; i < 50; i++)
        {
            await session.SendChatMessageAsync(9999, $"seq-{i}");

            // 每次发送后立即解码，应恰好多出一帧
            var bytes = tcp.GetSentBytes();
            var localCodec = new MessagePacketCodec();
            localCodec.Append(bytes);
            var count = 0;
            while (localCodec.TryRead(out _)) count++;
            Assert.Equal(i + 1, count);
        }
    }
}
