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
using System.Buffers;
using System.Diagnostics;
using Xunit;

namespace IntegrationTests;

/// <summary>
/// 离线能力验收测试（产品主线 3：完整离线能力）。
/// 验收场景：
/// - 离线发送：断网时事务化落库（Outbox Queued + Message Queued），恢复后由 OutboxProcessor 自动补发；
/// - 离线草稿：输入内容持久化，重启后恢复；乐观并发拒绝旧修订覆盖；
/// - 离线查看历史：本地持久化历史在断网时可完整读取；
/// - 重连补发：可重试失败自动重试，永久失败停止；手动重试复位；
/// - 端到端补发：真实协议层 OutboxProcessor 事件触发 → 上行 → ack → Sent。
/// </summary>
public class OfflineCapabilitiesTests : IDisposable
{
    private readonly string _dbPath;
    private readonly IDbContextFactory<ClientDbContext> _factory;
    private readonly DatabaseService _db;

    private const long OwnerId = 5001;
    private const long PeerId = 9001;
    private const string ConvId = "conv-5001-9001";

    public OfflineCapabilitiesTests()
    {
        // 文件库 + EF 连接池：处理器后台线程与主线程各自独立连接，避免共享内存连接的单连接并发冲突
        _dbPath = Path.Combine(Path.GetTempPath(), $"chat_it_{Guid.NewGuid():N}.db");
        _factory = new DbContextFactoryStub(_dbPath);
        _db = new DatabaseService(_factory);
        using var ctx = _factory.CreateDbContext();
        ctx.Database.EnsureCreated();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_dbPath);
    }

    /// <summary>离线发送：断网（无连接）时事务化双写落库，恢复后 ack 单事务推进 Sent。</summary>
    [Fact]
    public async Task Offline_Send_Persists_And_Ack_Advances_After_Reconnect()
    {
        var clientId = $"c-{Guid.NewGuid():N}"[..20];
        await _db.EnqueueOutboxWithMessageAsync(
            NewOutbox(clientId), NewLocalMessage(clientId));

        // 断网状态下两条记录均已持久化（事务化 Outbox）
        var outbox = await _db.GetOutboxByClientIdAsync(OwnerId, clientId);
        var message = await _db.GetMessageByClientIdAsync(OwnerId, clientId);
        Assert.NotNull(outbox);
        Assert.NotNull(message);
        Assert.Equal(OutboxStatus.Queued, outbox!.Status);
        Assert.Equal(MessageStatus.Queued, message!.Status);

        // 恢复连接：模拟服务器返回 ack（serverMessageId 回填 + 单事务推进）
        var ack = await _db.ApplyOutboxAckAsync(OwnerId, clientId, accepted: true, serverMessageId: "svr-1");
        Assert.True(ack.OutboxUpdated);
        Assert.Equal(ConvId, ack.ConversationId);
        Assert.Equal("svr-1", ack.ServerMessageId);

        outbox = await _db.GetOutboxByClientIdAsync(OwnerId, clientId);
        message = await _db.GetMessageByClientIdAsync(OwnerId, clientId);
        Assert.Equal(OutboxStatus.Sent, outbox!.Status);
        Assert.Equal(MessageStatus.Sent, message!.Status);
        Assert.Equal("svr-1", message!.MessageId);

        // 重复 ack 幂等：已 Sent 状态视为成功，不报乱序
        var dup = await _db.ApplyOutboxAckAsync(OwnerId, clientId, accepted: true, serverMessageId: "svr-1");
        Assert.True(dup.OutboxUpdated);
    }

    /// <summary>离线草稿：完整草稿（文本+回复/编辑/附件状态）持久化并可恢复；乐观并发拒绝旧修订。</summary>
    [Fact]
    public async Task Offline_Draft_Roundtrip_And_Optimistic_Concurrency()
    {
        var conversation = new LocalConversation
        {
            OwnerUserId = OwnerId,
            ConversationId = ConvId,
            Type = 1,
            PeerUserId = PeerId
        };
        await _db.UpsertConversationAsync(conversation);

        // 写入完整草稿（模拟输入中）：修订 1
        var draftState = """{"text":"你好","replyToMessageId":null}""";
        var written = await _db.UpdateConversationDraftAsync(
            OwnerId, ConvId, "你好", draftState, 1_700_000_000_001, revision: 1);
        Assert.True(written);

        var loaded = await _db.GetConversationAsync(OwnerId, ConvId);
        Assert.NotNull(loaded);
        Assert.Equal("你好", loaded!.Draft);
        Assert.Equal(draftState, loaded.DraftState);
        Assert.Equal(1_700_000_000_001, loaded.DraftUpdatedAtMs);
        Assert.Equal(1, loaded.DraftRevision);

        // 另一窗口写入更新修订（模拟并发）：修订 2 应成功
        var newer = await _db.UpdateConversationDraftAsync(
            OwnerId, ConvId, "你好啊", """{"text":"你好啊"}""", 1_700_000_000_002, revision: 2);
        Assert.True(newer);

        // 旧时间戳草稿（并发窗口更早的写入）：应被拒绝，不覆盖新草稿
        var stale = await _db.UpdateConversationDraftAsync(
            OwnerId, ConvId, "旧内容", "{}", 1_700_000_000_000, revision: 1);
        Assert.False(stale);

        loaded = await _db.GetConversationAsync(OwnerId, ConvId);
        Assert.Equal("你好啊", loaded!.Draft);
        Assert.Equal(2, loaded.DraftRevision);
    }

    /// <summary>离线查看历史：批量同步落库后断网（同一库新连接）仍可完整读取。</summary>
    [Fact]
    public async Task Offline_History_Readable_After_Restart()
    {
        var items = new List<MessageHistoryItemDto>();
        for (var i = 1; i <= 20; i++)
        {
            items.Add(new MessageHistoryItemDto
            {
                MessageId = $"hist-{i}",
                SenderUserId = i % 2 == 0 ? OwnerId : PeerId,
                ReceiverUserId = i % 2 == 0 ? PeerId : OwnerId,
                Content = $"历史消息 {i}",
                ReceivedAtMs = 1_700_000_000_000 + i
            });
        }

        await _db.ApplyHistoryBatchAsync(OwnerId, ConvId, items, cursor: null);

        // 模拟重启：全新 DatabaseService（无任何内存状态），仅从本地 DB 读取
        {
            var db2 = new DatabaseService(_factory);
            var history = await db2.GetMessagesAsync(OwnerId, ConvId, limit: 100);
            Assert.Equal(20, history.Count);
            Assert.Equal("历史消息 1", history[0].Content);
            Assert.Equal("历史消息 20", history[^1].Content);
            Assert.Contains(history, m => m.SenderUserId == PeerId);
        }

        // 幂等重放：同一批再次应用不产生重复
        await _db.ApplyHistoryBatchAsync(OwnerId, ConvId, items, cursor: null);
        var again = await _db.GetMessagesAsync(OwnerId, ConvId, limit: 100);
        Assert.Equal(20, again.Count);
    }

    /// <summary>重连补发：可重试失败自动进入待发队列，永久失败停止；手动重试复位后可成功。</summary>
    [Fact]
    public async Task Failed_Outbox_Retries_And_Permanent_Stops()
    {
        var clientId = $"c-{Guid.NewGuid():N}"[..20];
        await _db.EnqueueOutboxWithMessageAsync(NewOutbox(clientId), NewLocalMessage(clientId));

        // 可重试失败（网络抖动）：仍可被认领重发
        var marked = await _db.MarkOutboxFailureAsync(
            OwnerId, clientId, "TIMEOUT", "连接超时", OutboxFailureKind.Retryable, nextRetryAt: null);
        Assert.True(marked);

        var pending = await _db.GetPendingOutboxAsync(OwnerId);
        Assert.Contains(pending, o => o.ClientMessageId == clientId);

        // 手动重试：Failed → Queued（清失败现场，RetryCount 保留历史次数）
        var retried = await _db.RetryOutboxAsync(OwnerId, clientId);
        Assert.True(retried);
        var outbox = await _db.GetOutboxByClientIdAsync(OwnerId, clientId);
        Assert.Equal(OutboxStatus.Queued, outbox!.Status);
        Assert.Equal(OutboxFailureKind.None, outbox.FailureKind);
        Assert.Null(outbox.FailureReason);

        // 永久失败（参数非法）：生产认领路径排除，不再自动重试
        var permClientId = $"c-{Guid.NewGuid():N}"[..20];
        await _db.EnqueueOutboxWithMessageAsync(NewOutbox(permClientId), NewLocalMessage(permClientId));
        var afterPermEnqueue = await _db.GetOutboxByClientIdAsync(OwnerId, clientId);
        Assert.Equal(OutboxStatus.Queued, afterPermEnqueue!.Status);
        await _db.MarkOutboxFailureAsync(
            OwnerId, permClientId, "INVALID_ARGUMENT", "参数无效", OutboxFailureKind.Permanent, nextRetryAt: null);
        var afterPermMark = await _db.GetOutboxByClientIdAsync(OwnerId, clientId);
        Assert.Equal(OutboxStatus.Queued, afterPermMark!.Status);

        // 认领（模拟处理器领取任务）：Retryable 可认领 → Sending；Permanent 被排除
        var claims = await _db.ClaimPendingOutboxAsync(OwnerId, 50, DateTime.UtcNow, DateTime.UtcNow.AddMinutes(2), maxRetryCount: 10);
        Assert.Contains(claims, o => o.ClientMessageId == clientId);
        Assert.DoesNotContain(claims, o => o.ClientMessageId == permClientId);
        outbox = await _db.GetOutboxByClientIdAsync(OwnerId, permClientId);
        Assert.Equal(OutboxStatus.Failed, outbox!.Status);
        Assert.Equal(OutboxFailureKind.Permanent, outbox.FailureKind);

        // 认领后崩溃恢复：Sending 租约过期 → RecoverStaleSending 收回 Queued
        var claimed = await _db.GetOutboxByClientIdAsync(OwnerId, clientId);
        Assert.Equal(OutboxStatus.Sending, claimed!.Status);

        var recovered = await _db.RecoverStaleSendingAsync(OwnerId, DateTime.UtcNow.AddMinutes(3));
        Assert.Equal(1, recovered);
        outbox = await _db.GetOutboxByClientIdAsync(OwnerId, clientId);
        Assert.Equal(OutboxStatus.Queued, outbox!.Status);
    }

    /// <summary>端到端补发：真实协议层——事务入库 → OutboxProcessor 事件触发 → 上行 → ack → 双表 Sent。</summary>
    [Fact]
    public async Task OutboxProcessor_AutoSends_After_Enqueue_EndToEnd()
    {
        using var tcp = new ScriptedTcpClient();
        var serializer = new JsonPacketBodySerializer();
        var session = new ChatSessionClient(tcp, new MessagePacketCodec(), serializer);
        var eventBus = new InMemoryEventBus();
        var userContext = new StubCurrentUserContext(OwnerId);

        await session.ConnectAsync(new ServerEndpoint { ServerIpAddress = "127.0.0.1", ServerPort = 7000 });
        SetupAutoAuth(tcp, serializer, OwnerId);
        await session.AuthenticateAsync("token", OwnerId, null, null);
        Assert.True(session.IsAuthenticated);

        var store = new MessageStore(_db, eventBus, session);
        var coordinator = new ChatMessageCoordinator(session, store, userContext);
        var processor = new OutboxProcessor(_db, session, userContext, eventBus);
        try
        {
            processor.Start();

        // 拦截上行 ChatMessage 帧，注入 MessageAck（模拟服务器确认）
        tcp.OnFrameSent += (cmd, body) =>
        {
            if (cmd != PacketCommand.ChatMessage)
                return;
            var sent = serializer.Deserialize<ChatMessageDto>(new ReadOnlySequence<byte>(body));
            if (sent is null)
                return;
            InjectPacket(tcp, serializer, PacketCommand.MessageAck, new MessageAcknowledgementDto
            {
                ClientMessageId = sent.MessageId,
                CommandId = sent.MessageId,
                Accepted = true,
                AcknowledgedUtc = DateTime.UtcNow
            });
        };

        // 离线状态入库（与网络无关），然后发布事件触发即时排空
        var clientId = $"c-{Guid.NewGuid():N}"[..20];
        await _db.EnqueueOutboxWithMessageAsync(NewOutbox(clientId), NewLocalMessage(clientId));
        eventBus.Publish(new OutboxEnqueuedEvent(clientId, ConvId, PeerId));

        // 等待处理器完成 上行 → ack → 双表 Sent（轮询 ≤ 8s）
        await WaitUntilAsync(async () =>
        {
            var o = await _db.GetOutboxByClientIdAsync(OwnerId, clientId);
            var m = await _db.GetMessageByClientIdAsync(OwnerId, clientId);
            return o?.Status == OutboxStatus.Sent && m?.Status == MessageStatus.Sent;
        }, TimeSpan.FromSeconds(8));

        var outbox = await _db.GetOutboxByClientIdAsync(OwnerId, clientId);
        var message = await _db.GetMessageByClientIdAsync(OwnerId, clientId);
        Assert.Equal(OutboxStatus.Sent, outbox!.Status);
        Assert.Equal(MessageStatus.Sent, message!.Status);
        Assert.False(string.IsNullOrWhiteSpace(message.MessageId));
        }
        finally
        {
            processor.Dispose();
        }
    }

    // ── 辅助 ────────────────────────────────────────────

    private LocalOutboxMessage NewOutbox(string clientId) => new()
    {
        OwnerUserId = OwnerId,
        ClientMessageId = clientId,
        ConversationId = ConvId,
        TargetUserId = PeerId,
        Content = "离线发送测试",
        Status = OutboxStatus.Queued,
        QueuedAt = DateTime.UtcNow
    };

    private LocalMessage NewLocalMessage(string clientId) => new()
    {
        OwnerUserId = OwnerId,
        ClientMessageId = clientId,
        ConversationId = ConvId,
        SenderUserId = OwnerId,
        ReceiverUserId = PeerId,
        Content = "离线发送测试",
        ReceivedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        Status = MessageStatus.Queued,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (await condition())
                return;
            await Task.Delay(100);
        }

        throw new TimeoutException($"条件在 {timeout.TotalSeconds:F1}s 内未满足");
    }

    /// <summary>测试用当前用户上下文 stub（原子快照语义简化版）。</summary>
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

    /// <summary>可控的假 TCP 客户端：解析每次发送的帧，触发回调让测试模拟服务端响应。</summary>
    private sealed class ScriptedTcpClient : ITcpClient
    {
        private volatile bool _connected;

        public bool IsConnected => _connected;

        public event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStatusChanged;
        public event EventHandler<ReadOnlyMemory<byte>>? OnDataChunkReceived;

        /// <summary>每次发送一帧后触发（command, bodyBytes）。</summary>
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

        /// <summary>注入一段数据块给上层（模拟服务端下发）。</summary>
        public void InjectData(ReadOnlyMemory<byte> chunk)
            => OnDataChunkReceived?.Invoke(this, chunk);

        public void Dispose()
        {
            _connected = false;
            GC.SuppressFinalize(this);
        }
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

    /// <summary>设置自动鉴权响应：收到 AuthRequest 后立即注入 AuthResponse。</summary>
    private static void SetupAutoAuth(
        ScriptedTcpClient tcp,
        IPacketBodySerializer serializer,
        long userId,
        bool success = true)
    {
        tcp.OnFrameSent += (cmd, _) =>
        {
            if (cmd == PacketCommand.AuthRequest)
            {
                InjectPacket(tcp, serializer, PacketCommand.AuthResponse,
                    new AuthResponseDto { Success = success, UserId = userId });
            }
        };
    }
}
