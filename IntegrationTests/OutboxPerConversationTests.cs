using System.Buffers;
using Chat_App.Infrastructure.Events;
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
using Xunit;

namespace IntegrationTests;

/// <summary>
/// Outbox PerConversation FIFO + 全局有界并发（S3 优化 #4）：
/// 同一会话严格按入队顺序发送，不同会话并行（组间并发上限 2）。
/// </summary>
public class OutboxPerConversationTests : IDisposable
{
    private readonly string _dbPath;
    private readonly IDbContextFactory<ClientDbContext> _factory;
    private readonly DatabaseService _db;

    private const long OwnerId = 7501;
    private const long PeerId = 9501;
    private const string ConvA = "conv-a";
    private const string ConvB = "conv-b";

    public OutboxPerConversationTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"chat_fifo_{Guid.NewGuid():N}.db");
        _factory = new DbContextFactoryStub(_dbPath);
        _db = new DatabaseService(_factory);
        using var ctx = _factory.CreateDbContext();
        ctx.Database.EnsureCreated();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        for (var attempt = 0; attempt < 40; attempt++)
        {
            try { File.Delete(_dbPath); return; }
            catch (IOException) { Thread.Sleep(50); }
        }
    }

    private LocalOutboxMessage NewOutbox(string conversationId, string clientId, DateTime queuedAt) => new()
    {
        OwnerUserId = OwnerId,
        ClientMessageId = clientId,
        ConversationId = conversationId,
        TargetUserId = PeerId,
        Content = "FIFO 测试",
        Status = OutboxStatus.Queued,
        QueuedAt = queuedAt
    };

    /// <summary>
    /// 同一会话严格 FIFO + 不同会话并行：
    /// 入队顺序 a1,b1,a2,b2,a3,b3 → 发送记录中 A 组内必须为 a1,a2,a3，B 组内为 b1,b2,b3；
    /// 且两组首条存在时间重叠（组间确实并行，非串行批次）。
    /// </summary>
    [Fact]
    public async Task Same_Conversation_Fifo_And_Cross_Conversation_Parallelism()
    {
        var baseTime = DateTime.UtcNow;
        await _db.EnqueueOutboxAsync(NewOutbox(ConvA, "a1", baseTime.AddSeconds(-3)));
        await _db.EnqueueOutboxAsync(NewOutbox(ConvB, "b1", baseTime.AddSeconds(-2)));
        await _db.EnqueueOutboxAsync(NewOutbox(ConvA, "a2", baseTime.AddSeconds(-1)));
        await _db.EnqueueOutboxAsync(NewOutbox(ConvB, "b2", baseTime));
        await _db.EnqueueOutboxAsync(NewOutbox(ConvA, "a3", baseTime.AddSeconds(1)));
        await _db.EnqueueOutboxAsync(NewOutbox(ConvB, "b3", baseTime.AddSeconds(2)));

        var recorder = new SlowRecorderTcpClient(delayMs: 200);
        var session = new ChatSessionClient(recorder, new MessagePacketCodec(), new JsonPacketBodySerializer());
        var eventBus = new InMemoryEventBus();
        var userContext = new StubCurrentUserContext(OwnerId);

        // 自动回复鉴权与 MessageAck（模拟服务端），保证 Outbox 从 Sending 推进到 Sent
        SetupAutoAuth(recorder, OwnerId);
        SetupAutoAck(recorder);
        await session.ConnectAsync(new ServerEndpoint { ServerIpAddress = "127.0.0.1", ServerPort = 7000 });
        await session.AuthenticateAsync("token", OwnerId, null, null);

        // ACK 帧需要协调器会话路由 → MessageStore 推进 Outbox
        var store = new MessageStore(_db, eventBus, session);
        using var coordinator = new ChatMessageCoordinator(session, store, userContext);

        using var processor = new OutboxProcessor(_db, session, userContext, eventBus);
        processor.Start();
        foreach (var id in new[] { "a1", "b1", "a2", "b2", "a3", "b3" })
            eventBus.Publish(new OutboxEnqueuedEvent(id, id.StartsWith("a") ? ConvA : ConvB, 0));
        try
        {
            // 等全部 6 条 Sent
            await WaitUntilAsync(async () =>
            {
                var a1 = await _db.GetOutboxByClientIdAsync(OwnerId, "a1");
                var a3 = await _db.GetOutboxByClientIdAsync(OwnerId, "a3");
                var b3 = await _db.GetOutboxByClientIdAsync(OwnerId, "b3");
                return a1?.Status == OutboxStatus.Sent && a3?.Status == OutboxStatus.Sent && b3?.Status == OutboxStatus.Sent;
            }, TimeSpan.FromSeconds(20));
        }
        finally
        {
            processor.Dispose();
        }

        var sentA = recorder.SentOf(ConvA).ToList();
        var sentB = recorder.SentOf(ConvB).ToList();
        Assert.Equal(new[] { "a1", "a2", "a3" }, sentA);
        Assert.Equal(new[] { "b1", "b2", "b3" }, sentB);

        // 组间并行：B 组第一条的发送开始时刻早于 A 组第一条的完成时刻（200ms 单条耗时下重叠必然存在）
        var firstA = recorder.FirstOf(ConvA)!;
        var firstB = recorder.FirstOf(ConvB)!;
        var overlap = Math.Min(firstA!.Value.Start, firstB!.Value.Start) + 200 > Math.Max(firstA.Value.Start, firstB.Value.Start);
        Assert.True(overlap, "两个会话组应并行发送（首条发送窗口重叠）");

        // 组内 FIFO 的发送时间严格递增（a1 早于 a2 早于 a3）
        var timesA = recorder.TimesOf(ConvA).ToList();
        Assert.True(timesA[0] <= timesA[1] && timesA[1] <= timesA[2], "同一会话发送顺序必须严格递增");
    }

    private sealed class SlowRecorderTcpClient : ITcpClient
    {
        private readonly int _delayMs;
        private volatile bool _connected;
        private readonly object _lock = new();
        private readonly List<(string ConversationId, string ClientMessageId, long StartTicks)> _sends = [];

        public SlowRecorderTcpClient(int delayMs) => _delayMs = delayMs;

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

        public async Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken token = default)
        {
            var startedAt = Environment.TickCount64;
            var seq = new ReadOnlySequence<byte>(data);
            while (seq.Length > 0)
            {
                if (!MessagePacket.TryDeserialize(ref seq, out var pkt, out _))
                    break;
                if (pkt.Command == PacketCommand.ChatMessage)
                {
                    var dto = new JsonPacketBodySerializer().Deserialize<ChatMessageDto>(pkt.Body);
                    lock (_lock)
                        _sends.Add((dto!.ConversationId!, dto.MessageId ?? string.Empty, startedAt));
                }
                OnFrameSent?.Invoke(pkt.Command, pkt.Body.ToArray());
            }
            // 制造发送耗时窗口：让两个会话组有机会重叠
            await Task.Delay(_delayMs, token);
        }

        public Task ReceiveDataAsync(CancellationToken token) => Task.Delay(-1, token);
        public void Disconnect(string? reason = null)
        {
            if (!_connected) return;
            _connected = false;
            ConnectionStatusChanged?.Invoke(this, new ConnectionStateChangedEventArgs(ConnectionState.Disconnected, reason));
        }

        private readonly object _injectLock = new();

        public void InjectData(ReadOnlyMemory<byte> chunk)
        {
            // 模拟生产环境单接收循环语义：接收分片串行喂给 codec（codec 非线程安全）
            lock (_injectLock)
                OnDataChunkReceived?.Invoke(this, chunk);
        }

        public void Dispose() { _connected = false; GC.SuppressFinalize(this); }

        public int Count
        {
            get { lock (_lock) return _sends.Count; }
        }

        public IEnumerable<string> SentOf(string conversationId)
        {
            lock (_lock)
                return _sends.Where(s => s.ConversationId == conversationId).Select(s => s.ClientMessageId).ToList();
        }

        public (long Start, long End)? FirstOf(string conversationId)
        {
            lock (_lock)
            {
                var s = _sends.FirstOrDefault(x => x.ConversationId == conversationId);
                return s.ConversationId is null ? null : (s.StartTicks, s.StartTicks + _delayMs);
            }
        }

        public IEnumerable<long> TimesOf(string conversationId)
        {
            lock (_lock)
                return _sends.Where(s => s.ConversationId == conversationId).Select(s => s.StartTicks).ToList();
        }
    }

    private static void SetupAutoAuth(SlowRecorderTcpClient tcp, long userId)
    {
        tcp.OnFrameSent += (cmd, _) =>
        {
            if (cmd == PacketCommand.AuthRequest)
            {
                var serializer = new JsonPacketBodySerializer();
                InjectPacket(tcp, serializer, PacketCommand.AuthResponse,
                    new AuthResponseDto { Success = true, UserId = userId });
            }
        };
    }

    private static void SetupAutoAck(SlowRecorderTcpClient tcp)
    {
        var serializer = new JsonPacketBodySerializer();
        tcp.OnFrameSent += (cmd, body) =>
        {
            if (cmd != PacketCommand.ChatMessage)
                return;
            var dto = serializer.Deserialize<ChatMessageDto>(new ReadOnlySequence<byte>(body));
            if (dto is null)
                return;
            InjectPacket(tcp, serializer, PacketCommand.MessageAck, new MessageAcknowledgementDto
            {
                ClientMessageId = dto.MessageId,
                CommandId = $"svr-{dto.MessageId}",
                Accepted = true,
                AcknowledgedUtc = DateTime.UtcNow
            });
        };
    }

    private static void InjectPacket<T>(SlowRecorderTcpClient tcp, IPacketBodySerializer serializer, PacketCommand command, T? payload)
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

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (await condition())
                return;
            await Task.Delay(100);
        }
        throw new TimeoutException($"条件在 {timeout.TotalSeconds:F1}s 内未满足");
    }

    private sealed class DbContextFactoryStub(string dbPath) : IDbContextFactory<ClientDbContext>
    {
        public ClientDbContext CreateDbContext() => new(new DbContextOptionsBuilder<ClientDbContext>().UseSqlite($"Data Source={dbPath}").Options);
        public Task<ClientDbContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromResult(CreateDbContext());
    }

    private sealed class StubCurrentUserContext(long userId) : ICurrentUserContext
    {
        public long Generation => 1;
        public long? UserId => userId;
        public string? UserName => $"user-{userId}";
        public bool IsAuthenticated => true;
        public bool HasUserId => userId > 0;
        public UserSessionSnapshot Snapshot => new(userId, 1, UserName, null, null);
        public long RequireUserId() => userId;
        public bool TryGetUserId(out long id)
        {
            id = userId;
            return userId > 0;
        }
    }
}





