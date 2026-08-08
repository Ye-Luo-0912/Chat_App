using Chat_App.Infrastructure.Models;
using Chat_App.Infrastructure.Models.Context;
using Chat_App.Infrastructure.Persistence;
using Chat_App.Infrastructure.Serialization;
using Chat_App.Infrastructure.Services;
using Core.Interfaces;
using Core.Models;
using Core.Models.DTO;
using Core.Protocol;
using Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Buffers;
using Xunit;

namespace IntegrationTests;

/// <summary>
/// SyncEngine 生命周期与截断语义测试（P1-3/P1-4）：
/// - RestartAsync 严格等待旧任务退出后再启动新任务；
/// - 会话列表/历史页数达预算上限且服务端仍有更多 → Completed 事件标记 PartialLimitReached
///   （不得静默标记完整成功）。
/// </summary>
public class SyncEngineLifecycleTests : IDisposable
{
    private readonly string _dbPath;
    private readonly IDbContextFactory<ClientDbContext> _factory;
    private readonly DatabaseService _db;

    private const long OwnerId = 7301;
    private const long PeerId = 9301;

    public SyncEngineLifecycleTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"chat_synclife_{Guid.NewGuid():N}.db");
        _factory = new DbContextFactoryStub(_dbPath);
        _db = new DatabaseService(_factory);
        using var ctx = _factory.CreateDbContext();
        ctx.Database.EnsureCreated();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        TryDeleteWithRetry(_dbPath);
    }

    private static void TryDeleteWithRetry(string path)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            try
            {
                File.Delete(path);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(50);
            }
        }
    }

    private static readonly SessionStamp Session = new(OwnerId, 1, Guid.NewGuid());

    private sealed class DbContextFactoryStub(string dbPath) : IDbContextFactory<ClientDbContext>
    {
        public ClientDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<ClientDbContext>()
                .UseSqlite($"Data Source={dbPath}")
                .Options;
            return new ClientDbContext(options);
        }

        public Task<ClientDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CreateDbContext());
    }

    /// <summary>
    /// RestartAsync 严格等待旧任务退出：旧任务取消后不触发 Completed，
    /// 新任务完整运行并触发一次 Completed。
    /// </summary>
    [Fact]
    public async Task RestartAsync_Waits_For_Old_Task_Before_Starting_New()
    {
        using var tcp = new ScriptedTcpClient();
        var serializer = new JsonPacketBodySerializer();
        var session = new ChatSessionClient(tcp, new MessagePacketCodec(), serializer);
        SetupAutoAuth(tcp, serializer, OwnerId);

        var bootstrapCalls = 0;
        tcp.OnFrameSent += (cmd, body) =>
        {
            try
            {
                switch (cmd)
                {
                    case PacketCommand.AuthenticationRequest:
                        InjectPacket(tcp, serializer, PacketCommand.AuthenticationResponse,
                            new AuthResponseDto { Success = true, UserId = OwnerId });
                        break;
                    case PacketCommand.SyncBootstrapRequest:
                        {
                            var req = serializer.Deserialize<SyncBootstrapRequestDto>(new ReadOnlySequence<byte>(body));
                            if (req is null) return;
                            var call = Interlocked.Increment(ref bootstrapCalls);
                            var respond = () => InjectPacket(tcp, serializer, PacketCommand.SyncBootstrapResponse,
                                new SyncBootstrapResponseDto
                                {
                                    RequestId = req.RequestId ?? string.Empty,
                                    Succeeded = true,
                                    Conversations = Array.Empty<ConversationListItemDto>(),
                                    ConversationsHasMore = false,
                                    CatchUps = Array.Empty<ConversationHistoryCatchUpDto>()
                                });
                            // 首次 bootstrap 延迟响应（异步，不阻塞回调线程）：
                            // 保证旧任务在 RestartAsync 取消时仍处于运行中，且取消能正常中断其等待。
                            if (call == 1)
                                _ = Task.Run(async () => { await Task.Delay(500); respond(); });
                            else
                                respond();
                            break;
                        }
                }
            }
            catch
            {
                // 忽略
            }
        };

        await session.ConnectAsync(new ServerEndpoint { ServerIpAddress = "127.0.0.1", ServerPort = 7000 });
        await session.AuthenticateAsync("token", OwnerId, null, null);

        var eventBus = new InMemoryEventBus();
        var store = new MessageStore(_db, eventBus, session);
        var engine = new SyncEngine(session, store, _db, new SyncCheckpointStore(store, _db), new SyncConflictResolver());

        var completedCount = 0;
        engine.Completed += (_, e) => Interlocked.Increment(ref completedCount);

        // 启动旧任务后立即重启：旧任务（bootstrap 延迟中）被取消（不触发 Completed），新任务完成
        engine.Start(Session);
        await engine.RestartAsync(Session);
        await Task.Delay(300);

        Assert.Equal(1, completedCount); // 只有新任务的 Completed
        Assert.True(bootstrapCalls >= 2); // 旧任务（被取消）+ 新任务各发一次 bootstrap
        Assert.False(engine.IsSyncing);

        // StopAsync 幂等且等待退出
        await engine.StopAsync();
        await engine.StopAsync();
    }

    /// <summary>
    /// 会话列表分页达预算上限（MaxConversationPages）且服务端仍有更多：
    /// Completed 必须标记 PartialLimitReached，不得静默成功。
    /// </summary>
    [Fact]
    public async Task Conversation_Page_Budget_Truncation_Marks_Partial_Outcome()
    {
        using var tcp = new ScriptedTcpClient();
        var serializer = new JsonPacketBodySerializer();
        var session = new ChatSessionClient(tcp, new MessagePacketCodec(), serializer);
        SetupAutoAuth(tcp, serializer, OwnerId);

        tcp.OnFrameSent += (cmd, body) =>
        {
            try
            {
                switch (cmd)
                {
                    case PacketCommand.AuthenticationRequest:
                        InjectPacket(tcp, serializer, PacketCommand.AuthenticationResponse,
                            new AuthResponseDto { Success = true, UserId = OwnerId });
                        break;
                    case PacketCommand.SyncBootstrapRequest:
                        {
                            var req = serializer.Deserialize<SyncBootstrapRequestDto>(new ReadOnlySequence<byte>(body));
                            if (req is null) return;
                            InjectPacket(tcp, serializer, PacketCommand.SyncBootstrapResponse, new SyncBootstrapResponseDto
                            {
                                RequestId = req.RequestId ?? string.Empty,
                                Succeeded = true,
                                Conversations = new[] { ConvItem("conv-a", "a", 1_000L) },
                                ConversationsNextCursor = new ConversationListCursorDto { LastMessageAtMs = 1_000L, ConversationId = "conv-a" },
                                ConversationsHasMore = true, // 服务端声明还有更多会话
                                CatchUps = Array.Empty<ConversationHistoryCatchUpDto>()
                            });
                            break;
                        }
                    case PacketCommand.ConversationListRequest:
                        {
                            var req = serializer.Deserialize<ConversationListRequestDto>(new ReadOnlySequence<byte>(body));
                            if (req is null) return;
                            // 永远返回 HasMore=true：翻页将耗尽预算
                            InjectPacket(tcp, serializer, PacketCommand.ConversationListPage, new ConversationListResponseDto
                            {
                                RequestId = req.RequestId ?? string.Empty,
                                Succeeded = true,
                                Items = new[] { ConvItem("conv-more", "m", 2_000L) },
                                NextCursor = new ConversationListCursorDto { LastMessageAtMs = 2_000L, ConversationId = "conv-more" },
                                HasMore = true
                            });
                            break;
                        }
                }
            }
            catch
            {
                // 忽略
            }
        };

        await session.ConnectAsync(new ServerEndpoint { ServerIpAddress = "127.0.0.1", ServerPort = 7000 });
        await session.AuthenticateAsync("token", OwnerId, null, null);

        var eventBus = new InMemoryEventBus();
        var store = new MessageStore(_db, eventBus, session);
        var engine = new SyncEngine(session, store, _db, new SyncCheckpointStore(store, _db), new SyncConflictResolver());
        var completed = new TaskCompletionSource<SyncCompletedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.Completed += (_, e) => completed.TrySetResult(e);

        engine.Start(Session);
        var result = await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(result.Succeeded);
        // 预算截断：必须标记 Partial，而非完整成功
        Assert.Equal(SyncOutcome.PartialLimitReached, result.Outcome);
    }

    private static ConversationListItemDto ConvItem(string convId, string lastMsg, long atMs) => new()
    {
        ConversationId = convId,
        Type = ConversationTypeDto.Direct,
        PeerUserId = PeerId,
        LastMessageId = lastMsg,
        LastMessagePreview = lastMsg,
        LastMessageAtMs = atMs,
        LastSenderUserId = PeerId,
        UnreadCount = 0,
        IsPinned = false,
        IsMuted = false
    };

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

    private static void SetupAutoAuth(ScriptedTcpClient tcp, IPacketBodySerializer serializer, long userId)
    {
        tcp.OnFrameSent += (cmd, _) =>
        {
            if (cmd == PacketCommand.AuthenticationRequest)
            {
                InjectPacket(tcp, serializer, PacketCommand.AuthenticationResponse,
                    new AuthResponseDto { Success = true, UserId = userId });
            }
        };
    }

    private sealed class ScriptedTcpClient : ITcpClient
    {
        private volatile bool _connected;

        public bool IsConnected => _connected;

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
                    OnDataChunkReceived?.Invoke(this, TcpHandshakeTestServer.ServerHelloFrame);
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

        public void InjectData(ReadOnlyMemory<byte> chunk)
            => OnDataChunkReceived?.Invoke(this, chunk);

        public void Dispose()
        {
            _connected = false;
            GC.SuppressFinalize(this);
        }
    }
}

