using Core.Contracts.Auth;
using Core.Models;
using Core.Models.DTO;
using Chat_App.Infrastructure.Models;
using Chat_App.Infrastructure.Models.Context;
using Chat_App.Infrastructure.Serialization;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Chat_App.Infrastructure.Persistence;

/// <summary>
/// 本地数据库访问服务。
/// 通过 <see cref="IDbContextFactory{ClientDbContext}"/> 为每个操作创建独立的、短生命周期的 DbContext，
/// 避免单个非线程安全 DbContext 被多线程共享。
/// </summary>
public class DatabaseService(
    IDbContextFactory<ClientDbContext> contextFactory,
    IDatabaseWriteQueue? writeQueue = null) : IDatabaseService
{
    private static readonly CancellationToken None = CancellationToken.None;

    /// <summary>单写入队列（可选）：高频写入路径可通过此队列串行化，消除 SQLITE_BUSY 并发冲突。</summary>
    public IDatabaseWriteQueue? WriteQueue => writeQueue;

    /// <summary>
    /// 路由写入操作：注册了单写入队列则串行化执行（消除 SQLITE_BUSY），否则直接执行。
    /// 委托为自包含操作，内部自行管理 DbContext 与 SaveChangesAsync。
    /// </summary>
    private Task WriteAsync(Func<Task> impl, CancellationToken ct = default)
    {
        if (writeQueue is null)
            return impl();
        return writeQueue.EnqueueAsync(_ => impl(), ct);
    }

    private Task<T> WriteAsync<T>(Func<Task<T>> impl, CancellationToken ct = default)
    {
        if (writeQueue is null)
            return impl();
        return writeQueue.EnqueueAsync(_ => impl(), ct);
    }

    /// <summary>
    /// 判断 DbUpdateException 是否由 SQLite 唯一约束冲突引起（SQLITE_CONSTRAINT = 19）。
    /// 用于 upsert 的幂等处理：并发写入导致唯一索引冲突时视为成功（行已存在）。
    /// </summary>
    private static bool IsUniqueConstraintViolation(DbUpdateException ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is Microsoft.Data.Sqlite.SqliteException sqlEx && sqlEx.SqliteErrorCode == 19)
                return true;
        }
        return false;
    }

    public async Task<List<LocalFriend>> GetFriendsAsync(long ownerUserId)
    {
        await using var db = await contextFactory.CreateDbContextAsync(None);
        return await db.Friends
            .AsNoTracking()
            .Where(f => f.OwnerUserId == ownerUserId)
            .Select(f => new LocalFriend
            {
                OwnerUserId = f.OwnerUserId,
                FriendId = f.FriendId,
                FriendName = f.FriendName,
                AvatarUrl = f.AvatarUrl,
                Note = f.Note,
                Status = f.Status,
                IsDeleted = f.IsDeleted,
                GroupId = f.GroupId,
                GroupName = f.GroupName,
                CreatedAt = f.CreatedAt,
                LastSynced = f.LastSynced,
                IsOnline = f.IsOnline,
                IsPinned = f.IsPinned,
                PinnedAtMs = f.PinnedAtMs,
                IsMuted = f.IsMuted,
                MutedUntilMs = f.MutedUntilMs,
                LastMessagePreview = f.LastMessagePreview
            }).ToListAsync(None);
    }

    public async Task AddFriendAsync(List<LocalFriend> friends)
    {
        if (friends.Count == 0) return;
        await using var db = await contextFactory.CreateDbContextAsync(None);

        // 批量查询已存在记录：按 OwnerUserId 分组，每组用 FriendId 列表一次性查询，
        // 将 N 次 FirstOrDefaultAsync 减少为 OwnerUserId 数量次查询（通常仅 1 次）。
        var existingMap = new Dictionary<(long OwnerUserId, long FriendId), LocalFriend>();
        foreach (var group in friends.GroupBy(f => f.OwnerUserId))
        {
            var friendIds = group.Select(g => g.FriendId).ToList();
            await foreach (var ent in db.Friends
                .Where(f => f.OwnerUserId == group.Key && friendIds.Contains(f.FriendId))
                .AsAsyncEnumerable().WithCancellation(None))
            {
                existingMap[(ent.OwnerUserId, ent.FriendId)] = ent;
            }
        }

        foreach (var f in friends)
        {
            if (existingMap.TryGetValue((f.OwnerUserId, f.FriendId), out var ent))
            {
                // 全字段同步（含备注/分组/删除标记复活），保持本地与服务端一致。
                ent.FriendName = f.FriendName;
                ent.Note = f.Note;
                ent.Status = f.Status;
                ent.AvatarUrl = f.AvatarUrl;
                ent.GroupId = f.GroupId;
                ent.GroupName = f.GroupName;
                ent.IsDeleted = f.IsDeleted;
                ent.LastSynced = DateTime.UtcNow;
            }
            else
            {
                db.Friends.Add(f);
            }
        }
        await db.SaveChangesAsync(None);
    }

    public async Task<LocalFriend?> GetFriendByIdAsync(long id)
    {
        await using var db = await contextFactory.CreateDbContextAsync(None);
        return await db.Friends.AsNoTracking().FirstOrDefaultAsync(f => f.Id == id, None);
    }

    public async Task UpdateFriendAsync(LocalFriend updatedFriend)
    {
        await using var db = await contextFactory.CreateDbContextAsync(None);
        // ExecuteUpdateAsync 直接生成 UPDATE SQL，消除先查再改的往返。
        await db.Friends
            .Where(f => f.Id == updatedFriend.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(f => f.FriendId, updatedFriend.FriendId)
                .SetProperty(f => f.OwnerUserId, updatedFriend.OwnerUserId)
                .SetProperty(f => f.FriendName, updatedFriend.FriendName)
                .SetProperty(f => f.Note, updatedFriend.Note)
                .SetProperty(f => f.Status, updatedFriend.Status)
                .SetProperty(f => f.AvatarUrl, updatedFriend.AvatarUrl)
                .SetProperty(f => f.GroupId, updatedFriend.GroupId)
                .SetProperty(f => f.GroupName, updatedFriend.GroupName)
                .SetProperty(f => f.IsDeleted, updatedFriend.IsDeleted)
                .SetProperty(f => f.IsOnline, updatedFriend.IsOnline)
                .SetProperty(f => f.LastSynced, DateTime.UtcNow), None);
    }

    /// <summary>物理删除好友（按账户 + 服务端 FriendId，修复原按本地自增 Id 删不中的问题）。</summary>
    public async Task DeleteFriendAsync(long ownerUserId, long friendId)
    {
        await using var db = await contextFactory.CreateDbContextAsync(None);
        await db.Friends
            .Where(f => f.OwnerUserId == ownerUserId && f.FriendId == friendId)
            .ExecuteDeleteAsync(None);

    }

    /// <summary>Tombstone 删除好友：标记 IsDeleted，保留行以支撑历史会话与已读回执。</summary>
    public async Task MarkFriendDeletedAsync(long ownerUserId, long friendId)
    {
        await using var db = await contextFactory.CreateDbContextAsync(None);
        await db.Friends
            .Where(f => f.OwnerUserId == ownerUserId && f.FriendId == friendId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(f => f.IsDeleted, true)
                .SetProperty(f => f.LastSynced, DateTime.UtcNow), None);
    }

    // ---- 用户信息 ----

    /// <summary>
    /// 保存或更新本地用户信息（登录时全量写入服务端返回数据）。
    /// </summary>
    public async Task SaveUserAsync(LocalUser user)
    {
        await using var db = await contextFactory.CreateDbContextAsync(None);
        var existing = await db.Users.FirstOrDefaultAsync(u => u.UserId == user.UserId, None);
        if (existing is not null)
        {
            existing.Username         = user.Username;
            existing.AvatarUrl        = user.AvatarUrl;
            existing.Email            = user.Email;
            existing.Signature        = user.Signature;
            existing.Gender           = user.Gender;
            existing.Region           = user.Region;
            existing.Status           = user.Status;
            existing.PreviousLoginDate = user.PreviousLoginDate;
            existing.LastLoginTime    = user.LastLoginTime;
        }
        else
        {
            await db.Users.AddAsync(user, None);
        }
        await db.SaveChangesAsync(None);
    }

    public async Task<LocalUser?> GetUserAsync(long userId)
    {
        await using var db = await contextFactory.CreateDbContextAsync(None);
        return await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.UserId == userId, None);
    }

    // ---- Token ----

    public async Task SaveTokenAsync(AuthToken token)
    {
        await using var db = await contextFactory.CreateDbContextAsync(None);
        var oldToken = await db.Tokens.FirstOrDefaultAsync(None);
        if (oldToken is not null)
        {
            oldToken.UserId = token.UserId;
            oldToken.AccessToken = token.AccessToken;
            oldToken.RefreshToken = token.RefreshToken;
            oldToken.AccessTokenExpires = token.AccessTokenExpires;
            oldToken.RefreshTokenExpires = token.RefreshTokenExpires;
        }
        else
        {
            await db.Tokens.AddAsync(token, None);
        }
        await db.SaveChangesAsync(None);
    }

    public async Task<Token?> GetAccessTokenAsync()
    {
        await using var db = await contextFactory.CreateDbContextAsync(None);
        return await db.Tokens
            .AsNoTracking()
            .Select(t => new Token
            {
                TokenExpires = t.AccessTokenExpires,
                TokenValue = t.AccessToken
            })
            .FirstOrDefaultAsync(None);
    }

    public async Task<int> UpdateTokenAsync(AuthToken token)
    {
        await using var db = await contextFactory.CreateDbContextAsync(None);
        return await db.Tokens
            .Where(f => f.UserId == token.UserId)
            .ExecuteUpdateAsync(t
                => t.SetProperty(authToken => authToken.AccessToken, token.AccessToken)
                    .SetProperty(authToken => authToken.RefreshToken, token.RefreshToken)
                    .SetProperty(authToken => authToken.AccessTokenExpires, token.AccessTokenExpires)
                    .SetProperty(authToken => authToken.RefreshTokenExpires, token.RefreshTokenExpires), None);
    }

    public async Task<AuthToken?> GetTokenAsync()
    {
        await using var db = await contextFactory.CreateDbContextAsync(None);
        return await db.Tokens.AsNoTracking().FirstOrDefaultAsync(None);
    }

    public async Task DeleteTokenAsync()
    {
        await using var db = await contextFactory.CreateDbContextAsync(None);
        await db.Tokens.ExecuteDeleteAsync(None);
    }

    // ---- 服务器信息 ----

    /// <summary>
    /// 保存服务器信息：若已存在相同地址+端口的记录则更新，否则新增，避免重复。
    /// </summary>
    public async Task SaveServerInfoAsync(ServerEndpoint serverInfo)
    {
        await using var db = await contextFactory.CreateDbContextAsync(None);
        var existing = await db.Servers
            .FirstOrDefaultAsync(s => s.ServerIpAddress == serverInfo.ServerIpAddress
                                   && s.ServerPort == serverInfo.ServerPort, None);
        if (existing is not null)
        {
            existing.ServerName = serverInfo.ServerName;
            existing.IsPrimary = serverInfo.IsPrimary;
            existing.LastConnected = DateTime.UtcNow;
        }
        else
        {
            serverInfo.LastConnected = DateTime.UtcNow;
            await db.Servers.AddAsync(serverInfo, None);
        }
        await db.SaveChangesAsync(None);
    }

    public async Task<ServerEndpoint?> GetServerInfoAsync()
    {
        await using var db = await contextFactory.CreateDbContextAsync(None);
        return await db.Servers.AsNoTracking().FirstOrDefaultAsync(None);
    }

    public async Task DeleteServerInfoAsync()
    {
        await using var db = await contextFactory.CreateDbContextAsync(None);
        await db.Servers.ExecuteDeleteAsync(None);
    }

    // ---- 会话（持久化聊天系统）----

    public async Task<List<LocalConversation>> GetConversationsAsync(long ownerUserId)
    {
        await using var db = await contextFactory.CreateDbContextAsync(None);
        return await db.Conversations
            .AsNoTracking()
            .Where(c => c.OwnerUserId == ownerUserId)
            .ToListAsync(None);
    }

    public async Task<LocalConversation?> GetConversationAsync(long ownerUserId, string conversationId)
    {
        await using var db = await contextFactory.CreateDbContextAsync(None);
        return await db.Conversations
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.OwnerUserId == ownerUserId && c.ConversationId == conversationId, None);
    }

    public Task UpsertConversationAsync(LocalConversation conversation) => WriteAsync(() => UpsertConversationAsyncImpl(conversation));

    private async Task UpsertConversationAsyncImpl(LocalConversation conversation)
    {
        await using var db = await contextFactory.CreateDbContextAsync(None);
        var existing = await db.Conversations
            .FirstOrDefaultAsync(c => c.OwnerUserId == conversation.OwnerUserId
                                   && c.ConversationId == conversation.ConversationId, None);
        if (existing is not null)
        {
            existing.Type               = conversation.Type;
            existing.PeerUserId         = conversation.PeerUserId;
            existing.LastMessageId      = conversation.LastMessageId;
            existing.LastMessagePreview = conversation.LastMessagePreview;
            existing.LastMessageAtMs    = conversation.LastMessageAtMs;
            existing.LastSenderUserId   = conversation.LastSenderUserId;
            existing.UnreadCount        = conversation.UnreadCount;
            existing.LastReadMessageId  = conversation.LastReadMessageId;
            existing.LastReadAtMs       = conversation.LastReadAtMs;
            existing.IsPinned           = conversation.IsPinned;
            existing.PinnedAtMs         = conversation.PinnedAtMs;
            existing.IsMuted            = conversation.IsMuted;
            existing.MutedUntilMs       = conversation.MutedUntilMs;
            existing.Draft              = conversation.Draft;
            existing.LastSynced         = conversation.LastSynced;
        }
        else
        {
            await db.Conversations.AddAsync(conversation, None);
        }
        await db.SaveChangesAsync(None);
    }

    /// <summary>仅更新会话草稿，避免切换会话时全字段 Upsert 的开销。</summary>
    public Task UpdateConversationDraftAsync(long ownerUserId, string conversationId, string? draft) => WriteAsync(() => UpdateConversationDraftAsyncImpl(ownerUserId, conversationId, draft));

    private async Task UpdateConversationDraftAsyncImpl(long ownerUserId, string conversationId, string? draft)
    {
        await using var db = await contextFactory.CreateDbContextAsync(None);
        await db.Conversations
            .Where(c => c.OwnerUserId == ownerUserId && c.ConversationId == conversationId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Draft, draft), None);
    }

    public Task<bool> UpdateConversationDraftAsync(
        long ownerUserId, string conversationId, string? draft, string? draftState,
        long updatedAtMs, int revision) =>
        WriteAsync(() => UpdateConversationDraftAsyncImpl(ownerUserId, conversationId, draft, draftState, updatedAtMs, revision));

    private async Task<bool> UpdateConversationDraftAsyncImpl(
        long ownerUserId, string conversationId, string? draft, string? draftState,
        long updatedAtMs, int revision)
    {
        await using var db = await contextFactory.CreateDbContextAsync(None);
        // 乐观并发：仅当数据库版本旧于本次写入时更新（null 视为初始状态），新窗口的草稿不会被旧窗口覆盖。
        var rows = await db.Conversations
            .Where(c => c.OwnerUserId == ownerUserId
                     && c.ConversationId == conversationId
                     && (c.DraftUpdatedAtMs == null || c.DraftUpdatedAtMs < updatedAtMs))
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.Draft, draft)
                .SetProperty(c => c.DraftState, draftState)
                .SetProperty(c => c.DraftUpdatedAtMs, updatedAtMs)
                .SetProperty(c => c.DraftRevision, revision), None);
        return rows > 0;
    }

    public async Task DeleteConversationAsync(long ownerUserId, string conversationId)
    {
        await using var db = await contextFactory.CreateDbContextAsync(None);
        await db.Conversations
            .Where(c => c.OwnerUserId == ownerUserId && c.ConversationId == conversationId)
            .ExecuteDeleteAsync(None);
    }

    // ---- 消息----

    public async Task<List<LocalMessage>> GetMessagesAsync(long ownerUserId, string conversationId, int limit = 100, long? beforeReceivedAtMs = null, string? beforeMessageId = null)
    {
        await using var db = await contextFactory.CreateDbContextAsync(None);
        var query = db.Messages
            .AsNoTracking()
            .Where(m => m.OwnerUserId == ownerUserId && m.ConversationId == conversationId);
        if (beforeReceivedAtMs is long beforeMs)
        {
            if (!string.IsNullOrEmpty(beforeMessageId))
            {
                // 复合游标：(ReceivedAtMs, MessageId) 字典序严格小于游标，避免同时间戳分页遗漏/重复。
                query = query.Where(m => m.ReceivedAtMs < beforeMs
                                      || (m.ReceivedAtMs == beforeMs && string.Compare(m.MessageId, beforeMessageId) < 0));
            }
            else
            {
                query = query.Where(m => m.ReceivedAtMs < beforeMs);
            }
        }
        // 游标分页：按 (ReceivedAtMs, MessageId) 倒序取一页，再反转为时间正序，便于 UI 直接追加。
        var page = await query
            .OrderByDescending(m => m.ReceivedAtMs)
            .ThenByDescending(m => m.MessageId)
            .Take(limit)
            .ToListAsync(None);
        page.Reverse();
        return page;
    }

    public async Task<LocalMessage?> GetMessageByServerIdAsync(long ownerUserId, string messageId)
    {
        await using var db = await contextFactory.CreateDbContextAsync(None);
        return await db.Messages
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.OwnerUserId == ownerUserId && m.MessageId == messageId, None);
    }

    public async Task<LocalMessage?> GetMessageByClientIdAsync(long ownerUserId, string clientMessageId)
    {
        await using var db = await contextFactory.CreateDbContextAsync(None);
        return await db.Messages
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.OwnerUserId == ownerUserId && m.ClientMessageId == clientMessageId, None);
    }

    public Task UpsertMessageAsync(LocalMessage message) => WriteAsync(() => UpsertMessageAsyncImpl(message));

    private async Task UpsertMessageAsyncImpl(LocalMessage message)
    {
        await using var db = await contextFactory.CreateDbContextAsync(None);
        // 空串归一化为 NULL：唯一索引下 NULL 互异、空串会冲突。
        message.MessageId = string.IsNullOrEmpty(message.MessageId) ? null : message.MessageId;
        message.ClientMessageId = string.IsNullOrEmpty(message.ClientMessageId) ? null : message.ClientMessageId;

        LocalMessage? existing = null;
        if (message.MessageId is not null)
        {
            existing = await db.Messages
                .FirstOrDefaultAsync(m => m.OwnerUserId == message.OwnerUserId
                                       && m.MessageId == message.MessageId, None);
        }
        if (existing is null && message.ClientMessageId is not null)
        {
            existing = await db.Messages
                .FirstOrDefaultAsync(m => m.OwnerUserId == message.OwnerUserId
                                       && m.ClientMessageId == message.ClientMessageId, None);
        }
        if (existing is not null)
        {
            existing.ReceivedAtMs    = message.ReceivedAtMs;
            existing.DeliveredAtMs   = message.DeliveredAtMs;
            existing.ReadAtMs        = message.ReadAtMs;
            existing.RecalledAtMs    = message.RecalledAtMs;
            existing.AttachmentsJson = message.AttachmentsJson;
            existing.FailureReason   = message.FailureReason;
            existing.UpdatedAt       = message.UpdatedAt;
            // 撤回具有最高优先级，不可被历史同步覆盖。
            if (existing.Status != MessageStatus.Recalled)
                existing.Status = message.Status;
            // 编辑版本单调递增：仅当入站版本严格更新时才覆盖正文/版本/编辑时间。
            if (message.EditVersion > existing.EditVersion)
            {
                existing.EditVersion = message.EditVersion;
                existing.EditedAtMs  = message.EditedAtMs;
                existing.Content     = message.Content;
            }
            // 服务端确认后回填 MessageId（outbox 阶段仅写入 ClientMessageId）。
            if (string.IsNullOrEmpty(existing.MessageId) && message.MessageId is not null)
            {
                existing.MessageId = message.MessageId;
            }
        }
        else
        {
            await db.Messages.AddAsync(message, None);
        }
        // 唯一索引冲突视为幂等成功（行已存在）。
        try
        {
            await db.SaveChangesAsync(None);
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            // 幂等：并发写入导致唯一索引冲突，行已存在，忽略。
        }
    }

    public Task UpdateMessageStatusAsync(long ownerUserId, string? messageId, string? clientMessageId, MessageStatus status, string? failureReason = null, string? ackServerMessageId = null) => WriteAsync(() => UpdateMessageStatusAsyncImpl(ownerUserId, messageId, clientMessageId, status, failureReason, ackServerMessageId));

    private async Task UpdateMessageStatusAsyncImpl(long ownerUserId, string? messageId, string? clientMessageId, MessageStatus status, string? failureReason = null, string? ackServerMessageId = null)
    {
        await using var db = await contextFactory.CreateDbContextAsync(None);
        var query = db.Messages.Where(m => m.OwnerUserId == ownerUserId);
        if (!string.IsNullOrEmpty(messageId))
        {
            query = query.Where(m => m.MessageId == messageId);
        }
        else if (!string.IsNullOrEmpty(clientMessageId))
        {
            query = query.Where(m => m.ClientMessageId == clientMessageId);
        }
        else
        {
            return;
        }
        if (!string.IsNullOrEmpty(ackServerMessageId))
        {
            await query.ExecuteUpdateAsync(m
                => m.SetProperty(x => x.Status, status)
                    .SetProperty(x => x.FailureReason, failureReason)
                    .SetProperty(x => x.UpdatedAt, DateTime.UtcNow)
                    .SetProperty(x => x.MessageId, ackServerMessageId), None);
        }
        else
        {
            await query.ExecuteUpdateAsync(m
                => m.SetProperty(x => x.Status, status)
                    .SetProperty(x => x.FailureReason, failureReason)
                    .SetProperty(x => x.UpdatedAt, DateTime.UtcNow), None);
        }
    }

    public Task<MessageMutationResult> MarkMessageRecalledAsync(long ownerUserId, string messageId, long recalledAtMs) => WriteAsync(() => MarkMessageRecalledAsyncImpl(ownerUserId, messageId, recalledAtMs));

    private async Task<MessageMutationResult> MarkMessageRecalledAsyncImpl(long ownerUserId, string messageId, long recalledAtMs)
    {
        await using var db = await contextFactory.CreateDbContextAsync(None);
        var existing = await db.Messages.FirstOrDefaultAsync(m => m.OwnerUserId == ownerUserId && m.MessageId == messageId, None);
        if (existing is null)
            return MessageMutationResult.MessageMissing;

        // 撤回具有最高优先级：仅当尚未撤回，或入站 RecalledAtMs 更新时才写入。
        if (existing.RecalledAtMs is > 0 && existing.RecalledAtMs >= recalledAtMs)
            return MessageMutationResult.IgnoredStale;

        existing.Status = MessageStatus.Recalled;
        existing.RecalledAtMs = recalledAtMs;
        existing.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(None);
        return MessageMutationResult.Applied;
    }

    public Task<MessageMutationResult> ApplyMessageEditAsync(long ownerUserId, string messageId, string content, int editVersion, long editedAtMs) => WriteAsync(() => ApplyMessageEditAsyncImpl(ownerUserId, messageId, content, editVersion, editedAtMs));

    private async Task<MessageMutationResult> ApplyMessageEditAsyncImpl(long ownerUserId, string messageId, string content, int editVersion, long editedAtMs)
    {
        await using var db = await contextFactory.CreateDbContextAsync(None);
        var existing = await db.Messages.FirstOrDefaultAsync(m => m.OwnerUserId == ownerUserId && m.MessageId == messageId, None);
        if (existing is null)
            return MessageMutationResult.MessageMissing;
        if (existing.Status == MessageStatus.Recalled)
            return MessageMutationResult.AlreadyRecalled;

        // 编辑版本单调递增：仅当入站 EditVersion 严格大于已存版本时才应用。
        if (existing.EditVersion >= editVersion)
            return MessageMutationResult.IgnoredStale;

        existing.Content = content;
        existing.EditVersion = editVersion;
        existing.EditedAtMs = editedAtMs;
        existing.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(None);
        return MessageMutationResult.Applied;
    }

    public async Task<List<LocalMessage>> GetMessagesAfterAsync(long ownerUserId, string conversationId, long afterReceivedAtMs, int limit = 100)
    {
        await using var db = await contextFactory.CreateDbContextAsync(None);
        return await db.Messages
            .AsNoTracking()
            .Where(m => m.OwnerUserId == ownerUserId
                     && m.ConversationId == conversationId
                     && m.ReceivedAtMs > afterReceivedAtMs)
            .OrderBy(m => m.ReceivedAtMs)
            .Take(limit)
            .ToListAsync(None);
    }

    /// <summary>
    /// 事务性应用入站消息：在单个 DbContext + 单个事务内完成
    /// 消息 upsert + 附件批量 upsert + 会话摘要原子更新 + 未读数递增，保证原子性。
    /// </summary>
    public Task ApplyIncomingMessageAsync(LocalMessage message, List<LocalAttachment> attachments, LocalConversation? conversationUpdate) => WriteAsync(() => ApplyIncomingMessageAsyncImpl(message, attachments, conversationUpdate));

    private async Task ApplyIncomingMessageAsyncImpl(LocalMessage message, List<LocalAttachment> attachments, LocalConversation? conversationUpdate)
    {
        await using var db = await contextFactory.CreateDbContextAsync(None);
        await using var transaction = await db.Database.BeginTransactionAsync(None);

        // ---- Upsert LocalMessage（镜像 UpsertMessageAsync：先 OwnerUserId + MessageId，回退 OwnerUserId + ClientMessageId）----
        // 空串归一化为 NULL：唯一索引下 NULL 互异、空串会冲突。
        message.MessageId = string.IsNullOrEmpty(message.MessageId) ? null : message.MessageId;
        message.ClientMessageId = string.IsNullOrEmpty(message.ClientMessageId) ? null : message.ClientMessageId;

        LocalMessage? existingMessage = null;
        if (message.MessageId is not null)
        {
            existingMessage = await db.Messages
                .FirstOrDefaultAsync(m => m.OwnerUserId == message.OwnerUserId
                                       && m.MessageId == message.MessageId, None);
        }
        if (existingMessage is null && message.ClientMessageId is not null)
        {
            existingMessage = await db.Messages
                .FirstOrDefaultAsync(m => m.OwnerUserId == message.OwnerUserId
                                       && m.ClientMessageId == message.ClientMessageId, None);
        }
        if (existingMessage is not null)
        {
            existingMessage.ReceivedAtMs    = message.ReceivedAtMs;
            existingMessage.DeliveredAtMs   = message.DeliveredAtMs;
            existingMessage.ReadAtMs        = message.ReadAtMs;
            existingMessage.RecalledAtMs    = message.RecalledAtMs;
            existingMessage.AttachmentsJson = message.AttachmentsJson;
            existingMessage.FailureReason   = message.FailureReason;
            existingMessage.UpdatedAt       = message.UpdatedAt;
            // 撤回具有最高优先级，不可被历史同步覆盖。
            if (existingMessage.Status != MessageStatus.Recalled)
                existingMessage.Status = message.Status;
            // 编辑版本单调递增：仅当入站版本严格更新时才覆盖正文/版本/编辑时间。
            if (message.EditVersion > existingMessage.EditVersion)
            {
                existingMessage.EditVersion = message.EditVersion;
                existingMessage.EditedAtMs  = message.EditedAtMs;
                existingMessage.Content     = message.Content;
            }
            // 服务端确认后回填 MessageId（outbox 阶段仅写入 ClientMessageId）。
            if (string.IsNullOrEmpty(existingMessage.MessageId) && message.MessageId is not null)
            {
                existingMessage.MessageId = message.MessageId;
            }
        }
        else
        {
            await db.Messages.AddAsync(message, None);
        }

        // ---- Upsert LocalAttachment 批量（镜像 UpsertAttachmentAsync：按 OwnerUserId + AttachmentId，回退 ClientAttachmentId）----
        if (attachments is { Count: > 0 })
        {
            foreach (var attachment in attachments)
            {
                LocalAttachment? existingAttachment = null;
                if (!string.IsNullOrEmpty(attachment.AttachmentId))
                {
                    existingAttachment = await db.Attachments
                        .FirstOrDefaultAsync(a => a.OwnerUserId == attachment.OwnerUserId
                                              && a.AttachmentId == attachment.AttachmentId, None);
                }
                if (existingAttachment is null && !string.IsNullOrEmpty(attachment.ClientAttachmentId))
                {
                    existingAttachment = await db.Attachments
                        .FirstOrDefaultAsync(a => a.OwnerUserId == attachment.OwnerUserId
                                              && a.ClientAttachmentId == attachment.ClientAttachmentId, None);
                }
                if (existingAttachment is not null)
                {
                    existingAttachment.AttachmentId       = attachment.AttachmentId ?? existingAttachment.AttachmentId;
                    existingAttachment.ClientAttachmentId = attachment.ClientAttachmentId ?? existingAttachment.ClientAttachmentId;
                    existingAttachment.MessageId          = attachment.MessageId ?? existingAttachment.MessageId;
                    existingAttachment.ConversationId     = attachment.ConversationId ?? existingAttachment.ConversationId;
                    existingAttachment.FileName           = attachment.FileName ?? existingAttachment.FileName;
                    existingAttachment.ContentType        = attachment.ContentType;
                    existingAttachment.SizeBytes          = attachment.SizeBytes;
                    existingAttachment.Sha256             = attachment.Sha256 ?? existingAttachment.Sha256;
                    existingAttachment.DownloadPath       = attachment.DownloadPath ?? existingAttachment.DownloadPath;
                    existingAttachment.ObjectKey          = attachment.ObjectKey ?? existingAttachment.ObjectKey;
                    existingAttachment.ThumbnailPath      = attachment.ThumbnailPath ?? existingAttachment.ThumbnailPath;
                    existingAttachment.LocalCachePath     = attachment.LocalCachePath ?? existingAttachment.LocalCachePath;
                    existingAttachment.LocalThumbnailPath = attachment.LocalThumbnailPath ?? existingAttachment.LocalThumbnailPath;
                    existingAttachment.LocalUploadingPath = attachment.LocalUploadingPath ?? existingAttachment.LocalUploadingPath;
                    existingAttachment.RetryCount         = attachment.RetryCount;
                    existingAttachment.Status             = attachment.Status;
                    existingAttachment.FailureReason      = attachment.FailureReason ?? existingAttachment.FailureReason;
                    existingAttachment.UpdatedAt          = DateTime.UtcNow;
                }
                else
                {
                    await db.Attachments.AddAsync(attachment, None);
                }
            }
        }

        // ---- Upsert LocalConversation 会话摘要 + 未读数（ApplyIncomingMessageAsync 事务内的读-改-写逻辑）----
        if (conversationUpdate is not null)
        {
            var existingConversation = await db.Conversations
                .FirstOrDefaultAsync(c => c.OwnerUserId == conversationUpdate.OwnerUserId
                                       && c.ConversationId == conversationUpdate.ConversationId, None);
            if (existingConversation is null)
            {
                // 新建会话：conversationUpdate.UnreadCount（0 或 1）直接作为绝对值。
                await db.Conversations.AddAsync(new LocalConversation
                {
                    OwnerUserId        = conversationUpdate.OwnerUserId,
                    ConversationId     = conversationUpdate.ConversationId,
                    Type               = conversationUpdate.Type,
                    PeerUserId         = conversationUpdate.PeerUserId,
                    LastMessageId      = conversationUpdate.LastMessageId,
                    LastMessagePreview = conversationUpdate.LastMessagePreview,
                    LastMessageAtMs    = conversationUpdate.LastMessageAtMs,
                    LastSenderUserId   = conversationUpdate.LastSenderUserId,
                    UnreadCount        = conversationUpdate.UnreadCount,
                    LastReadMessageId  = null,
                    LastReadAtMs       = null,
                    IsPinned           = false,
                    PinnedAtMs         = null,
                    IsMuted            = false,
                    MutedUntilMs       = null,
                    LastSynced         = conversationUpdate.LastSynced
                }, None);
            }
            else
            {
                // 仅当新消息时间戳晚于会话最近消息时更新摘要，避免乱序消息回退最近消息。
                if (conversationUpdate.LastMessageAtMs is long newAtMs
                    && newAtMs > (existingConversation.LastMessageAtMs ?? 0))
                {
                    existingConversation.LastMessageId      = conversationUpdate.LastMessageId;
                    existingConversation.LastMessagePreview = conversationUpdate.LastMessagePreview;
                    existingConversation.LastMessageAtMs    = newAtMs;
                    existingConversation.LastSenderUserId   = conversationUpdate.LastSenderUserId;
                    // conversationUpdate.UnreadCount 为增量（发送方非自己时为 1）；
                    // 仅当会话已读水位早于该消息时才递增，避免对已读消息重复计数。
                    if (conversationUpdate.UnreadCount > 0
                        && (existingConversation.LastReadAtMs is null
                            || existingConversation.LastReadAtMs < newAtMs))
                    {
                        existingConversation.UnreadCount += conversationUpdate.UnreadCount;
                    }
                }
                existingConversation.LastSynced = DateTime.UtcNow;
            }
        }

        // 唯一索引冲突视为幂等成功（消息已存在）。
        try
        {
            await db.SaveChangesAsync(None);
            await transaction.CommitAsync(None);
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            // 幂等：并发写入导致唯一索引冲突，行已存在，忽略（事务自动回滚）。
        }
    }

    /// <summary>
    /// 批量应用历史同步条目：单 DbContext + 单事务完成
    /// 批量幂等插入（撤回最高优先级 + 编辑版本单调合并 + MessageId 回填）
    /// + 附件批量 upsert + 会话摘要单调更新（不递增未读）+ 同步水位推进。
    /// cursor 非空且单调更新时才推进水位；传 null 表示不推进。
    /// </summary>
    public Task ApplyHistoryBatchAsync(long ownerUserId, string conversationId, IReadOnlyList<MessageHistoryItemDto> items, LocalSyncCursor? cursor)
        => WriteAsync(() => ApplyHistoryBatchAsyncImpl(ownerUserId, conversationId, items, cursor));

    private async Task ApplyHistoryBatchAsyncImpl(long ownerUserId, string conversationId, IReadOnlyList<MessageHistoryItemDto> items, LocalSyncCursor? cursor)
    {
        if (items is null || items.Count == 0)
            return;

        await using var db = await contextFactory.CreateDbContextAsync(None);
        await using var transaction = await db.Database.BeginTransactionAsync(None);

        // 一次加载该会话已有消息/附件，建立内存索引，杜绝逐条往返。
        var existingMessages = await db.Messages
            .Where(m => m.OwnerUserId == ownerUserId && m.ConversationId == conversationId)
            .ToListAsync(None);
        var messagesByServerId = new Dictionary<string, LocalMessage>(StringComparer.Ordinal);
        var messagesByClientId = new Dictionary<string, LocalMessage>(StringComparer.Ordinal);
        foreach (var m in existingMessages)
        {
            if (m.MessageId is not null) messagesByServerId[m.MessageId] = m;
            if (m.ClientMessageId is not null) messagesByClientId[m.ClientMessageId] = m;
        }

        var existingAttachments = await db.Attachments
            .Where(a => a.OwnerUserId == ownerUserId && a.ConversationId == conversationId)
            .ToListAsync(None);
        var attachmentsByServerId = new Dictionary<string, LocalAttachment>(StringComparer.Ordinal);
        var attachmentsByClientId = new Dictionary<string, LocalAttachment>(StringComparer.Ordinal);
        foreach (var a in existingAttachments)
        {
            if (a.AttachmentId is not null) attachmentsByServerId[a.AttachmentId] = a;
            if (a.ClientAttachmentId is not null) attachmentsByClientId[a.ClientAttachmentId] = a;
        }

        // 批次最大时间戳：水位与会话摘要推进依据（内存单循环）。
        var maxItem = items[0];
        for (var i = 1; i < items.Count; i++)
        {
            if (items[i].ReceivedAtMs > maxItem.ReceivedAtMs)
                maxItem = items[i];
        }

        foreach (var item in items)
        {
            var messageId = string.IsNullOrWhiteSpace(item.MessageId) ? null : item.MessageId;
            var clientId = string.IsNullOrWhiteSpace(item.ClientMessageId) ? null : item.ClientMessageId;

            LocalMessage? existing = null;
            if (messageId is not null) existing = messagesByServerId.GetValueOrDefault(messageId);
            if (existing is null && clientId is not null) existing = messagesByClientId.GetValueOrDefault(clientId);

            if (existing is not null)
            {
                // 单调合并（镜像 UpsertMessageAsyncImpl）：撤回最高优先级、编辑版本单调、MessageId 回填。
                existing.ReceivedAtMs = item.ReceivedAtMs;
                existing.DeliveredAtMs = item.DeliveredAtMs;
                existing.ReadAtMs = item.ReadAtMs;
                if (existing.Status != MessageStatus.Recalled && item.RecalledAtMs is > 0)
                {
                    existing.Status = MessageStatus.Recalled;
                    existing.RecalledAtMs = item.RecalledAtMs;
                }
                if (item.EditVersion > existing.EditVersion)
                {
                    existing.EditVersion = item.EditVersion;
                    existing.EditedAtMs = item.EditedAtMs;
                    existing.Content = item.Content ?? string.Empty;
                }
                if (string.IsNullOrEmpty(existing.MessageId) && messageId is not null)
                    existing.MessageId = messageId;
                existing.AttachmentsJson = AttachmentJson.Serialize(item.Attachments);
                existing.UpdatedAt = DateTime.UtcNow;
            }
            else
            {
                var message = new LocalMessage
                {
                    OwnerUserId = ownerUserId,
                    MessageId = messageId,
                    ClientMessageId = clientId,
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
                    AttachmentsJson = AttachmentJson.Serialize(item.Attachments),
                    ReplyToMessageId = item.ReplyToMessageId,
                    ReplyToSenderUserId = item.ReplyToSenderUserId,
                    ReplyToPreview = item.ReplyToPreview,
                    ForwardedFromMessageId = item.ForwardedFromMessageId,
                    ForwardedFromSenderUserId = item.ForwardedFromSenderUserId,
                    ForwardedFromPreview = item.ForwardedFromPreview,
                    Status = item.RecalledAtMs.HasValue ? MessageStatus.Recalled : MessageStatus.Delivered,
                    FailureReason = null,
                    RetryCount = 0,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };
                await db.Messages.AddAsync(message, None);
                if (messageId is not null) messagesByServerId[messageId] = message;
                if (clientId is not null) messagesByClientId[clientId] = message;
            }

            // 附件批量 upsert（镜像 ApplyIncomingMessageAsyncImpl：先 AttachmentId，回退 ClientAttachmentId）。
            if (item.Attachments is { Count: > 0 })
            {
                foreach (var a in item.Attachments)
                {
                    var attachmentId = string.IsNullOrWhiteSpace(a.AttachmentId) ? null : a.AttachmentId;
                    LocalAttachment? existingAttachment = null;
                    if (attachmentId is not null)
                        existingAttachment = attachmentsByServerId.GetValueOrDefault(attachmentId!);
                    if (existingAttachment is null && clientId is not null)
                        existingAttachment = attachmentsByClientId.GetValueOrDefault(clientId);

                    var now = DateTime.UtcNow;
                    if (existingAttachment is not null)
                    {
                        existingAttachment.AttachmentId = attachmentId ?? existingAttachment.AttachmentId;
                        existingAttachment.MessageId = messageId ?? existingAttachment.MessageId;
                        existingAttachment.ConversationId = conversationId;
                        existingAttachment.FileName = a.FileName ?? existingAttachment.FileName;
                        existingAttachment.ContentType = a.ContentType;
                        existingAttachment.SizeBytes = a.SizeBytes;
                        existingAttachment.DownloadPath = a.DownloadApiHint ?? existingAttachment.DownloadPath;
                        existingAttachment.UpdatedAt = now;
                    }
                    else
                    {
                        var attachment = new LocalAttachment
                        {
                            OwnerUserId = ownerUserId,
                            AttachmentId = attachmentId,
                            ClientAttachmentId = clientId,
                            MessageId = messageId,
                            ConversationId = conversationId,
                            FileName = a.FileName,
                            ContentType = a.ContentType,
                            SizeBytes = a.SizeBytes,
                            DownloadPath = a.DownloadApiHint,
                            Status = AttachmentStatus.Available,
                            CreatedAt = now,
                            UpdatedAt = now
                        };
                        await db.Attachments.AddAsync(attachment, None);
                        if (attachmentId is not null) attachmentsByServerId[attachmentId] = attachment;
                    }
                }
            }
        }

        // 会话摘要单调更新（历史同步不递增未读数）。
        var conversation = await db.Conversations
            .FirstOrDefaultAsync(c => c.OwnerUserId == ownerUserId && c.ConversationId == conversationId, None);
        if (conversation is not null && maxItem.ReceivedAtMs > (conversation.LastMessageAtMs ?? 0))
        {
            conversation.LastMessageId = maxItem.MessageId;
            conversation.LastMessagePreview = TruncatePreview(maxItem.Content);
            conversation.LastMessageAtMs = maxItem.ReceivedAtMs;
            conversation.LastSenderUserId = maxItem.SenderUserId;
            conversation.LastSynced = DateTime.UtcNow;
        }

        // 同步水位推进（单调：仅当批次最大时间戳超过已存水位，由调用方保证 cursor 语义）。
        if (cursor is not null)
        {
            var existingCursor = await db.SyncCursors
                .FirstOrDefaultAsync(c => c.OwnerUserId == ownerUserId && c.ConversationId == conversationId, None);
            if (existingCursor is null)
            {
                cursor.OwnerUserId = ownerUserId;
                cursor.ConversationId = conversationId;
                cursor.UpdatedAt = DateTime.UtcNow;
                await db.SyncCursors.AddAsync(cursor, None);
            }
            else if (cursor.AfterReceivedAtMs > existingCursor.AfterReceivedAtMs)
            {
                existingCursor.AfterReceivedAtMs = cursor.AfterReceivedAtMs;
                existingCursor.AfterMessageId = cursor.AfterMessageId;
                existingCursor.UpdatedAt = DateTime.UtcNow;
            }
        }

        try
        {
            await db.SaveChangesAsync(None);
            await transaction.CommitAsync(None);
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            // 幂等：并发写入唯一索引冲突，行已存在，忽略（事务自动回滚）。
        }
    }

    private static string TruncatePreview(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return string.Empty;
        return content.Length <= 200 ? content : content[..200];
    }

    // ---- Outbox----

    public Task<long> EnqueueOutboxAsync(LocalOutboxMessage outbox) => WriteAsync(() => EnqueueOutboxAsyncImpl(outbox));

    private async Task<long> EnqueueOutboxAsyncImpl(LocalOutboxMessage outbox)
    {
        await using var db = await contextFactory.CreateDbContextAsync(None);
        await db.OutboxMessages.AddAsync(outbox, None);
        await db.SaveChangesAsync(None);
        return outbox.Id;
    }

    public async Task<LocalOutboxMessage?> GetOutboxByClientIdAsync(long ownerUserId, string clientMessageId)
    {
        await using var db = await contextFactory.CreateDbContextAsync(None);
        return await db.OutboxMessages
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.OwnerUserId == ownerUserId && o.ClientMessageId == clientMessageId, None);
    }

    public async Task<List<LocalOutboxMessage>> GetPendingOutboxAsync(long ownerUserId, int limit = 50)
    {
        await using var db = await contextFactory.CreateDbContextAsync(None);
        // Status: 0=Queued, 3=Failed；按下次重试时间（或入队时间）升序处理。
        return await db.OutboxMessages
            .AsNoTracking()
            .Where(o => o.OwnerUserId == ownerUserId && (o.Status == OutboxStatus.Queued || o.Status == OutboxStatus.Failed))
            .OrderBy(o => o.NextRetryAt ?? o.QueuedAt)
            .Take(limit)
            .ToListAsync(None);
    }

    public Task UpdateOutboxStatusAsync(long ownerUserId, string clientMessageId, OutboxStatus status, string? messageId = null, string? failureReason = null) => WriteAsync(() => UpdateOutboxStatusAsyncImpl(ownerUserId, clientMessageId, status, messageId, failureReason));

    private async Task UpdateOutboxStatusAsyncImpl(long ownerUserId, string clientMessageId, OutboxStatus status, string? messageId = null, string? failureReason = null)
    {
        await using var db = await contextFactory.CreateDbContextAsync(None);
        var query = db.OutboxMessages
            .Where(o => o.OwnerUserId == ownerUserId && o.ClientMessageId == clientMessageId);
        var now = DateTime.UtcNow;
        if (!string.IsNullOrEmpty(messageId))
        {
            await query.ExecuteUpdateAsync(o
                => o.SetProperty(x => x.Status, status)
                    .SetProperty(x => x.MessageId, messageId)
                    .SetProperty(x => x.SentAt, now)
                    .SetProperty(x => x.FailureReason, failureReason), None);
        }
        else if (!string.IsNullOrEmpty(failureReason))
        {
            await query.ExecuteUpdateAsync(o
                => o.SetProperty(x => x.Status, status)
                    .SetProperty(x => x.FailureReason, failureReason), None);
        }
        else
        {
            await query.ExecuteUpdateAsync(o
                => o.SetProperty(x => x.Status, status), None);
        }
    }

    public async Task DeleteOutboxAsync(long ownerUserId, string clientMessageId)
    {
        await using var db = await contextFactory.CreateDbContextAsync(None);
        await db.OutboxMessages
            .Where(o => o.OwnerUserId == ownerUserId && o.ClientMessageId == clientMessageId)
            .ExecuteDeleteAsync(None);
    }

    /// <inheritdoc />
    public Task<OutboxAckResult> ApplyOutboxAckAsync(long ownerUserId, string clientMessageId, bool accepted, string? serverMessageId = null, string? failureReason = null) => WriteAsync(() => ApplyOutboxAckAsyncImpl(ownerUserId, clientMessageId, accepted, serverMessageId, failureReason));

    private async Task<OutboxAckResult> ApplyOutboxAckAsyncImpl(long ownerUserId, string clientMessageId, bool accepted, string? serverMessageId = null, string? failureReason = null)
    {
        if (string.IsNullOrWhiteSpace(clientMessageId))
            return default;

        await using var db = await contextFactory.CreateDbContextAsync(None);
        await using var transaction = await db.Database.BeginTransactionAsync(None);
        var now = DateTime.UtcNow;

        var outbox = await db.OutboxMessages
            .FirstOrDefaultAsync(o => o.OwnerUserId == ownerUserId
                                   && o.ClientMessageId == clientMessageId, None);
        if (outbox is null)
        {
            // 未知 ClientMessageId：跨账户/重复 ACK，忽略。
            await transaction.CommitAsync(None);
            return default;
        }

        bool transitioned;
        if (accepted)
        {
            if (outbox.Status == OutboxStatus.Sent)
            {
                // 幂等：已 Sent 的重复 ACK 视为成功，避免误告警。
                transitioned = true;
            }
            else if (outbox.Status is OutboxStatus.Queued or OutboxStatus.Sending)
            {
                outbox.Status = OutboxStatus.Sent;
                outbox.MessageId = serverMessageId;
                outbox.SentAt = now;
                outbox.FailureReason = null;
                outbox.AttemptId = null;
                outbox.AttemptStartedAt = null;
                outbox.LeaseUntil = null;
                outbox.LastErrorCode = null;
                outbox.FailureKind = OutboxFailureKind.None;
                outbox.NextRetryAt = null;

                // LocalMessage：同一事务内条件更新（Queued/Sending → Sent）。
                await db.Messages
                    .Where(m => m.OwnerUserId == ownerUserId
                             && m.ClientMessageId == clientMessageId
                             && new[] { MessageStatus.Queued, MessageStatus.Sending }.Contains(m.Status))
                    .ExecuteUpdateAsync(m => m
                        .SetProperty(x => x.Status, MessageStatus.Sent)
                        .SetProperty(x => x.MessageId, serverMessageId)
                        .SetProperty(x => x.FailureReason, (string?)null)
                        .SetProperty(x => x.UpdatedAt, now), None);

                transitioned = true;
            }
            else
            {
                // 已 Failed/Cancelled 后再收到接受 ACK：乱序，拒绝（保留失败现场）。
                transitioned = false;
            }
        }
        else
        {
            if (outbox.Status is OutboxStatus.Queued or OutboxStatus.Sending or OutboxStatus.Failed)
            {
                outbox.Status = OutboxStatus.Failed;
                outbox.FailureReason = failureReason;

                await db.Messages
                    .Where(m => m.OwnerUserId == ownerUserId
                             && m.ClientMessageId == clientMessageId
                             && new[] { MessageStatus.Queued, MessageStatus.Sending }.Contains(m.Status))
                    .ExecuteUpdateAsync(m => m
                        .SetProperty(x => x.Status, MessageStatus.Failed)
                        .SetProperty(x => x.FailureReason, failureReason)
                        .SetProperty(x => x.UpdatedAt, now), None);

                transitioned = true;
            }
            else
            {
                // 已 Sent 后收到拒绝 ACK：乱序，忽略（服务端曾接受过）。
                transitioned = false;
            }
        }

        await db.SaveChangesAsync(None);
        await transaction.CommitAsync(None);

        var serverId = accepted ? (serverMessageId ?? outbox.MessageId) : null;
        return new OutboxAckResult(transitioned, outbox.ConversationId, serverId);
    }

    /// <inheritdoc />
    public Task<List<LocalOutboxMessage>> ClaimPendingOutboxAsync(long ownerUserId, int limit, DateTime now, DateTime leaseUntil, int maxRetryCount) => WriteAsync(() => ClaimPendingOutboxAsyncImpl(ownerUserId, limit, now, leaseUntil, maxRetryCount));

    private async Task<List<LocalOutboxMessage>> ClaimPendingOutboxAsyncImpl(long ownerUserId, int limit, DateTime now, DateTime leaseUntil, int maxRetryCount)
    {
        await using var db = await contextFactory.CreateDbContextAsync(None);

        var candidates = await db.OutboxMessages
            .Where(o => o.OwnerUserId == ownerUserId
                     && (o.Status == OutboxStatus.Queued
                         || (o.Status == OutboxStatus.Failed
                             && o.RetryCount < maxRetryCount
                             && o.FailureKind != OutboxFailureKind.Permanent))
                     && (o.NextRetryAt == null || o.NextRetryAt <= now))
            .OrderBy(o => o.NextRetryAt ?? o.QueuedAt)
            .Take(limit)
            .ToListAsync(None);

        var claimed = new List<LocalOutboxMessage>(candidates.Count);
        var attemptId = Guid.NewGuid().ToString("N");
        foreach (var candidate in candidates)
        {
            // 条件更新兜底：行状态已被并发改写时放弃认领（写入队列已串行化，此为双保险）。
            var affected = await db.OutboxMessages
                .Where(o => o.Id == candidate.Id && o.Status == candidate.Status)
                .ExecuteUpdateAsync(o => o
                    .SetProperty(x => x.Status, OutboxStatus.Sending)
                    .SetProperty(x => x.AttemptId, attemptId)
                    .SetProperty(x => x.AttemptStartedAt, now)
                    .SetProperty(x => x.LeaseUntil, leaseUntil)
                    .SetProperty(x => x.NextRetryAt, (DateTime?)null), None);
            if (affected != 1)
                continue;

            candidate.Status = OutboxStatus.Sending;
            candidate.AttemptId = attemptId;
            candidate.AttemptStartedAt = now;
            candidate.LeaseUntil = leaseUntil;
            candidate.NextRetryAt = null;
            claimed.Add(candidate);
        }
        return claimed;
    }

    /// <inheritdoc />
    public Task<int> RecoverStaleSendingAsync(long ownerUserId, DateTime now) => WriteAsync(() => RecoverStaleSendingAsyncImpl(ownerUserId, now));

    private async Task<int> RecoverStaleSendingAsyncImpl(long ownerUserId, DateTime now)
    {
        await using var db = await contextFactory.CreateDbContextAsync(None);
        return await db.OutboxMessages
            .Where(o => o.OwnerUserId == ownerUserId
                     && o.Status == OutboxStatus.Sending
                     && o.LeaseUntil != null
                     && o.LeaseUntil < now)
            .ExecuteUpdateAsync(o => o
                .SetProperty(x => x.Status, OutboxStatus.Queued)
                .SetProperty(x => x.AttemptId, (string?)null)
                .SetProperty(x => x.AttemptStartedAt, (DateTime?)null)
                .SetProperty(x => x.LeaseUntil, (DateTime?)null), None);
    }

    /// <inheritdoc />
    public Task<bool> MarkOutboxFailureAsync(long ownerUserId, string clientMessageId, string? errorCode, string? failureReason, OutboxFailureKind failureKind, DateTime? nextRetryAt) => WriteAsync(() => MarkOutboxFailureAsyncImpl(ownerUserId, clientMessageId, errorCode, failureReason, failureKind, nextRetryAt));

    private async Task<bool> MarkOutboxFailureAsyncImpl(long ownerUserId, string clientMessageId, string? errorCode, string? failureReason, OutboxFailureKind failureKind, DateTime? nextRetryAt)
    {
        await using var db = await contextFactory.CreateDbContextAsync(None);
        await using var transaction = await db.Database.BeginTransactionAsync(None);
        var now = DateTime.UtcNow;

        var outbox = await db.OutboxMessages
            .FirstOrDefaultAsync(o => o.OwnerUserId == ownerUserId && o.ClientMessageId == clientMessageId, None);
        if (outbox is null || outbox.Status is not (OutboxStatus.Queued or OutboxStatus.Sending or OutboxStatus.Failed))
        {
            await transaction.CommitAsync(None);
            return false;
        }

        outbox.Status = OutboxStatus.Failed;
        outbox.FailureReason = failureReason;
        outbox.LastErrorCode = errorCode;
        outbox.FailureKind = failureKind;
        outbox.RetryCount += 1;
        outbox.NextRetryAt = nextRetryAt;
        outbox.AttemptId = null;
        outbox.AttemptStartedAt = null;
        outbox.LeaseUntil = null;

        await db.Messages
            .Where(m => m.OwnerUserId == ownerUserId
                     && m.ClientMessageId == clientMessageId
                     && new[] { MessageStatus.Queued, MessageStatus.Sending }.Contains(m.Status))
            .ExecuteUpdateAsync(m => m
                .SetProperty(x => x.Status, MessageStatus.Failed)
                .SetProperty(x => x.FailureReason, failureReason)
                .SetProperty(x => x.UpdatedAt, now), None);

        await db.SaveChangesAsync(None);
        await transaction.CommitAsync(None);
        return true;
    }

    /// <inheritdoc />
    public Task<bool> RetryOutboxAsync(long ownerUserId, string clientMessageId) => WriteAsync(() => RetryOutboxAsyncImpl(ownerUserId, clientMessageId));

    private async Task<bool> RetryOutboxAsyncImpl(long ownerUserId, string clientMessageId)
    {
        await using var db = await contextFactory.CreateDbContextAsync(None);
        var now = DateTime.UtcNow;

        var affected = await db.OutboxMessages
            .Where(o => o.OwnerUserId == ownerUserId
                     && o.ClientMessageId == clientMessageId
                     && new[] { OutboxStatus.Failed, OutboxStatus.Cancelled }.Contains(o.Status))
            .ExecuteUpdateAsync(o => o
                .SetProperty(x => x.Status, OutboxStatus.Queued)
                .SetProperty(x => x.FailureReason, (string?)null)
                .SetProperty(x => x.LastErrorCode, (string?)null)
                .SetProperty(x => x.FailureKind, OutboxFailureKind.None)
                .SetProperty(x => x.NextRetryAt, (DateTime?)null)
                .SetProperty(x => x.AttemptId, (string?)null)
                .SetProperty(x => x.AttemptStartedAt, (DateTime?)null)
                .SetProperty(x => x.LeaseUntil, (DateTime?)null), None);

        if (affected == 0)
            return false;

        await db.Messages
            .Where(m => m.OwnerUserId == ownerUserId && m.ClientMessageId == clientMessageId)
            .ExecuteUpdateAsync(m => m
                .SetProperty(x => x.Status, MessageStatus.Queued)
                .SetProperty(x => x.FailureReason, (string?)null)
                .SetProperty(x => x.UpdatedAt, now), None);
        return true;
    }

    /// <inheritdoc />
    public Task<bool> CancelOutboxAsync(long ownerUserId, string clientMessageId) => WriteAsync(() => CancelOutboxAsyncImpl(ownerUserId, clientMessageId));

    private async Task<bool> CancelOutboxAsyncImpl(long ownerUserId, string clientMessageId)
    {
        await using var db = await contextFactory.CreateDbContextAsync(None);
        var now = DateTime.UtcNow;

        var affected = await db.OutboxMessages
            .Where(o => o.OwnerUserId == ownerUserId
                     && o.ClientMessageId == clientMessageId
                     && new[] { OutboxStatus.Queued, OutboxStatus.Sending }.Contains(o.Status))
            .ExecuteUpdateAsync(o => o
                .SetProperty(x => x.Status, OutboxStatus.Cancelled)
                .SetProperty(x => x.AttemptId, (string?)null)
                .SetProperty(x => x.AttemptStartedAt, (DateTime?)null)
                .SetProperty(x => x.LeaseUntil, (DateTime?)null), None);

        if (affected == 0)
            return false;

        await db.Messages
            .Where(m => m.OwnerUserId == ownerUserId
                     && m.ClientMessageId == clientMessageId
                     && new[] { MessageStatus.Queued, MessageStatus.Sending }.Contains(m.Status))
            .ExecuteUpdateAsync(m => m
                .SetProperty(x => x.Status, MessageStatus.Failed)
                .SetProperty(x => x.FailureReason, "已取消发送")
                .SetProperty(x => x.UpdatedAt, now), None);
        return true;
    }

    /// <inheritdoc />
    public Task<int> CleanupOutboxAsync(long ownerUserId, DateTime olderThan) => WriteAsync(() => CleanupOutboxAsyncImpl(ownerUserId, olderThan));

    private async Task<int> CleanupOutboxAsyncImpl(long ownerUserId, DateTime olderThan)
    {
        await using var db = await contextFactory.CreateDbContextAsync(None);
        return await db.OutboxMessages
            .Where(o => o.OwnerUserId == ownerUserId
                     && new[] { OutboxStatus.Sent, OutboxStatus.Cancelled }.Contains(o.Status)
                     && o.QueuedAt < olderThan)
            .ExecuteDeleteAsync(None);
    }

    /// <summary>
    /// 事务性写入 Outbox + LocalMessage（事务化 Outbox）。
    /// 在单个 DbContext + 单个事务内完成两表 upsert，保证原子性。
    /// </summary>
    public Task EnqueueOutboxWithMessageAsync(LocalOutboxMessage outbox, LocalMessage message) => WriteAsync(() => EnqueueOutboxWithMessageAsyncImpl(outbox, message));

    private async Task EnqueueOutboxWithMessageAsyncImpl(LocalOutboxMessage outbox, LocalMessage message)
    {
        await using var db = await contextFactory.CreateDbContextAsync(None);
        await using var transaction = await db.Database.BeginTransactionAsync(None);

        // Upsert LocalOutboxMessage（按 OwnerUserId + ClientMessageId）
        var existingOutbox = await db.OutboxMessages
            .FirstOrDefaultAsync(o => o.OwnerUserId == outbox.OwnerUserId
                                   && o.ClientMessageId == outbox.ClientMessageId, None);
        if (existingOutbox is not null)
        {
            existingOutbox.MessageId                 = outbox.MessageId;
            existingOutbox.ConversationId            = outbox.ConversationId;
            existingOutbox.TargetUserId              = outbox.TargetUserId;
            existingOutbox.Content                   = outbox.Content;
            existingOutbox.AttachmentIdsJson         = outbox.AttachmentIdsJson;
            existingOutbox.ReplyToMessageId          = outbox.ReplyToMessageId;
            existingOutbox.ReplyToSenderUserId       = outbox.ReplyToSenderUserId;
            existingOutbox.ReplyToPreview            = outbox.ReplyToPreview;
            existingOutbox.ForwardedFromMessageId    = outbox.ForwardedFromMessageId;
            existingOutbox.ForwardedFromSenderUserId = outbox.ForwardedFromSenderUserId;
            existingOutbox.ForwardedFromPreview      = outbox.ForwardedFromPreview;
            existingOutbox.Status                    = outbox.Status;
            existingOutbox.FailureReason             = outbox.FailureReason;
            existingOutbox.QueuedAt                  = outbox.QueuedAt;
            existingOutbox.SentAt                    = outbox.SentAt;
            existingOutbox.NextRetryAt               = outbox.NextRetryAt;
            existingOutbox.AttemptId                 = outbox.AttemptId;
            existingOutbox.AttemptStartedAt          = outbox.AttemptStartedAt;
            existingOutbox.LeaseUntil                = outbox.LeaseUntil;
            existingOutbox.LastErrorCode             = outbox.LastErrorCode;
            existingOutbox.FailureKind               = outbox.FailureKind;
        }
        else
        {
            await db.OutboxMessages.AddAsync(outbox, None);
        }

        // Upsert LocalMessage（先按 OwnerUserId + MessageId，回退 OwnerUserId + ClientMessageId）
        LocalMessage? existingMessage = null;
        if (!string.IsNullOrEmpty(message.MessageId))
        {
            existingMessage = await db.Messages
                .FirstOrDefaultAsync(m => m.OwnerUserId == message.OwnerUserId
                                       && m.MessageId == message.MessageId, None);
        }
        if (existingMessage is null && !string.IsNullOrEmpty(message.ClientMessageId))
        {
            existingMessage = await db.Messages
                .FirstOrDefaultAsync(m => m.OwnerUserId == message.OwnerUserId
                                       && m.ClientMessageId == message.ClientMessageId, None);
        }
        if (existingMessage is not null)
        {
            existingMessage.Content         = message.Content;
            existingMessage.ReceivedAtMs    = message.ReceivedAtMs;
            existingMessage.DeliveredAtMs   = message.DeliveredAtMs;
            existingMessage.ReadAtMs        = message.ReadAtMs;
            existingMessage.RecalledAtMs    = message.RecalledAtMs;
            existingMessage.EditVersion     = message.EditVersion;
            existingMessage.EditedAtMs      = message.EditedAtMs;
            existingMessage.AttachmentsJson = message.AttachmentsJson;
            existingMessage.Status          = message.Status;
            existingMessage.FailureReason   = message.FailureReason;
            existingMessage.UpdatedAt       = message.UpdatedAt;
            // 服务端确认后回填 MessageId（outbox 阶段仅写入 ClientMessageId）。
            if (string.IsNullOrEmpty(existingMessage.MessageId) && !string.IsNullOrEmpty(message.MessageId))
                existingMessage.MessageId = message.MessageId;
        }
        else
        {
            await db.Messages.AddAsync(message, None);
        }

        await db.SaveChangesAsync(None);
        await transaction.CommitAsync(None);
    }

    /// <summary>
    /// 更新 Outbox 状态并推进重试元数据。
    /// 仅当 status == Failed 时递增 RetryCount 并设置 NextRetryAt（指数退避 + jitter）。
    /// </summary>
    public Task UpdateOutboxStatusWithRetryAsync(long ownerUserId, string clientMessageId, OutboxStatus status, string? messageId = null, string? failureReason = null) => WriteAsync(() => UpdateOutboxStatusWithRetryAsyncImpl(ownerUserId, clientMessageId, status, messageId, failureReason));

    private async Task UpdateOutboxStatusWithRetryAsyncImpl(long ownerUserId, string clientMessageId, OutboxStatus status, string? messageId = null, string? failureReason = null)
    {
        await using var db = await contextFactory.CreateDbContextAsync(None);
        var existing = await db.OutboxMessages
            .FirstOrDefaultAsync(o => o.OwnerUserId == ownerUserId
                                   && o.ClientMessageId == clientMessageId, None);
        if (existing is null)
            return;

        var now = DateTime.UtcNow;
        existing.Status        = status;
        existing.FailureReason = failureReason;
        if (!string.IsNullOrEmpty(messageId))
            existing.MessageId = messageId;
        if (status != OutboxStatus.Failed)
            existing.SentAt = now;

        if (status == OutboxStatus.Failed)
        {
            existing.RetryCount++;
            // 指数退避：min(2^retryCount * 2, MaxBackoffSec) 秒 + 0~2s 随机 jitter
            const int maxBackoffSec = 300;
            var delaySec = Math.Min(Math.Pow(2, existing.RetryCount) * 2, maxBackoffSec);
            var jitterSec = Random.Shared.NextDouble() * 2;
            existing.NextRetryAt = now.AddSeconds(delaySec + jitterSec);
        }
        else if (status == OutboxStatus.Sent)
        {
            existing.NextRetryAt = null;
        }

        await db.SaveChangesAsync(None);
    }

    // ---- 同步水位----

    public async Task<LocalSyncCursor?> GetSyncCursorAsync(long ownerUserId, string conversationId)
    {
        await using var db = await contextFactory.CreateDbContextAsync(None);
        return await db.SyncCursors
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.OwnerUserId == ownerUserId && s.ConversationId == conversationId, None);
    }

    public Task UpsertSyncCursorAsync(LocalSyncCursor cursor) => WriteAsync(() => UpsertSyncCursorAsyncImpl(cursor));

    private async Task UpsertSyncCursorAsyncImpl(LocalSyncCursor cursor)
    {
        await using var db = await contextFactory.CreateDbContextAsync(None);
        var existing = await db.SyncCursors
            .FirstOrDefaultAsync(s => s.OwnerUserId == cursor.OwnerUserId && s.ConversationId == cursor.ConversationId, None);
        if (existing is not null)
        {
            // 单调高水位：仅向前推进，绝不回退。
            if (cursor.AfterReceivedAtMs > existing.AfterReceivedAtMs)
            {
                existing.AfterReceivedAtMs = cursor.AfterReceivedAtMs;
                existing.AfterMessageId    = cursor.AfterMessageId;
            }
            existing.UpdatedAt = cursor.UpdatedAt;
        }
        else
        {
            await db.SyncCursors.AddAsync(cursor, None);
        }
        await db.SaveChangesAsync(None);
    }

    public async Task<List<LocalSyncCursor>> GetAllSyncCursorsAsync(long ownerUserId)
    {
        await using var db = await contextFactory.CreateDbContextAsync(None);
        return await db.SyncCursors
            .AsNoTracking()
            .Where(c => c.OwnerUserId == ownerUserId)
            .ToListAsync(None);
    }

    // ---- 会话已读状态----

    public async Task<LocalConversationReadState?> GetReadStateAsync(long ownerUserId, string conversationId)
    {
        await using var db = await contextFactory.CreateDbContextAsync(None);
        return await db.ConversationReadStates
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.OwnerUserId == ownerUserId && r.ConversationId == conversationId, None);
    }

    public Task UpsertReadStateAsync(LocalConversationReadState readState) => WriteAsync(() => UpsertReadStateAsyncImpl(readState));

    private async Task UpsertReadStateAsyncImpl(LocalConversationReadState readState)
    {
        await using var db = await contextFactory.CreateDbContextAsync(None);
        var existing = await db.ConversationReadStates
            .FirstOrDefaultAsync(r => r.OwnerUserId == readState.OwnerUserId && r.ConversationId == readState.ConversationId, None);
        if (existing is not null)
        {
            existing.LastReadMessageId = readState.LastReadMessageId;
            existing.LastReadAtMs      = readState.LastReadAtMs;
            existing.UnreadCount       = readState.UnreadCount;
            existing.UpdatedAt         = readState.UpdatedAt;
        }
        else
        {
            await db.ConversationReadStates.AddAsync(readState, None);
        }
        await db.SaveChangesAsync(None);
    }

    public Task MarkConversationMessagesReadAsync(long ownerUserId, string conversationId, long? beforeReceivedAtMs) => WriteAsync(() => MarkConversationMessagesReadAsyncImpl(ownerUserId, conversationId, beforeReceivedAtMs));

    private async Task MarkConversationMessagesReadAsyncImpl(long ownerUserId, string conversationId, long? beforeReceivedAtMs)
    {
        await using var db = await contextFactory.CreateDbContextAsync(None);
        // 仅标记自己发出的消息为已读（对端已读回执表示对方读了我的消息）；
        // 不覆盖已读、不标记撤回/失败消息。
        var query = db.Messages.Where(m => m.OwnerUserId == ownerUserId
                                         && m.ConversationId == conversationId
                                         && m.SenderUserId == ownerUserId
                                         && m.Status != MessageStatus.Recalled
                                         && m.Status != MessageStatus.Failed
                                         && m.Status != MessageStatus.Read);
        if (beforeReceivedAtMs.HasValue)
            query = query.Where(m => m.ReceivedAtMs <= beforeReceivedAtMs.Value);

        await query.ExecuteUpdateAsync(s => s
            .SetProperty(m => m.Status, MessageStatus.Read)
            .SetProperty(m => m.ReadAtMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            .SetProperty(m => m.UpdatedAt, DateTime.UtcNow), None);
    }

    // ---- 附件元数据（阶段 3）----

    public async Task<List<LocalAttachment>> GetAttachmentsByMessageIdAsync(long ownerUserId, string messageId)
    {
        await using var db = await contextFactory.CreateDbContextAsync(None);
        return await db.Attachments
            .AsNoTracking()
            .Where(a => a.OwnerUserId == ownerUserId && a.MessageId == messageId)
            .ToListAsync(None);
    }

    public async Task<LocalAttachment?> GetAttachmentByAttachmentIdAsync(long ownerUserId, string attachmentId)
    {
        await using var db = await contextFactory.CreateDbContextAsync(None);
        return await db.Attachments
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.OwnerUserId == ownerUserId && a.AttachmentId == attachmentId, None);
    }

    public async Task<LocalAttachment?> GetAttachmentByClientAttachmentIdAsync(long ownerUserId, string clientAttachmentId)
    {
        await using var db = await contextFactory.CreateDbContextAsync(None);
        return await db.Attachments
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.OwnerUserId == ownerUserId && a.ClientAttachmentId == clientAttachmentId, None);
    }

    public async Task<LocalAttachment?> GetAttachmentBySha256Async(long ownerUserId, string sha256)
    {
        await using var db = await contextFactory.CreateDbContextAsync(None);
        // Status == Available(1) 优先
        return await db.Attachments
            .AsNoTracking()
            .Where(a => a.OwnerUserId == ownerUserId && a.Sha256 == sha256)
            .OrderByDescending(a => a.Status == AttachmentStatus.Available ? 1 : 0)
            .FirstOrDefaultAsync(None);
    }
    public Task UpsertAttachmentAsync(LocalAttachment attachment) => WriteAsync(() => UpsertAttachmentAsyncImpl(attachment));

    private async Task UpsertAttachmentAsyncImpl(LocalAttachment attachment)
    {
        await using var db = await contextFactory.CreateDbContextAsync(None);
        // 空串归一化为 NULL：唯一索引下 NULL 互异、空串会冲突。
        attachment.AttachmentId = string.IsNullOrEmpty(attachment.AttachmentId) ? null : attachment.AttachmentId;
        attachment.ClientAttachmentId = string.IsNullOrEmpty(attachment.ClientAttachmentId) ? null : attachment.ClientAttachmentId;
        LocalAttachment? existing = null;
        if (attachment.AttachmentId is not null)
        {
            existing = await db.Attachments
                .FirstOrDefaultAsync(a => a.OwnerUserId == attachment.OwnerUserId
                                      && a.AttachmentId == attachment.AttachmentId, None);
        }
        if (existing is null && attachment.ClientAttachmentId is not null)
        {
            existing = await db.Attachments
                .FirstOrDefaultAsync(a => a.OwnerUserId == attachment.OwnerUserId
                                      && a.ClientAttachmentId == attachment.ClientAttachmentId, None);
        }        if (existing is not null)
        {
            existing.AttachmentId       = attachment.AttachmentId ?? existing.AttachmentId;
            existing.ClientAttachmentId = attachment.ClientAttachmentId ?? existing.ClientAttachmentId;
            existing.MessageId          = attachment.MessageId ?? existing.MessageId;
            existing.ConversationId     = attachment.ConversationId ?? existing.ConversationId;
            existing.FileName           = attachment.FileName ?? existing.FileName;
            existing.ContentType        = attachment.ContentType;
            existing.SizeBytes          = attachment.SizeBytes;
            existing.Sha256             = attachment.Sha256 ?? existing.Sha256;
            existing.DownloadPath       = attachment.DownloadPath ?? existing.DownloadPath;
            existing.ObjectKey          = attachment.ObjectKey ?? existing.ObjectKey;
            existing.ThumbnailPath      = attachment.ThumbnailPath ?? existing.ThumbnailPath;
            existing.LocalCachePath     = attachment.LocalCachePath ?? existing.LocalCachePath;
            existing.LocalThumbnailPath = attachment.LocalThumbnailPath ?? existing.LocalThumbnailPath;
            existing.LocalUploadingPath = attachment.LocalUploadingPath ?? existing.LocalUploadingPath;
            existing.RetryCount         = attachment.RetryCount;
            existing.Status             = attachment.Status;
            existing.FailureReason      = attachment.FailureReason ?? existing.FailureReason;
            existing.UpdatedAt          = DateTime.UtcNow;
        }
        else
        {
            await db.Attachments.AddAsync(attachment, None);
        }
        // 唯一索引冲突视为幂等成功（附件已存在）。
        try
        {
            await db.SaveChangesAsync(None);
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            // 幂等：并发写入导致唯一索引冲突，行已存在，忽略。
        }
    }
    public Task UpdateAttachmentStatusAsync(long ownerUserId, string? attachmentId, string? clientAttachmentId, AttachmentStatus status, string? downloadPath = null, string? failureReason = null) => WriteAsync(() => UpdateAttachmentStatusAsyncImpl(ownerUserId, attachmentId, clientAttachmentId, status, downloadPath, failureReason));

    private async Task UpdateAttachmentStatusAsyncImpl(long ownerUserId, string? attachmentId, string? clientAttachmentId, AttachmentStatus status, string? downloadPath = null, string? failureReason = null)
    {
        await using var db = await contextFactory.CreateDbContextAsync(None);
        var query = db.Attachments.Where(a => a.OwnerUserId == ownerUserId);
        if (!string.IsNullOrEmpty(attachmentId))
        {
            query = query.Where(a => a.AttachmentId == attachmentId);
        }
        else if (!string.IsNullOrEmpty(clientAttachmentId))
        {
            query = query.Where(a => a.ClientAttachmentId == clientAttachmentId);
        }
        else
        {
            return;
        }
        if (!string.IsNullOrEmpty(downloadPath))
        {
            await query.ExecuteUpdateAsync(a
                => a.SetProperty(x => x.Status, status)
                    .SetProperty(x => x.DownloadPath, downloadPath)
                    .SetProperty(x => x.FailureReason, failureReason)
                    .SetProperty(x => x.UpdatedAt, DateTime.UtcNow), None);
        }
        else
        {
            await query.ExecuteUpdateAsync(a
                => a.SetProperty(x => x.Status, status)
                    .SetProperty(x => x.FailureReason, failureReason)
                    .SetProperty(x => x.UpdatedAt, DateTime.UtcNow), None);
        }
    }

    /// <summary>更新附件本地上传路径与重试次数。localUploadingPath 传 null 清空该字段；retryCount 传 null 不修改。</summary>
    public Task UpdateAttachmentUploadPathAsync(long ownerUserId, string? clientAttachmentId, string? localUploadingPath, int? retryCount = null) => WriteAsync(() => UpdateAttachmentUploadPathAsyncImpl(ownerUserId, clientAttachmentId, localUploadingPath, retryCount));

    private async Task UpdateAttachmentUploadPathAsyncImpl(long ownerUserId, string? clientAttachmentId, string? localUploadingPath, int? retryCount = null)
    {
        if (string.IsNullOrEmpty(clientAttachmentId))
            return;
        await using var db = await contextFactory.CreateDbContextAsync(None);
        var query = db.Attachments.Where(a => a.OwnerUserId == ownerUserId
                                            && a.ClientAttachmentId == clientAttachmentId);
        if (retryCount.HasValue)
        {
            await query.ExecuteUpdateAsync(a
                => a.SetProperty(x => x.LocalUploadingPath, localUploadingPath)
                    .SetProperty(x => x.RetryCount, retryCount.Value)
                    .SetProperty(x => x.UpdatedAt, DateTime.UtcNow), None);
        }
        else
        {
            await query.ExecuteUpdateAsync(a
                => a.SetProperty(x => x.LocalUploadingPath, localUploadingPath)
                    .SetProperty(x => x.UpdatedAt, DateTime.UtcNow), None);
        }
    }

    public async Task DeleteAttachmentAsync(long ownerUserId, string attachmentId)
    {
        await using var db = await contextFactory.CreateDbContextAsync(None);
        await db.Attachments
            .Where(a => a.OwnerUserId == ownerUserId && a.AttachmentId == attachmentId)
            .ExecuteDeleteAsync(None);
    }

    public async Task<List<LocalAttachment>> GetUploadingAttachmentsAsync(long ownerUserId)
    {
        await using var db = await contextFactory.CreateDbContextAsync(None);
        // Status: 0=Uploading；按 CreatedAt 升序。
        return await db.Attachments
            .AsNoTracking()
            .Where(a => a.OwnerUserId == ownerUserId && a.Status == AttachmentStatus.Uploading)
            .OrderBy(a => a.CreatedAt)
            .ToListAsync(None);
    }
}
