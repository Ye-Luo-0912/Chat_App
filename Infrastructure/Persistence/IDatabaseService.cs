using Core.Contracts.Auth;
using Core.Models;
using Infrastructure.Data;
using Infrastructure.Models;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Chat_App.Infrastructure.Persistence;

public interface IDatabaseService
{
    // 好友相关
    Task<List<LocalFriend>> GetFriendsAsync(long ownerUserId);
    Task AddFriendAsync(List<LocalFriend> friend);
    Task<LocalFriend?> GetFriendByIdAsync(long id);
    Task UpdateFriendAsync(LocalFriend updatedFriend);
    Task DeleteFriendAsync(long id);

    // 用户信息相关
    Task SaveUserAsync(LocalUser user);
    Task<LocalUser?> GetUserAsync(long userId);

    // Token 相关
    Task SaveTokenAsync(AuthToken token);
    Task<AuthToken?> GetTokenAsync();
    Task<Token?> GetAccessTokenAsync();
    Task<int> UpdateTokenAsync(AuthToken token);
    Task DeleteTokenAsync();

    // 服务器信息相关
    Task SaveServerInfoAsync(ServerEndpoint serverInfo);
    Task<ServerEndpoint?> GetServerInfoAsync();
    Task DeleteServerInfoAsync();

    // ---- 会话（P0-6 持久化聊天系统）----
    Task<List<LocalConversation>> GetConversationsAsync(long ownerUserId);
    Task<LocalConversation?> GetConversationAsync(long ownerUserId, string conversationId);
    Task UpsertConversationAsync(LocalConversation conversation);
    Task DeleteConversationAsync(long ownerUserId, string conversationId);

    // ---- 消息（P0-6）----
    Task<List<LocalMessage>> GetMessagesAsync(long ownerUserId, string conversationId, int limit = 100, long? beforeReceivedAtMs = null);
    Task<LocalMessage?> GetMessageByServerIdAsync(long ownerUserId, string messageId);
    Task<LocalMessage?> GetMessageByClientIdAsync(long ownerUserId, string clientMessageId);
    Task UpsertMessageAsync(LocalMessage message);
    Task UpdateMessageStatusAsync(long ownerUserId, string? messageId, string? clientMessageId, MessageStatus status, string? failureReason = null, string? ackServerMessageId = null);
    Task MarkMessageRecalledAsync(long ownerUserId, string messageId, long recalledAtMs);
    Task ApplyMessageEditAsync(long ownerUserId, string messageId, string content, int editVersion, long editedAtMs);
    Task<List<LocalMessage>> GetMessagesAfterAsync(long ownerUserId, string conversationId, long afterReceivedAtMs, int limit = 100);

    // ---- Outbox（P0-6）----
    Task<long> EnqueueOutboxAsync(LocalOutboxMessage outbox);
    Task<LocalOutboxMessage?> GetOutboxByClientIdAsync(long ownerUserId, string clientMessageId);
    Task<List<LocalOutboxMessage>> GetPendingOutboxAsync(long ownerUserId, int limit = 50);
    Task UpdateOutboxStatusAsync(long ownerUserId, string clientMessageId, OutboxStatus status, string? messageId = null, string? failureReason = null);
    Task DeleteOutboxAsync(long ownerUserId, string clientMessageId);

    // ---- 同步水位（P0-6）----
    Task<LocalSyncCursor?> GetSyncCursorAsync(long ownerUserId, string conversationId);
    Task UpsertSyncCursorAsync(LocalSyncCursor cursor);
    Task<List<LocalSyncCursor>> GetAllSyncCursorsAsync(long ownerUserId);

    // ---- 会话已读状态（P0-6）----
    Task<LocalConversationReadState?> GetReadStateAsync(long ownerUserId, string conversationId);
    Task UpsertReadStateAsync(LocalConversationReadState readState);

    /// <summary>批量标记会话内消息为已读（ReceivedAtMs <= beforeReceivedAtMs 的消息）。</summary>
    Task MarkConversationMessagesReadAsync(long ownerUserId, string conversationId, long? beforeReceivedAtMs);

    // ---- 附件元数据（阶段 3）----
    Task<List<LocalAttachment>> GetAttachmentsByMessageIdAsync(long ownerUserId, string messageId);
    Task<LocalAttachment?> GetAttachmentByAttachmentIdAsync(long ownerUserId, string attachmentId);
    Task<LocalAttachment?> GetAttachmentByClientAttachmentIdAsync(long ownerUserId, string clientAttachmentId);
    Task<LocalAttachment?> GetAttachmentBySha256Async(long ownerUserId, string sha256);
    Task UpsertAttachmentAsync(LocalAttachment attachment);
    Task UpdateAttachmentStatusAsync(long ownerUserId, string? attachmentId, string? clientAttachmentId, byte status, string? downloadPath = null, string? failureReason = null);

    /// <summary>更新附件的本地上传路径和重试次数。传 null 表示不修改对应字段（localUploadingPath 传空字符串可清空）。</summary>
    Task UpdateAttachmentUploadPathAsync(long ownerUserId, string? clientAttachmentId, string? localUploadingPath, int? retryCount = null);
    Task DeleteAttachmentAsync(long ownerUserId, string attachmentId);
    Task<List<LocalAttachment>> GetUploadingAttachmentsAsync(long ownerUserId);
}
