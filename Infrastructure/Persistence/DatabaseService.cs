using Core.Contracts.Auth;
using Core.Models;
using Infrastructure.Data;
using Infrastructure.Models;
using Infrastructure.Models.Context;
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
/// 避免单个非线程安全 DbContext 被多线程共享（P0-4）。
/// </summary>
public class DatabaseService(IDbContextFactory<ClientDbContext> contextFactory) : IDatabaseService
{
    private static readonly CancellationToken None = CancellationToken.None;

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
                Status = f.Status,
                AvatarUrl = f.AvatarUrl,
                LastSynced = f.LastSynced
            }).ToListAsync(None);
    }

    public async Task AddFriendAsync(List<LocalFriend> friend)
    {
        await using var db = await contextFactory.CreateDbContextAsync(None);
        await db.Friends.AddRangeAsync(friend, None);
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
        var friend = await db.Friends.FirstOrDefaultAsync(f => f.Id == updatedFriend.Id, None);
        if (friend != null)
        {
            db.Friends.Update(friend);
            await db.SaveChangesAsync(None);
        }
    }

    public async Task DeleteFriendAsync(long id)
    {
        await using var db = await contextFactory.CreateDbContextAsync(None);
        await db.Friends
            .Where(f => f.Id == id)
            .ExecuteDeleteAsync(None);
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

    // ---- 会话（P0-6 持久化聊天系统）----

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

    public async Task UpsertConversationAsync(LocalConversation conversation)
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
            existing.LastSynced         = conversation.LastSynced;
        }
        else
        {
            await db.Conversations.AddAsync(conversation, None);
        }
        await db.SaveChangesAsync(None);
    }

    public async Task DeleteConversationAsync(long ownerUserId, string conversationId)
    {
        await using var db = await contextFactory.CreateDbContextAsync(None);
        await db.Conversations
            .Where(c => c.OwnerUserId == ownerUserId && c.ConversationId == conversationId)
            .ExecuteDeleteAsync(None);
    }

    // ---- 消息（P0-6）----

    public async Task<List<LocalMessage>> GetMessagesAsync(long ownerUserId, string conversationId, int limit = 100, long? beforeReceivedAtMs = null)
    {
        await using var db = await contextFactory.CreateDbContextAsync(None);
        var query = db.Messages
            .AsNoTracking()
            .Where(m => m.OwnerUserId == ownerUserId && m.ConversationId == conversationId);
        if (beforeReceivedAtMs is long beforeMs)
        {
            query = query.Where(m => m.ReceivedAtMs < beforeMs);
        }
        // 游标分页：按 ReceivedAtMs 倒序取一页，再反转为时间正序，便于 UI 直接追加。
        var page = await query
            .OrderByDescending(m => m.ReceivedAtMs)
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

    public async Task UpsertMessageAsync(LocalMessage message)
    {
        await using var db = await contextFactory.CreateDbContextAsync(None);
        LocalMessage? existing = null;
        if (!string.IsNullOrEmpty(message.MessageId))
        {
            existing = await db.Messages
                .FirstOrDefaultAsync(m => m.OwnerUserId == message.OwnerUserId
                                       && m.MessageId == message.MessageId, None);
        }
        if (existing is null && !string.IsNullOrEmpty(message.ClientMessageId))
        {
            existing = await db.Messages
                .FirstOrDefaultAsync(m => m.OwnerUserId == message.OwnerUserId
                                       && m.ClientMessageId == message.ClientMessageId, None);
        }
        if (existing is not null)
        {
            existing.Content         = message.Content;
            existing.ReceivedAtMs    = message.ReceivedAtMs;
            existing.DeliveredAtMs   = message.DeliveredAtMs;
            existing.ReadAtMs        = message.ReadAtMs;
            existing.RecalledAtMs    = message.RecalledAtMs;
            existing.EditVersion     = message.EditVersion;
            existing.EditedAtMs      = message.EditedAtMs;
            existing.AttachmentsJson = message.AttachmentsJson;
            existing.Status          = message.Status;
            existing.FailureReason   = message.FailureReason;
            existing.UpdatedAt       = message.UpdatedAt;
            // 服务端确认后回填 MessageId（outbox 阶段仅写入 ClientMessageId）。
            if (string.IsNullOrEmpty(existing.MessageId) && !string.IsNullOrEmpty(message.MessageId))
            {
                existing.MessageId = message.MessageId;
            }
        }
        else
        {
            await db.Messages.AddAsync(message, None);
        }
        await db.SaveChangesAsync(None);
    }

    public async Task UpdateMessageStatusAsync(long ownerUserId, string? messageId, string? clientMessageId, MessageStatus status, string? failureReason = null, string? ackServerMessageId = null)
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

    public async Task MarkMessageRecalledAsync(long ownerUserId, string messageId, long recalledAtMs)
    {
        await using var db = await contextFactory.CreateDbContextAsync(None);
        await db.Messages
            .Where(m => m.OwnerUserId == ownerUserId && m.MessageId == messageId)
            .ExecuteUpdateAsync(m
                => m.SetProperty(x => x.Status, MessageStatus.Recalled)
                    .SetProperty(x => x.RecalledAtMs, recalledAtMs)
                    .SetProperty(x => x.UpdatedAt, DateTime.UtcNow), None);
    }

    public async Task ApplyMessageEditAsync(long ownerUserId, string messageId, string content, int editVersion, long editedAtMs)
    {
        await using var db = await contextFactory.CreateDbContextAsync(None);
        await db.Messages
            .Where(m => m.OwnerUserId == ownerUserId && m.MessageId == messageId)
            .ExecuteUpdateAsync(m
                => m.SetProperty(x => x.Content, content)
                    .SetProperty(x => x.EditVersion, editVersion)
                    .SetProperty(x => x.EditedAtMs, editedAtMs)
                    .SetProperty(x => x.UpdatedAt, DateTime.UtcNow), None);
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

    // ---- Outbox（P0-6）----

    public async Task<long> EnqueueOutboxAsync(LocalOutboxMessage outbox)
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

    public async Task UpdateOutboxStatusAsync(long ownerUserId, string clientMessageId, OutboxStatus status, string? messageId = null, string? failureReason = null)
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

    // ---- 同步水位（P0-6）----

    public async Task<LocalSyncCursor?> GetSyncCursorAsync(long ownerUserId, string conversationId)
    {
        await using var db = await contextFactory.CreateDbContextAsync(None);
        return await db.SyncCursors
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.OwnerUserId == ownerUserId && s.ConversationId == conversationId, None);
    }

    public async Task UpsertSyncCursorAsync(LocalSyncCursor cursor)
    {
        await using var db = await contextFactory.CreateDbContextAsync(None);
        var existing = await db.SyncCursors
            .FirstOrDefaultAsync(s => s.OwnerUserId == cursor.OwnerUserId && s.ConversationId == cursor.ConversationId, None);
        if (existing is not null)
        {
            existing.AfterReceivedAtMs = cursor.AfterReceivedAtMs;
            existing.AfterMessageId    = cursor.AfterMessageId;
            existing.UpdatedAt         = cursor.UpdatedAt;
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

    // ---- 会话已读状态（P0-6）----

    public async Task<LocalConversationReadState?> GetReadStateAsync(long ownerUserId, string conversationId)
    {
        await using var db = await contextFactory.CreateDbContextAsync(None);
        return await db.ConversationReadStates
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.OwnerUserId == ownerUserId && r.ConversationId == conversationId, None);
    }

    public async Task UpsertReadStateAsync(LocalConversationReadState readState)
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

    public async Task MarkConversationMessagesReadAsync(long ownerUserId, string conversationId, long? beforeReceivedAtMs)
    {
        await using var db = await contextFactory.CreateDbContextAsync(None);
        var query = db.Messages.Where(m => m.OwnerUserId == ownerUserId
                                         && m.ConversationId == conversationId
                                         && m.Status != MessageStatus.Recalled);
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
            .OrderByDescending(a => a.Status == 1 ? 1 : 0)
            .FirstOrDefaultAsync(None);
    }
    public async Task UpsertAttachmentAsync(LocalAttachment attachment)
    {
        await using var db = await contextFactory.CreateDbContextAsync(None);
        LocalAttachment? existing = null;
        if (!string.IsNullOrEmpty(attachment.AttachmentId))
        {
            existing = await db.Attachments
                .FirstOrDefaultAsync(a => a.OwnerUserId == attachment.OwnerUserId
                                      && a.AttachmentId == attachment.AttachmentId, None);
        }
        if (existing is null && !string.IsNullOrEmpty(attachment.ClientAttachmentId))
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
        await db.SaveChangesAsync(None);
    }
    public async Task UpdateAttachmentStatusAsync(long ownerUserId, string? attachmentId, string? clientAttachmentId, byte status, string? downloadPath = null, string? failureReason = null)
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
    public async Task UpdateAttachmentUploadPathAsync(long ownerUserId, string? clientAttachmentId, string? localUploadingPath, int? retryCount = null)
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
            .Where(a => a.OwnerUserId == ownerUserId && a.Status == 0)
            .OrderBy(a => a.CreatedAt)
            .ToListAsync(None);
    }
}
