using System.Buffers;
using System.Buffers.Binary;
using Chat_App.Infrastructure.Serialization;
using ChatApp.Binary.Core;
using ChatApp.Shared.Protocol.Tcp;
using ChatApp.Shared.Protocol.Tcp.Binary;
using ChatApp.Shared.Protocol.Tcp.Binary.Schemas;
using Core.Interfaces;
using Core.Models;
using Core.Models.DTO;
using Core.Protocol.Binary;
using Core.Protocol;
using Core.Services;
using Xunit;
using ChatMessageContract = ChatApp.Shared.Protocol.Tcp.ChatMessage;

namespace Protocol.Tests;

/// <summary>
/// 二进制载荷（chatapp-bin-v1）协商与双 codec 分流的握手契约测试。
/// 握手段始终 JSON；ServerHello.PayloadFormat 决定连接级格式；
/// 未知格式按协议违例断连；json 回退保持既有行为。
/// </summary>
public sealed class TcpBinaryNegotiationTests
{
    [Fact]
    public async Task ClientHello_AdvertisesBinaryPayloadFeatureBit()
    {
        using var tcp = new BinaryContractTcpClient();
        using var session = CreateSession(tcp);

        await session.ConnectAsync(Endpoint());

        var hello = new JsonPacketBodySerializer().Deserialize<ClientHello>(
            Assert.Single(Decode(tcp.GetSentBytes())).Body);
        Assert.NotNull(hello);
        Assert.True((hello.FeatureBits & (uint)GatewayFeature.BinaryPayload) != 0);
    }

    [Fact]
    public async Task ClientHello_AdvertiseDisabled_OmitsBinaryPayloadBit()
    {
        using var tcp = new BinaryContractTcpClient
        {
            // 服务端未启用二进制：不回显 BinaryPayload 位，只回应 json。
            ServerHelloResponse = CreateServerHello(
                ProtocolPayloadFormat.Json,
                GatewayFeature.CommandCapabilities | GatewayFeature.ConversationSync)
        };
        using var session = CreateSession(tcp);
        session.AdvertiseBinaryPayload = false;

        await session.ConnectAsync(Endpoint());

        var hello = new JsonPacketBodySerializer().Deserialize<ClientHello>(
            Assert.Single(Decode(tcp.GetSentBytes())).Body);
        Assert.NotNull(hello);
        Assert.True((hello.FeatureBits & (uint)GatewayFeature.BinaryPayload) == 0);
        Assert.Equal(SessionPayloadFormat.Json, session.NegotiatedPayloadFormat);
    }

    [Fact]
    public async Task ServerHello_BinaryPayloadFormat_CompletesHandshakeInBinaryMode()
    {
        using var tcp = new BinaryContractTcpClient
        {
            ServerHelloResponse = CreateServerHello(BinaryPayloadFormat.Id)
        };
        using var session = CreateSession(tcp);

        await session.ConnectAsync(Endpoint());

        Assert.True(session.HasCompletedHandshake);
        Assert.Equal(SessionPayloadFormat.ChatAppBinaryV1, session.NegotiatedPayloadFormat);
        Assert.True((session.NegotiatedFeatureBits & (uint)GatewayFeature.BinaryPayload) != 0);
    }

    [Fact]
    public async Task ServerHello_JsonPayloadFormat_KeepsJsonFallback()
    {
        using var tcp = new BinaryContractTcpClient
        {
            ServerHelloResponse = CreateServerHello(ProtocolPayloadFormat.Json)
        };
        using var session = CreateSession(tcp);

        await session.ConnectAsync(Endpoint());

        Assert.True(session.HasCompletedHandshake);
        Assert.Equal(SessionPayloadFormat.Json, session.NegotiatedPayloadFormat);
    }

    [Fact]
    public async Task ServerHello_UnknownPayloadFormat_IsProtocolViolationAndDisconnects()
    {
        using var tcp = new BinaryContractTcpClient
        {
            ServerHelloResponse = CreateServerHello("msgpack")
        };
        using var session = CreateSession(tcp);

        ProtocolErrorDto? observed = null;
        session.ProtocolError += (_, error) => observed = error;

        await Assert.ThrowsAsync<ProtocolRequestException>(() => session.ConnectAsync(Endpoint()));

        Assert.False(session.IsConnected);
        Assert.False(session.HasCompletedHandshake);
        Assert.NotNull(observed);
        Assert.True(observed.IsFatal);
        Assert.Equal(nameof(ProtocolErrorCode.ProtocolViolation), observed.ErrorCode);
    }

    [Fact]
    public async Task BinarySession_EncodesC2SFramesWithSharedSchema()
    {
        using var tcp = new BinaryContractTcpClient
        {
            ServerHelloResponse = CreateServerHello(BinaryPayloadFormat.Id)
        };
        using var session = CreateSession(tcp);

        await session.ConnectAsync(Endpoint());
        var clientMessageId = await session.SendChatMessageAsync(1001, "hello binary");

        var chatFrame = Decode(tcp.GetSentBytes())
            .Single(packet => packet.Command == PacketCommand.ChatMessage);
        var decode = TcpBinaryWireCodec.TryDecode(
            PacketCommand.ChatMessage, chatFrame.Body, BinaryLimits.Default);

        Assert.Equal(TcpBinaryWireStatus.Decoded, decode.Status);
        var message = Assert.IsType<ChatMessageContract>(decode.Value);
        Assert.Equal(1001, message.TargetUserId);
        Assert.Equal("hello binary", message.Content);
        Assert.Equal(clientMessageId, message.MessageId);
        // 上行幂等键回填：ClientMessageId 与 MessageId 一致（与 JSON 契约一致）。
        Assert.Equal(clientMessageId, message.ClientMessageId);
        Assert.True(message.SentAtMs > 0);
    }

    [Fact]
    public async Task BinarySession_JsonFallback_SendsJsonC2SFrames()
    {
        using var tcp = new BinaryContractTcpClient
        {
            ServerHelloResponse = CreateServerHello(ProtocolPayloadFormat.Json)
        };
        using var session = CreateSession(tcp);

        await session.ConnectAsync(Endpoint());
        await session.SendChatMessageAsync(1001, "hello json");

        var chatFrame = Decode(tcp.GetSentBytes())
            .Single(packet => packet.Command == PacketCommand.ChatMessage);
        var message = new JsonPacketBodySerializer().Deserialize<ChatMessageDto>(chatFrame.Body);

        Assert.NotNull(message);
        Assert.Equal("hello json", message.Content);
    }

    [Fact]
    public async Task BinarySession_DecodesBinaryS2CFramesAndRoutesRequestId()
    {
        using var tcp = new BinaryContractTcpClient
        {
            ServerHelloResponse = CreateServerHello(BinaryPayloadFormat.Id)
        };
        using var session = CreateSession(tcp);
        tcp.FrameSent = command =>
        {
            switch (command)
            {
                case PacketCommand.AuthenticationRequest:
                    tcp.Inject(BinaryFrame(PacketCommand.AuthenticationResponse, new AuthenticationResponse
                    {
                        Success = true,
                        UserId = 42
                    }));
                    break;
                case PacketCommand.ConversationListRequest:
                    tcp.Inject(BinaryFrame(PacketCommand.ConversationListPage, new ConversationListPage
                    {
                        RequestId = LastConversationRequestId(tcp),
                        Succeeded = true,
                        Items = [new TcpConversationListItem { ConversationId = "conv-1", Title = "binary" }]
                    }));
                    break;
            }
        };

        await session.ConnectAsync(Endpoint());
        await session.AuthenticateAsync("access-token", 42, null, null);

        Assert.True(session.IsAuthenticated);

        var response = await session.QueryConversationListAsync(10);

        Assert.True(response.Succeeded);
        var item = Assert.Single(response.Items);
        Assert.Equal("conv-1", item.ConversationId);
        Assert.Equal("binary", item.Title);
        Assert.Equal(0, session.PendingRequestCount);
    }

    [Fact]
    public async Task BinarySession_DecodesBinaryMessageAcknowledgement()
    {
        using var tcp = new BinaryContractTcpClient
        {
            ServerHelloResponse = CreateServerHello(BinaryPayloadFormat.Id)
        };
        using var session = CreateSession(tcp);

        await session.ConnectAsync(Endpoint());

        MessageAcknowledgementDto? observed = null;
        session.MessageAcknowledged += (_, ack) => observed = ack;

        tcp.Inject(BinaryFrame(PacketCommand.MessageAcknowledgement, new MessageAcknowledgement
        {
            ClientMessageId = "client-1",
            CommandId = "cmd-1",
            Accepted = true,
            AcknowledgedAtMs = 1_700_000_000_000
        }));

        Assert.NotNull(observed);
        Assert.True(observed.Accepted);
        Assert.Equal("client-1", observed.ClientMessageId);
        Assert.Equal(1_700_000_000_000, new DateTimeOffset(observed.AcknowledgedUtc).ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task BinarySession_MalformedPayload_IsProtocolViolationAndDisconnects()
    {
        using var tcp = new BinaryContractTcpClient
        {
            ServerHelloResponse = CreateServerHello(BinaryPayloadFormat.Id)
        };
        using var session = CreateSession(tcp);

        await session.ConnectAsync(Endpoint());

        ProtocolErrorDto? observed = null;
        session.ProtocolError += (_, error) => observed = error;

        // 覆盖命令的畸形载荷：非法字段标记（空载荷会被解码为全默认 DTO，不构成违规）。
        tcp.Inject(Frame(PacketCommand.ChatMessage, [0xFF, 0xFF]));

        Assert.False(session.IsConnected);
        Assert.NotNull(observed);
        Assert.True(observed.IsFatal);
        Assert.Equal(nameof(ProtocolErrorCode.ProtocolViolation), observed.ErrorCode);
    }

    [Fact]
    public async Task BinarySession_UncoveredCommand_IsProtocolViolationAndDisconnects()
    {
        using var tcp = new BinaryContractTcpClient
        {
            ServerHelloResponse = CreateServerHello(BinaryPayloadFormat.Id)
        };
        using var session = CreateSession(tcp);

        await session.ConnectAsync(Endpoint());

        ProtocolErrorDto? observed = null;
        session.ProtocolError += (_, error) => observed = error;

        // Error 命令在 schema 内，但用非法二进制体触发 DecodeFailure。
        tcp.Inject(Frame(PacketCommand.Error, [0xFF, 0xFF]));

        Assert.False(session.IsConnected);
        Assert.NotNull(observed);
        Assert.True(observed.IsFatal);
        Assert.Equal(nameof(ProtocolErrorCode.ProtocolViolation), observed.ErrorCode);
    }

    // ──────────── 工具 ────────────

    private static ChatSessionClient CreateSession(BinaryContractTcpClient tcp) =>
        new(tcp, new MessagePacketCodec(), new JsonPacketBodySerializer());

    private static ServerEndpoint Endpoint() => new()
    {
        ServerIpAddress = "127.0.0.1",
        ServerPort = 7000
    };

    private static ServerHello CreateServerHello(
        string payloadFormat,
        GatewayFeature featureBits =
            GatewayFeature.CommandCapabilities |
            GatewayFeature.ConversationSync |
            GatewayFeature.BinaryPayload) => new()
    {
        ProtocolVersion = 1,
        FeatureBits = (uint)featureBits,
        ServerDeviceId = "binary-test-gateway",
        ServerTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        HeartbeatIntervalMs = 15_000,
        MaxPayloadBytes = 1_048_576,
        ResumeSupported = false,
        PayloadFormat = payloadFormat
    };

    private static byte[] Frame(PacketCommand command, ReadOnlySpan<byte> body)
    {
        var frame = new ArrayBufferWriter<byte>(MessagePacket.HeaderSize + body.Length);
        var packet = new MessagePacket(command, new ReadOnlySequence<byte>(body.ToArray()));
        Assert.True(new MessagePacketCodec().TryWrite(packet, frame, out _));
        return frame.WrittenSpan.ToArray();
    }

    private static byte[] BinaryFrame<T>(PacketCommand command, T shared) where T : class
    {
        var buffer = new byte[BinaryLimits.Default.MaxMessageBytes];
        var encode = TcpBinaryWireEncoder.TryEncode(shared, buffer, BinaryLimits.Default);
        Assert.Equal(TcpBinaryWireEncodeStatus.Encoded, encode.Status);
        return Frame(command, buffer.AsSpan(0, encode.Written));
    }

    private static List<MessagePacket> Decode(byte[] bytes)
    {
        var codec = new MessagePacketCodec();
        codec.Append(bytes);
        var frames = new List<MessagePacket>();
        while (codec.TryRead(out var packet))
            frames.Add(packet);
        return frames;
    }

    /// <summary>ConversationListPage 需要回显请求 RequestId；从最近发出的二进制请求帧中提取。</summary>
    private static string LastConversationRequestId(BinaryContractTcpClient tcp)
    {
        var requestFrame = Decode(tcp.GetSentBytes())
            .Last(packet => packet.Command == PacketCommand.ConversationListRequest);
        var decode = TcpBinaryWireCodec.TryDecode(
            PacketCommand.ConversationListRequest, requestFrame.Body, BinaryLimits.Default);
        Assert.Equal(TcpBinaryWireStatus.Decoded, decode.Status);
        return Assert.IsType<ConversationListRequest>(decode.Value).RequestId!;
    }

    private sealed class BinaryContractTcpClient : ITcpClient
    {
        private readonly object _gate = new();
        private readonly List<byte> _sent = [];

        public bool IsConnected { get; private set; }

        public event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStatusChanged;
        public event EventHandler<ReadOnlyMemory<byte>>? OnDataChunkReceived;
        public Action<PacketCommand>? FrameSent { get; set; }
        public ServerHello ServerHelloResponse { get; set; } = new()
        {
            ProtocolVersion = 1,
            FeatureBits = (uint)(GatewayFeature.CommandCapabilities | GatewayFeature.BinaryPayload),
            ServerDeviceId = "binary-test-gateway",
            ServerTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            HeartbeatIntervalMs = 15_000,
            MaxPayloadBytes = 1_048_576,
            ResumeSupported = false,
            PayloadFormat = ProtocolPayloadFormat.Json
        };

        public Task ConnectAsync(ServerEndpoint endpoint, CancellationToken token = default)
        {
            IsConnected = true;
            ConnectionStatusChanged?.Invoke(
                this,
                new ConnectionStateChangedEventArgs(ConnectionState.Connected));
            return Task.CompletedTask;
        }

        public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken token = default)
        {
            lock (_gate)
                _sent.AddRange(data.Span.ToArray());

            var codec = new MessagePacketCodec();
            codec.Append(data);
            if (codec.TryRead(out var packet))
            {
                if (packet.Command == PacketCommand.ClientHello)
                {
                    // 握手段始终 JSON：ClientHello 后回 JSON ServerHello。
                    var body = new ArrayBufferWriter<byte>();
                    new JsonPacketBodySerializer().Serialize(body, ServerHelloResponse);
                    Inject(Frame(PacketCommand.ServerHello, body.WrittenSpan));
                }
                FrameSent?.Invoke(packet.Command);
            }
            return Task.CompletedTask;
        }

        public Task ReceiveDataAsync(CancellationToken token) => Task.Delay(Timeout.Infinite, token);

        public void Disconnect(string? reason = null)
        {
            IsConnected = false;
            ConnectionStatusChanged?.Invoke(
                this,
                new ConnectionStateChangedEventArgs(ConnectionState.Disconnected, reason));
        }

        public byte[] GetSentBytes()
        {
            lock (_gate)
                return _sent.ToArray();
        }

        public void Inject(ReadOnlyMemory<byte> frame) =>
            OnDataChunkReceived?.Invoke(this, frame);

        public void Dispose() => IsConnected = false;
    }
}
