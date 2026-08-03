using Core.Models;
using Chat_App.Infrastructure.Models;

namespace Chat_App.Infrastructure.Events;

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

/// <summary>本地已读状态变更（本端清未读，不表示对端已读）。</summary>
public record LocalUnreadClearedEvent(string ConversationId);

/// <summary>对端已读水位推进（服务端 103/105 回执确认的序列水位，用于 UI 展示对端已读）。</summary>
public record PeerReadWatermarkAdvancedEvent(string ConversationId, long ReadAtMs, string? LastReadMessageId);

/// <summary>Outbox 条目状态变更。</summary>
public record OutboxStatusChangedEvent(
    string ClientMessageId, OutboxStatus NewStatus, string? ServerMessageId, string? FailureReason = null);

/// <summary>新 Outbox 条目已入库（UI 事务持久化完成），提示排空器立即发送。</summary>
public record OutboxEnqueuedEvent(string ClientMessageId, string ConversationId, long TargetUserId);
