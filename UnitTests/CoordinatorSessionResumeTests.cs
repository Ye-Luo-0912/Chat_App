using System.Buffers;
using Chat_App.Infrastructure.Models;
using Chat_App.Infrastructure.Models.Context;
using Chat_App.Infrastructure.Persistence;
using Chat_App.Infrastructure.Serialization;
using Chat_App.Presentation.ViewModels.Chat;
using Chat_App.Services;
using ChatApp.Shared.Protocol.Tcp;
using Core.Interfaces;
using Core.Protocol;
using Core.Services;
using Core.Buffers;
using Core.Models;
using Core.Models.DTO;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace UnitTests;

/// <summary>
/// 连接协调器 Resume 重连状态机测试：
/// ChatConnectionCoordinator + 真实 DatabaseService（临时 SQLite）+ 脚本化假网关。
/// 验证 Resume 优先、失败回退完整认证、token 生命周期（轮换保存 / 失败清除 / 依赖不可用保留）。
/// </summary>
public class CoordinatorSessionResumeTests
{
    private const long TestUserId = 42;

    [Fact]
    public async Task ResumeSuccess_SkipsAuthenticateAsync_AndRotatesToken()
    {
        using var db = new DbHarness();
        await db.SeedAsync(resumeToken: "local-token");
        var gateway = new ScriptedResumeGateway
        {
            HelloReply = hello => string.IsNullOrEmpty(hello.ResumeToken)
                ? [ServerHelloFrame]
                : [Frame(PacketCommand.ResumeResponse, new ResumeResponse
                {
                    Success = true,
                    UserId = TestUserId,
                    SessionId = "session-1",
                    ResumeToken = "rotated-token"
                })]
        };
        using var session = CreateSession(gateway);
        var userState = new StubUserState();
        using var coordinator = CreateCoordinator(db, session, gateway, userState);

        await coordinator.ConnectAsync();

        // Resume 成功：只发 ClientHello，不再发 AuthenticationRequest。
        Assert.Equal([PacketCommand.ClientHello], gateway.SentCommands);
        Assert.True(session.IsAuthenticated);
        Assert.Equal(ChatConnectionStatus.Connected, coordinator.Status);
        Assert.Equal("session-1", userState.SessionId);
        // 网关轮换的新 token 已持久化，重启后仍可 Resume。
        Assert.Equal("rotated-token", await db.Db.GetResumeTokenAsync());

        await coordinator.StopAsync();
    }

    [Fact]
    public async Task ResumeFailed_ClearsToken_FallsBackToFullAuth_AndSavesNewToken()
    {
        using var db = new DbHarness();
        await db.SeedAsync(resumeToken: "expired-token");
        var gateway = new ScriptedResumeGateway
        {
            HelloReply = hello => string.IsNullOrEmpty(hello.ResumeToken)
                ? [ServerHelloFrame]
                : [ErrorFrame(ProtocolErrorCode.ResumeFailed), ServerHelloFrame],
            AuthenticationReply = () => Frame(PacketCommand.AuthenticationResponse, new AuthResponseDto
            {
                Success = true,
                UserId = TestUserId,
                SessionId = "session-1",
                ResumeToken = "fresh-token"
            })
        };
        using var session = CreateSession(gateway);
        var userState = new StubUserState();
        using var coordinator = CreateCoordinator(db, session, gateway, userState);

        await coordinator.ConnectAsync();

        // 回退真实发生：完整认证已执行，会话就绪。
        Assert.Contains(PacketCommand.AuthenticationRequest, gateway.SentCommands);
        Assert.True(session.IsAuthenticated);
        Assert.Equal(ChatConnectionStatus.Connected, coordinator.Status);
        // 过期 token 已清除；认证颁发的新 token 已落库。
        Assert.Equal("fresh-token", await db.Db.GetResumeTokenAsync());

        await coordinator.StopAsync();
    }

    [Fact]
    public async Task DependencyUnavailable_KeepsToken_WhenFallbackAuthFails()
    {
        using var db = new DbHarness();
        await db.SeedAsync(resumeToken: "keepme-token");
        var gateway = new ScriptedResumeGateway
        {
            HelloReply = hello => string.IsNullOrEmpty(hello.ResumeToken)
                ? [ServerHelloFrame]
                : [
                    ErrorFrame(ProtocolErrorCode.DependencyUnavailable, retryAfterMs: 1_000),
                    ServerHelloFrame
                ],
            // 回退认证同样因依赖不可用失败：本地 token 必须保留供下次重试。
            AuthenticationReply = () => Frame(PacketCommand.AuthenticationResponse, new AuthResponseDto
            {
                Success = false,
                ErrorMessage = "auth dependency unavailable"
            })
        };
        using var session = CreateSession(gateway);
        var userState = new StubUserState();
        using var coordinator = CreateCoordinator(db, session, gateway, userState);

        // 鉴权失败 → AuthenticationFailed → 自动重连关闭，异常向上传播。
        await Assert.ThrowsAnyAsync<Exception>(() => coordinator.ConnectAsync());

        Assert.False(session.IsAuthenticated);
        Assert.Equal("keepme-token", await db.Db.GetResumeTokenAsync());
    }

    [Fact]
    public async Task NoLocalToken_FullAuth_PersistsIssuedToken()
    {
        using var db = new DbHarness();
        await db.SeedAsync(resumeToken: null);
        var gateway = new ScriptedResumeGateway
        {
            HelloReply = _ => [ServerHelloFrame],
            AuthenticationReply = () => Frame(PacketCommand.AuthenticationResponse, new AuthResponseDto
            {
                Success = true,
                UserId = TestUserId,
                SessionId = "session-1",
                ResumeToken = "issued-token"
            })
        };
        using var session = CreateSession(gateway);
        var userState = new StubUserState();
        using var coordinator = CreateCoordinator(db, session, gateway, userState);

        await coordinator.ConnectAsync();

        Assert.True(session.IsAuthenticated);
        Assert.Equal("issued-token", await db.Db.GetResumeTokenAsync());

        await coordinator.StopAsync();
    }

    // ── harness ──

    private static ChatSessionClient CreateSession(ScriptedResumeGateway gateway) =>
        new(gateway, new MessagePacketCodec(), new JsonPacketBodySerializer());

    private static ChatConnectionCoordinator CreateCoordinator(
        DbHarness db, ChatSessionClient session, ScriptedResumeGateway gateway, StubUserState userState)
    {
        var coordinator = new ChatConnectionCoordinator(db.Db, session, userState, new StubNotifications());
        coordinator.RegisterEventHandlers();
        return coordinator;
    }

    /// <summary>真实 DatabaseService + 临时 SQLite（EnsureCreated）；种子含 token 行与服务器端点。</summary>
    private sealed class DbHarness : IDisposable
    {
        public readonly string DbPath;
        public readonly IDbContextFactory<ClientDbContext> Factory;
        public readonly DatabaseService Db;

        public DbHarness()
        {
            DbPath = Path.Combine(Path.GetTempPath(), $"chat_coord_resume_{Guid.NewGuid():N}.db");
            Factory = new SingleFileContextFactory(DbPath);
            Db = new DatabaseService(Factory);
            using var ctx = Factory.CreateDbContext();
            ctx.Database.EnsureCreated();
        }

        public async Task SeedAsync(string? resumeToken)
        {
            await Db.SaveTokenAsync(new AuthToken
            {
                UserId = TestUserId,
                AccessToken = "access-token",
                RefreshToken = "refresh-token",
                AccessTokenExpires = DateTime.UtcNow.AddHours(1),
                RefreshTokenExpires = DateTime.UtcNow.AddDays(7),
                SessionId = "session-1",
                DeviceIdHash = 987_654_321L
            });
            await Db.SaveServerInfoAsync(new ServerEndpoint
            {
                ServerIpAddress = "127.0.0.1",
                ServerPort = 7000,
                ServerName = "coordinator-resume-test"
            });
            if (resumeToken is not null)
                await Db.SaveResumeTokenAsync(resumeToken);
        }

        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            for (var attempt = 0; attempt < 40; attempt++)
            {
                try { File.Delete(DbPath); return; }
                catch (IOException) { Thread.Sleep(50); }
            }
        }

        private sealed class SingleFileContextFactory(string path) : IDbContextFactory<ClientDbContext>
        {
            public ClientDbContext CreateDbContext() =>
                new(new DbContextOptionsBuilder<ClientDbContext>().UseSqlite($"Data Source={path}").Options);

            public Task<ClientDbContext> CreateDbContextAsync(CancellationToken ct = default) =>
                Task.FromResult(CreateDbContext());
        }
    }

    private static ServerEndpoint Endpoint() => new()
    {
        ServerIpAddress = "127.0.0.1",
        ServerPort = 7000
    };

    private static byte[] ErrorFrame(ProtocolErrorCode code, int? retryAfterMs = null)
    {
        var body = new ArrayBufferWriter<byte>();
        new JsonPacketBodySerializer().Serialize(body, new ProtocolErrorFrame
        {
            Code = code,
            Message = code == ProtocolErrorCode.DependencyUnavailable
                ? "resume dependency unavailable, retry after backoff"
                : "resume token invalid or expired",
            RetryAfterMs = retryAfterMs
        });
        return WriteFrame(PacketCommand.Error, body);
    }

    private static byte[] Frame<T>(PacketCommand command, T payload)
    {
        var body = new ArrayBufferWriter<byte>();
        new JsonPacketBodySerializer().Serialize(body, payload);
        return WriteFrame(command, body);
    }

    private static byte[] WriteFrame(PacketCommand command, ArrayBufferWriter<byte> body)
    {
        var frame = new ArrayBufferWriter<byte>(MessagePacket.HeaderSize + body.WrittenCount);
        var packet = new MessagePacket(
            command,
            new ReadOnlySequence<byte>(body.WrittenMemory));
        Assert.True(new MessagePacketCodec().TryWrite(packet, frame, out _));
        return frame.WrittenSpan.ToArray();
    }

    /// <summary>JSON ServerHello 帧（本地构造：UnitTests 工程不引用 IntegrationTests 的共享帧工厂）。</summary>
    private static readonly byte[] ServerHelloFrame = BuildServerHelloFrame();

    private static byte[] BuildServerHelloFrame()
    {
        var body = new ArrayBufferWriter<byte>();
        new JsonPacketBodySerializer().Serialize(body, new ServerHello
        {
            ProtocolVersion = 1,
            FeatureBits = (uint)(GatewayFeature.CommandCapabilities |
                                 GatewayFeature.ConversationSync |
                                 GatewayFeature.ConversationPreferences |
                                 GatewayFeature.MessageMutation |
                                 GatewayFeature.PresenceAndTyping |
                                 GatewayFeature.GroupManagement |
                                 GatewayFeature.RelationshipRead |
                                 GatewayFeature.CallSignaling |
                                 GatewayFeature.SessionResume),
            ServerDeviceId = "unit-test-gateway",
            ServerTimeMs = 1_700_000_000_000,
            HeartbeatIntervalMs = 15_000,
            MaxPayloadBytes = 1_048_576,
            ResumeSupported = true,
            PayloadFormat = ProtocolPayloadFormat.Json
        });
        return WriteFrame(PacketCommand.ServerHello, body);
    }

    /// <summary>脚本化假网关：按 hello 内容（是否携带 token）应答，记录实际发出的命令。</summary>
    private sealed class ScriptedResumeGateway : ITcpClient
    {
        private readonly object _gate = new();
        private readonly List<byte> _sent = [];
        private readonly List<PacketCommand> _commands = [];

        public bool IsConnected { get; private set; }

        public Func<ClientHello, IReadOnlyList<byte[]>>? HelloReply { get; init; }
        public Func<byte[]?>? AuthenticationReply { get; init; }

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
                        var hello = new JsonPacketBodySerializer().Deserialize<ClientHello>(packet.Body);
                        if (hello is not null && HelloReply is not null)
                        {
                            foreach (var frame in HelloReply(hello))
                                OnDataChunkReceived?.Invoke(this, frame);
                        }
                        break;

                    case PacketCommand.AuthenticationRequest:
                        var reply = AuthenticationReply?.Invoke();
                        if (reply is not null)
                            OnDataChunkReceived?.Invoke(this, reply);
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

    /// <summary>可写的当前用户状态 stub：预置已登录用户，记录会话戳写入。</summary>
    private sealed class StubUserState : ICurrentUserState
    {
        public long Generation { get; private set; } = 1;
        public long? UserId { get; private set; } = TestUserId;
        public string? SessionId { get; private set; }
        public ulong? DeviceHash { get; private set; }
        public string? UserName => UserId is { } id ? $"user-{id}" : null;
        public bool IsAuthenticated => UserId is > 0;
        public bool HasUserId => UserId is > 0;
        public UserSessionSnapshot Snapshot => new(UserId ?? 0, Generation, UserName, SessionId, DeviceHash);

        public void SetAuthenticatedSession(long userId, string? userName, string? sessionId, ulong? deviceHash, long connectionGeneration)
        {
            UserId = userId;
            SessionId = sessionId;
            DeviceHash = deviceHash;
            Generation++;
        }

        public void BumpConnectionGeneration(long connectionGeneration) => Generation++;
        public void BumpTokenGeneration() => Generation++;
        public void SetCurrentUser(long userId, string? userName) { UserId = userId; Generation++; }
        public void SetSession(string? sessionId, ulong? deviceHash) { SessionId = sessionId; DeviceHash = deviceHash; }
        public void Clear() { UserId = null; SessionId = null; DeviceHash = null; Generation++; }

        public long RequireUserId() => UserId ?? throw new InvalidOperationException("未登录");
        public bool TryGetUserId(out long id)
        {
            id = UserId ?? 0;
            return UserId is > 0;
        }
    }

    private sealed class StubNotifications : INotificationService
    {
        public void ShowError(string message, string title = "错误") { }
        public void ShowWarning(string message, string title = "警告") { }
        public void ShowInfo(string message, string title = "提示") { }
        public void ShowSuccess(string message, string title = "成功") { }
    }
}
