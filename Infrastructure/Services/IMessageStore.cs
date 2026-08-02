using Core.Models.DTO;
using Core.Models;
using Chat_App.Infrastructure.Models;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Chat_App.Infrastructure.Services;

/// <summary>
/// 消息持久化服务：网络消息 → 去重 → 本地事务持久化 → 发布领域事件 → UI 增量更新。
/// 所有持久化方法必须携带 <see cref="SessionStamp"/>，不再隐式读取全局当前用户，
/// 防止账户切换后旧异步链路的事件写入新账户空间。
/// 所有方法线程安全（内部通过 DbContext 工厂隔离）。
/// </summary>
public interface IMessageStore
{
    /// <summary>持久化收到的聊天消息（来自 ChatMessage 事件）。返回是否为新消息。</summary>
    Task<bool> PersistIncomingAsync(SessionStamp session, ChatMessageDto dto, CancellationToken ct = default);

    /// <summary>持久化同步 catch-up 的历史消息条目。</summary>
    Task PersistHistoryAsync(SessionStamp session, string conversationId, IReadOnlyList<MessageHistoryItemDto> items, CancellationToken ct = default);

    /// <summary>处理 MessageAck：单事务更新 outbox + message 状态（单调状态机）。</summary>
    Task HandleAckAsync(SessionStamp session, MessageAcknowledgementDto ack, CancellationToken ct = default);

    /// <summary>处理 MessageRecalled 通知。</summary>
    Task HandleRecalledAsync(SessionStamp session, MessageRecalledUpdateDto update, CancellationToken ct = default);

    /// <summary>处理 MessageEdited 通知。</summary>
    Task HandleEditedAsync(SessionStamp session, MessageEditedUpdateDto update, CancellationToken ct = default);

    /// <summary>处理 ConversationChanged 通知：更新会话摘要。</summary>
    Task HandleConversationChangedAsync(SessionStamp session, ConversationChangedDto dto, CancellationToken ct = default);

    /// <summary>从本地 DB 加载会话历史消息（按时间正序）。</summary>
    Task<List<LocalMessage>> LoadHistoryAsync(SessionStamp session, string conversationId, int limit = 100, long? beforeReceivedAtMs = null, string? beforeMessageId = null, CancellationToken ct = default);

    /// <summary>标记会话已读：清零未读数、记录已读水位。</summary>
    Task MarkConversationReadAsync(SessionStamp session, string conversationId, string? lastReadMessageId, CancellationToken ct = default);

    /// <summary>获取指定账户所有会话摘要（用于会话列表）。</summary>
    Task<List<LocalConversation>> GetConversationsAsync(SessionStamp session, CancellationToken ct = default);

    /// <summary>获取指定账户所有会话的同步水位（用于 SyncBootstrap 请求的 watermarks）。</summary>
    Task<IReadOnlyList<ConversationSyncWatermarkDto>> GetSyncWatermarksAsync(SessionStamp session, CancellationToken ct = default);

    /// <summary>处理对端已读回执（103）：更新本地消息状态为 Read。</summary>
    Task HandleReceiptAsync(SessionStamp session, MessageReceiptDto dto, CancellationToken ct = default);

    /// <summary>处理已读状态更新推送（105）：批量更新消息 Read 状态与未读数。</summary>
    Task HandleReceiptUpdatedAsync(SessionStamp session, MessageReceiptUpdatedDto dto, CancellationToken ct = default);

    /// <summary>处理未读数变更推送（113）。</summary>
    Task HandleUnreadCountChangedAsync(SessionStamp session, UnreadCountChangedDto dto, CancellationToken ct = default);

    /// <summary>通过网络拉取会话历史（106/107），持久化到本地 DB 并返回。</summary>
    Task<List<LocalMessage>> FetchAndPersistHistoryAsync(SessionStamp session, string conversationId, int limit = 50, long? beforeReceivedAtMs = null, string? beforeMessageId = null, CancellationToken ct = default);

    /// <summary>主动标记会话已读（110）：本地落库 + 上行网络请求。</summary>
    Task MarkConversationReadAndNotifyAsync(SessionStamp session, string conversationId, string? lastReadMessageId, CancellationToken ct = default);

    void Reset();
}
