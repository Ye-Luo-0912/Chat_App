using Core.Models.DTO;
using Infrastructure.Models;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Core.Interfaces;

/// <summary>
/// 消息持久化服务：网络消息 → 去重 → 本地事务持久化 → 发布领域事件 → UI 增量更新。
/// 所有方法线程安全（内部通过 DbContext 工厂隔离）。
/// </summary>
public interface IMessageStore
{
    /// <summary>持久化收到的聊天消息（来自 ChatMessage 事件）。返回是否为新消息。</summary>
    Task<bool> PersistIncomingAsync(ChatMessageDto dto, CancellationToken ct = default);

    /// <summary>持久化同步 catch-up 的历史消息条目。</summary>
    Task PersistHistoryAsync(string conversationId, IReadOnlyList<MessageHistoryItemDto> items, CancellationToken ct = default);

    /// <summary>处理 MessageAck：更新对应 outbox 和 message 状态。</summary>
    Task HandleAckAsync(MessageAcknowledgementDto ack, CancellationToken ct = default);

    /// <summary>处理 MessageRecalled 通知。</summary>
    Task HandleRecalledAsync(MessageRecalledUpdateDto update, CancellationToken ct = default);

    /// <summary>处理 MessageEdited 通知。</summary>
    Task HandleEditedAsync(MessageEditedUpdateDto update, CancellationToken ct = default);

    /// <summary>处理 ConversationChanged 通知：更新会话摘要。</summary>
    Task HandleConversationChangedAsync(ConversationChangedDto dto, CancellationToken ct = default);

    /// <summary>从本地 DB 加载会话历史消息（按时间正序）。</summary>
    Task<List<LocalMessage>> LoadHistoryAsync(string conversationId, int limit = 100, long? beforeReceivedAtMs = null, string? beforeMessageId = null, CancellationToken ct = default);

    /// <summary>标记会话已读：清零未读数、记录已读水位。</summary>
    Task MarkConversationReadAsync(string conversationId, string? lastReadMessageId, CancellationToken ct = default);

    /// <summary>获取当前用户所有会话摘要（用于会话列表）。</summary>
    Task<List<LocalConversation>> GetConversationsAsync(CancellationToken ct = default);

    /// <summary>获取当前账户所有会话的同步水位（用于 SyncBootstrap 请求的 watermarks）。</summary>
    Task<IReadOnlyList<ConversationSyncWatermarkDto>> GetSyncWatermarksAsync(CancellationToken ct = default);

    /// <summary>登出时清空当前账户的内存状态（不删 DB 数据）。</summary>
    /// <summary>处理对端已读回执（103）：更新本地消息状态为 Read。</summary>
    Task HandleReceiptAsync(MessageReceiptDto dto, CancellationToken ct = default);

    /// <summary>处理已读状态更新推送（105）：批量更新消息 Read 状态与未读数。</summary>
    Task HandleReceiptUpdatedAsync(MessageReceiptUpdatedDto dto, CancellationToken ct = default);

    /// <summary>处理未读数变更推送（113）。</summary>
    Task HandleUnreadCountChangedAsync(UnreadCountChangedDto dto, CancellationToken ct = default);

    /// <summary>通过网络拉取会话历史（106/107），持久化到本地 DB 并返回。</summary>
    Task<List<LocalMessage>> FetchAndPersistHistoryAsync(string conversationId, int limit = 50, long? beforeReceivedAtMs = null, string? beforeMessageId = null, CancellationToken ct = default);

    /// <summary>主动标记会话已读（110）：本地落库 + 上行网络请求。</summary>
    Task MarkConversationReadAndNotifyAsync(string conversationId, string? lastReadMessageId, CancellationToken ct = default);

    void Reset();
}
