using Core.Contracts.Auth;
using Core.Models;
using Core.Models.DTO;
using Chat_App.Infrastructure.Models;
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
    /// <summary>物理删除好友（按账户 + 服务端 FriendId）。</summary>
    Task DeleteFriendAsync(long ownerUserId, long friendId);
    /// <summary>Tombstone 删除好友：标记 IsDeleted，保留行支撑历史会话。</summary>
    Task MarkFriendDeletedAsync(long ownerUserId, long friendId);

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

    /// <summary>
    /// 登录会话原子持久化：Token + 用户画像 + 服务器端点（可选）在单个事务内提交。
    /// 任一写入失败则整体回滚，杜绝内存状态与数据库状态分叉。
    /// 调用方在任务成功返回后再发布 SessionStarted 并启动会话。
    /// </summary>
    Task PersistLoginSessionAsync(AuthToken token, LocalUser user, ServerEndpoint? endpoint);

    // ---- 会话（持久化聊天系统）----
    Task<List<LocalConversation>> GetConversationsAsync(long ownerUserId);
    Task<LocalConversation?> GetConversationAsync(long ownerUserId, string conversationId);
    Task UpsertConversationAsync(LocalConversation conversation);

    /// <summary>本地归档/删除状态落库（不随服务端同步 Upsert 覆盖）。</summary>
    Task SetConversationLocalStateAsync(long ownerUserId, string conversationId, bool? archived = null, bool? deleted = null);
    Task DeleteConversationAsync(long ownerUserId, string conversationId);

    /// <summary>仅更新会话草稿（轻量写入，切换会话时保存输入框文本）。</summary>
    Task UpdateConversationDraftAsync(long ownerUserId, string conversationId, string? draft);

    /// <summary>
    /// 完整草稿写入（乐观并发）：仅当本地保存版本旧于当前时写入，防止多窗口旧草稿覆盖新草稿。
    /// 返回是否实际写入（false = 数据库已有更新版本，本次丢弃）。
    /// </summary>
    Task<bool> UpdateConversationDraftAsync(
        long ownerUserId, string conversationId, string? draft, string? draftState,
        long updatedAtMs, int revision);

    // ---- 消息----
    /// <summary>分页拉取会话历史消息（向后翻页， newest-first）。</summary>
    /// <param name="limit">单页条数上限。</param>
    /// <param name="beforeReceivedAtMs">游标：仅返回 ReceivedAtMs 严格小于该值的消息。</param>
    /// <param name="beforeMessageId">同毫秒消息的 tie-breaker：与 beforeReceivedAtMs 配合排除该 Id 及之后的消息。</param>
    /// <remarks>首次加载可不传游标；翻页时传入上一页最早消息的时间戳与 Id。</remarks>
    Task<List<LocalMessage>> GetMessagesAsync(long ownerUserId, string conversationId, int limit = 100, long? beforeReceivedAtMs = null, string? beforeMessageId = null);
    Task<LocalMessage?> GetMessageByServerIdAsync(long ownerUserId, string messageId);
    Task<LocalMessage?> GetMessageByClientIdAsync(long ownerUserId, string clientMessageId);
    Task UpsertMessageAsync(LocalMessage message);
    /// <summary>更新单条消息状态。</summary>
    /// <param name="messageId">服务端消息 Id；与 clientMessageId 二选一非空用于定位行。</param>
    /// <param name="clientMessageId">客户端幂等 Id；服务端尚未回填 MessageId 时用它定位。</param>
    /// <param name="ackServerMessageId">ACK 接受时回填的服务端消息 Id（仅 status=Sent 时有意义）。</param>
    /// <param name="failureReason">status=Failed 时的失败原因。</param>
    Task UpdateMessageStatusAsync(long ownerUserId, string? messageId, string? clientMessageId, MessageStatus status, string? failureReason = null, string? ackServerMessageId = null);
    /// <summary>标记消息撤回（条件时间单调）。返回应用结果，仅 Applied 表示真实变化。</summary>
    Task<MessageMutationResult> MarkMessageRecalledAsync(long ownerUserId, string messageId, long recalledAtMs);

    /// <summary>应用消息编辑（版本单调 + 未撤回）。返回应用结果，仅 Applied 表示真实变化。</summary>
    Task<MessageMutationResult> ApplyMessageEditAsync(long ownerUserId, string messageId, string content, int editVersion, long editedAtMs);
    /// <summary>前向增量拉取：返回 ReceivedAtMs &gt; afterReceivedAtMs 的消息，用于重连后 catch-up。</summary>
    Task<List<LocalMessage>> GetMessagesAfterAsync(long ownerUserId, string conversationId, long afterReceivedAtMs, int limit = 100);

    /// <summary>
    /// 事务性应用入站消息：在单个 DbContext + 单个事务内完成
    /// 消息 upsert + 附件批量 upsert + 会话摘要原子更新 + 未读数递增。
    /// </summary>
    Task ApplyIncomingMessageAsync(LocalMessage message, List<LocalAttachment> attachments, LocalConversation? conversationUpdate);

    /// <summary>
    /// 批量应用历史同步：单 DbContext + 单事务完成幂等插入/单调合并、附件 upsert、
    /// 会话摘要单调更新与水位推进。cursor 为 null 不推进水位；
    /// 非空时仅当其 AfterReceivedAtMs 大于已存水位才覆盖。
    /// </summary>
    Task ApplyHistoryBatchAsync(long ownerUserId, string conversationId, IReadOnlyList<MessageHistoryItemDto> items, LocalSyncCursor? cursor);

    // ---- Outbox----
    Task<long> EnqueueOutboxAsync(LocalOutboxMessage outbox);
    Task<LocalOutboxMessage?> GetOutboxByClientIdAsync(long ownerUserId, string clientMessageId);
    /// <summary>取出待发送/重试的 Outbox 记录（Queued/Sending/Failed），按创建时间升序，供 OutboxProcessor 轮询。</summary>
    Task<List<LocalOutboxMessage>> GetPendingOutboxAsync(long ownerUserId, int limit = 50);
    Task UpdateOutboxStatusAsync(long ownerUserId, string clientMessageId, OutboxStatus status, string? messageId = null, string? failureReason = null);
    Task DeleteOutboxAsync(long ownerUserId, string clientMessageId);

    /// <summary>
    /// 事务性写入 Outbox + LocalMessage（事务化 Outbox）。
    /// 在单个 DbContext + 单个事务内完成两表 upsert，保证原子性。
    /// </summary>
    Task EnqueueOutboxWithMessageAsync(LocalOutboxMessage outbox, LocalMessage message);

    /// <summary>
    /// 更新 Outbox 状态并推进重试元数据。
    /// 仅当 status == Failed 时递增 RetryCount 并设置 NextRetryAt（指数退避 + jitter）。
    /// </summary>
    Task UpdateOutboxStatusWithRetryAsync(long ownerUserId, string clientMessageId, OutboxStatus status, string? messageId = null, string? failureReason = null);

    /// <summary>
    /// 认领待发送条目（租约）：原子地将可发送条目（Queued，或 Failed 且未超重试上限、
    /// 已到重试时间）置为 Sending，并写入 AttemptId/AttemptStartedAt/LeaseUntil。
    /// 返回认领成功（状态确实转换）的条目。
    /// </summary>
    Task<List<LocalOutboxMessage>> ClaimPendingOutboxAsync(long ownerUserId, int limit, DateTime now, DateTime leaseUntil, int maxRetryCount);

    /// <summary>
    /// 启动/周期恢复：将租约过期的 Sending 条目（LeaseUntil &lt; now）回收为 Queued，
    /// 清空尝试字段，供下轮认领。返回恢复条数。
    /// </summary>
    Task<int> RecoverStaleSendingAsync(long ownerUserId, DateTime now);

    /// <summary>
    /// 记录发送失败：Outbox 置 Failed（带分类/错误码/重试时间），
    /// LocalMessage 同步置 Failed；单事务。
    /// </summary>
    Task<bool> MarkOutboxFailureAsync(long ownerUserId, string clientMessageId, string? errorCode, string? failureReason, OutboxFailureKind failureKind, DateTime? nextRetryAt);

    /// <summary>手动重试：Failed/Cancelled → Queued，重置尝试元数据与分类。返回是否转换成功。</summary>
    Task<bool> RetryOutboxAsync(long ownerUserId, string clientMessageId);

    /// <summary>取消发送：Queued/Sending → Cancelled（LocalMessage 同步 Cancelled→Failed 展示）。返回是否转换成功。</summary>
    Task<bool> CancelOutboxAsync(long ownerUserId, string clientMessageId);

    /// <summary>清理已结束条目：删除 olderThan 之前的 Sent/Cancelled 记录。返回删除条数。</summary>
    Task<int> CleanupOutboxAsync(long ownerUserId, DateTime olderThan);

    /// <summary>
    /// ACK 单事务处理：Outbox 与 LocalMessage 在单个事务内原子推进。
    /// 条件更新（单调状态机）：Queued/Sending → Sent（接受）或 → Failed（拒绝）；
    /// 已 Sent 的重复 ACK 视为幂等成功；其它不允许的状态转换返回 OutboxUpdated=false。
    /// </summary>
    Task<OutboxAckResult> ApplyOutboxAckAsync(long ownerUserId, string clientMessageId, bool accepted, string? serverMessageId = null, string? failureReason = null);

    // ---- 同步水位----
    Task<LocalSyncCursor?> GetSyncCursorAsync(long ownerUserId, string conversationId);
    Task UpsertSyncCursorAsync(LocalSyncCursor cursor);
    Task<List<LocalSyncCursor>> GetAllSyncCursorsAsync(long ownerUserId);

    // ---- 会话已读状态----
    Task<LocalConversationReadState?> GetReadStateAsync(long ownerUserId, string conversationId);
    Task UpsertReadStateAsync(LocalConversationReadState readState);

    /// <summary>批量标记会话内消息为已读（ReceivedAtMs <= beforeReceivedAtMs 的消息）。</summary>
    Task MarkConversationMessagesReadAsync(long ownerUserId, string conversationId, long? beforeReceivedAtMs);

    // ---- 附件元数据（阶段 3）----
    Task<List<LocalAttachment>> GetAttachmentsByMessageIdAsync(long ownerUserId, string messageId);
    Task<LocalAttachment?> GetAttachmentByAttachmentIdAsync(long ownerUserId, string attachmentId);
    Task<LocalAttachment?> GetAttachmentByClientAttachmentIdAsync(long ownerUserId, string clientAttachmentId);
    Task<LocalAttachment?> GetAttachmentBySha256Async(long ownerUserId, string sha256);
    /// <summary>按 AttachmentId upsert 附件元数据；AttachmentId 为空时按 ClientAttachmentId 定位（上传中场景）。</summary>
    Task UpsertAttachmentAsync(LocalAttachment attachment);
    /// <summary>更新附件状态。</summary>
    /// <param name="attachmentId">服务端附件 Id；与 clientAttachmentId 二选一非空定位行。</param>
    /// <param name="clientAttachmentId">上传中尚未拿到服务端 Id 时用它定位。</param>
    /// <param name="status">见 <see cref="AttachmentStatus"/>。</param>
    /// <param name="downloadPath">下载路径（presign confirm 后回填）。</param>
    /// <param name="failureReason">status=Failed/Abandoned 时的原因。</param>
    Task UpdateAttachmentStatusAsync(long ownerUserId, string? attachmentId, string? clientAttachmentId, AttachmentStatus status, string? downloadPath = null, string? failureReason = null);

    /// <summary>更新附件的本地上传路径和重试次数。传 null 表示不修改对应字段（localUploadingPath 传空字符串可清空）。</summary>
    Task UpdateAttachmentUploadPathAsync(long ownerUserId, string? clientAttachmentId, string? localUploadingPath, int? retryCount = null);
    Task DeleteAttachmentAsync(long ownerUserId, string attachmentId);

    /// <summary>查询可恢复的附件（Uploading 与 Failed），Abandoned 除外。</summary>
    Task<List<LocalAttachment>> GetRecoverableAttachmentsAsync(long ownerUserId);
}
