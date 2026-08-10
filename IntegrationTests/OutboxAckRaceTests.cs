using Chat_App.Infrastructure.Events;
using Chat_App.Infrastructure.Models;
using Chat_App.Infrastructure.Models.Context;
using Chat_App.Infrastructure.Persistence;
using Chat_App.Infrastructure.Services;
using Core.Models;
using Core.Models.DTO;
using Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IntegrationTests;

/// <summary>
/// ACK 与发送状态机竞态回归测试：
/// 服务端 ACK 极快返回时（网络 RTT 低于本地 Sending 状态落库耗时），
/// Outbox 仍处于 Queued —— 状态机必须允许 Queued→Sent 跳级（条件更新 + AllowedFrom），
/// 不允许 ACK 被拒绝或反向覆盖；重复 ACK 必须幂等忽略。
/// </summary>
public class OutboxAckRaceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly IDbContextFactory<ClientDbContext> _factory;
    private readonly DatabaseService _db;

    private const long OwnerId = 6001;
    private const long PeerId = 9002;
    private const string ConvId = "conv-6001-9002";

    public OutboxAckRaceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"chat_ackrace_{Guid.NewGuid():N}.db");
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

    /// <summary>删除测试库文件：OutboxProcessor 的 fire-and-forget 排空任务可能仍在释放连接，重试等待。</summary>
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

    private LocalOutboxMessage NewOutbox(string clientId) => new()
    {
        OwnerUserId = OwnerId,
        ClientMessageId = clientId,
        ConversationId = ConvId,
        TargetUserId = PeerId,
        Content = "竞态测试",
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
        Content = "竞态测试",
        ReceivedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        Status = MessageStatus.Queued,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    private static readonly SessionStamp Session = new(OwnerId, 1, Guid.NewGuid());

    /// <summary>订阅事件并记录，用于断言发布内容。</summary>
    private static (MessageStore Store, List<object> Received) CreateStore(DatabaseService db)
    {
        var eventBus = new InMemoryEventBus();
        var received = new List<object>();
        eventBus.Subscribe<OutboxStatusChangedEvent>(e => { lock (received) received.Add(e); });
        eventBus.Subscribe<MessageStatusChangedEvent>(e => { lock (received) received.Add(e); });
        var store = new MessageStore(db, eventBus, null!);
        return (store, received);
    }

    /// <summary>
    /// 核心竞态：ACK 到达时 Outbox 仍是 Queued（Sending 状态尚未落库），
    /// 必须直接推进到 Sent，而不是被状态机拒绝。
    /// </summary>
    [Fact]
    public async Task Ack_Arrives_Before_Sending_Persisted_Advances_Queued_To_Sent()
    {
        var clientId = $"c-{Guid.NewGuid():N}"[..20];
        await _db.EnqueueOutboxWithMessageAsync(NewOutbox(clientId), NewLocalMessage(clientId));

        var (store, received) = CreateStore(_db);

        // ACK 直接到达（不经 OutboxProcessor 的 Sending 认领，模拟"先于 Sending 落库"）
        await store.HandleAckAsync(Session, new MessageAcknowledgementDto
        {
            ClientMessageId = clientId,
            CommandId = $"svr-{clientId}",
            Accepted = true,
            AcknowledgedUtc = DateTime.UtcNow
        });

        var outbox = await _db.GetOutboxByClientIdAsync(OwnerId, clientId);
        var message = await _db.GetMessageByClientIdAsync(OwnerId, clientId);
        Assert.NotNull(outbox);
        Assert.NotNull(message);
        Assert.Equal(OutboxStatus.Sent, outbox!.Status);
        Assert.Equal(MessageStatus.Sent, message!.Status);
        Assert.Equal($"svr-{clientId}", message.MessageId);

        // 领域事件：Sent 状态变更必须发布（UI 依赖）
        Assert.Contains(received.OfType<OutboxStatusChangedEvent>(),
            e => e.ClientMessageId == clientId && e.NewStatus == OutboxStatus.Sent);
        Assert.Contains(received.OfType<MessageStatusChangedEvent>(),
            e => e.ClientMessageId == clientId && e.NewStatus == MessageStatus.Sent);

        // 指标闭环：ACK 端到端延迟（入队 → 确认）必须记录样本
        Assert.Equal(1, store.Counters["acks_handled"]);
        Assert.True(store.Histograms["outbox_ack_latency_ms"].Count > 0,
            "outbox_ack_latency_ms 应至少有一个样本");
    }

    /// <summary>重复/乱序 ACK：已 Sent 后再来 ACK，状态机必须幂等拒绝，不反向覆盖、不重复发布。</summary>
    [Fact]
    public async Task Duplicate_Ack_Is_Idempotently_Ignored()
    {
        var clientId = $"c-{Guid.NewGuid():N}"[..20];
        await _db.EnqueueOutboxWithMessageAsync(NewOutbox(clientId), NewLocalMessage(clientId));

        var (store, received) = CreateStore(_db);

        await store.HandleAckAsync(Session, new MessageAcknowledgementDto
        {
            ClientMessageId = clientId,
            CommandId = "svr-1",
            Accepted = true
        });
        // 第二次 ACK（重复）：应被状态机忽略
        await store.HandleAckAsync(Session, new MessageAcknowledgementDto
        {
            ClientMessageId = clientId,
            CommandId = "svr-2",
            Accepted = true
        });

        var outbox = await _db.GetOutboxByClientIdAsync(OwnerId, clientId);
        var message = await _db.GetMessageByClientIdAsync(OwnerId, clientId);
        Assert.Equal(OutboxStatus.Sent, outbox!.Status);
        Assert.Equal(MessageStatus.Sent, message!.Status);
        Assert.Equal("svr-1", message.MessageId); // 不反向覆盖
        Assert.Single(received.OfType<OutboxStatusChangedEvent>());
        Assert.Single(received.OfType<MessageStatusChangedEvent>());
    }

    [Fact]
    public async Task Duplicate_Transactional_Enqueue_Does_Not_Regress_Acknowledged_Outbox_Or_Newer_Message()
    {
        var clientId = $"c-{Guid.NewGuid():N}"[..20];
        var serverId = $"svr-{clientId}";
        var initialOutbox = NewOutbox(clientId);
        initialOutbox.Content = "首次载荷";
        initialOutbox.AttachmentIdsJson = "[\"attachment-1\"]";
        var initialMessage = NewLocalMessage(clientId);
        initialMessage.Content = "首次载荷";
        initialMessage.AttachmentsJson = "[\"attachment-1\"]";
        var stableReceivedAtMs = initialMessage.ReceivedAtMs;
        await _db.EnqueueOutboxWithMessageAsync(initialOutbox, initialMessage);

        var (store, _) = CreateStore(_db);
        await store.HandleAckAsync(Session, new MessageAcknowledgementDto
        {
            ClientMessageId = clientId,
            CommandId = serverId,
            Accepted = true,
            AcknowledgedUtc = DateTime.UtcNow
        });

        var changedAtMs = stableReceivedAtMs + 10_000;
        await _db.UpsertMessageAsync(new LocalMessage
        {
            OwnerUserId = OwnerId,
            MessageId = serverId,
            ClientMessageId = clientId,
            ConversationId = ConvId,
            SenderUserId = OwnerId,
            ReceiverUserId = PeerId,
            Content = "服务端较新编辑",
            ReceivedAtMs = stableReceivedAtMs,
            ChangedAtMs = changedAtMs,
            DeliveredAtMs = stableReceivedAtMs + 1_000,
            ReadAtMs = stableReceivedAtMs + 2_000,
            RecalledAtMs = stableReceivedAtMs + 3_000,
            EditVersion = 2,
            EditedAtMs = stableReceivedAtMs + 4_000,
            AttachmentsJson = "[\"attachment-new\"]",
            ReactionsJson = "[{\"emoji\":\"ok\",\"count\":2}]",
            Status = MessageStatus.Recalled,
            CreatedAt = initialMessage.CreatedAt,
            UpdatedAt = DateTime.UtcNow
        });

        var acknowledgedOutbox = await _db.GetOutboxByClientIdAsync(OwnerId, clientId);
        Assert.NotNull(acknowledgedOutbox);
        var sentAt = acknowledgedOutbox!.SentAt;
        var queuedAt = acknowledgedOutbox.QueuedAt;

        var duplicateOutbox = NewOutbox(clientId);
        duplicateOutbox.Content = "陈旧重复载荷";
        duplicateOutbox.AttachmentIdsJson = null;
        duplicateOutbox.QueuedAt = queuedAt.AddHours(1);
        var duplicateMessage = NewLocalMessage(clientId);
        duplicateMessage.Content = "陈旧重复载荷";
        duplicateMessage.ReceivedAtMs = stableReceivedAtMs + 99_000;
        duplicateMessage.ChangedAtMs = 0;
        duplicateMessage.EditVersion = 1;
        duplicateMessage.AttachmentsJson = null;
        duplicateMessage.ReactionsJson = null;
        await _db.EnqueueOutboxWithMessageAsync(duplicateOutbox, duplicateMessage);

        var outbox = await _db.GetOutboxByClientIdAsync(OwnerId, clientId);
        var message = await _db.GetMessageByClientIdAsync(OwnerId, clientId);
        Assert.NotNull(outbox);
        Assert.NotNull(message);
        Assert.Equal(OutboxStatus.Sent, outbox!.Status);
        Assert.Equal(serverId, outbox.MessageId);
        Assert.Equal(sentAt, outbox.SentAt);
        Assert.Equal(queuedAt, outbox.QueuedAt);
        Assert.Equal("首次载荷", outbox.Content);
        Assert.Equal("[\"attachment-1\"]", outbox.AttachmentIdsJson);
        Assert.Equal(MessageStatus.Recalled, message!.Status);
        Assert.Equal(serverId, message.MessageId);
        Assert.Equal(stableReceivedAtMs, message.ReceivedAtMs);
        Assert.Equal(changedAtMs, message.ChangedAtMs);
        Assert.Equal(stableReceivedAtMs + 1_000, message.DeliveredAtMs);
        Assert.Equal(stableReceivedAtMs + 2_000, message.ReadAtMs);
        Assert.Equal(stableReceivedAtMs + 3_000, message.RecalledAtMs);
        Assert.Equal(2, message.EditVersion);
        Assert.Equal("服务端较新编辑", message.Content);
        Assert.Equal("[\"attachment-new\"]", message.AttachmentsJson);
        Assert.Equal("[{\"emoji\":\"ok\",\"count\":2}]", message.ReactionsJson);
    }

    /// <summary>ACK 拒绝（Accepted=false）在 Queued 时到达：直接推进 Failed，可重试。</summary>
    [Fact]
    public async Task Rejected_Ack_Before_Sending_Persisted_Advances_Queued_To_Failed()
    {
        var clientId = $"c-{Guid.NewGuid():N}"[..20];
        await _db.EnqueueOutboxWithMessageAsync(NewOutbox(clientId), NewLocalMessage(clientId));

        var (store, received) = CreateStore(_db);

        await store.HandleAckAsync(Session, new MessageAcknowledgementDto
        {
            ClientMessageId = clientId,
            Accepted = false,
            ErrorCode = "REJECTED"
        });

        var outbox = await _db.GetOutboxByClientIdAsync(OwnerId, clientId);
        var message = await _db.GetMessageByClientIdAsync(OwnerId, clientId);
        Assert.Equal(OutboxStatus.Failed, outbox!.Status);
        Assert.Equal(MessageStatus.Failed, message!.Status);
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
}




