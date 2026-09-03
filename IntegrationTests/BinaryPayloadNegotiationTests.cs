using System.Buffers;
using Chat_App.Infrastructure.Serialization;
using ChatApp.Binary.Core;
using ChatApp.Shared.Protocol.Tcp;
using ChatApp.Shared.Protocol.Tcp.Binary;
using ChatApp.Shared.Protocol.Tcp.Binary.Schemas;
using Core.Interfaces;
using Core.Models;
using Core.Models.DTO;
using Core.Protocol;
using Core.Protocol.Binary;
using Core.Services;
using Xunit;
using ChatMessageContract = ChatApp.Shared.Protocol.Tcp.ChatMessage;

namespace IntegrationTests;

/// <summary>
/// BIN-INTEGRATION-3 客户端接入的端到端验收：
/// 假网关以 PayloadFormat=chatapp-bin-v1 完成握手（握手段始终 JSON），
/// 客户端随后对 S2C 帧按共享 schema 解码、对 C2S 帧按共享 schema 编码；
/// 同时验收 json 回退（老服务端）行为不变。
/// </summary>
public sealed class BinaryPayloadNegotiationTests
{
    private const long OwnerId = 42;

    [Fact]
    public async Task BinaryNegotiation_CompletesHandshakeAndDecodesBinaryS2C()
    {
        using var tcp = new BinaryScriptedTcpClient();
        var session = await ConnectAsync(tcp);

        Assert.Equal(SessionPayloadFormat.ChatAppBinaryV1, session.NegotiatedPayloadFormat);
        Assert.True((session.NegotiatedFeatureBits & (uint)GatewayFeature.BinaryPayload) != 0);

        MessageAcknowledgementDto? observed = null;
        session.MessageAcknowledged += (_, ack) => observed = ack;

        tcp.InjectData(TcpHandshakeTestServer.CreateBinaryFrame(
            PacketCommand.MessageAcknowledgement,
            new MessageAcknowledgement
            {
                ClientMessageId = "client-1",
                CommandId = "cmd-1",
                Accepted = true,
                AcknowledgedAtMs = 1_700_000_000_000
            }));

        Assert.NotNull(observed);
        Assert.True(observed.Accepted);
        Assert.Equal("client-1", observed.ClientMessageId);
        Assert.Equal(
            1_700_000_000_000,
            new DateTimeOffset(observed.AcknowledgedUtc).ToUnixTimeMilliseconds());

        await session.DisconnectAsync();
    }

    [Fact]
    public async Task BinaryNegotiation_EncodesC2SFramesWithSharedSchema()
    {
        using var tcp = new BinaryScriptedTcpClient();
        var session = await ConnectAsync(tcp);

        var clientMessageId = await session.SendChatMessageAsync(1001, "binary hello");

        var chatBody = await tcp.WaitForFrameAsync(PacketCommand.ChatMessage);
        var decode = TcpBinaryWireCodec.TryDecode(PacketCommand.ChatMessage, chatBody.Span, BinaryLimits.Default);
        Assert.Equal(TcpBinaryWireStatus.Decoded, decode.Status);

        var message = Assert.IsType<ChatMessageContract>(decode.Value);
        Assert.Equal(1001, message.TargetUserId);
        Assert.Equal("binary hello", message.Content);
        Assert.Equal(clientMessageId, message.MessageId);
        Assert.Equal(clientMessageId, message.ClientMessageId);
        Assert.True(message.SentAtMs > 0);

        await session.DisconnectAsync();
    }

    [Fact]
    public async Task BinaryNegotiation_RequestIdRoutingCompletesPendingRpc()
    {
        using var tcp = new BinaryScriptedTcpClient();
        var session = await ConnectAsync(tcp);

        var pending = session.QueryConversationListAsync(10);
        var requestBody = await tcp.WaitForFrameAsync(PacketCommand.ConversationListRequest);
        var requestDecode = TcpBinaryWireCodec.TryDecode(
            PacketCommand.ConversationListRequest, requestBody.Span, BinaryLimits.Default);
        Assert.Equal(TcpBinaryWireStatus.Decoded, requestDecode.Status);
        var requestId = Assert.IsType<ConversationListRequest>(requestDecode.Value).RequestId!;

        tcp.InjectData(TcpHandshakeTestServer.CreateBinaryFrame(
            PacketCommand.ConversationListPage,
            new ConversationListPage
            {
                RequestId = requestId,
                Succeeded = true,
                Items = [new TcpConversationListItem { ConversationId = "conv-bin", Title = "binary page" }]
            }));

        var response = await pending;
        Assert.True(response.Succeeded);
        Assert.Equal("conv-bin", Assert.Single(response.Items).ConversationId);
        Assert.Equal(0, session.PendingRequestCount);

        await session.DisconnectAsync();
    }

    [Fact]
    public async Task JsonFallback_KeepsLegacyJsonBehavior()
    {
        using var tcp = new BinaryScriptedTcpClient(useBinaryHandshake: false);
        var session = await ConnectAsync(tcp);

        Assert.Equal(SessionPayloadFormat.Json, session.NegotiatedPayloadFormat);

        MessageAcknowledgementDto? observed = null;
        session.MessageAcknowledged += (_, ack) => observed = ack;
        InjectJsonFrame(tcp, PacketCommand.MessageAcknowledgement, new MessageAcknowledgementDto
        {
            ClientMessageId = "client-1",
            Accepted = true
        });

        Assert.NotNull(observed);
        Assert.True(observed.Accepted);

        var pending = session.QueryConversationListAsync(10);
        var requestBody = await tcp.WaitForFrameAsync(PacketCommand.ConversationListRequest);
        var request = new JsonPacketBodySerializer().Deserialize<ConversationListRequestDto>(new ReadOnlySequence<byte>(requestBody));
        Assert.NotNull(request);

        InjectJsonFrame(tcp, PacketCommand.ConversationListPage, new ConversationListResponseDto
        {
            RequestId = request.RequestId!,
            Succeeded = true,
            Items = [new TcpConversationListItem { ConversationId = "conv-json" }]
        });
        var response = await pending;
        Assert.Equal("conv-json", Assert.Single(response.Items).ConversationId);

        await session.DisconnectAsync();
    }

    // ── 测试底座 ──

    private static async Task<ChatSessionClient> ConnectAsync(BinaryScriptedTcpClient tcp)
    {
        // 鉴权回包与请求同格式：二进制会话回共享 schema 帧，JSON 会话回 JSON 帧。
        tcp.OnFrameSent += (cmd, body) =>
        {
            if (cmd != PacketCommand.AuthenticationRequest)
                return;

            var binaryDecode = TcpBinaryWireCodec.TryDecode(
                PacketCommand.AuthenticationRequest, body.Span, BinaryLimits.Default);
            if (binaryDecode.Status == TcpBinaryWireStatus.Decoded)
            {
                var request = Assert.IsType<AuthenticationRequest>(binaryDecode.Value);
                if (request.AccessToken != "token")
                    return;
                tcp.InjectData(TcpHandshakeTestServer.CreateBinaryFrame(
                    PacketCommand.AuthenticationResponse,
                    new AuthenticationResponse { Success = true, UserId = OwnerId }));
                return;
            }

            var jsonRequest = new JsonPacketBodySerializer().Deserialize<AuthRequestDto>(new ReadOnlySequence<byte>(body));
            if (jsonRequest is { AccessToken: "token" })
            {
                InjectJsonFrame(tcp, PacketCommand.AuthenticationResponse,
                    new AuthResponseDto { Success = true, UserId = OwnerId });
            }
        };

        var session = new ChatSessionClient(tcp, new MessagePacketCodec(), new JsonPacketBodySerializer());
        await session.ConnectAsync(new ServerEndpoint { ServerIpAddress = "127.0.0.1", ServerPort = 7000 });
        await session.AuthenticateAsync("token", OwnerId, null, null);
        Assert.True(session.IsAuthenticated);
        return session;
    }

    private static void InjectJsonFrame<T>(BinaryScriptedTcpClient tcp, PacketCommand command, T? payload)
    {
        var writer = new ArrayBufferWriter<byte>(MessagePacket.HeaderSize + 64);
        new JsonPacketBodySerializer().Serialize(writer, payload);
        var packet = new MessagePacket(
            command,
            writer.WrittenCount == 0
                ? ReadOnlySequence<byte>.Empty
                : new ReadOnlySequence<byte>(writer.WrittenSpan.ToArray()));
        var frameWriter = new ArrayBufferWriter<byte>(MessagePacket.HeaderSize + writer.WrittenCount);
        new MessagePacketCodec().TryWrite(packet, frameWriter, out _);
        tcp.InjectData(frameWriter.WrittenMemory);
    }

    /// <summary>可控假 TCP 客户端：按 ClientHello 回指定格式的 JSON ServerHello，缓存出站帧体供断言。</summary>
    private sealed class BinaryScriptedTcpClient(bool useBinaryHandshake = true) : ITcpClient
    {
        private readonly object _gate = new();
        private readonly Dictionary<PacketCommand, ReadOnlyMemory<byte>> _frameBodies = new();
        private readonly Dictionary<PacketCommand, TaskCompletionSource<ReadOnlyMemory<byte>>> _waiters = new();

        public bool IsConnected { get; private set; }

        public event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStatusChanged;
        public event EventHandler<ReadOnlyMemory<byte>>? OnDataChunkReceived;
        public event Action<PacketCommand, ReadOnlyMemory<byte>>? OnFrameSent;

        public Task ConnectAsync(ServerEndpoint endpoint, CancellationToken token = default)
        {
            IsConnected = true;
            ConnectionStatusChanged?.Invoke(this, new ConnectionStateChangedEventArgs(ConnectionState.Connected));
            return Task.CompletedTask;
        }

        public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken token = default)
        {
            var seq = new ReadOnlySequence<byte>(data);
            while (MessagePacket.TryDeserialize(ref seq, out var pkt, out _))
            {
                if (pkt.Command == PacketCommand.ClientHello)
                {
                    // 握手段始终 JSON；按测试意图选择 ServerHello 的 PayloadFormat。
                    OnDataChunkReceived?.Invoke(
                        this,
                        useBinaryHandshake
                            ? TcpHandshakeTestServer.BinaryServerHelloFrame
                            : TcpHandshakeTestServer.ServerHelloFrame);
                    continue;
                }

                var body = pkt.Body.ToArray();
                TaskCompletionSource<ReadOnlyMemory<byte>>? waiter = null;
                lock (_gate)
                {
                    _frameBodies[pkt.Command] = body;
                    if (_waiters.Remove(pkt.Command, out var registered))
                        waiter = registered;
                }
                waiter?.TrySetResult(body);
                OnFrameSent?.Invoke(pkt.Command, body);
            }
            return Task.CompletedTask;
        }

        /// <summary>等待指定命令的出站帧体；已发出则立即返回，否则挂起等待。</summary>
        public async Task<ReadOnlyMemory<byte>> WaitForFrameAsync(PacketCommand command)
        {
            TaskCompletionSource<ReadOnlyMemory<byte>>? tcs;
            lock (_gate)
            {
                if (_frameBodies.TryGetValue(command, out var body))
                    return body;
                tcs = new TaskCompletionSource<ReadOnlyMemory<byte>>(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters[command] = tcs;
            }
            return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        public Task ReceiveDataAsync(CancellationToken token) => Task.Delay(Timeout.Infinite, token);

        public void Disconnect(string? reason = null)
        {
            IsConnected = false;
            ConnectionStatusChanged?.Invoke(
                this,
                new ConnectionStateChangedEventArgs(ConnectionState.Disconnected, reason));
        }

        public void InjectData(ReadOnlyMemory<byte> frame) =>
            OnDataChunkReceived?.Invoke(this, frame);

        public void Dispose() => IsConnected = false;
    }
}
