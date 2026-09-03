using System.Buffers;
using Chat_App.Infrastructure.Serialization;
using ChatApp.Shared.Protocol.Tcp;
using Core.Interfaces;
using Core.Models;
using Core.Models.DTO;
using Core.Protocol;
using Core.Protocol.Binary;
using Core.Services;
using Xunit;

namespace IntegrationTests;

/// <summary>
/// 客户端断线 Resume 集成测试（客户端层）：
/// 使用 TcpHandshakeTestServer 的 Resume 场景帧变体（ResumeResponse / Error+ServerHello），
/// 通过脚本化假网关驱动真实 ChatSessionClient，验证握手—恢复—回退全链路。
/// </summary>
public class SessionResumeTests
{
    private const long UserId = 42;

    [Fact]
    public async Task ScriptedGateway_ResumeSuccessFrame_EstablishesSessionWithoutAuthRequest()
    {
        var gateway = new ScriptedResumeGateway
        {
            HelloReply = hello => string.IsNullOrEmpty(hello.ResumeToken)
                ? [TcpHandshakeTestServer.ServerHelloFrame]
                : [TcpHandshakeTestServer.CreateResumeSuccessFrame(UserId, "rotated")]
        };
        using var session = CreateSession(gateway);

        await session.ConnectAsync(Endpoint(), default, "local-token");

        // 网关契约：恢复成功只回 ResumeResponse，客户端直接进入已认证状态。
        Assert.True(session.HasCompletedHandshake);
        Assert.True(session.IsAuthenticated);
        Assert.Equal(UserId, session.CurrentUserId);
        Assert.Equal(SessionPayloadFormat.Json, session.NegotiatedPayloadFormat);
        Assert.True((session.NegotiatedFeatureBits & (uint)GatewayFeature.BinaryPayload) == 0);
        Assert.Equal([PacketCommand.ClientHello], gateway.SentCommands);
        var result = session.LastResumeResult!;
        Assert.True(result.Success);
        Assert.Equal("rotated", result.ResumeToken);
        Assert.Equal("resume-session", result.SessionId);
    }

    [Fact]
    public async Task ScriptedGateway_ResumeFailureFrames_FallBackToFullAuth()
    {
        var gateway = new ScriptedResumeGateway
        {
            HelloReply = hello => string.IsNullOrEmpty(hello.ResumeToken)
                ? [TcpHandshakeTestServer.ServerHelloFrame]
                : [
                    TcpHandshakeTestServer.CreateResumeFailureErrorFrame(),
                    TcpHandshakeTestServer.ServerHelloFrame
                ],
            AuthenticationReply = () => Frame(AuthResponse(null))
        };
        using var session = CreateSession(gateway);

        await session.ConnectAsync(Endpoint(), default, "expired-token");

        // 失败回退：握手以 ServerHello 完成，失败分类为 InvalidToken（协调器据此清 token）。
        Assert.True(session.HasCompletedHandshake);
        Assert.False(session.IsAuthenticated);
        Assert.Equal(ResumeFailureKind.InvalidToken, session.LastResumeResult!.FailureKind);

        await session.AuthenticateAsync("access-token", UserId, "session-1", null);
        Assert.True(session.IsAuthenticated);
        Assert.Contains(PacketCommand.AuthenticationRequest, gateway.SentCommands);
    }

    [Fact]
    public async Task ScriptedGateway_PlainServerHello_CompletesHandshakeWithoutResumeOutcome()
    {
        // 未启用 Resume 的网关：忽略 token 直接回 ServerHello，LastResumeResult 保持 null。
        var gateway = new ScriptedResumeGateway
        {
            HelloReply = _ => [TcpHandshakeTestServer.ServerHelloFrame]
        };
        using var session = CreateSession(gateway);

        await session.ConnectAsync(Endpoint(), default, "token");

        Assert.True(session.HasCompletedHandshake);
        Assert.False(session.IsAuthenticated);
        Assert.Null(session.LastResumeResult);
    }

    // ── harness ──

    private static ChatSessionClient CreateSession(ScriptedResumeGateway gateway) =>
        new(gateway, new MessagePacketCodec(), new JsonPacketBodySerializer());

    private static ServerEndpoint Endpoint() => new()
    {
        ServerIpAddress = "127.0.0.1",
        ServerPort = 7000
    };

    private static AuthResponseDto AuthResponse(string? newResumeToken) => new()
    {
        Success = true,
        UserId = UserId,
        SessionId = "session-1",
        ResumeToken = newResumeToken
    };

    private static byte[] Frame(AuthResponseDto payload)
    {
        var body = new ArrayBufferWriter<byte>();
        new JsonPacketBodySerializer().Serialize(body, payload);
        var frame = new ArrayBufferWriter<byte>(MessagePacket.HeaderSize + body.WrittenCount);
        var packet = new MessagePacket(
            PacketCommand.AuthenticationResponse,
            new ReadOnlySequence<byte>(body.WrittenMemory));
        Assert.True(new MessagePacketCodec().TryWrite(packet, frame, out _));
        return frame.WrittenSpan.ToArray();
    }

    /// <summary>
    /// 假网关：按脚本应答 ClientHello（Resume 成功 / 失败回退 / 忽略 token）与 AuthenticationRequest。
    /// 记录客户端实际发出的命令序列与最近一次 ClientHello。
    /// </summary>
    private sealed class ScriptedResumeGateway : ITcpClient
    {
        private readonly object _gate = new();
        private readonly List<byte> _sent = [];
        private readonly List<PacketCommand> _commands = [];

        public bool IsConnected { get; private set; }

        /// <summary>ClientHello 应答脚本：按 hello 内容（是否携带 token）返回要注入的帧序列。</summary>
        public Func<ClientHello, IReadOnlyList<ReadOnlyMemory<byte>>>? HelloReply { get; init; }

        /// <summary>AuthenticationRequest 应答脚本：null 表示未配置；返回 null 表示忽略该请求。</summary>
        public Func<ReadOnlyMemory<byte>?>? AuthenticationReply { get; init; }

        public ClientHello? LastHello { get; private set; }

        public IReadOnlyList<PacketCommand> SentCommands
        {
            get { lock (_gate) return _commands.ToArray(); }
        }

        public event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStatusChanged;
        public event EventHandler<ReadOnlyMemory<byte>>? OnDataChunkReceived;

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

            var seq = new ReadOnlySequence<byte>(data);
            while (MessagePacket.TryDeserialize(ref seq, out var packet, out _))
            {
                lock (_gate)
                    _commands.Add(packet.Command);

                switch (packet.Command)
                {
                    case PacketCommand.ClientHello:
                        // 握手段恒 JSON。
                        LastHello = new JsonPacketBodySerializer().Deserialize<ClientHello>(packet.Body);
                        if (LastHello is not null && HelloReply is not null)
                        {
                            foreach (var frame in HelloReply(LastHello))
                                OnDataChunkReceived?.Invoke(this, frame);
                        }
                        break;

                    case PacketCommand.AuthenticationRequest:
                        var reply = AuthenticationReply?.Invoke();
                        if (reply is not null)
                            OnDataChunkReceived?.Invoke(this, reply.Value);
                        break;
                }
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

        public void Dispose() => IsConnected = false;
    }
}
