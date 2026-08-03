using Chat_App.Infrastructure.Persistence;
using Chat_App.Infrastructure.Events;
using Core.Helpers;
using Core.Interfaces;
using Core.Models;
using Core.Models.DTO;
using Chat_App.Infrastructure.Models;
using Chat_App.Infrastructure.Serialization;

namespace Chat_App.Infrastructure.Services;

/// <summary>
/// 消息持久化服务：网络消息 → 去重 → 本地持久化 → 发布领域事件。
/// 依赖 IDatabaseService（仓储）、IEventBus（事件总线）。
/// 所有方法携带 SessionStamp，账户隔离由调用方传入的会话标识保证。
/// </summary>
public sealed class MessageStore : IMessageStore
{
    private const byte ConversationTypeDirect = 1;

    private readonly IDatabaseService _db;
    private readonly IEventBus _eventBus;
    private readonly IChatSessionClient _chatSession;

    public MessageStore(IDatabaseService db, IEventBus eventBus, IChatSessionClient chatSession)
    {
        _db = db;
        _eventBus = eventBus;
        _chatSession = chatSession;
    }

    /// <inheritdoc />
    public async Task<bool> PersistIncomingAsync(SessionStamp session, ChatMessageDto dto, CancellationToken ct = default)
    {
        var owner = session.OwnerUserId;
        var conversationId = ResolveConversationId(dto.ConversationId, owner, dto.SenderUserId, dto.TargetUserId);
        if (conversationId is null)
            return false;

        var receivedAtMs = ToUnixMs(dto.SentUtc);
        var content = dto.Content?.Trim() ?? string.Empty;

        var existing = dto.MessageId is null
            ? null
            : await _db.GetMessageByServerIdAsync(owner, dto.MessageId);
        if (existing is not null)
            return false;

        var message = new LocalMessage
        {
            OwnerUserId = owner,
            MessageId = dto.MessageId,
            ClientMessageId = null,
            ConversationId = conversationId,
            SenderUserId = dto.SenderUserId,
            ReceiverUserId = dto.TargetUserId,
            Content = content,
            ReceivedAtMs = receivedAtMs,
            DeliveredAtMs = null,
            ReadAtMs = null,
            RecalledAtMs = null,
            EditVersion = 1,
            EditedAtMs = null,
            AttachmentsJson = AttachmentJson.Serialize(dto.Attachments),
            ReplyToMessageId = dto.ReplyToMessageId,
            ReplyToSenderUserId = dto.ReplyToSenderUserId,
            ReplyToPreview = dto.ReplyToPreview,
            ForwardedFromMessageId = dto.ForwardedFromMessageId,
            ForwardedFromSenderUserId = dto.ForwardedFromSenderUserId,
            ForwardedFromPreview = dto.ForwardedFromPreview,
            Status = MessageStatus.Delivered,
            FailureReason = null,
            RetryCount = 0,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        // 阶段 3：构建附件元数据列表（与消息在单个事务内原子写入 LocalAttachment 表）
        var attachments = new List<LocalAttachment>();
        if (dto.Attachments is { Count: > 0 })
        {
            foreach (var att in dto.Attachments)
            {
                if (string.IsNullOrWhiteSpace(att.AttachmentId))
                    continue;
                attachments.Add(new LocalAttachment
                {
                    OwnerUserId = owner,
                    AttachmentId = att.AttachmentId,
                    MessageId = message.MessageId,
                    ConversationId = conversationId,
                    FileName = att.FileName,
                    ContentType = att.ContentType,
                    SizeBytes = att.SizeBytes,
                    DownloadPath = att.DownloadApiHint,
                    ThumbnailPath = att.ThumbnailApiHint,
                    Status = att.Status == 1 ? AttachmentStatus.Available : AttachmentStatus.Uploading,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                });
            }
        }

        // 构建会话摘要更新（字段逻辑与 ApplyIncomingMessageAsync 事务内的读-改-写一致），
        // 由 ApplyIncomingMessageAsync 在事务内完成读-改-写 + 未读数递增。
        var existingConversation = await _db.GetConversationAsync(owner, conversationId);
        var isNewConversation = existingConversation is null;
        var conversationUpdate = new LocalConversation
        {
            OwnerUserId = owner,
            ConversationId = conversationId,
            Type = ConversationTypeDirect,
            PeerUserId = ConversationId.TryGetPeerUserId(conversationId, owner),
            LastMessageId = message.MessageId,
            LastMessagePreview = PreviewText.Truncate(content, 100),
            LastMessageAtMs = receivedAtMs,
            LastSenderUserId = dto.SenderUserId,
            // 发送方为自己时不递增未读；非自己时为增量 1（仅对未读消息生效，由事务内逻辑判定）。
            UnreadCount = dto.SenderUserId == owner ? 0 : 1,
            LastSynced = DateTime.UtcNow
        };

        // 单事务原子写入：消息 + 附件 + 会话摘要（持久化层事务边界）
        await _db.ApplyIncomingMessageAsync(message, attachments, conversationUpdate);

        _eventBus.Publish(new MessagePersistedEvent(message, isNewConversation));
        return true;
    }

    /// <inheritdoc />
    public async Task PersistHistoryAsync(SessionStamp session, string conversationId, IReadOnlyList<MessageHistoryItemDto> items, CancellationToken ct = default)
    {
        if (items is null || items.Count == 0)
            return;

        var owner = session.OwnerUserId;

        // 批次最大时间戳：目标水位（批量方法内做单调判断，旧水位不回退）。
        var maxItem = items[0];
        for (var i = 1; i < items.Count; i++)
        {
            if (items[i].ReceivedAtMs > maxItem.ReceivedAtMs)
                maxItem = items[i];
        }

        var cursor = new LocalSyncCursor
        {
            OwnerUserId = owner,
            ConversationId = conversationId,
            AfterReceivedAtMs = maxItem.ReceivedAtMs,
            AfterMessageId = maxItem.MessageId
        };

        // 单 DbContext + 单事务批量应用：插入/合并 + 附件 + 会话摘要 + 水位一次完成。
        await _db.ApplyHistoryBatchAsync(owner, conversationId, items, cursor).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task ApplyHistoryBatchAsync(SessionStamp session, string conversationId, IReadOnlyList<MessageHistoryItemDto> items, LocalSyncCursor? cursor, CancellationToken ct = default)
    {
        if (items is null || items.Count == 0)
            return;

        // cursor 为空表示不推进水位；否则由批量方法做单调判断后落库。
        await _db.ApplyHistoryBatchAsync(session.OwnerUserId, conversationId, items, cursor).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task HandleAckAsync(SessionStamp session, MessageAcknowledgementDto ack, CancellationToken ct = default)
    {
        var owner = session.OwnerUserId;
        if (string.IsNullOrEmpty(ack.ClientMessageId))
            return;
        var clientMessageId = ack.ClientMessageId!;

        // 单事务 + 条件更新：Outbox 与 LocalMessage 原子推进；
        // 状态仅在允许的前置状态下生效（Queued/Sending → Sent），杜绝 ACK 反向覆盖。
        var result = await _db.ApplyOutboxAckAsync(owner, clientMessageId, ack.Accepted, ack.CommandId,
            string.IsNullOrWhiteSpace(ack.ErrorMessage) ? ack.ErrorCode : ack.ErrorMessage);

        if (!result.OutboxUpdated)
        {
            // 状态机不允许该转换（如已 Sent / 已 Cancelled）：重复 ACK 或乱序，忽略。
            LogWarningDuplicateAck(clientMessageId, ack.Accepted);
            return;
        }

        if (ack.Accepted)
        {
            var serverMessageId = ack.CommandId;
            _eventBus.Publish(new OutboxStatusChangedEvent(clientMessageId, OutboxStatus.Sent, serverMessageId));
            if (result.ConversationId is not null)
                _eventBus.Publish(new MessageStatusChangedEvent(result.ConversationId, serverMessageId, clientMessageId, MessageStatus.Sent, null));
        }
        else
        {
            var failureReason = string.IsNullOrWhiteSpace(ack.ErrorMessage) ? ack.ErrorCode : ack.ErrorMessage;
            _eventBus.Publish(new OutboxStatusChangedEvent(clientMessageId, OutboxStatus.Failed, null));
            if (result.ConversationId is not null)
                _eventBus.Publish(new MessageStatusChangedEvent(result.ConversationId, null, clientMessageId, MessageStatus.Failed, failureReason));
        }
    }

    private static void LogWarningDuplicateAck(string clientMessageId, bool accepted)
    {
        Serilog.Log.Warning(
            "Outbox ACK 被状态机拒绝（重复或乱序）ClientMessageId={ClientMessageId} Accepted={Accepted}",
            clientMessageId, accepted);
    }

    /// <inheritdoc />
    public async Task HandleRecalledAsync(SessionStamp session, MessageRecalledUpdateDto update, CancellationToken ct = default)
    {
        var owner = session.OwnerUserId;
        var result = await _db.MarkMessageRecalledAsync(owner, update.MessageId, update.RecalledAtMs);

        // 仅真实状态变化才发布领域事件，避免 UI 重复/回退处理。
        if (result != MessageMutationResult.Applied)
            return;

        var conversationId = ResolveConversationId(update.ConversationId, owner, update.SenderUserId, update.ReceiverUserId);
        if (conversationId is not null)
            _eventBus.Publish(new MessageRecalledEvent(conversationId, update.MessageId, update.RecalledAtMs));
    }

    /// <inheritdoc />
    public async Task HandleEditedAsync(SessionStamp session, MessageEditedUpdateDto update, CancellationToken ct = default)
    {
        var owner = session.OwnerUserId;
        var result = await _db.ApplyMessageEditAsync(owner, update.MessageId, update.Content, update.EditVersion, update.EditedAtMs);

        // 仅真实状态变化才发布领域事件（数据库拒绝的旧编辑不再广播给 UI）。
        if (result != MessageMutationResult.Applied)
            return;

        var conversationId = ResolveConversationId(update.ConversationId, owner, update.SenderUserId, update.ReceiverUserId);
        if (conversationId is not null)
            _eventBus.Publish(new MessageEditedEvent(conversationId, update.MessageId, update.Content, update.EditVersion, update.EditedAtMs));
    }

    /// <inheritdoc />
    public async Task HandleConversationChangedAsync(SessionStamp session, ConversationChangedDto dto, CancellationToken ct = default)
    {
        var owner = session.OwnerUserId;
        var conv = await _db.GetConversationAsync(owner, dto.ConversationId);

        if (conv is null)
        {
            conv = new LocalConversation
            {
                OwnerUserId = owner,
                ConversationId = dto.ConversationId,
                Type = (byte)dto.Type,
                PeerUserId = dto.PeerUserId,
                LastMessageId = dto.LastMessageId,
                LastMessagePreview = dto.LastMessagePreview,
                LastMessageAtMs = dto.LastMessageAtMs,
                LastSenderUserId = dto.LastSenderUserId,
                UnreadCount = 0,
                LastReadMessageId = null,
                LastReadAtMs = null,
                IsPinned = dto.IsPinned ?? false,
                PinnedAtMs = dto.IsPinned == true ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() : null,
                IsMuted = dto.IsMuted ?? false,
                MutedUntilMs = dto.MutedUntilMs,
                LastSynced = DateTime.UtcNow
            };
        }
        else
        {
            conv.Type = (byte)dto.Type;
            if (dto.PeerUserId.HasValue)
                conv.PeerUserId = dto.PeerUserId;
            if (dto.LastMessageId is not null)
                conv.LastMessageId = dto.LastMessageId;
            if (dto.LastMessagePreview is not null)
                conv.LastMessagePreview = dto.LastMessagePreview;
            if (dto.LastMessageAtMs.HasValue)
                conv.LastMessageAtMs = dto.LastMessageAtMs;
            if (dto.LastSenderUserId.HasValue)
                conv.LastSenderUserId = dto.LastSenderUserId;
            if (dto.IsPinned.HasValue)
            {
                conv.IsPinned = dto.IsPinned.Value;
                if (dto.IsPinned.Value && !conv.PinnedAtMs.HasValue)
                    conv.PinnedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                else if (!dto.IsPinned.Value)
                    conv.PinnedAtMs = null;
            }
            if (dto.IsMuted.HasValue)
                conv.IsMuted = dto.IsMuted.Value;
            conv.MutedUntilMs = dto.MutedUntilMs;
            conv.LastSynced = DateTime.UtcNow;
        }

        await _db.UpsertConversationAsync(conv);
        _eventBus.Publish(new ConversationUpdatedEvent(conv));
    }

    /// <inheritdoc />
    public Task<List<LocalMessage>> LoadHistoryAsync(SessionStamp session, string conversationId, int limit = 100, long? beforeReceivedAtMs = null, string? beforeMessageId = null, CancellationToken ct = default)
    {
        var owner = session.OwnerUserId;
        return _db.GetMessagesAsync(owner, conversationId, limit, beforeReceivedAtMs, beforeMessageId);
    }

    /// <inheritdoc />
    public async Task MarkConversationReadAsync(SessionStamp session, string conversationId, string? lastReadMessageId, CancellationToken ct = default)
    {
        var owner = session.OwnerUserId;
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var now = DateTime.UtcNow;

        var readState = await _db.GetReadStateAsync(owner, conversationId)
            ?? new LocalConversationReadState { OwnerUserId = owner, ConversationId = conversationId };
        readState.LastReadMessageId = lastReadMessageId;
        readState.LastReadAtMs = nowMs;
        readState.UnreadCount = 0;
        readState.UpdatedAt = now;
        await _db.UpsertReadStateAsync(readState);

        var conv = await _db.GetConversationAsync(owner, conversationId);
        if (conv is not null)
        {
            conv.UnreadCount = 0;
            conv.LastReadMessageId = lastReadMessageId;
            conv.LastReadAtMs = nowMs;
            await _db.UpsertConversationAsync(conv);
        }

        _eventBus.Publish(new LocalUnreadClearedEvent(conversationId));
    }

    /// <inheritdoc />
    public Task<List<LocalConversation>> GetConversationsAsync(SessionStamp session, CancellationToken ct = default)
    {
        var owner = session.OwnerUserId;
        return _db.GetConversationsAsync(owner);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ConversationSyncWatermarkDto>> GetSyncWatermarksAsync(SessionStamp session, CancellationToken ct = default)
    {
        var owner = session.OwnerUserId;
        var cursors = await _db.GetAllSyncCursorsAsync(owner);
        return cursors
            .Where(c => !string.IsNullOrWhiteSpace(c.AfterMessageId))
            .Select(c => new ConversationSyncWatermarkDto
            {
                ConversationId = c.ConversationId,
                AfterReceivedAtMs = c.AfterReceivedAtMs,
                AfterMessageId = c.AfterMessageId
            })
            .ToList();
    }

    /// <inheritdoc />
    public async Task HandleReceiptAsync(SessionStamp session, MessageReceiptDto dto, CancellationToken ct = default)
    {
        var owner = session.OwnerUserId;
        var conversationId = ResolveConversationId(dto.ConversationId, owner, dto.ReaderUserId ?? 0, dto.ReceiverUserId ?? 0);
        if (conversationId is null)
            return;

        if (dto.LastReadAtMs.HasValue)
            await _db.MarkConversationMessagesReadAsync(owner, conversationId, dto.LastReadAtMs.Value);

        var readState = await _db.GetReadStateAsync(owner, conversationId)
            ?? new LocalConversationReadState { OwnerUserId = owner, ConversationId = conversationId };
        if (!string.IsNullOrWhiteSpace(dto.LastReadMessageId))
            readState.LastReadMessageId = dto.LastReadMessageId;
        if (dto.LastReadAtMs.HasValue)
            readState.LastReadAtMs = dto.LastReadAtMs;
        readState.UpdatedAt = DateTime.UtcNow;
        await _db.UpsertReadStateAsync(readState);

        _eventBus.Publish(new PeerReadWatermarkAdvancedEvent(conversationId,
            dto.LastReadAtMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), dto.LastReadMessageId));
    }

    /// <inheritdoc />
    public async Task HandleReceiptUpdatedAsync(SessionStamp session, MessageReceiptUpdatedDto dto, CancellationToken ct = default)
    {
        var owner = session.OwnerUserId;
        var conversationId = dto.ConversationId;
        if (string.IsNullOrWhiteSpace(conversationId))
            return;

        if (dto.LastReadAtMs.HasValue)
            await _db.MarkConversationMessagesReadAsync(owner, conversationId, dto.LastReadAtMs.Value);

        var readState = await _db.GetReadStateAsync(owner, conversationId)
            ?? new LocalConversationReadState { OwnerUserId = owner, ConversationId = conversationId };
        if (!string.IsNullOrWhiteSpace(dto.LastReadMessageId))
            readState.LastReadMessageId = dto.LastReadMessageId;
        if (dto.LastReadAtMs.HasValue)
            readState.LastReadAtMs = dto.LastReadAtMs;
        readState.UpdatedAt = DateTime.UtcNow;
        await _db.UpsertReadStateAsync(readState);

        _eventBus.Publish(new PeerReadWatermarkAdvancedEvent(conversationId,
            dto.LastReadAtMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), dto.LastReadMessageId));
    }

    /// <inheritdoc />
    public async Task HandleUnreadCountChangedAsync(SessionStamp session, UnreadCountChangedDto dto, CancellationToken ct = default)
    {
        var owner = session.OwnerUserId;
        var conv = await _db.GetConversationAsync(owner, dto.ConversationId);
        if (conv is not null)
        {
            conv.UnreadCount = dto.UnreadCount;
            if (!string.IsNullOrWhiteSpace(dto.LastReadMessageId))
                conv.LastReadMessageId = dto.LastReadMessageId;
            if (dto.LastReadAtMs.HasValue)
                conv.LastReadAtMs = dto.LastReadAtMs;
            await _db.UpsertConversationAsync(conv);
            _eventBus.Publish(new ConversationUpdatedEvent(conv));
        }

        var readState = await _db.GetReadStateAsync(owner, dto.ConversationId)
            ?? new LocalConversationReadState { OwnerUserId = owner, ConversationId = dto.ConversationId };
        readState.UnreadCount = dto.UnreadCount;
        if (!string.IsNullOrWhiteSpace(dto.LastReadMessageId))
            readState.LastReadMessageId = dto.LastReadMessageId;
        if (dto.LastReadAtMs.HasValue)
            readState.LastReadAtMs = dto.LastReadAtMs;
        readState.UpdatedAt = DateTime.UtcNow;
        await _db.UpsertReadStateAsync(readState);
    }

    /// <inheritdoc />
    public async Task<List<LocalMessage>> FetchAndPersistHistoryAsync(SessionStamp session, string conversationId, int limit = 50, long? beforeReceivedAtMs = null, string? beforeMessageId = null, CancellationToken ct = default)
    {
        var owner = session.OwnerUserId;
        var response = await _chatSession.QueryMessageHistoryAsync(conversationId, limit, beforeReceivedAtMs, beforeMessageId, ct);
        if (response.Items is { Count: > 0 })
            await PersistHistoryAsync(session, conversationId, response.Items, ct);
        return await _db.GetMessagesAsync(owner, conversationId, limit, beforeReceivedAtMs, beforeMessageId);
    }

    /// <inheritdoc />
    public async Task MarkConversationReadAndNotifyAsync(SessionStamp session, string conversationId, string? lastReadMessageId, CancellationToken ct = default)
    {
        await MarkConversationReadAsync(session, conversationId, lastReadMessageId, ct);
        try
        {
            await _chatSession.MarkConversationReadAsync(conversationId, lastReadMessageId, null, ct);
        }
        catch
        {
            // 网络失败不影响本地已读状态
        }
    }

    public void Reset()
    {
        // 无内存状态需清理；DB 数据按 OwnerUserId 隔离，登出时不删除。
    }

    private static string? ResolveConversationId(string? explicitId, long selfId, long partyA, long partyB)
    {
        if (!string.IsNullOrWhiteSpace(explicitId))
            return explicitId;
        var peer = partyA == selfId ? partyB : partyA;
        if (peer <= 0 || peer == selfId)
            return null;
        return ConversationId.CreateDirect(selfId, peer);
    }

    private static long ToUnixMs(DateTime utc)
    {
        var kind = utc.Kind == DateTimeKind.Utc ? utc : DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        return new DateTimeOffset(kind).ToUnixTimeMilliseconds();
    }
}
