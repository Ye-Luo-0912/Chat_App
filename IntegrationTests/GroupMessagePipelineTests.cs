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
using Xunit;

namespace IntegrationTests;

/// <summary>
/// 群聊消息主链验收测试（P0-1）：
/// 群消息按 ConversationId 寻址（Outbox.ConversationType=Group、TargetUserId 为空）——
/// 离线排队、恢复连接后上行（帧带 conversationId）、ack 推进 Sent、
/// 附件发送、回复保留实际发送者 Id、成员被移除后未发送 Outbox 永久失败不再重试。
/// </summary>
public class GroupMessagePipelineTests : IDisposable
{
    private readonly string _dbPath;
    private readonly IDbContextFactory<ClientDbContext> _factory;
    private readonly DatabaseService _db;

    private const long OwnerId = 7101;
    private const long MemberId = 9101;
    private const string GroupId = "conv-grp-1001";

    public GroupMessagePipelineTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"chat_groupmsg_{Guid.NewGuid():N}.db");
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

    private LocalOutboxMessage NewGroupOutbox(string clientId, string? attachmentIdsJson = null) => new()
    {
        OwnerUserId = OwnerId,
        ClientMessageId = clientId,
        ConversationId = GroupId,
        ConversationType = (byte)ConversationTypeDto.Group,
        TargetUserId = null, // 群聊按会话寻址，无对端用户
        Content = "群聊消息",
        AttachmentIdsJson = attachmentIdsJson,
        Status = OutboxStatus.Queued,
        QueuedAt = DateTime.UtcNow
    };

    private LocalMessage NewGroupLocalMessage(string clientId, long? replyToSender = null) => new()
    {
        OwnerUserId = OwnerId,
        ClientMessageId = clientId,
        ConversationId = GroupId,
        SenderUserId = OwnerId,
        ReceiverUserId = 0,
        Content = "群聊消息",
        ReceivedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        ReplyToSenderUserId = replyToSender,
        Status = MessageStatus.Queued,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    /// <summary>
    /// 群聊文本发送全链：离线入队（Group 类型）→ 上行帧（conversationId 寻址、无 targetUserId）→ ack → 双表 Sent。
    /// </summary>
    [Fact]
    public async Task Group_Text_Send_Offline_Enqueue_Reconnect_Ack_Sent()
    {
        var clientId = $"g-{Guid.NewGuid():N}"[..20];
        await _db.EnqueueOutboxWithMessageAsync(NewGroupOutbox(clientId), NewGroupLocalMessage(clientId));

        // 离线状态可读
        var before = await _db.GetOutboxByClientIdAsync(OwnerId, clientId);
        Assert.NotNull(before);
        Assert.Equal(OutboxStatus.Queued, before!.Status);
        Assert.Null(before.TargetUserId);

        using var tcp = new ScriptedTcpClient();
        var serializer = new JsonPacketBodySerializer();
        var session = new ChatSessionClient(tcp, new MessagePacketCodec(), serializer);
        var eventBus = new InMemoryEventBus();
        var userContext = new StubCurrentUserContext(OwnerId);

        await session.ConnectAsync(new ServerEndpoint { ServerIpAddress = "127.0.0.1", ServerPort = 7000 });
        SetupAutoAuth(tcp, serializer, OwnerId);
        await session.AuthenticateAsync("token", OwnerId, null, null);
        Assert.True(session.IsAuthenticated);

        // 拦截上行 ChatMessage：断言群聊寻址（conversationId + targetUserId=0），并注入 ack
        var sentLock = new object();
        ChatMessageDto? sent = null;
        tcp.OnFrameSent += (cmd, body) =>
        {
            if (cmd != PacketCommand.ChatMessage)
                return;
            var dto = serializer.Deserialize<ChatMessageDto>(new ReadOnlySequence<byte>(body));
            if (dto is null)
                return;
            lock (sentLock)
                sent = dto;
            InjectPacket(tcp, serializer, PacketCommand.MessageAcknowledgement, new MessageAcknowledgementDto
            {
                ClientMessageId = dto.MessageId,
                CommandId = $"svr-{clientId}",
                Accepted = true,
                AcknowledgedUtc = DateTime.UtcNow
            });
        };

        var store = new MessageStore(_db, eventBus, session);
        var coordinator = new ChatMessageCoordinator(session, store, userContext);
        var processor = new OutboxProcessor(_db, session, userContext, eventBus);
        try
        {
            processor.Start();
            eventBus.Publish(new OutboxEnqueuedEvent(clientId, GroupId, 0));

            await WaitUntilAsync(async () =>
            {
                var o = await _db.GetOutboxByClientIdAsync(OwnerId, clientId);
                var m = await _db.GetMessageByClientIdAsync(OwnerId, clientId);
                if (o?.Status != OutboxStatus.Sent || m?.Status != MessageStatus.Sent)
                    return false;
                lock (sentLock)
                    return sent is not null;
            }, TimeSpan.FromSeconds(8));
        }
        finally
        {
            processor.Dispose();
        }

        ChatMessageDto? captured;
        lock (sentLock)
            captured = sent;

        // 上行帧按会话寻址：conversationId 正确、无对端用户
        Assert.NotNull(captured);
        Assert.Equal(GroupId, captured!.ConversationId);
        Assert.Equal(0, captured.TargetUserId);
        Assert.Equal(clientId, captured.MessageId);

        var outbox = await _db.GetOutboxByClientIdAsync(OwnerId, clientId);
        var message = await _db.GetMessageByClientIdAsync(OwnerId, clientId);
        Assert.Equal(OutboxStatus.Sent, outbox!.Status);
        Assert.Equal(MessageStatus.Sent, message!.Status);
        Assert.Equal($"svr-{clientId}", message.MessageId);
    }

    /// <summary>群聊附件发送：附件 Id 随群消息上行。</summary>
    [Fact]
    public async Task Group_Attachment_Send_Carries_Attachment_Ids()
    {
        var clientId = $"ga-{Guid.NewGuid():N}"[..20];
        var attJson = AttachmentJson.SerializeIds(["att-1", "att-2"]);
        await _db.EnqueueOutboxWithMessageAsync(
            NewGroupOutbox(clientId, attJson),
            NewGroupLocalMessage(clientId));

        using var tcp = new ScriptedTcpClient();
        var serializer = new JsonPacketBodySerializer();
        var session = new ChatSessionClient(tcp, new MessagePacketCodec(), serializer);
        var eventBus = new InMemoryEventBus();
        var userContext = new StubCurrentUserContext(OwnerId);

        await session.ConnectAsync(new ServerEndpoint { ServerIpAddress = "127.0.0.1", ServerPort = 7000 });
        SetupAutoAuth(tcp, serializer, OwnerId);
        await session.AuthenticateAsync("token", OwnerId, null, null);

        var sentLock = new object();
        ChatMessageDto? sent = null;
        tcp.OnFrameSent += (cmd, body) =>
        {
            if (cmd != PacketCommand.ChatMessage)
                return;
            var dto = serializer.Deserialize<ChatMessageDto>(new ReadOnlySequence<byte>(body));
            if (dto is null)
                return;
            lock (sentLock)
                sent = dto;
            InjectPacket(tcp, serializer, PacketCommand.MessageAcknowledgement, new MessageAcknowledgementDto
            {
                ClientMessageId = dto.MessageId,
                CommandId = $"svr-{clientId}",
                Accepted = true,
                AcknowledgedUtc = DateTime.UtcNow
            });
        };

        var store = new MessageStore(_db, eventBus, session);
        var coordinator = new ChatMessageCoordinator(session, store, userContext);
        var processor = new OutboxProcessor(_db, session, userContext, eventBus);
        try
        {
            processor.Start();
            eventBus.Publish(new OutboxEnqueuedEvent(clientId, GroupId, 0));

            await WaitUntilAsync(async () =>
            {
                var o = await _db.GetOutboxByClientIdAsync(OwnerId, clientId);
                if (o?.Status != OutboxStatus.Sent)
                    return false;
                lock (sentLock)
                    return sent is not null;
            }, TimeSpan.FromSeconds(8));
        }
        finally
        {
            processor.Dispose();
        }

        ChatMessageDto? captured;
        lock (sentLock)
            captured = sent;

        Assert.NotNull(captured);
        Assert.Equal(GroupId, captured!.ConversationId);
        Assert.NotNull(captured.AttachmentIds);
        Assert.Equal(2, captured.AttachmentIds!.Count);
    }

    /// <summary>群消息回复：实际发送者 Id 随回复元数据保留（群聊回复对方消息时携带其 UserId）。</summary>
    [Fact]
    public async Task Group_Reply_Preserves_Actual_Sender_UserId()
    {
        var clientId = $"gr-{Guid.NewGuid():N}"[..20];
        // 回复群内成员 MemberId 的消息：ReplyToSenderUserId = MemberId（实际发送者）
        var outbox = NewGroupOutbox(clientId);
        outbox.ReplyToMessageId = "svr-reply-target";
        outbox.ReplyToSenderUserId = MemberId;
        outbox.ReplyToPreview = "原文预览";
        var local = NewGroupLocalMessage(clientId, replyToSender: MemberId);
        local.ReplyToMessageId = "svr-reply-target";
        await _db.EnqueueOutboxWithMessageAsync(outbox, local);

        var message = await _db.GetMessageByClientIdAsync(OwnerId, clientId);
        Assert.NotNull(message);
        Assert.Equal(MemberId, message!.ReplyToSenderUserId);
        Assert.Equal("svr-reply-target", message.ReplyToMessageId);
    }

    /// <summary>
    /// 成员被移除 → 该会话未发送 Outbox 永久失败（不再自动重试）。
    /// 覆盖数据库直调与协调器事件路径（GroupMemberRemoved 且被移除的是当前用户）。
    /// </summary>
    [Fact]
    public async Task Member_Removed_Marks_Pending_Outbox_Permanent_Failure()
    {
        var clientId = $"gm-{Guid.NewGuid():N}"[..20];
        await _db.EnqueueOutboxWithMessageAsync(NewGroupOutbox(clientId), NewGroupLocalMessage(clientId));

        // 数据库直调路径
        var affected = await _db.MarkOutboxPermanentByConversationAsync(OwnerId, GroupId, "成员已被移出群聊");
        Assert.Equal(1, affected);
        var outbox = await _db.GetOutboxByClientIdAsync(OwnerId, clientId);
        Assert.NotNull(outbox);
        Assert.Equal(OutboxStatus.Failed, outbox!.Status);
        Assert.Equal(OutboxFailureKind.Permanent, outbox.FailureKind);
        Assert.Equal("MEMBER_REMOVED", outbox.LastErrorCode);

        // 协调器事件路径：重新入队一条，注入 GroupMemberRemoved（UserId == 当前用户）→ 同样永久失败
        var clientId2 = $"gm2-{Guid.NewGuid():N}"[..20];
        await _db.EnqueueOutboxWithMessageAsync(NewGroupOutbox(clientId2), NewGroupLocalMessage(clientId2));

        using var tcp = new ScriptedTcpClient();
        var serializer = new JsonPacketBodySerializer();
        var session = new ChatSessionClient(tcp, new MessagePacketCodec(), serializer);
        var eventBus = new InMemoryEventBus();
        var userContext = new StubCurrentUserContext(OwnerId);

        var store = new MessageStore(_db, eventBus, session);
        using var coordinator = new ChatMessageCoordinator(session, store, userContext);

        // 注入服务端下发的成员被移除帧（被移除的是当前用户）→ 会话路由 → 协调器 → Outbox 永久失败
        InjectPacket(tcp, serializer, PacketCommand.MemberRemoved, new MemberRemovedUpdateDto
        {
            ConversationId = GroupId,
            UserId = OwnerId,
            ActorUserId = MemberId,
            OccurredAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        });

        await WaitUntilAsync(async () =>
        {
            var o = await _db.GetOutboxByClientIdAsync(OwnerId, clientId2);
            return o?.Status == OutboxStatus.Failed && o.FailureKind == OutboxFailureKind.Permanent;
        }, TimeSpan.FromSeconds(8));

        var outbox2 = await _db.GetOutboxByClientIdAsync(OwnerId, clientId2);
        Assert.Equal(OutboxStatus.Failed, outbox2!.Status);
        Assert.Equal(OutboxFailureKind.Permanent, outbox2.FailureKind);
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

