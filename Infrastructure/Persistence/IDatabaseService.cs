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
}
