using Chat_App.Infrastructure.Serialization;
using Core.Interfaces;
using Core.Models;
using Core.Models.DTO;
using Core.Protocol;
using Core.Services;
using System.Buffers;
using System.Buffers.Binary;
using System.Text.Json;
using Xunit;

namespace Protocol.Tests;

/// <summary>
/// RPC 请求/响应往返的延迟直方图验收：
/// 每次 SendRequestAsync 完成（成功/超时/取消）必须向 rpc_latency_ms 记录一个样本，
/// rpc_requests 计数器同步递增——保证网络层可观测性（p95/p99 RPC 延迟）有数据闭环。
/// </summary>
public class RpcLatencyMetricsTests
{
    /// <summary>收集发送字节并可注入服务端帧的假 TCP 客户端。</summary>
    private sealed class CapturingTcpClient : ITcpClient
    {
        private readonly List<byte> _sink = new();
        private readonly object _lock = new();
        private volatile bool _connected = true;

        public bool IsConnected => _connected;
        public event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStatusChanged;
        public event EventHandler<ReadOnlyMemory<byte>>? OnDataChunkReceived;

        public void SimulateIncomingChunk(ReadOnlyMemory<byte> chunk) => OnDataChunkReceived?.Invoke(this, chunk);

        public Task ConnectAsync(ServerEndpoint endpoint, CancellationToken token = default)
        {
            _connected = true;
            ConnectionStatusChanged?.Invoke(this, new ConnectionStateChangedEventArgs(ConnectionState.Connected, "Connected"));
            return Task.CompletedTask;
        }

        public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken token = default)
        {
            lock (_lock) _sink.AddRange(data.Span.ToArray());
            return Task.CompletedTask;
        }

        public Task ReceiveDataAsync(CancellationToken token) => Task.Delay(-1, token);

        public void Disconnect(string? reason = null)
        {
            _connected = false;
            ConnectionStatusChanged?.Invoke(this, new ConnectionStateChangedEventArgs(ConnectionState.Disconnected, reason ?? "Disconnected"));
        }

        public void Dispose() => _connected = false;

        public byte[] GetSentBytes()
        {
            lock (_lock) return _sink.ToArray();
        }
    }

    private static (ChatSessionClient Session, CapturingTcpClient Tcp) CreateAuthedClient()
    {
        var tcp = new CapturingTcpClient();
        var session = new ChatSessionClient(tcp, new MessagePacketCodec(), new JsonPacketBodySerializer());
        typeof(ChatSessionClient).GetProperty("IsAuthenticated")!.SetValue(session, true);
        return (session, tcp);
    }

    private static byte[] Frame(PacketCommand command, ReadOnlySpan<byte> body)
    {
        var frame = new byte[MessagePacket.HeaderSize + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, MessagePacket.MagicNumber);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(MessagePacket.CommandOffset), (ushort)command);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(MessagePacket.LengthOffset), body.Length);
        body.CopyTo(frame.AsSpan(MessagePacket.HeaderSize));
        return frame;
    }

    [Fact]
    public async Task Rpc_Response_Records_Latency_Sample()
    {
        var (session, tcp) = CreateAuthedClient();

        var rpc = session.QueryConversationListAsync(limit: 20);

        // 从发送字节中解码出请求帧，取 RequestId 以构造匹配的响应。
        var codec = new MessagePacketCodec();
        codec.Append(tcp.GetSentBytes());
        Assert.True(codec.TryRead(out var requestPacket), "应已发出请求帧");
        Assert.Equal(PacketCommand.ConversationListRequest, requestPacket.Command);
        var requestDto = new JsonPacketBodySerializer().Deserialize<ConversationListRequestDto>(requestPacket.Body);
        Assert.NotNull(requestDto);
        Assert.False(string.IsNullOrWhiteSpace(requestDto.RequestId),
            "请求帧必须携带 RequestId 才能匹配响应");

        var responseBody = JsonSerializer.SerializeToUtf8Bytes(
            new ConversationListResponseDto { RequestId = requestDto.RequestId!, Items = [] },
            ChatJsonContext.Default.ConversationListResponseDto);
        tcp.SimulateIncomingChunk(Frame(PacketCommand.ConversationListPage, responseBody));

        var result = await rpc;
        Assert.NotNull(result);

        // 指标闭环：恰好一个 RPC 请求 + 一个延迟样本
        Assert.Equal(1, session.Counters["rpc_requests"]);
        var snapshot = session.Histograms["rpc_latency_ms"];
        Assert.True(snapshot.Count > 0, "rpc_latency_ms 应至少有一个样本");
        Assert.True(snapshot.MaxMs >= 0);
    }

    [Fact]
    public async Task Rpc_Cancelled_Still_Records_Latency_Sample()
    {
        var (session, _) = CreateAuthedClient();

        // 取消路径（无响应注入，调用方主动取消）：延迟样本同样必须记录（取消/超时即延迟证据）。
        using var cts = new CancellationTokenSource(100);
        var rpc = session.QueryConversationListAsync(limit: 20, ct: cts.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rpc);

        Assert.Equal(1, session.Counters["rpc_requests"]);
        Assert.True(session.Histograms["rpc_latency_ms"].Count > 0);
    }
}
