using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Chat_App.Infrastructure.Serialization;
using ChatApp.Shared.Protocol.Tcp;
using Core.Interfaces;
using Core.Models;
using Core.Models.DTO;
using Core.Protocol;
using Core.Protocol.Binary;
using Core.Services;
using Xunit;

namespace Protocol.Tests;

public sealed class TcpHandshakeContractTests
{
    [Fact]
    public async Task ConnectAsync_SendsCanonicalClientHelloAsFirstFrame()
    {
        using var tcp = new ContractTcpClient();
        using var session = CreateSession(tcp);

        await session.ConnectAsync(Endpoint());

        var frames = Decode(tcp.GetSentBytes());
        var frame = Assert.Single(frames);
        Assert.Equal(PacketCommand.ClientHello, frame.Command);

        var hello = new JsonPacketBodySerializer().Deserialize<ClientHello>(frame.Body);
        Assert.NotNull(hello);
        Assert.Equal((ushort)1, hello.ProtocolVersion);
        Assert.Equal(32, hello.InstallationId?.Length);
        Assert.Equal(MessagePacket.MaxBodySize, hello.MaxPayloadBytes);
        Assert.True((hello.FeatureBits & (uint)GatewayFeature.CommandCapabilities) != 0);
        Assert.True(hello.ClientTimeMs > 0);
        Assert.Null(hello.ResumeToken);
    }

    [Fact]
    public async Task ServerHello_CompletesHandshakeAndPublishesNegotiatedLimits()
    {
        using var tcp = new ContractTcpClient();
        using var session = CreateSession(tcp);
        var featureBits = (uint)(GatewayFeature.CommandCapabilities | GatewayFeature.ConversationSync);
        tcp.ServerHelloResponse = new ServerHello
        {
            ProtocolVersion = 1,
            FeatureBits = featureBits,
            ServerDeviceId = Guid.NewGuid().ToString("N"),
            ServerTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            HeartbeatIntervalMs = 15_000,
            MaxPayloadBytes = 1_048_576,
            ResumeSupported = false,
            PayloadFormat = ProtocolPayloadFormat.Json
        };

        await session.ConnectAsync(Endpoint());

        Assert.True(session.HasCompletedHandshake);
        Assert.Equal((ushort)1, session.NegotiatedProtocolVersion);
        Assert.Equal(featureBits, session.NegotiatedFeatureBits);
        Assert.Equal(15_000, session.ServerHeartbeatIntervalMs);
        Assert.Equal(1_048_576, session.ServerMaxPayloadBytes);
    }

    [Fact]
    public async Task ConnectAsync_DoesNotCompleteUntilServerHelloArrives()
    {
        using var tcp = new ContractTcpClient { AutoReplyServerHello = false };
        using var session = CreateSession(tcp);

        var connectTask = session.ConnectAsync(Endpoint());
        await tcp.ClientHelloSent.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.False(connectTask.IsCompleted);
        Assert.False(session.HasCompletedHandshake);

        tcp.Inject(Frame(PacketCommand.ServerHello, ContractTcpClient.CreateDefaultServerHello()));
        await connectTask.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.True(session.HasCompletedHandshake);
    }

    [Fact]
    public async Task DisconnectDuringHandshake_FailsCurrentGeneration_AndReconnectCanComplete()
    {
        using var tcp = new ContractTcpClient { AutoReplyServerHello = false };
        using var session = CreateSession(tcp);

        var firstConnect = session.ConnectAsync(Endpoint());
        await tcp.ClientHelloSent.Task.WaitAsync(TimeSpan.FromSeconds(1));
        tcp.Disconnect("link lost");

        await Assert.ThrowsAsync<IOException>(() => firstConnect);
        Assert.False(session.HasCompletedHandshake);

        tcp.AutoReplyServerHello = true;
        await session.ConnectAsync(Endpoint());

        Assert.True(session.HasCompletedHandshake);
        Assert.Equal(2, session.ConnectionGeneration);
    }

    [Fact]
    public async Task AuthenticationRequest_IsAlwaysQueuedAfterClientHello()
    {
        using var tcp = new ContractTcpClient();
        using var session = CreateSession(tcp);
        tcp.FrameSent = command =>
        {
            if (command == PacketCommand.AuthenticationRequest)
            {
                tcp.Inject(Frame(PacketCommand.AuthenticationResponse, new AuthResponseDto
                {
                    Success = true,
                    UserId = 42
                }));
            }
        };

        await session.ConnectAsync(Endpoint());
        await session.AuthenticateAsync("access-token", 42, null, null);

        var commands = Decode(tcp.GetSentBytes())
            .Select(static packet => packet.Command)
            .ToArray();
        Assert.Equal(
            [PacketCommand.ClientHello, PacketCommand.AuthenticationRequest],
            commands);
        Assert.True(session.IsAuthenticated);
    }

    [Fact]
    public async Task CanonicalProtocolError_IsMappedToExistingClientEvent()
    {
        using var tcp = new ContractTcpClient();
        using var session = CreateSession(tcp);
        await session.ConnectAsync(Endpoint());

        ProtocolErrorDto? observed = null;
        session.ProtocolError += (_, error) => observed = error;

        tcp.Inject(Frame(PacketCommand.Error, new ProtocolErrorFrame
        {
            Code = ProtocolErrorCode.RateLimited,
            Fatal = false,
            RetryAfterMs = 750,
            Message = "slow down",
            OriginCommand = (ushort)PacketCommand.PresenceQuery
        }));

        Assert.NotNull(observed);
        Assert.Equal(PacketCommand.PresenceQuery, observed.Command);
        Assert.Equal(nameof(ProtocolErrorCode.RateLimited), observed.ErrorCode);
        Assert.Equal("slow down", observed.ErrorMessage);
        Assert.Equal(750, observed.RetryAfterMs);
        Assert.False(observed.IsFatal);
    }

    [Fact]
    public async Task LegacyProtocolError_WithRetryAfter_IsNotMisclassifiedAsCanonical()
    {
        using var tcp = new ContractTcpClient();
        using var session = CreateSession(tcp);
        await session.ConnectAsync(Endpoint());

        ProtocolErrorDto? observed = null;
        session.ProtocolError += (_, error) => observed = error;

        tcp.Inject(Frame(PacketCommand.Error, new ProtocolErrorDto
        {
            RequestId = "legacy-request",
            Command = PacketCommand.PresenceQuery,
            ErrorCode = "LEGACY_RATE_LIMIT",
            ErrorMessage = "legacy slow down",
            RetryAfterMs = 500
        }));

        Assert.NotNull(observed);
        Assert.Equal("legacy-request", observed.RequestId);
        Assert.Equal("LEGACY_RATE_LIMIT", observed.ErrorCode);
        Assert.Equal(500, observed.RetryAfterMs);
    }

    [Fact]
    public async Task CanonicalError_WithOriginCommand_FailsPendingRpcImmediately()
    {
        using var tcp = new ContractTcpClient();
        using var session = CreateSession(tcp);
        tcp.FrameSent = command =>
        {
            if (command == PacketCommand.AuthenticationRequest)
            {
                tcp.Inject(Frame(PacketCommand.AuthenticationResponse, new AuthResponseDto
                {
                    Success = true,
                    UserId = 42
                }));
            }
            else if (command == PacketCommand.PresenceQuery)
            {
                tcp.Inject(Frame(PacketCommand.Error, new ProtocolErrorFrame
                {
                    Code = ProtocolErrorCode.FeatureNotNegotiated,
                    Message = "presence disabled",
                    OriginCommand = (ushort)PacketCommand.PresenceQuery
                }));
            }
        };

        await session.ConnectAsync(Endpoint());
        await session.AuthenticateAsync("access-token", 42, null, null);

        var exception = await Assert.ThrowsAsync<ProtocolRequestException>(
            () => session.QueryPresenceAsync([1001]));

        Assert.Equal(nameof(ProtocolErrorCode.FeatureNotNegotiated), exception.Error.ErrorCode);
        Assert.Equal(PacketCommand.PresenceQuery, exception.Error.Command);
        Assert.Equal(0, session.PendingRequestCount);
    }

    [Fact]
    public async Task UnsolicitedResumeResponse_IsFatalAndNeverAuthenticatesClient()
    {
        using var tcp = new ContractTcpClient();
        using var session = CreateSession(tcp);
        await session.ConnectAsync(Endpoint());

        ProtocolErrorDto? observed = null;
        session.ProtocolError += (_, error) => observed = error;

        tcp.Inject(Frame(PacketCommand.ResumeResponse, new ResumeResponse
        {
            Success = true,
            UserId = 42,
            ResumeToken = "unsolicited"
        }));

        Assert.False(session.IsAuthenticated);
        Assert.False(session.IsConnected);
        Assert.NotNull(observed);
        Assert.True(observed.IsFatal);
        Assert.Equal(nameof(ProtocolErrorCode.ProtocolViolation), observed.ErrorCode);
    }

    // ── Resume 成功路径：ClientHello 携带 token，网关只回 ResumeResponse（无 ServerHello）──

    [Fact]
    public async Task ResumeConnect_Success_AuthenticatesWithoutAuthenticationRequest()
    {
        using var tcp = new ContractTcpClient { AutoReplyServerHello = false };
        using var session = CreateSession(tcp);
        tcp.ClientHelloReceived = hello =>
        {
            // 网关契约：携带 token 且已协商 SessionResume 时尝试恢复，成功只回 ResumeResponse。
            if (!string.IsNullOrEmpty(hello.ResumeToken))
            {
                tcp.Inject(Frame(PacketCommand.ResumeResponse, new ResumeResponse
                {
                    Success = true,
                    UserId = 42,
                    SessionId = "session-1",
                    DeviceId = "device-1",
                    ResumeToken = "rotated-token",
                    LastConversationSequence = 7
                }));
            }
        };

        await session.ConnectAsync(Endpoint(), default, "valid-token");

        Assert.True(session.HasCompletedHandshake);
        Assert.True(session.IsAuthenticated);
        Assert.Equal(42, session.CurrentUserId);
        Assert.Equal(SessionPayloadFormat.Json, session.NegotiatedPayloadFormat);
        // Resume 路径连接恒 JSON：网关剥离 BinaryPayload 协商位，客户端同步收敛。
        Assert.True((session.NegotiatedFeatureBits & (uint)GatewayFeature.BinaryPayload) == 0);
        Assert.True((session.NegotiatedFeatureBits & (uint)GatewayFeature.SessionResume) != 0);

        var result = session.LastResumeResult;
        Assert.NotNull(result);
        Assert.True(result!.Success);
        Assert.Equal("rotated-token", result.ResumeToken);
        Assert.Equal(7, result.LastConversationSequence);

        // 全程只有 ClientHello：未再发 AuthenticationRequest。
        Assert.Equal(
            [PacketCommand.ClientHello],
            Decode(tcp.GetSentBytes()).Select(static f => f.Command).ToArray());
    }

    [Fact]
    public async Task ResumeConnect_ClientHello_CarriesTokenAndSessionResumeBit()
    {
        using var tcp = new ContractTcpClient { AutoReplyServerHello = false };
        using var session = CreateSession(tcp);
        tcp.ClientHelloReceived = hello => tcp.Inject(Frame(PacketCommand.ResumeResponse,
            new ResumeResponse { Success = true, UserId = 42, ResumeToken = "rotated" }));

        await session.ConnectAsync(Endpoint(), default, "stored-token");

        var hello = new JsonPacketBodySerializer().Deserialize<ClientHello>(
            Assert.Single(Decode(tcp.GetSentBytes())).Body);
        Assert.NotNull(hello);
        Assert.Equal("stored-token", hello.ResumeToken);
        Assert.True((hello.FeatureBits & (uint)GatewayFeature.SessionResume) != 0);
    }

    // ── Resume 失败回退：Error(ResumeFailed 等) + ServerHello → 完整认证 ──

    [Fact]
    public async Task ResumeConnect_ResumeFailed_FallsBackToHandshakeForFullAuth()
    {
        using var tcp = new ContractTcpClient { AutoReplyServerHello = false };
        using var session = CreateSession(tcp);
        tcp.ClientHelloReceived = _ =>
        {
            // 网关语义：Resume 失败先发 Error 再照常发 ServerHello，客户端可回退完整认证。
            tcp.Inject(Frame(PacketCommand.Error, new ProtocolErrorFrame
            {
                Code = ProtocolErrorCode.ResumeFailed,
                Message = "resume token invalid or expired"
            }));
            tcp.Inject(Frame(PacketCommand.ServerHello, ContractTcpClient.CreateDefaultServerHello()));
        };

        await session.ConnectAsync(Endpoint(), default, "expired-token");

        // 握手完成但未认证：协调器据 LastResumeResult 清 token 后走 AuthenticateAsync。
        Assert.True(session.HasCompletedHandshake);
        Assert.False(session.IsAuthenticated);
        Assert.NotNull(session.LastResumeResult);
        Assert.False(session.LastResumeResult!.Success);
        Assert.Equal(ResumeFailureKind.InvalidToken, session.LastResumeResult.FailureKind);

        tcp.FrameSent = command =>
        {
            if (command == PacketCommand.AuthenticationRequest)
            {
                tcp.Inject(Frame(PacketCommand.AuthenticationResponse, new AuthResponseDto
                {
                    Success = true,
                    UserId = 42,
                    ResumeToken = "fresh-token"
                }));
            }
        };
        await session.AuthenticateAsync("access-token", 42, null, null);
        Assert.True(session.IsAuthenticated);
        Assert.Equal("fresh-token", session.LastIssuedResumeToken);
    }

    [Fact]
    public async Task ResumeConnect_DependencyUnavailable_ReportsRetryableFailure()
    {
        using var tcp = new ContractTcpClient { AutoReplyServerHello = false };
        using var session = CreateSession(tcp);
        tcp.ClientHelloReceived = _ =>
        {
            tcp.Inject(Frame(PacketCommand.Error, new ProtocolErrorFrame
            {
                Code = ProtocolErrorCode.DependencyUnavailable,
                Message = "resume dependency unavailable, retry after backoff",
                RetryAfterMs = 1_000
            }));
            tcp.Inject(Frame(PacketCommand.ServerHello, ContractTcpClient.CreateDefaultServerHello()));
        };

        await session.ConnectAsync(Endpoint(), default, "token");

        Assert.True(session.HasCompletedHandshake);
        Assert.False(session.IsAuthenticated);
        Assert.Equal(ResumeFailureKind.DependencyUnavailable, session.LastResumeResult!.FailureKind);
    }

    [Fact]
    public async Task ResumeConnect_SuccessFalseResponse_IsTreatedAsFailureNotViolation()
    {
        using var tcp = new ContractTcpClient { AutoReplyServerHello = false };
        using var session = CreateSession(tcp);
        tcp.ClientHelloReceived = _ =>
        {
            // 协议防御分支：带内拒绝（Success=false）同样走失败回退，而非协议违例。
            tcp.Inject(Frame(PacketCommand.ResumeResponse,
                new ResumeResponse { Success = false, FailureKind = ResumeFailureKind.InvalidToken }));
            tcp.Inject(Frame(PacketCommand.ServerHello, ContractTcpClient.CreateDefaultServerHello()));
        };

        await session.ConnectAsync(Endpoint(), default, "token");

        Assert.True(session.HasCompletedHandshake);
        Assert.False(session.IsAuthenticated);
        Assert.False(session.LastResumeResult!.Success);
        Assert.True(session.IsConnected);
    }

    [Fact]
    public async Task ResumeConnect_ServerIgnoresToken_HandshakeCompletesForFullAuth()
    {
        // 未启用 Resume 的网关：忽略 token，直接回 ServerHello。
        using var tcp = new ContractTcpClient();
        using var session = CreateSession(tcp);

        await session.ConnectAsync(Endpoint(), default, "token");

        Assert.True(session.HasCompletedHandshake);
        Assert.False(session.IsAuthenticated);
        Assert.Null(session.LastResumeResult);
    }

    [Fact]
    public async Task AdvertiseSessionResume_Disabled_OmitsCapabilityBit()
    {
        using var tcp = new ContractTcpClient();
        using var session = new ChatSessionClient(
            tcp, new MessagePacketCodec(), new JsonPacketBodySerializer())
        {
            AdvertiseSessionResume = false
        };

        await session.ConnectAsync(Endpoint(), default, "token-must-not-be-sent");

        var hello = new JsonPacketBodySerializer().Deserialize<ClientHello>(
            Assert.Single(Decode(tcp.GetSentBytes())).Body);
        Assert.NotNull(hello);
        Assert.Null(hello.ResumeToken);
        Assert.True((hello.FeatureBits & (uint)GatewayFeature.SessionResume) == 0);
    }

    [Fact]
    public async Task PersistentDeviceIdentity_ProducesStableInstallationId()
    {
        const string deviceId = "a-persistent-device-id";
        var identity = new StubDeviceIdentity(deviceId);
        using var tcp1 = new ContractTcpClient();
        using var tcp2 = new ContractTcpClient();
        using var session1 = new ChatSessionClient(
            tcp1, new MessagePacketCodec(), new JsonPacketBodySerializer(), identity);
        using var session2 = new ChatSessionClient(
            tcp2, new MessagePacketCodec(), new JsonPacketBodySerializer(), identity);

        await session1.ConnectAsync(Endpoint());
        await session2.ConnectAsync(Endpoint());

        var hello1 = new JsonPacketBodySerializer().Deserialize<ClientHello>(
            Assert.Single(Decode(tcp1.GetSentBytes())).Body);
        var hello2 = new JsonPacketBodySerializer().Deserialize<ClientHello>(
            Assert.Single(Decode(tcp2.GetSentBytes())).Body);
        var expected = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(deviceId)).AsSpan(0, 16)).ToLowerInvariant();

        Assert.Equal(expected, hello1!.InstallationId);
        Assert.Equal(expected, hello2!.InstallationId);
    }

    [Fact]
    public void ClientPacketApi_UsesSharedPacketCommandAsItsSingleSource()
    {
        Assert.Equal(
            typeof(PacketCommand),
            typeof(MessagePacket).GetProperty(nameof(MessagePacket.Command))!.PropertyType);
        Assert.DoesNotContain(
            typeof(MessagePacket).Assembly.GetTypes(),
            static type => type.IsEnum && type.Name == nameof(PacketCommand));
    }

    [Fact]
    public void ClientCodec_RoundTripsEverySharedPacketCommand()
    {
        foreach (var command in Enum.GetValues<PacketCommand>())
        {
            var encoded = new ArrayBufferWriter<byte>();
            Assert.True(new MessagePacketCodec().TryWrite(
                new MessagePacket(command, ReadOnlySequence<byte>.Empty),
                encoded,
                out var written));
            Assert.Equal(MessagePacket.HeaderSize, written);
            Assert.Equal(
                (ushort)command,
                BinaryPrimitives.ReadUInt16LittleEndian(
                    encoded.WrittenSpan.Slice(MessagePacket.CommandOffset, sizeof(ushort))));

            var decoder = new MessagePacketCodec();
            decoder.Append(encoded.WrittenMemory);
            Assert.True(decoder.TryRead(out var decoded));
            Assert.Equal(command, decoded.Command);
        }
    }

    private static ChatSessionClient CreateSession(ContractTcpClient tcp) =>
        new(tcp, new MessagePacketCodec(), new JsonPacketBodySerializer());

    private static ServerEndpoint Endpoint() => new()
    {
        ServerIpAddress = "127.0.0.1",
        ServerPort = 7000
    };

    private static byte[] Frame<T>(PacketCommand command, T payload)
    {
        var serializer = new JsonPacketBodySerializer();
        var body = new ArrayBufferWriter<byte>();
        serializer.Serialize(body, payload);

        var frame = new ArrayBufferWriter<byte>(MessagePacket.HeaderSize + body.WrittenCount);
        var packet = new MessagePacket(
            command,
            new ReadOnlySequence<byte>(body.WrittenMemory));
        Assert.True(new MessagePacketCodec().TryWrite(packet, frame, out _));
        return frame.WrittenSpan.ToArray();
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

    private sealed class ContractTcpClient : ITcpClient
    {
        private readonly object _gate = new();
        private readonly List<byte> _sent = [];

        public bool IsConnected { get; private set; }

        public event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStatusChanged;
        public event EventHandler<ReadOnlyMemory<byte>>? OnDataChunkReceived;
        public Action<PacketCommand>? FrameSent { get; set; }
        public Action<ClientHello>? ClientHelloReceived { get; set; }
        public bool AutoReplyServerHello { get; set; } = true;
        public ServerHello ServerHelloResponse { get; set; } = CreateDefaultServerHello();
        public TaskCompletionSource<bool> ClientHelloSent { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public static ServerHello CreateDefaultServerHello() => new()
        {
            ProtocolVersion = 1,
            FeatureBits = (uint)(GatewayFeature.CommandCapabilities |
                                 GatewayFeature.ConversationSync |
                                 GatewayFeature.ConversationPreferences |
                                 GatewayFeature.MessageMutation |
                                 GatewayFeature.PresenceAndTyping |
                                 GatewayFeature.GroupManagement),
            ServerDeviceId = "test-gateway",
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
                    ClientHelloSent.TrySetResult(true);
                    // 握手段恒 JSON：反序列化后交给测试脚本决定服务端应答（ServerHello/ResumeResponse/Error）。
                    ClientHelloReceived?.Invoke(
                        new JsonPacketBodySerializer().Deserialize<ClientHello>(packet.Body)!);
                    if (AutoReplyServerHello)
                        Inject(Frame(PacketCommand.ServerHello, ServerHelloResponse));
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

    private sealed class StubDeviceIdentity(string deviceId) : ILocalDeviceIdentity
    {
        public string DeviceId { get; } = deviceId;
        public string UserAgent => "ChatApp-Test/1.0";
    }
}
