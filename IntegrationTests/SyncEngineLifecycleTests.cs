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
    /// Restart 等待旧代退出期间，更新的 Stop 必须撤销其启动权限；期间 Start 不得与旧代重叠。
    /// Stop 完成后仍可为另一个账户安全启动新代。
    /// </summary>
    [Fact]
    public async Task Stop_Supersedes_Pending_Restart_And_Prevents_Overlapping_Start()
    {
        using var tcp = new ScriptedTcpClient();
        var serializer = new JsonPacketBodySerializer();
        using var chatSession = new ChatSessionClient(tcp, new MessagePacketCodec(), serializer);
        SetupAutoAuth(tcp, serializer, OwnerId);

        tcp.OnFrameSent += (cmd, body) =>
        {
            if (cmd != PacketCommand.SyncBootstrapRequest)
                return;
            var req = serializer.Deserialize<SyncBootstrapRequestDto>(new ReadOnlySequence<byte>(body));
            if (req is null)
                return;
            InjectPacket(tcp, serializer, PacketCommand.SyncBootstrapResponse,
                new SyncBootstrapResponseDto
                {
                    RequestId = req.RequestId ?? string.Empty,
                    Succeeded = true,
                    Conversations = [],
                    ConversationsHasMore = false,
                    CatchUps = []
                });
        };

        await chatSession.ConnectAsync(new ServerEndpoint { ServerIpAddress = "127.0.0.1", ServerPort = 7000 });
        await chatSession.AuthenticateAsync("token", OwnerId, null, null);

        var checkpoint = new BlockingCheckpointStore();
        var store = new MessageStore(_db, new InMemoryEventBus(), chatSession);
        var engine = new SyncEngine(chatSession, store, _db, checkpoint, new SyncConflictResolver());
        var completedSessions = new List<SessionStamp>();
        var finalCompleted = new TaskCompletionSource<SyncCompletedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var finalSession = new SessionStamp(OwnerId + 2, 3, Guid.NewGuid());
        engine.Completed += (_, e) =>
        {
            lock (completedSessions)
                completedSessions.Add(e.Session);
            if (e.Session == finalSession)
                finalCompleted.TrySetResult(e);
        };

        var oldSession = new SessionStamp(OwnerId, 1, Guid.NewGuid());
        var supersededSession = new SessionStamp(OwnerId + 1, 2, Guid.NewGuid());
        engine.Start(oldSession);
        await checkpoint.FirstEntered.WaitAsync(TimeSpan.FromSeconds(5));

        var restart = engine.RestartAsync(supersededSession);
        await checkpoint.FirstCancellationObserved.WaitAsync(TimeSpan.FromSeconds(5));
        var stop = engine.StopAsync();

        // 旧任务仍在 checkpoint 中退出；此时 Start 必须被现有唯一运行实例挡住。
        engine.Start(finalSession);
        Assert.Equal(1, checkpoint.CallCount);
        Assert.Equal(1, checkpoint.MaxConcurrentCalls);

        checkpoint.ReleaseFirst();
        await Task.WhenAll(restart, stop).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(engine.IsSyncing);
        Assert.Equal(1, checkpoint.CallCount);
        lock (completedSessions)
        {
            Assert.DoesNotContain(supersededSession, completedSessions);
            Assert.DoesNotContain(finalSession, completedSessions);
        }

        // Stop 已完成后，新账户显式 Start 可以启动；它与旧代绝不重叠。
        engine.Start(finalSession);
        await checkpoint.SecondEntered.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, checkpoint.CallCount);
        Assert.Equal(1, checkpoint.MaxConcurrentCalls);
        checkpoint.ReleaseSecond();

        var result = await finalCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result.Succeeded);
        Assert.Equal(finalSession, result.Session);
        lock (completedSessions)
        {
            Assert.DoesNotContain(supersededSession, completedSessions);
            Assert.Equal(1, completedSessions.Count(session => session == finalSession));
        }
        await engine.StopAsync();
    }

    [Fact]
    public async Task Bootstrap_Conversation_HasMore_Without_Cursor_Fails_Closed()
    {
        var result = await RunConversationPaginationScenarioAsync(
            req => new SyncBootstrapResponseDto
            {
                RequestId = req.RequestId ?? string.Empty,
                Succeeded = true,
                Conversations = [ConvItem("conv-bootstrap", "b", 1_000L)],
                ConversationsHasMore = true,
                ConversationsNextCursor = null,
                CatchUps = []
            });

        Assert.False(result.Succeeded);
        Assert.Equal("CONVERSATION_LIST_CURSOR_MISSING", result.ErrorCode);
        Assert.Null(await _db.GetConversationAsync(OwnerId, "conv-bootstrap"));
    }

    [Fact]
    public async Task Conversation_Continuation_Failure_Does_Not_Report_Completed()
    {
        var result = await RunConversationPaginationScenarioAsync(
            SuccessfulBootstrapWithContinuation,
            req => new ConversationListResponseDto
            {
                RequestId = req.RequestId ?? string.Empty,
                Succeeded = false,
                ErrorCode = "DOWNSTREAM_UNAVAILABLE",
                ErrorMessage = "temporary failure"
            });

        Assert.False(result.Succeeded);
        Assert.Equal("CONVERSATION_LIST_PAGE_FAILED", result.ErrorCode);
        Assert.Contains("DOWNSTREAM_UNAVAILABLE", result.ErrorMessage);
        Assert.NotNull(await _db.GetConversationAsync(OwnerId, "conv-first"));
    }

    [Fact]
    public async Task Conversation_Continuation_HasMore_Without_Cursor_Fails_Closed()
    {
        var result = await RunConversationPaginationScenarioAsync(
            SuccessfulBootstrapWithContinuation,
            req => new ConversationListResponseDto
            {
                RequestId = req.RequestId ?? string.Empty,
                Succeeded = true,
                Items = [ConvItem("conv-malformed-page", "m", 2_000L)],
                HasMore = true,
                NextCursor = null
            });

        Assert.False(result.Succeeded);
        Assert.Equal("CONVERSATION_LIST_CURSOR_MISSING", result.ErrorCode);
        Assert.NotNull(await _db.GetConversationAsync(OwnerId, "conv-first"));
        Assert.Null(await _db.GetConversationAsync(OwnerId, "conv-malformed-page"));
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

    private async Task<SyncCompletedEventArgs> RunConversationPaginationScenarioAsync(
        Func<SyncBootstrapRequestDto, SyncBootstrapResponseDto> bootstrap,
        Func<ConversationListRequestDto, ConversationListResponseDto>? continuation = null)
    {
        using var tcp = new ScriptedTcpClient();
        var serializer = new JsonPacketBodySerializer();
        using var session = new ChatSessionClient(tcp, new MessagePacketCodec(), serializer);
        SetupAutoAuth(tcp, serializer, OwnerId);

        tcp.OnFrameSent += (cmd, body) =>
        {
            switch (cmd)
            {
                case PacketCommand.SyncBootstrapRequest:
                    {
                        var req = serializer.Deserialize<SyncBootstrapRequestDto>(new ReadOnlySequence<byte>(body));
                        if (req is not null)
                            InjectPacket(tcp, serializer, PacketCommand.SyncBootstrapResponse, bootstrap(req));
                        break;
                    }
                case PacketCommand.ConversationListRequest when continuation is not null:
                    {
                        var req = serializer.Deserialize<ConversationListRequestDto>(new ReadOnlySequence<byte>(body));
                        if (req is not null)
                            InjectPacket(tcp, serializer, PacketCommand.ConversationListPage, continuation(req));
                        break;
                    }
            }
        };

        await session.ConnectAsync(new ServerEndpoint { ServerIpAddress = "127.0.0.1", ServerPort = 7000 });
        await session.AuthenticateAsync("token", OwnerId, null, null);

        var store = new MessageStore(_db, new InMemoryEventBus(), session);
        var engine = new SyncEngine(session, store, _db, new SyncCheckpointStore(store, _db), new SyncConflictResolver());
        var completed = new TaskCompletionSource<SyncCompletedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        engine.Completed += (_, e) => completed.TrySetResult(e);

        engine.Start(Session);
        var result = await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await engine.StopAsync();
        return result;
    }

    private static SyncBootstrapResponseDto SuccessfulBootstrapWithContinuation(SyncBootstrapRequestDto req)
        => new()
        {
            RequestId = req.RequestId ?? string.Empty,
            Succeeded = true,
            Conversations = [ConvItem("conv-first", "f", 1_000L)],
            ConversationsHasMore = true,
            ConversationsNextCursor = new ConversationListCursorDto
            {
                LastMessageAtMs = 1_000L,
                ConversationId = "conv-first"
            },
            CatchUps = []
        };

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

    private sealed class BlockingCheckpointStore : ISyncCheckpointStore
    {
        private readonly TaskCompletionSource<bool> _firstEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _firstCancellationObserved =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _releaseFirst =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _secondEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _releaseSecond =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _callCount;
        private int _activeCalls;
        private int _maxConcurrentCalls;

        public Task FirstEntered => _firstEntered.Task;
        public Task FirstCancellationObserved => _firstCancellationObserved.Task;
        public Task SecondEntered => _secondEntered.Task;
        public int CallCount => Volatile.Read(ref _callCount);
        public int MaxConcurrentCalls => Volatile.Read(ref _maxConcurrentCalls);

        public void ReleaseFirst() => _releaseFirst.TrySetResult(true);
        public void ReleaseSecond() => _releaseSecond.TrySetResult(true);

        public async Task<IReadOnlyList<ConversationSyncWatermarkDto>> GetWatermarksAsync(
            SessionStamp session,
            CancellationToken ct = default)
        {
            var call = Interlocked.Increment(ref _callCount);
            var active = Interlocked.Increment(ref _activeCalls);
            UpdateMaximum(ref _maxConcurrentCalls, active);
            try
            {
                if (call == 1)
                {
                    using var registration = ct.Register(
                        static state => ((TaskCompletionSource<bool>)state!).TrySetResult(true),
                        _firstCancellationObserved);
                    _firstEntered.TrySetResult(true);
                    await _releaseFirst.Task.ConfigureAwait(false);
                }
                else if (call == 2)
                {
                    _secondEntered.TrySetResult(true);
                    await _releaseSecond.Task.ConfigureAwait(false);
                }
                return [];
            }
            finally
            {
                Interlocked.Decrement(ref _activeCalls);
            }
        }

        public Task SaveWatermarkAsync(
            SessionStamp session,
            string conversationId,
            long afterReceivedAtMs,
            string afterMessageId,
            CancellationToken ct = default)
            => Task.CompletedTask;

        private static void UpdateMaximum(ref int target, int candidate)
        {
            var current = Volatile.Read(ref target);
            while (candidate > current)
            {
                var observed = Interlocked.CompareExchange(ref target, candidate, current);
                if (observed == current)
                    return;
                current = observed;
            }
        }
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

