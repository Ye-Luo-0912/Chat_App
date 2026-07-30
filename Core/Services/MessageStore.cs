using System.Text.Json;
using Chat_App.Infrastructure.Persistence;
using Core.Events;
using Core.Helpers;
using Core.Interfaces;
using Core.Models.DTO;
using Infrastructure.Models;

namespace Core.Services;

/// <summary>
/// 消息持久化服务：网络消息 → 去重 → 本地持久化 → 发布领域事件。
/// 依赖 IDatabaseService（仓储）、IEventBus（事件总线）、ICurrentUserContext（账户隔离）。
/// </summary>
public sealed class MessageStore : IMessageStore
{
    private const byte StatusSent = 2;
    private const byte StatusDelivered = 3;
    private const byte StatusFailed = 4;
    private const byte StatusRecalled = 5;

    private const byte OutboxStatusSent = 2;
    private const byte OutboxStatusFailed = 3;

    private const byte ConversationTypeDirect = 1;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IDatabaseService _db;
    private readonly IEventBus _eventBus;
    private readonly ICurrentUserContext _currentUserContext;

    public MessageStore(IDatabaseService db, IEventBus eventBus, ICurrentUserContext currentUserContext)
    {
        _db = db;
        _eventBus = eventBus;
        _currentUserContext = currentUserContext;
    }

    /// <inheritdoc />
    public async Task<bool> PersistIncomingAsync(ChatMessageDto dto, CancellationToken ct = default)
    {
        var owner = _currentUserContext.RequireUserId();
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
            AttachmentsJson = SerializeAttachments(dto.Attachments),
            ReplyToMessageId = dto.ReplyToMessageId,
            ReplyToSenderUserId = dto.ReplyToSenderUserId,
            ReplyToPreview = dto.ReplyToPreview,
            ForwardedFromMessageId = dto.ForwardedFromMessageId,
            ForwardedFromSenderUserId = dto.ForwardedFromSenderUserId,
            ForwardedFromPreview = dto.ForwardedFromPreview,
            Status = StatusDelivered,
            FailureReason = null,
            RetryCount = 0,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        await _db.UpsertMessageAsync(message);

        var isNewConversation = await UpdateConversationSummaryAsync(
            owner, conversationId, message.MessageId, content, receivedAtMs, dto.SenderUserId);

        _eventBus.Publish(new MessagePersistedEvent(message, isNewConversation));
        return true;
    }

    /// <inheritdoc />
    public async Task PersistHistoryAsync(string conversationId, IReadOnlyList<MessageHistoryItemDto> items, CancellationToken ct = default)
    {
        if (items is null || items.Count == 0)
            return;

        var owner = _currentUserContext.RequireUserId();

        foreach (var item in items)
        {
            var existing = string.IsNullOrEmpty(item.MessageId)
                ? null
                : await _db.GetMessageByServerIdAsync(owner, item.MessageId);
            if (existing is not null)
                continue;

            var message = new LocalMessage
            {
                OwnerUserId = owner,
                MessageId = item.MessageId,
                ClientMessageId = string.IsNullOrEmpty(item.ClientMessageId) ? null : item.ClientMessageId,
                ConversationId = conversationId,
                SenderUserId = item.SenderUserId,
                ReceiverUserId = item.ReceiverUserId,
                Content = item.Content ?? string.Empty,
                ReceivedAtMs = item.ReceivedAtMs,
                DeliveredAtMs = item.DeliveredAtMs,
                ReadAtMs = item.ReadAtMs,
                RecalledAtMs = item.RecalledAtMs,
                EditVersion = item.EditVersion <= 0 ? 1 : item.EditVersion,
                EditedAtMs = item.EditedAtMs,
                AttachmentsJson = SerializeAttachments(item.Attachments),
                ReplyToMessageId = item.ReplyToMessageId,
                ReplyToSenderUserId = item.ReplyToSenderUserId,
                ReplyToPreview = item.ReplyToPreview,
                ForwardedFromMessageId = item.ForwardedFromMessageId,
                ForwardedFromSenderUserId = item.ForwardedFromSenderUserId,
                ForwardedFromPreview = item.ForwardedFromPreview,
                Status = item.RecalledAtMs.HasValue ? StatusRecalled : StatusDelivered,
                FailureReason = null,
                RetryCount = 0,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            await _db.UpsertMessageAsync(message);
        }

        var maxItem = items[0];
        for (int i = 1; i < items.Count; i++)
        {
            if (items[i].ReceivedAtMs > maxItem.ReceivedAtMs)
                maxItem = items[i];
        }

        var cursor = await _db.GetSyncCursorAsync(owner, conversationId)
            ?? new LocalSyncCursor { OwnerUserId = owner, ConversationId = conversationId };
        cursor.AfterReceivedAtMs = maxItem.ReceivedAtMs;
        cursor.AfterMessageId = maxItem.MessageId;
        cursor.UpdatedAt = DateTime.UtcNow;
        await _db.UpsertSyncCursorAsync(cursor);
    }

    /// <inheritdoc />
    public async Task HandleAckAsync(MessageAcknowledgementDto ack, CancellationToken ct = default)
    {
        var owner = _currentUserContext.RequireUserId();
        if (string.IsNullOrEmpty(ack.ClientMessageId))
            return;
        var clientMessageId = ack.ClientMessageId!;

        var outbox = await _db.GetOutboxByClientIdAsync(owner, clientMessageId);
        var conversationId = outbox?.ConversationId;

        if (ack.Accepted)
        {
            var serverMessageId = ack.CommandId;
            await _db.UpdateOutboxStatusAsync(owner, clientMessageId, OutboxStatusSent, serverMessageId, null);
            await _db.UpdateMessageStatusAsync(owner, null, clientMessageId, StatusSent, null);

            _eventBus.Publish(new OutboxStatusChangedEvent(clientMessageId, OutboxStatusSent, serverMessageId));
            if (conversationId is not null)
                _eventBus.Publish(new MessageStatusChangedEvent(conversationId, null, clientMessageId, StatusSent, null));
        }
        else
        {
            var failureReason = string.IsNullOrWhiteSpace(ack.ErrorMessage) ? ack.ErrorCode : ack.ErrorMessage;
            await _db.UpdateOutboxStatusAsync(owner, clientMessageId, OutboxStatusFailed, null, failureReason);
            await _db.UpdateMessageStatusAsync(owner, null, clientMessageId, StatusFailed, failureReason);

            _eventBus.Publish(new OutboxStatusChangedEvent(clientMessageId, OutboxStatusFailed, null));
            if (conversationId is not null)
                _eventBus.Publish(new MessageStatusChangedEvent(conversationId, null, clientMessageId, StatusFailed, failureReason));
        }
    }

    /// <inheritdoc />
    public async Task HandleRecalledAsync(MessageRecalledUpdateDto update, CancellationToken ct = default)
    {
        var owner = _currentUserContext.RequireUserId();
        await _db.MarkMessageRecalledAsync(owner, update.MessageId, update.RecalledAtMs);

        var conversationId = ResolveConversationId(update.ConversationId, owner, update.SenderUserId, update.ReceiverUserId);
        if (conversationId is not null)
            _eventBus.Publish(new MessageRecalledEvent(conversationId, update.MessageId, update.RecalledAtMs));
    }

    /// <inheritdoc />
    public async Task HandleEditedAsync(MessageEditedUpdateDto update, CancellationToken ct = default)
    {
        var owner = _currentUserContext.RequireUserId();
        await _db.ApplyMessageEditAsync(owner, update.MessageId, update.Content, update.EditVersion, update.EditedAtMs);

        var conversationId = ResolveConversationId(update.ConversationId, owner, update.SenderUserId, update.ReceiverUserId);
        if (conversationId is not null)
            _eventBus.Publish(new MessageEditedEvent(conversationId, update.MessageId, update.Content, update.EditVersion, update.EditedAtMs));
    }

    /// <inheritdoc />
    public async Task HandleConversationChangedAsync(ConversationChangedDto dto, CancellationToken ct = default)
    {
        var owner = _currentUserContext.RequireUserId();
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
    public Task<List<LocalMessage>> LoadHistoryAsync(string conversationId, int limit = 100, long? beforeReceivedAtMs = null, CancellationToken ct = default)
    {
        var owner = _currentUserContext.RequireUserId();
        return _db.GetMessagesAsync(owner, conversationId, limit, beforeReceivedAtMs);
    }

    /// <inheritdoc />
    public async Task MarkConversationReadAsync(string conversationId, string? lastReadMessageId, CancellationToken ct = default)
    {
        var owner = _currentUserContext.RequireUserId();
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

        _eventBus.Publish(new ConversationReadEvent(conversationId, nowMs));
    }

    /// <inheritdoc />
    public Task<List<LocalConversation>> GetConversationsAsync(CancellationToken ct = default)
    {
        var owner = _currentUserContext.RequireUserId();
        return _db.GetConversationsAsync(owner);
    }

    /// <inheritdoc />
    public void Reset()
    {
        // 无内存状态需清理；DB 数据按 OwnerUserId 隔离，登出时不删除。
    }

    private async Task<bool> UpdateConversationSummaryAsync(
        long owner, string conversationId, string? messageId, string content, long receivedAtMs, long senderUserId)
    {
        var conv = await _db.GetConversationAsync(owner, conversationId);
        if (conv is null)
        {
            conv = new LocalConversation
            {
                OwnerUserId = owner,
                ConversationId = conversationId,
                Type = ConversationTypeDirect,
                PeerUserId = ConversationId.TryGetPeerUserId(conversationId, owner),
                LastMessageId = messageId,
                LastMessagePreview = BuildPreview(content),
                LastMessageAtMs = receivedAtMs,
                LastSenderUserId = senderUserId,
                UnreadCount = senderUserId == owner ? 0 : 1,
                LastReadMessageId = null,
                LastReadAtMs = null,
                IsPinned = false,
                PinnedAtMs = null,
                IsMuted = false,
                MutedUntilMs = null,
                LastSynced = DateTime.UtcNow
            };
            await _db.UpsertConversationAsync(conv);
            return true;
        }

        if (receivedAtMs > (conv.LastMessageAtMs ?? 0))
        {
            conv.LastMessageId = messageId;
            conv.LastMessagePreview = BuildPreview(content);
            conv.LastMessageAtMs = receivedAtMs;
            conv.LastSenderUserId = senderUserId;
            if (senderUserId != owner && (conv.LastReadAtMs is null || conv.LastReadAtMs < receivedAtMs))
                conv.UnreadCount = conv.UnreadCount + 1;
        }
        conv.LastSynced = DateTime.UtcNow;
        await _db.UpsertConversationAsync(conv);
        return false;
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

    private static string BuildPreview(string content)
    {
        var s = content?.Trim() ?? string.Empty;
        const int Max = 100;
        return s.Length <= Max ? s : s[..Max] + "…";
    }

    private static long ToUnixMs(DateTime utc)
    {
        var kind = utc.Kind == DateTimeKind.Utc ? utc : DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        return new DateTimeOffset(kind).ToUnixTimeMilliseconds();
    }

    private static string? SerializeAttachments(IReadOnlyList<AttachmentRefDto>? attachments)
        => attachments is null || attachments.Count == 0 ? null : JsonSerializer.Serialize(attachments, JsonOptions);
}
