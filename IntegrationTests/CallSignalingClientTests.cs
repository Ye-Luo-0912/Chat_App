using Chat_App.Infrastructure.Serialization;
using ChatApp.Shared.Protocol.Tcp;
using Core.Interfaces;
using Core.Models;
using Core.Models.DTO;
using Core.Protocol;
using Core.Services;
using System.Buffers;
using Xunit;

namespace IntegrationTests;

/// <summary>
/// CALL-E2E-2 客户端通话信令控制面 wire 层验证。
/// <para>
/// 验证：能力协商（CallSignaling 位派生）、invite 命令请求-响应往返（camelCase wire，
/// grant 原样携带 + SDP 有界）、对端 <c>CallSignal</c> S2C push 事件、本地参数校验
/// 与断线 fail-closed 回收。使用 <see cref="ScriptedTcpClient"/> 假网关驱动
/// <see cref="ChatSessionClient"/>，协议包编码/解码走真实实现。
/// </para>
/// </summary>
public sealed class CallSignalingClientTests
{
    private const long OwnerId = 5001;
    private const long PeerId = 5002;

    // ── 能力协商 ──

    [Fact]
    public async Task CallSignaling_Negotiated_When_Server_Echoes_Capability()
    {
        var serializer = new JsonPacketBodySerializer();
        using var tcp = new ScriptedTcpClient();
        var session = await ConnectAndAuthenticateAsync(tcp, serializer);
        Assert.True(session.SupportsCallSignaling);
    }

    [Fact]
    public async Task CallSignaling_FailClosed_When_Not_Negotiated()
    {
        var serializer = new JsonPacketBodySerializer();
        using var tcp = new ScriptedTcpClient();
        var session = await ConnectAndAuthenticateAsync(tcp, serializer, echoCallSignaling: false);
        Assert.False(session.SupportsCallSignaling);
        await Assert.ThrowsAsync<NotSupportedException>(() => session.SendCallCommandAsync(
            new CallCommandRequestDto { CommandId = "c", CallId = "call", ActorUserId = OwnerId }));
    }

    [Fact]
    public async Task Messaging_And_Sync_Unaffected_When_CallSignaling_Not_Negotiated()
    {
        var serializer = new JsonPacketBodySerializer();
        using var tcp = new ScriptedTcpClient();
        var session = await ConnectAndAuthenticateAsync(tcp, serializer, echoCallSignaling: false);

        // 关闭通话能力：call 命令 fail-closed，CallSignaling 能力位未协商。
        Assert.Equal(0u, session.NegotiatedFeatureBits & (uint)GatewayFeature.CallSignaling);

        // 消息与同步能力位不受影响。
        Assert.NotEqual(0u, session.NegotiatedFeatureBits & (uint)GatewayFeature.ConversationSync);
        Assert.NotEqual(0u, session.NegotiatedFeatureBits & (uint)GatewayFeature.MessageMutation);

        // 非通话路径仍正常工作：消息发送帧照常发出。
        var chatFrameSeen = false;
        tcp.OnFrameSent += (cmd, _) =>
        {
            if (cmd == PacketCommand.ChatMessage)
                chatFrameSeen = true;
        };

        var messageId = await session.SendChatMessageAsync(PeerId, "hello-while-call-disabled");
        Assert.False(string.IsNullOrEmpty(messageId));
        Assert.True(chatFrameSeen, "关闭 CallSignaling 能力后消息仍应正常发出");
    }

    // ── 命令请求-响应往返（wire 字段 + camelCase） ──

    [Fact]
    public async Task Invite_Command_RoundTrips_With_Grant_And_Sdp()
    {
        var serializer = new JsonPacketBodySerializer();
        using var tcp = new ScriptedTcpClient();
        var session = await ConnectAndAuthenticateAsync(tcp, serializer);

        CallCommandRequestDto? captured = null;
        tcp.OnFrameSent += (cmd, body) =>
        {
            if (cmd != PacketCommand.CallCommandRequest)
                return;
            var seen = serializer.Deserialize<CallCommandRequestDto>(new ReadOnlySequence<byte>(body));
            captured = seen;
            if (seen is null)
                return;
            InjectPacket(tcp, serializer, PacketCommand.CallCommandResponse,
                new CallCommandResponseDto
                {
                    RequestId = seen.RequestId,
                    CallId = seen.CallId,
                    Succeeded = true,
                    State = CallStateDto.Ringing,
                    EndReason = CallEndReasonDto.None,
                    Revision = seen.Revision
                });
        };

        const string sdp = "v=0\r\no=caller 1 1 IN IP4 127.0.0.1\r\ns=-\r\nm=audio 40000 RTP/AVP 0\r\n";
        var response = await session.SendCallCommandAsync(new CallCommandRequestDto
        {
            CommandId = "cmd-invite-1",
            CallId = "call-1",
            Type = CallCommandTypeDto.Invite,
            ActorUserId = OwnerId,
            Revision = 1,
            Grant = new CallGrantDto
            {
                CallId = "call-1",
                CallerUserId = OwnerId,
                CalleeUserId = PeerId,
                ExpiresAtMs = 1_900_000_000_000L,
                Nonce = "nonce-1",
                Signature = "sig-1"
            },
            Sdp = sdp,
            ClientOccurredAtMs = 1_900_000_000_000L
        });

        // 响应按 RequestId 精确配对
        Assert.True(response.Succeeded);
        Assert.Equal("call-1", response.CallId);
        Assert.Equal(CallStateDto.Ringing, response.State);
        Assert.Equal(1, response.Revision);
        Assert.False(response.Replayed);
        Assert.Null(response.ErrorCode);

        // wire 请求字段完整（camelCase 序列化往返）
        Assert.NotNull(captured);
        Assert.Equal("cmd-invite-1", captured.CommandId);
        Assert.Equal("call-1", captured.CallId);
        Assert.Equal(CallCommandTypeDto.Invite, captured.Type);
        Assert.Equal(OwnerId, captured.ActorUserId);
        Assert.Equal(1, captured.Revision);
        Assert.Equal(sdp, captured.Sdp);
        Assert.False(string.IsNullOrWhiteSpace(captured.RequestId));
        Assert.NotNull(captured.Grant);
        Assert.Equal("call-1", captured.Grant.CallId);
        Assert.Equal(PeerId, captured.Grant.CalleeUserId);
        Assert.Equal("sig-1", captured.Grant.Signature);
    }

    // ── 对端 CallSignal S2C push 事件 ──

    [Fact]
    public async Task CallSignal_Push_Raises_Event()
    {
        var serializer = new JsonPacketBodySerializer();
        using var tcp = new ScriptedTcpClient();
        var session = await ConnectAndAuthenticateAsync(tcp, serializer);

        CallSignalDto? received = null;
        session.CallSignalReceived += (_, signal) => received = signal;

        InjectPacket(tcp, serializer, PacketCommand.CallSignal, new CallSignalDto
        {
            SignalId = "sig-1",
            CallId = "call-1",
            FromUserId = PeerId,
            ToUserId = OwnerId,
            Kind = CallCommandTypeDto.Invite,
            Sdp = "v=0\r\no=callee 1 1 IN IP4 127.0.0.1\r\ns=-\r\nm=audio 40001 RTP/AVP 0\r\n",
            Revision = 1,
            OccurredAtMs = 1_900_000_000_000L
        });

        Assert.NotNull(received);
        Assert.Equal("sig-1", received.SignalId);
        Assert.Equal("call-1", received.CallId);
        Assert.Equal(PeerId, received.FromUserId);
        Assert.Equal(OwnerId, received.ToUserId);
        Assert.Equal(CallCommandTypeDto.Invite, received.Kind);
        Assert.Contains("m=audio", received.Sdp);
        Assert.Equal(1, received.Revision);
    }

    // ── 本地参数校验 ──

    [Fact]
    public async Task SendCallCommand_Validates_Inputs()
    {
        var serializer = new JsonPacketBodySerializer();
        using var tcp = new ScriptedTcpClient();
        var session = await ConnectAndAuthenticateAsync(tcp, serializer);

        await Assert.ThrowsAsync<ArgumentNullException>(() => session.SendCallCommandAsync(null!));

        await Assert.ThrowsAsync<ArgumentException>(() => session.SendCallCommandAsync(
            new CallCommandRequestDto { CommandId = "", CallId = "call", ActorUserId = OwnerId }));
        await Assert.ThrowsAsync<ArgumentException>(() => session.SendCallCommandAsync(
            new CallCommandRequestDto { CommandId = new string('x', TcpCallConstants.MaxCommandIdBytes + 1), CallId = "call", ActorUserId = OwnerId }));
        await Assert.ThrowsAsync<ArgumentException>(() => session.SendCallCommandAsync(
            new CallCommandRequestDto { CommandId = "c", CallId = "", ActorUserId = OwnerId }));
        await Assert.ThrowsAsync<ArgumentException>(() => session.SendCallCommandAsync(
            new CallCommandRequestDto { CommandId = "c", CallId = "call", ActorUserId = 0 }));

        // SDP 超预算：严格拒绝，不截断
        var oversized = new string('a', TcpCallConstants.MaxSdpBytes + 1);
        await Assert.ThrowsAsync<ArgumentException>(() => session.SendCallCommandAsync(
            new CallCommandRequestDto { CommandId = "c", CallId = "call", ActorUserId = OwnerId, Sdp = oversized }));
    }

    // ── 断线 fail-closed：在途命令以 IOException 失败，pending 清零 ──

    [Fact]
    public async Task Disconnect_Fails_Pending_CallCommand_And_Empties()
    {
        var serializer = new JsonPacketBodySerializer();
        using var tcp = new ScriptedTcpClient();
        var session = await ConnectAndAuthenticateAsync(tcp, serializer);

        var request = session.SendCallCommandAsync(new CallCommandRequestDto
        {
            CommandId = "cmd-1",
            CallId = "call-1",
            Type = CallCommandTypeDto.Accept,
            ActorUserId = OwnerId,
            Revision = 2
        });
        await WaitForCallCommandFrameAsync(tcp);
        tcp.Disconnect("验收断线");

        var ex = await Assert.ThrowsAsync<IOException>(() => request);
        Assert.Contains("验收断线", ex.Message);
        Assert.Equal(0, session.PendingRequestCount);
    }

    // ── 测试底座 ──

    private static async Task<ChatSessionClient> ConnectAndAuthenticateAsync(
        ScriptedTcpClient tcp,
        JsonPacketBodySerializer serializer,
        bool echoCallSignaling = true)
    {
        tcp.HandshakeFrame = echoCallSignaling
            ? TcpHandshakeTestServer.ServerHelloFrame
            : BuildServerHelloFrame(serializer, withCallSignaling: false);
        tcp.OnFrameSent += (cmd, _) =>
        {
            if (cmd == PacketCommand.AuthenticationRequest)
            {
                InjectPacket(tcp, serializer, PacketCommand.AuthenticationResponse,
                    new AuthResponseDto { Success = true, UserId = OwnerId });
            }
        };

        var session = new ChatSessionClient(tcp, new MessagePacketCodec(), serializer);
        await session.ConnectAsync(new ServerEndpoint { ServerIpAddress = "127.0.0.1", ServerPort = 7000 });
        await session.AuthenticateAsync("token", OwnerId, null, null);
        Assert.True(session.IsAuthenticated);
        return session;
    }

    private static async Task WaitForCallCommandFrameAsync(ScriptedTcpClient tcp)
    {
        for (var i = 0; i < 500; i++)
        {
            if (tcp.CallCommandFrameSeen)
                return;
            await Task.Delay(10);
        }
        throw new TimeoutException("等待 CallCommandRequest 帧超时");
    }

    private static void InjectPacket<T>(
        ScriptedTcpClient tcp,
        IPacketBodySerializer serializer,
        PacketCommand command,
        T? payload)
    {
        var writer = new ArrayBufferWriter<byte>(MessagePacket.HeaderSize + 64);
        serializer.Serialize(writer, payload);
        var bodyLen = writer.WrittenCount;
        var packet = new MessagePacket(command,
            bodyLen == 0 ? ReadOnlySequence<byte>.Empty : new ReadOnlySequence<byte>(writer.WrittenSpan.ToArray()));
        var frameWriter = new ArrayBufferWriter<byte>(MessagePacket.HeaderSize + bodyLen);
        new MessagePacketCodec().TryWrite(packet, frameWriter, out _);
        tcp.InjectData(frameWriter.WrittenMemory);
    }

    private static ReadOnlyMemory<byte> BuildServerHelloFrame(JsonPacketBodySerializer serializer, bool withCallSignaling)
    {
        var bits = GatewayFeature.CommandCapabilities |
                   GatewayFeature.ConversationSync |
                   GatewayFeature.ConversationPreferences |
                   GatewayFeature.MessageMutation |
                   GatewayFeature.PresenceAndTyping |
                   GatewayFeature.GroupManagement |
                   GatewayFeature.RelationshipRead;
        if (withCallSignaling)
            bits |= GatewayFeature.CallSignaling;

        var body = new ArrayBufferWriter<byte>();
        serializer.Serialize(body, new ServerHello
        {
            ProtocolVersion = 1,
            FeatureBits = (uint)bits,
            ServerDeviceId = "integration-test-gateway",
            ServerTimeMs = 1_700_000_000_000,
            HeartbeatIntervalMs = 15_000,
            MaxPayloadBytes = 1_048_576,
            ResumeSupported = false,
            PayloadFormat = ProtocolPayloadFormat.Json
        });

        var frame = new ArrayBufferWriter<byte>(MessagePacket.HeaderSize + body.WrittenCount);
        var packet = new MessagePacket(PacketCommand.ServerHello, new ReadOnlySequence<byte>(body.WrittenMemory));
        if (!new MessagePacketCodec().TryWrite(packet, frame, out _))
            throw new InvalidOperationException("无法构造测试 ServerHello 帧");
        return frame.WrittenSpan.ToArray();
    }

    /// <summary>可控的假 TCP 客户端：解析每次发送的帧，回显握手/鉴权，暴露 CallCommandRequest 观测。</summary>
    private sealed class ScriptedTcpClient : ITcpClient
    {
        private volatile bool _connected;
        private ReadOnlyMemory<byte> _handshakeFrame = TcpHandshakeTestServer.ServerHelloFrame;

        public ReadOnlyMemory<byte> HandshakeFrame
        {
            get => _handshakeFrame;
            set => _handshakeFrame = value;
        }

        public bool IsConnected => _connected;
        public bool CallCommandFrameSeen { get; private set; }

        public event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStatusChanged;
        public event EventHandler<ReadOnlyMemory<byte>>? OnDataChunkReceived;
        public event Action<PacketCommand, ReadOnlyMemory<byte>>? OnFrameSent;

        public Task ConnectAsync(ServerEndpoint endpoint, CancellationToken token = default)
        {
            _connected = true;
            ConnectionStatusChanged?.Invoke(this, new ConnectionStateChangedEventArgs(ConnectionState.Connected));
            return Task.CompletedTask;
        }

        public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken token = default)
        {
            var seq = new ReadOnlySequence<byte>(data);
            while (seq.Length > 0)
            {
                if (!MessagePacket.TryDeserialize(ref seq, out var pkt, out _))
                    break;
                if (pkt.Command == PacketCommand.ClientHello)
                    OnDataChunkReceived?.Invoke(this, _handshakeFrame);
                if (pkt.Command == PacketCommand.CallCommandRequest)
                    CallCommandFrameSeen = true;
                OnFrameSent?.Invoke(pkt.Command, pkt.Body.ToArray());
            }
            return Task.CompletedTask;
        }

        public Task ReceiveDataAsync(CancellationToken token) => Task.Delay(-1, token);

        public void Disconnect(string? reason = null)
        {
            if (!_connected)
                return;
            _connected = false;
            ConnectionStatusChanged?.Invoke(this, new ConnectionStateChangedEventArgs(ConnectionState.Disconnected, reason));
        }

        public void InjectData(ReadOnlyMemory<byte> chunk)
            => OnDataChunkReceived?.Invoke(this, chunk);

        public void Dispose()
        {
            _connected = false;
            GC.SuppressFinalize(this);
        }
    }
}
