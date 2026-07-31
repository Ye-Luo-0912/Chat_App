using Core.Models;
using Infrastructure.Models;

namespace Infrastructure.Events;

/// <summary>新消息已持久化到本地 DB。</summary>
public record MessagePersistedEvent(LocalMessage Message, bool IsNewConversation);

/// <summary>消息状态变更（sending/sent/failed/delivered/recalled）。</summary>
public record MessageStatusChangedEvent(string ConversationId, string? MessageId, string? ClientMessageId, MessageStatus NewStatus, string? FailureReason);

/// <summary>消息被撤回。</summary>
public record MessageRecalledEvent(string ConversationId, string MessageId, long RecalledAtMs);

/// <summary>消息被编辑。</summary>
public record MessageEditedEvent(string ConversationId, string MessageId, string Content, int EditVersion, long EditedAtMs);

/// <summary>会话摘要更新（最后消息/未读数/偏好变更）。</summary>
public record ConversationUpdatedEvent(LocalConversation Conversation);

/// <summary>会话已读状态变更（未读数清零）。</summary>
public record ConversationReadEvent(string ConversationId, long ReadAtMs);

/// <summary>Outbox 条目状态变更。</summary>
public record OutboxStatusChangedEvent(string ClientMessageId, OutboxStatus NewStatus, string? ServerMessageId);
