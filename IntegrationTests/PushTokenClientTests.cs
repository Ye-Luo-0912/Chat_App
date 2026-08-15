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
/// APP-OPS-1 推送令牌管理 wire 层验证。
/// <para>
/// 验证：RegisterPushToken 请求-响应往返（camelCase wire、platform/token/appDeviceLabel 字段完整）、
/// UnregisterPushToken 往返（精确 token / 按设备注销）、本地参数校验与断线 fail-closed 回收。
/// 使用 <see cref="ScriptedTcpClient"/> 假网关驱动 <see cref="ChatSessionClient"/>，协议包编码/解码走真实实现。
/// </para>
/// </summary>
public sealed class PushTokenClientTests
{
    private const long OwnerId = 6001;

    // ── 注册请求-响应往返（wire 字段 + camelCase） ──

    [Fact]
    public async Task RegisterPushToken_RoundTrips_With_Platform_And_Token()
    {
        var serializer = new JsonPacketBodySerializer();
        using var tcp = new ScriptedTcpClient();
        var session = await ConnectAndAuthenticateAsync(tcp, serializer);

        RegisterPushTokenRequestDto? captured = null;
        tcp.OnFrameSent += (cmd, body) =>
        {
            if (cmd != PacketCommand.RegisterPushTokenRequest)
                return;
            var seen = serializer.Deserialize<RegisterPushTokenRequestDto>(new ReadOnlySequence<byte>(body));
            if (seen is null)
                return;
            captured = seen;
            InjectPacket(tcp, serializer, PacketCommand.RegisterPushTokenResponse,
                new RegisterPushTokenResponseDto
                {
                    RequestId = seen.RequestId ?? string.Empty,
                    Succeeded = true,
                    ActiveTokenCount = 3
                });
        };

        var response = await session.RegisterPushTokenAsync(new RegisterPushTokenRequestDto
        {
            Platform = PushPlatformDto.Fcm,
            Token = "fcm-token-abc123",
            AppDeviceLabel = "chat-desktop"
        });

        Assert.True(response.Succeeded);
        Assert.Equal(3, response.ActiveTokenCount);

        // wire 请求字段完整（camelCase 序列化往返）
        Assert.NotNull(captured);
        Assert.Equal(PushPlatformDto.Fcm, captured.Platform);
        Assert.Equal("fcm-token-abc123", captured.Token);
        Assert.Equal("chat-desktop", captured.AppDeviceLabel);
        Assert.False(string.IsNullOrWhiteSpace(captured.RequestId));
    }

    [Fact]
    public async Task RegisterPushToken_BusinessRejection_Propagates()
    {
        var serializer = new JsonPacketBodySerializer();
        using var tcp = new ScriptedTcpClient();
        var session = await ConnectAndAuthenticateAsync(tcp, serializer);

        tcp.OnFrameSent += (cmd, body) =>
        {
            if (cmd != PacketCommand.RegisterPushTokenRequest)
                return;
            var seen = serializer.Deserialize<RegisterPushTokenRequestDto>(new ReadOnlySequence<byte>(body));
            if (seen is null)
                return;
            // 服务端以注册响应携带 Succeeded=false 与稳定错误码拒绝。
            InjectPacket(tcp, serializer, PacketCommand.RegisterPushTokenResponse,
                new RegisterPushTokenResponseDto
                {
                    RequestId = seen.RequestId ?? string.Empty,
                    Succeeded = false,
                    ErrorCode = "invalid_push_token_request",
                    ErrorMessage = "推送令牌注册请求参数无效。",
                    ActiveTokenCount = 0
                });
        };

        var response = await session.RegisterPushTokenAsync(new RegisterPushTokenRequestDto
        {
            Platform = PushPlatformDto.Apns,
            Token = "apns-device-token"
        });

        Assert.False(response.Succeeded);
        Assert.Equal("invalid_push_token_request", response.ErrorCode);
        Assert.Equal(0, response.ActiveTokenCount);
    }

    // ── 注销请求-响应往返（精确 token / 按设备） ──

    [Fact]
    public async Task UnregisterPushToken_ByExactToken_RoundTrips()
    {
        var serializer = new JsonPacketBodySerializer();
        using var tcp = new ScriptedTcpClient();
        var session = await ConnectAndAuthenticateAsync(tcp, serializer);

        UnregisterPushTokenRequestDto? captured = null;
        tcp.OnFrameSent += (cmd, body) =>
        {
            if (cmd != PacketCommand.UnregisterPushTokenRequest)
                return;
            var seen = serializer.Deserialize<UnregisterPushTokenRequestDto>(new ReadOnlySequence<byte>(body));
            if (seen is null)
                return;
            captured = seen;
            InjectPacket(tcp, serializer, PacketCommand.UnregisterPushTokenResponse,
                new UnregisterPushTokenResponseDto
                {
                    RequestId = seen.RequestId ?? string.Empty,
                    Succeeded = true,
                    ActiveTokenCount = 2
                });
        };

        var response = await session.UnregisterPushTokenAsync(new UnregisterPushTokenRequestDto
        {
            Token = "fcm-token-abc123"
        });

        Assert.True(response.Succeeded);
        Assert.Equal(2, response.ActiveTokenCount);
        Assert.NotNull(captured);
        Assert.Equal("fcm-token-abc123", captured.Token);
        Assert.False(string.IsNullOrWhiteSpace(captured.RequestId));
    }

    [Fact]
    public async Task UnregisterPushToken_ByDevice_Omits_Token_On_Wire()
    {
        var serializer = new JsonPacketBodySerializer();
        using var tcp = new ScriptedTcpClient();
        var session = await ConnectAndAuthenticateAsync(tcp, serializer);

        UnregisterPushTokenRequestDto? captured = null;
        tcp.OnFrameSent += (cmd, body) =>
        {
            if (cmd != PacketCommand.UnregisterPushTokenRequest)
                return;
            var seen = serializer.Deserialize<UnregisterPushTokenRequestDto>(new ReadOnlySequence<byte>(body));
            if (seen is null)
                return;
            captured = seen;
            InjectPacket(tcp, serializer, PacketCommand.UnregisterPushTokenResponse,
                new UnregisterPushTokenResponseDto
                {
                    RequestId = seen.RequestId ?? string.Empty,
                    Succeeded = true,
                    ActiveTokenCount = 0
                });
        };

        var response = await session.UnregisterPushTokenAsync(new UnregisterPushTokenRequestDto());

        Assert.True(response.Succeeded);
        Assert.Equal(0, response.ActiveTokenCount);
        Assert.NotNull(captured);
        Assert.Null(captured.Token);
    }

    // ── 本地参数校验 ──

    [Fact]
    public async Task RegisterPushToken_Validates_Inputs()
    {
        var serializer = new JsonPacketBodySerializer();
        using var tcp = new ScriptedTcpClient();
        var session = await ConnectAndAuthenticateAsync(tcp, serializer);

        await Assert.ThrowsAsync<ArgumentNullException>(() => session.RegisterPushTokenAsync(null!));

        // 空/过长 token
        await Assert.ThrowsAsync<ArgumentException>(() => session.RegisterPushTokenAsync(
            new RegisterPushTokenRequestDto { Platform = PushPlatformDto.Fcm, Token = "" }));
        await Assert.ThrowsAsync<ArgumentException>(() => session.RegisterPushTokenAsync(
            new RegisterPushTokenRequestDto { Platform = PushPlatformDto.Fcm, Token = new string('x', PushTokenLimits.MaxTokenLength + 1) }));

        // 平台非法
        await Assert.ThrowsAsync<ArgumentException>(() => session.RegisterPushTokenAsync(
            new RegisterPushTokenRequestDto { Platform = (PushPlatformDto)0, Token = "t" }));

        // appDeviceLabel 超长
        await Assert.ThrowsAsync<ArgumentException>(() => session.RegisterPushTokenAsync(
            new RegisterPushTokenRequestDto
            {
                Platform = PushPlatformDto.WebPush,
                Token = "t",
                AppDeviceLabel = new string('a', PushTokenLimits.MaxAppDeviceLabelLength + 1)
            }));
    }

    [Fact]
    public async Task UnregisterPushToken_Validates_Inputs()
    {
        var serializer = new JsonPacketBodySerializer();
        using var tcp = new ScriptedTcpClient();
        var session = await ConnectAndAuthenticateAsync(tcp, serializer);

        await Assert.ThrowsAsync<ArgumentNullException>(() => session.UnregisterPushTokenAsync(null!));
        await Assert.ThrowsAsync<ArgumentException>(() => session.UnregisterPushTokenAsync(
            new UnregisterPushTokenRequestDto { Token = new string('x', PushTokenLimits.MaxTokenLength + 1) }));
    }

    // ── 断线 fail-closed：在途 push 命令以 IOException 失败，pending 清零 ──

    [Fact]
    public async Task Disconnect_Fails_Pending_Push_And_Empties()
    {
        var serializer = new JsonPacketBodySerializer();
        using var tcp = new ScriptedTcpClient();
        var session = await ConnectAndAuthenticateAsync(tcp, serializer);

        var register = session.RegisterPushTokenAsync(new RegisterPushTokenRequestDto
        {
            Platform = PushPlatformDto.Fcm,
            Token = "pending-token"
        });
        await WaitForRegisterFrameAsync(tcp);
        tcp.Disconnect("验收断线");

        var ex = await Assert.ThrowsAsync<IOException>(() => register);
        Assert.Contains("验收断线", ex.Message);
        Assert.Equal(0, session.PendingRequestCount);
    }

    // ── 测试底座 ──

    private static async Task<ChatSessionClient> ConnectAndAuthenticateAsync(
        ScriptedTcpClient tcp,
        JsonPacketBodySerializer serializer)
    {
        tcp.HandshakeFrame = TcpHandshakeTestServer.ServerHelloFrame;
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

    private static async Task WaitForRegisterFrameAsync(ScriptedTcpClient tcp)
    {
        for (var i = 0; i < 500; i++)
        {
            if (tcp.RegisterFrameSeen)
                return;
            await Task.Delay(10);
        }
        throw new TimeoutException("等待 RegisterPushTokenRequest 帧超时");
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

    /// <summary>可控的假 TCP 客户端：解析每次发送的帧，回显握手/鉴权，暴露 push 请求观测。</summary>
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
        public bool RegisterFrameSeen { get; private set; }

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
                if (pkt.Command == PacketCommand.RegisterPushTokenRequest)
                    RegisterFrameSeen = true;
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