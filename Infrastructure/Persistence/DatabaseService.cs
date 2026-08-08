using Core.Contracts.Auth;
using Core.Models;
using Core.Models.DTO;
using Chat_App.Infrastructure.Identity;
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
            existing.Username = user.Username;
            existing.AvatarUrl = user.AvatarUrl;
            existing.Email = user.Email;
            existing.Signature = user.Signature;
            existing.Gender = user.Gender;
            existing.Region = user.Region;
            existing.Status = user.Status;
            existing.PreviousLoginDate = user.PreviousLoginDate;
            existing.LastLoginTime = user.LastLoginTime;
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
        // 令牌以 DPAPI 密文落库，明文不出现在 SQLite 文件中。
        var secret = new AuthToken
        {
            UserId = token.UserId,
            AccessToken = SecretProtector.Protect(token.AccessToken) ?? string.Empty,
            RefreshToken = SecretProtector.Protect(token.RefreshToken) ?? string.Empty,
            AccessTokenExpires = token.AccessTokenExpires,
            RefreshTokenExpires = token.RefreshTokenExpires,
            SessionId = token.SessionId,
            DeviceIdHash = token.DeviceIdHash,
            DeviceCredential = SecretProtector.Protect(token.DeviceCredential)
        };

        await using var db = await contextFactory.CreateDbContextAsync(None);
        var oldToken = await db.Tokens.FirstOrDefaultAsync(None);
        if (oldToken is not null)
        {
            oldToken.UserId = secret.UserId;
            oldToken.AccessToken = secret.AccessToken;
            oldToken.RefreshToken = secret.RefreshToken ?? string.Empty;
            oldToken.AccessTokenExpires = secret.AccessTokenExpires;
            oldToken.RefreshTokenExpires = secret.RefreshTokenExpires;
            oldToken.SessionId = secret.SessionId;
            oldToken.DeviceIdHash = secret.DeviceIdHash;
            oldToken.DeviceCredential = secret.DeviceCredential;
        }
        else
        {
            await db.Tokens.AddAsync(secret, None);
        }
        await db.SaveChangesAsync(None);
    }

    public async Task<Token?> GetAccessTokenAsync()
    {
        await using var db = await contextFactory.CreateDbContextAsync(None);
        // 密文行不能直接返回，需按存储介质解密（Windows DPAPI / 非 Windows 明文）。
        var stored = await db.Tokens.AsNoTracking().FirstOrDefaultAsync(None);
        if (stored is null)
            return null;
        return new Token
        {
            TokenExpires = stored.AccessTokenExpires,
            TokenValue = SecretProtector.Unprotect(stored.AccessToken) ?? string.Empty
        };
    }

    public async Task<int> UpdateTokenAsync(AuthToken token)
    {
        var secret = new AuthToken
        {
            UserId = token.UserId,
            AccessToken = SecretProtector.Protect(token.AccessToken) ?? string.Empty,
            RefreshToken = SecretProtector.Protect(token.RefreshToken) ?? string.Empty,
            AccessTokenExpires = token.AccessTokenExpires,
            RefreshTokenExpires = token.RefreshTokenExpires,
            SessionId = token.SessionId,
            DeviceIdHash = token.DeviceIdHash,
            DeviceCredential = SecretProtector.Protect(token.DeviceCredential)
        };

        await using var db = await contextFactory.CreateDbContextAsync(None);
        return await db.Tokens
            .Where(f => f.UserId == token.UserId)
            .ExecuteUpdateAsync(t
                => t.SetProperty(authToken => authToken.AccessToken, secret.AccessToken)
                    .SetProperty(authToken => authToken.RefreshToken, secret.RefreshToken)
                    .SetProperty(authToken => authToken.AccessTokenExpires, secret.AccessTokenExpires)
                    .SetProperty(authToken => authToken.RefreshTokenExpires, secret.RefreshTokenExpires)
                    .SetProperty(authToken => authToken.DeviceCredential, secret.DeviceCredential), None);
    }

    public async Task<AuthToken?> GetTokenAsync()
    {
        await using var db = await contextFactory.CreateDbContextAsync(None);
        var stored = await db.Tokens.AsNoTracking().FirstOrDefaultAsync(None);
        if (stored is null)
            return null;

        return new AuthToken
        {
            UserId = stored.UserId,
            AccessToken = SecretProtector.Unprotect(stored.AccessToken) ?? string.Empty,
            RefreshToken = SecretProtector.Unprotect(stored.RefreshToken) ?? string.Empty,
            AccessTokenExpires = stored.AccessTokenExpires,
            RefreshTokenExpires = stored.RefreshTokenExpires,
            SessionId = stored.SessionId,
            DeviceIdHash = stored.DeviceIdHash,
            DeviceCredential = SecretProtector.Unprotect(stored.DeviceCredential)
        };
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
            existing.UseTls = serverInfo.UseTls;
            existing.TlsServerName = serverInfo.TlsServerName;
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
        // 优先主服务器，其次最近连接过的服务器；绝不再"任意取第一行"。
        return await db.Servers
            .AsNoTracking()
            .OrderByDescending(s => s.IsPrimary)
            .ThenByDescending(s => s.LastConnected)
            .FirstOrDefaultAsync(None);
    }

    /// <summary>
    /// 登录会话原子持久化：Token（DPAPI 密文）+ 用户画像 + 服务器端点
    /// 在单个 DbContext + 单个事务内提交，任一失败整体回滚。
    /// </summary>
    public async Task PersistLoginSessionAsync(AuthToken token, LocalUser user, ServerEndpoint? endpoint)
    {
        var secret = new AuthToken
        {
            UserId = token.UserId,
            AccessToken = SecretProtector.Protect(token.AccessToken) ?? string.Empty,
            RefreshToken = SecretProtector.Protect(token.RefreshToken) ?? string.Empty,
            AccessTokenExpires = token.AccessTokenExpires,
            RefreshTokenExpires = token.RefreshTokenExpires,
            SessionId = token.SessionId,
            DeviceIdHash = token.DeviceIdHash,
            DeviceCredential = SecretProtector.Protect(token.DeviceCredential)
        };

        await using var db = await contextFactory.CreateDbContextAsync(None);
        await using var transaction = await db.Database.BeginTransactionAsync(None);

        // Token：单行 upsert。
        var oldToken = await db.Tokens.FirstOrDefaultAsync(None);
        if (oldToken is not null)
        {
            oldToken.UserId = secret.UserId;
            oldToken.AccessToken = secret.AccessToken;
            oldToken.RefreshToken = secret.RefreshToken ?? string.Empty;
            oldToken.AccessTokenExpires = secret.AccessTokenExpires;
            oldToken.RefreshTokenExpires = secret.RefreshTokenExpires;
            oldToken.SessionId = secret.SessionId;
            oldToken.DeviceIdHash = secret.DeviceIdHash;
            oldToken.DeviceCredential = secret.DeviceCredential;
        }
        else
        {
            await db.Tokens.AddAsync(secret, None);
        }

        // 用户画像 upsert。
        var existingUser = await db.Users.FirstOrDefaultAsync(u => u.UserId == user.UserId, None);
        if (existingUser is not null)
        {
            existingUser.Username = user.Username;
            existingUser.AvatarUrl = user.AvatarUrl;
            existingUser.Email = user.Email;
            existingUser.Signature = user.Signature;
            existingUser.Gender = user.Gender;
            existingUser.Region = user.Region;
            existingUser.Status = user.Status;
            existingUser.PreviousLoginDate = user.PreviousLoginDate;
            existingUser.LastLoginTime = user.LastLoginTime;
        }
        else
        {
            await db.Users.AddAsync(user, None);
        }

        // 服务器端点 upsert（按地址+端口匹配；本次登录即最近连接）。
        if (endpoint is not null)
        {
            var existingServer = await db.Servers
                .FirstOrDefaultAsync(s => s.ServerIpAddress == endpoint.ServerIpAddress
                                       && s.ServerPort == endpoint.ServerPort, None);
            if (existingServer is not null)
            {
                existingServer.ServerName = endpoint.ServerName;
                existingServer.UseTls = endpoint.UseTls;
                existingServer.TlsServerName = endpoint.TlsServerName;
                existingServer.IsPrimary = endpoint.IsPrimary;
                existingServer.LastConnected = DateTime.UtcNow;
            }
            else
            {
                endpoint.LastConnected = DateTime.UtcNow;
                await db.Servers.AddAsync(endpoint, None);
            }
        }

        await db.SaveChangesAsync(None);
        await transaction.CommitAsync(None);
    }

    public async Task DeleteServerInfoAsync()
    {
        await using var db = await contextFactory.CreateDbContextAsync(None);
        await db.Servers.ExecuteDeleteAsync(None);
    }

    // ---- 会话（持久化聊天系统）----

    /// <summary>加载会话列表（不含已删除），排序与 UI 一致：置顶优先，其次最后活动时间。</summary>
    public async Task<List<LocalConversation>> GetConversationsAsync(long ownerUserId)
    {
        await using var db = await contextFactory.CreateDbContextAsync(None);
        return await db.Conversations
            .AsNoTracking()
            .Where(c => c.OwnerUserId == ownerUserId && !c.IsDeleted)
            .OrderByDescending(c => c.IsPinned)
            .ThenByDescending(c => c.PinnedAtMs)
            .ThenByDescending(c => c.LastMessageAtMs)
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

    /// <summary>本地归档/删除状态落库（不随服务端同步 Upsert 覆盖，本地状态独立于远端）。</summary>
    public Task SetConversationLocalStateAsync(long ownerUserId, string conversationId, bool? archived = null, bool? deleted = null) =>
        WriteAsync(() => SetConversationLocalStateAsyncImpl(ownerUserId, conversationId, archived, deleted));

    private async Task SetConversationLocalStateAsyncImpl(long ownerUserId, string conversationId, bool? archived, bool? deleted)
    {
        await using var db = await contextFactory.CreateDbContextAsync(None);
        var entity = await db.Conversations
            .FirstOrDefaultAsync(c => c.OwnerUserId == ownerUserId && c.ConversationId == conversationId, None);
        if (entity is null)
            return;
        if (archived is bool a)
            entity.Archived = a;
        if (deleted is bool d)
            entity.IsDeleted = d;
        await db.SaveChangesAsync(None);
    }

    private async Task UpsertConversationAsyncImpl(LocalConversation conversation)
    {
        await using var db = await contextFactory.CreateDbContextAsync(None);
        var existing = await db.Conversations
            .FirstOrDefaultAsync(c => c.OwnerUserId == conversation.OwnerUserId
                                   && c.ConversationId == conversation.ConversationId, None);
        if (existing is not null)
        {
            // 本地 Upsert 不覆盖草稿/归档/删除等本地专属字段（服务端投影另有 ApplyRemoteConversationProjectionAsync）。
            existing.Type = conversation.Type;
            existing.PeerUserId = conversation.PeerUserId;
            existing.GroupTitle = conversation.GroupTitle;
            existing.LastMessageId = conversation.LastMessageId;
            existing.LastMessagePreview = conversation.LastMessagePreview;
            existing.LastMessageAtMs = conversation.LastMessageAtMs;
            existing.LastSenderUserId = conversation.LastSenderUserId;
            existing.UnreadCount = conversation.UnreadCount;
            existing.LastReadMessageId = conversation.LastReadMessageId;
            existing.LastReadAtMs = conversation.LastReadAtMs;
            existing.IsPinned = conversation.IsPinned;
            existing.PinnedAtMs = conversation.PinnedAtMs;
            existing.IsMuted = conversation.IsMuted;
            existing.MutedUntilMs = conversation.MutedUntilMs;
            existing.LastSynced = conversation.LastSynced;
        }
        else
        {
            await db.Conversations.AddAsync(conversation, None);
        }
        await db.SaveChangesAsync(None);
    }

    /// <summary>
    /// 应用服务端会话投影（同步链路专用）：只更新服务端拥有的字段
    /// （Type/PeerUserId/GroupTitle/LastMessage*/Unread*/Pinned/Muted），
    /// 绝不触碰本地专属字段（Draft/DraftState/DraftRevision/Archived/IsDeleted/LocalUIState）。
    /// </summary>
    public Task ApplyRemoteConversationProjectionAsync(LocalConversation projection) =>
        WriteAsync(() => ApplyRemoteConversationProjectionAsyncImpl(projection));

    private async Task ApplyRemoteConversationProjectionAsyncImpl(LocalConversation projection)
    {
        await using var db = await contextFactory.CreateDbContextAsync(None);
        var existing = await db.Conversations
            .FirstOrDefaultAsync(c => c.OwnerUserId == projection.OwnerUserId
                                   && c.ConversationId == projection.ConversationId, None);
        if (existing is not null)
        {
            existing.Type = projection.Type;
            existing.PeerUserId = projection.PeerUserId;
            existing.GroupTitle = projection.GroupTitle;
            existing.LastMessageId = projection.LastMessageId;
            existing.LastMessagePreview = projection.LastMessagePreview;
            existing.LastMessageAtMs = projection.LastMessageAtMs;
            existing.LastSenderUserId = projection.LastSenderUserId;
            existing.UnreadCount = projection.UnreadCount;
            existing.LastReadMessageId = projection.LastReadMessageId;
            existing.LastReadAtMs = projection.LastReadAtMs;
            existing.IsPinned = projection.IsPinned;
            existing.PinnedAtMs = projection.PinnedAtMs;
            existing.IsMuted = projection.IsMuted;
            existing.MutedUntilMs = projection.MutedUntilMs;
            existing.LastSynced = projection.LastSynced;
        }
        else
        {
            await db.Conversations.AddAsync(projection, None);
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
            existing.ReceivedAtMs = message.ReceivedAtMs;
            existing.DeliveredAtMs = message.DeliveredAtMs;
            existing.ReadAtMs = message.ReadAtMs;
            existing.RecalledAtMs = message.RecalledAtMs;
            existing.AttachmentsJson = message.AttachmentsJson;
            existing.FailureReason = message.FailureReason;
            existing.UpdatedAt = message.UpdatedAt;
            // 严格状态机：仅接受合法转换（撤回为终态、Read→Sent 等非法转换被拒绝）。
            if (MessageStatusTransitions.CanTransition(existing.Status, message.Status))
                existing.Status = message.Status;
            // 编辑版本单调递增：仅当入站版本严格更新时才覆盖正文/版本/编辑时间。
            if (message.EditVersion > existing.EditVersion)
            {
                existing.EditVersion = message.EditVersion;
                existing.EditedAtMs = message.EditedAtMs;
                existing.Content = message.Content;
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
        // 严格状态机：仅更新处于允许源状态的行（Read→Sent 等非法转换不生效）。
        var allowedFrom = MessageStatusTransitions.AllowedFrom(status);
        query = query.Where(m => allowedFrom.Contains(m.Status));
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
            existingMessage.ReceivedAtMs = message.ReceivedAtMs;
            existingMessage.DeliveredAtMs = message.DeliveredAtMs;
            existingMessage.ReadAtMs = message.ReadAtMs;
            existingMessage.RecalledAtMs = message.RecalledAtMs;
            existingMessage.AttachmentsJson = message.AttachmentsJson;
            existingMessage.FailureReason = message.FailureReason;
            existingMessage.UpdatedAt = message.UpdatedAt;
            // 严格状态机：仅接受合法转换（撤回为终态、Read→Sent 等非法转换被拒绝）。
            if (MessageStatusTransitions.CanTransition(existingMessage.Status, message.Status))
                existingMessage.Status = message.Status;
            // 编辑版本单调递增：仅当入站版本严格更新时才覆盖正文/版本/编辑时间。
            if (message.EditVersion > existingMessage.EditVersion)
            {
                existingMessage.EditVersion = message.EditVersion;
                existingMessage.EditedAtMs = message.EditedAtMs;
                existingMessage.Content = message.Content;
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
                    existingAttachment.AttachmentId = attachment.AttachmentId ?? existingAttachment.AttachmentId;
                    existingAttachment.ClientAttachmentId = attachment.ClientAttachmentId ?? existingAttachment.ClientAttachmentId;
                    existingAttachment.MessageId = attachment.MessageId ?? existingAttachment.MessageId;
                    existingAttachment.ConversationId = attachment.ConversationId ?? existingAttachment.ConversationId;
                    existingAttachment.FileName = attachment.FileName ?? existingAttachment.FileName;
                    existingAttachment.ContentType = attachment.ContentType;
                    existingAttachment.SizeBytes = attachment.SizeBytes;
                    existingAttachment.Sha256 = attachment.Sha256 ?? existingAttachment.Sha256;
                    existingAttachment.DownloadPath = attachment.DownloadPath ?? existingAttachment.DownloadPath;
                    existingAttachment.ObjectKey = attachment.ObjectKey ?? existingAttachment.ObjectKey;
                    existingAttachment.ThumbnailPath = attachment.ThumbnailPath ?? existingAttachment.ThumbnailPath;
                    existingAttachment.LocalCachePath = attachment.LocalCachePath ?? existingAttachment.LocalCachePath;
                    existingAttachment.LocalThumbnailPath = attachment.LocalThumbnailPath ?? existingAttachment.LocalThumbnailPath;
                    existingAttachment.LocalUploadingPath = attachment.LocalUploadingPath ?? existingAttachment.LocalUploadingPath;
                    existingAttachment.RetryCount = attachment.RetryCount;
                    existingAttachment.Status = attachment.Status;
                    existingAttachment.FailureReason = attachment.FailureReason ?? existingAttachment.FailureReason;
                    existingAttachment.UpdatedAt = DateTime.UtcNow;
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
                    OwnerUserId = conversationUpdate.OwnerUserId,
                    ConversationId = conversationUpdate.ConversationId,
                    Type = conversationUpdate.Type,
                    PeerUserId = conversationUpdate.PeerUserId,
                    LastMessageId = conversationUpdate.LastMessageId,
                    LastMessagePreview = conversationUpdate.LastMessagePreview,
                    LastMessageAtMs = conversationUpdate.LastMessageAtMs,
                    LastSenderUserId = conversationUpdate.LastSenderUserId,
                    UnreadCount = conversationUpdate.UnreadCount,
                    LastReadMessageId = null,
                    LastReadAtMs = null,
                    IsPinned = false,
                    PinnedAtMs = null,
                    IsMuted = false,
                    MutedUntilMs = null,
                    LastSynced = conversationUpdate.LastSynced
                }, None);
            }
            else
            {
                // 仅当新消息时间戳晚于会话最近消息时更新摘要，避免乱序消息回退最近消息。
                if (conversationUpdate.LastMessageAtMs is long newAtMs
                    && newAtMs > (existingConversation.LastMessageAtMs ?? 0))
                {
                    existingConversation.LastMessageId = conversationUpdate.LastMessageId;
                    existingConversation.LastMessagePreview = conversationUpdate.LastMessagePreview;
                    existingConversation.LastMessageAtMs = newAtMs;
                    existingConversation.LastSenderUserId = conversationUpdate.LastSenderUserId;
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
                // 幂等：已 Sent 的重复 ACK 视为成功；以 AlreadySent 标记，
                // 调用方静默忽略（不更新、不发布重复事件、不告警）。
                await transaction.CommitAsync(None);
                return new OutboxAckResult(false, outbox.ConversationId, outbox.MessageId, AlreadySent: true);
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
        // QueuedAt 随结果带出：上层以此计算 ACK 端到端延迟（入队 → 服务端确认）。
        return new OutboxAckResult(transitioned, outbox.ConversationId, serverId,
            QueuedAtUtc: outbox.QueuedAt);
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

    /// <summary>
    /// 按会话将未发送 Outbox 标记为永久失败（群聊成员被移除/退出/解散时调用）：
    /// 该会话的 Queued/Sending 条目不再自动重试。返回受影响条数。
    /// </summary>
    public Task<int> MarkOutboxPermanentByConversationAsync(long ownerUserId, string conversationId, string reason) =>
        WriteAsync(() => MarkOutboxPermanentByConversationAsyncImpl(ownerUserId, conversationId, reason));

    private async Task<int> MarkOutboxPermanentByConversationAsyncImpl(long ownerUserId, string conversationId, string reason)
    {
        await using var db = await contextFactory.CreateDbContextAsync(None);
        var now = DateTime.UtcNow;
        var affected = await db.OutboxMessages
            .Where(o => o.OwnerUserId == ownerUserId
                     && o.ConversationId == conversationId
                     && (o.Status == OutboxStatus.Queued || o.Status == OutboxStatus.Sending))
            .ExecuteUpdateAsync(o => o
                .SetProperty(x => x.Status, OutboxStatus.Failed)
                .SetProperty(x => x.FailureKind, OutboxFailureKind.Permanent)
                .SetProperty(x => x.LastErrorCode, "MEMBER_REMOVED")
                .SetProperty(x => x.FailureReason, reason)
                .SetProperty(x => x.NextRetryAt, (DateTime?)null)
                .SetProperty(x => x.AttemptId, (string?)null)
                .SetProperty(x => x.AttemptStartedAt, (DateTime?)null)
                .SetProperty(x => x.LeaseUntil, (DateTime?)null), None);
        return affected;
    }

    /// <summary>
    /// 主动释放一批 Outbox 条目租约（Sending → Queued）：处理器停止时调用，
    /// 使尚未开始发送的条目无需等待租约过期即可重新发送。
    /// 仅释放 AttemptId 匹配的条目（防止误释放他人正在发送的租约）。返回释放条数。
    /// </summary>
    public Task<int> ReleaseOutboxLeasesAsync(long ownerUserId, IReadOnlyList<string> clientMessageIds, string attemptId) =>
        WriteAsync(() => ReleaseOutboxLeasesAsyncImpl(ownerUserId, clientMessageIds, attemptId));

    private async Task<int> ReleaseOutboxLeasesAsyncImpl(long ownerUserId, IReadOnlyList<string> clientMessageIds, string attemptId)
    {
        if (clientMessageIds.Count == 0)
            return 0;
        await using var db = await contextFactory.CreateDbContextAsync(None);
        return await db.OutboxMessages
            .Where(o => o.OwnerUserId == ownerUserId
                     && o.Status == OutboxStatus.Sending
                     && o.AttemptId == attemptId
                     && clientMessageIds.Contains(o.ClientMessageId))
            .ExecuteUpdateAsync(o => o
                .SetProperty(x => x.Status, OutboxStatus.Queued)
                .SetProperty(x => x.AttemptId, (string?)null)
                .SetProperty(x => x.AttemptStartedAt, (DateTime?)null)
                .SetProperty(x => x.LeaseUntil, (DateTime?)null), None);
    }

    /// <summary>
    /// FTS5 本地消息搜索：按全文匹配 Content（带转义，防 FTS 注入），
    /// 账户隔离（OwnerUserId 必筛），可选会话过滤 + 时间游标分页（ReceivedAtMs 倒序）。
    /// 返回匹配的完整 LocalMessage（join Messages 取全列）。
    /// </summary>
    public Task<List<LocalMessage>> SearchMessagesAsync(
        long ownerUserId, string query, string? conversationId = null,
        int limit = 50, long? beforeReceivedAtMs = null) =>
        WriteAsync(() => SearchMessagesAsyncImpl(ownerUserId, query, conversationId, limit, beforeReceivedAtMs));

    private async Task<List<LocalMessage>> SearchMessagesAsyncImpl(
        long ownerUserId, string query, string? conversationId,
        int limit, long? beforeReceivedAtMs)
    {
        var match = FtsQueryBuilder.BuildMatchQuery(query);
        var like = FtsQueryBuilder.BuildLikePattern(query);
        if ((match is null && like is null) || limit <= 0)
            return new List<LocalMessage>();

        await using var db = await contextFactory.CreateDbContextAsync(None);
        // FTS5 前缀命中（索引加速）∪ LIKE 兜底（非前缀子串，如词在末尾）：
        // 外部内容表 MATCH + join Messages 取全列；账户隔离 + 会话/时间游标分页。
        // rowid 与 Messages.Id 对齐（content_rowid='Id'）；参数按出现顺序编号。
        var sb = new System.Text.StringBuilder("""
            SELECT m."Id", m."OwnerUserId", m."MessageId", m."ClientMessageId", m."ConversationId",
                   m."SenderUserId", m."ReceiverUserId", m."Content", m."ReceivedAtMs",
                   m."DeliveredAtMs", m."ReadAtMs", m."RecalledAtMs", m."EditVersion", m."EditedAtMs",
                   m."AttachmentsJson", m."ReplyToMessageId", m."ReplyToSenderUserId", m."ReplyToPreview",
                   m."ForwardedFromMessageId", m."ForwardedFromSenderUserId", m."ForwardedFromPreview",
                   m."Status", m."FailureReason", m."RetryCount", m."CreatedAt", m."UpdatedAt"
            FROM "Messages" m
            WHERE m."OwnerUserId" = {0}
              AND (
                  EXISTS (
                      SELECT 1 FROM "MessagesFts" f
                      WHERE f.rowid = m."Id" AND "MessagesFts" MATCH {1}
                  )
                  OR m."Content" LIKE {2} ESCAPE '\'
              )
            """);
        var args = new List<object> { ownerUserId, match ?? string.Empty, like ?? string.Empty };
        if (conversationId is not null)
        {
            sb.Append("AND m.\"ConversationId\" = {").Append(args.Count).Append("} ");
            args.Add(conversationId);
        }
        if (beforeReceivedAtMs is not null)
        {
            sb.Append("AND m.\"ReceivedAtMs\" < {").Append(args.Count).Append("} ");
            args.Add(beforeReceivedAtMs.Value);
        }
        sb.Append("ORDER BY m.\"ReceivedAtMs\" DESC LIMIT {").Append(args.Count).Append('}');
        args.Add(limit);

        return await db.Database.SqlQueryRaw<LocalMessage>(sb.ToString(), args.ToArray())
            .ToListAsync(None);
    }

    /// <summary>
    /// 成员加入/角色变更落库（版本单调）：仅当事件时间晚于已记录时间才应用（防重放/乱序）。
    /// 成员存在时同时更新角色；被移除成员被更新的加入事件重新激活。
    /// </summary>
    public Task<bool> UpsertGroupMemberAsync(
        long ownerUserId, string conversationId, long userId, byte role,
        long occurredAtMs, long revision) =>
        WriteAsync(() => UpsertGroupMemberAsyncImpl(ownerUserId, conversationId, userId, role, occurredAtMs, revision));

    private async Task<bool> UpsertGroupMemberAsyncImpl(
        long ownerUserId, string conversationId, long userId, byte role,
        long occurredAtMs, long revision)
    {
        await using var db = await contextFactory.CreateDbContextAsync(None);
        var now = DateTime.UtcNow;
        var existing = await db.GroupMembers
            .FirstOrDefaultAsync(m => m.OwnerUserId == ownerUserId
                                   && m.ConversationId == conversationId
                                   && m.UserId == userId, None);
        if (existing is not null)
        {
            // 版本单调：仅接受更晚的事件（重放/乱序保护）。
            if (existing.JoinedAtMs > occurredAtMs && (existing.RemovedAtMs is null || existing.RemovedAtMs > occurredAtMs))
                return false;
            var applied = false;
            if (occurredAtMs >= existing.JoinedAtMs)
            {
                existing.Role = role;
                existing.JoinedAtMs = occurredAtMs;
                existing.Revision = revision;
                applied = true;
            }
            if (existing.RemovedAtMs is not null)
            {
                // 重新加入：清除移除时间。
                existing.RemovedAtMs = null;
                applied = true;
            }
            if (!applied)
                return false;
            existing.UpdatedAt = now;
            await db.SaveChangesAsync(None);
            return true;
        }

        db.GroupMembers.Add(new LocalGroupMember
        {
            OwnerUserId = ownerUserId,
            ConversationId = conversationId,
            UserId = userId,
            Role = role,
            JoinedAtMs = occurredAtMs,
            RemovedAtMs = null,
            Revision = revision,
            CreatedAt = now,
            UpdatedAt = now
        });
        try
        {
            await db.SaveChangesAsync(None);
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            // 并发插入冲突（多设备同时加入同一成员）：行已存在，
            // 以已存在行的版本单调语义重新应用本次事件（时间更晚则更新角色）。
            var raced = await db.GroupMembers
                .FirstOrDefaultAsync(m => m.OwnerUserId == ownerUserId
                                       && m.ConversationId == conversationId
                                       && m.UserId == userId, None);
            if (raced is null)
                return false;
            if (occurredAtMs > raced.JoinedAtMs)
            {
                raced.Role = role;
                raced.JoinedAtMs = occurredAtMs;
                raced.Revision = revision;
                raced.RemovedAtMs = null;
                raced.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(None);
            }
        }
        return true;
    }

    /// <summary>成员被移除/退出落库（RemovedAtMs 单调）：仅当事件时间晚于已记录移除时间才应用。</summary>
    public Task<bool> MarkGroupMemberRemovedAsync(
        long ownerUserId, string conversationId, long userId, long occurredAtMs, long revision) =>
        WriteAsync(() => MarkGroupMemberRemovedAsyncImpl(ownerUserId, conversationId, userId, occurredAtMs, revision));

    private async Task<bool> MarkGroupMemberRemovedAsyncImpl(
        long ownerUserId, string conversationId, long userId, long occurredAtMs, long revision)
    {
        await using var db = await contextFactory.CreateDbContextAsync(None);
        var now = DateTime.UtcNow;
        var existing = await db.GroupMembers
            .FirstOrDefaultAsync(m => m.OwnerUserId == ownerUserId
                                   && m.ConversationId == conversationId
                                   && m.UserId == userId, None);
        if (existing is null)
            return false; // 未知成员移除：无本地记录可标记

        // 版本单调：仅接受更晚的移除（已移除的旧事件重放不得复活成员）。
        if (existing.RemovedAtMs is > 0 && existing.RemovedAtMs >= occurredAtMs)
            return false;
        existing.RemovedAtMs = occurredAtMs;
        existing.Revision = revision;
        existing.UpdatedAt = now;
        await db.SaveChangesAsync(None);
        return true;
    }

    /// <summary>查询活跃成员列表（未被移除），按加入时间升序。</summary>
    public Task<List<LocalGroupMember>> GetGroupMembersAsync(long ownerUserId, string conversationId) =>
        WriteAsync(() => GetGroupMembersAsyncImpl(ownerUserId, conversationId));

    private async Task<List<LocalGroupMember>> GetGroupMembersAsyncImpl(long ownerUserId, string conversationId)
    {
        await using var db = await contextFactory.CreateDbContextAsync(None);
        return await db.GroupMembers
            .Where(m => m.OwnerUserId == ownerUserId
                     && m.ConversationId == conversationId
                     && m.RemovedAtMs == null)
            .OrderBy(m => m.JoinedAtMs)
            .ToListAsync(None);
    }

    /// <summary>
    /// 活跃成员分页（升序游标）：返回 JoinedAtMs 大于 afterJoinedAtMs 的前 limit 条。
    /// 大群成员列表虚拟化分页的仓储层支持。
    /// </summary>
    public Task<List<LocalGroupMember>> GetGroupMembersPageAsync(
        long ownerUserId, string conversationId, int limit, long? afterJoinedAtMs = null) =>
        WriteAsync(() => GetGroupMembersPageAsyncImpl(ownerUserId, conversationId, limit, afterJoinedAtMs));

    private async Task<List<LocalGroupMember>> GetGroupMembersPageAsyncImpl(
        long ownerUserId, string conversationId, int limit, long? afterJoinedAtMs)
    {
        await using var db = await contextFactory.CreateDbContextAsync(None);
        var query = db.GroupMembers
            .Where(m => m.OwnerUserId == ownerUserId
                     && m.ConversationId == conversationId
                     && m.RemovedAtMs == null);
        if (afterJoinedAtMs is not null)
            query = query.Where(m => m.JoinedAtMs > afterJoinedAtMs);
        return await query
            .OrderBy(m => m.JoinedAtMs)
            .Take(limit)
            .ToListAsync(None);
    }

    /// <summary>群状态 upsert（标题/修订版本单调）：仅当事件时间晚于已记录时间才应用。</summary>
    public Task<bool> UpsertGroupStateAsync(
        long ownerUserId, string conversationId, string? title,
        long occurredAtMs, long memberRevision, long conversationRevision) =>
        WriteAsync(() => UpsertGroupStateAsyncImpl(ownerUserId, conversationId, title, occurredAtMs, memberRevision, conversationRevision));

    private async Task<bool> UpsertGroupStateAsyncImpl(
        long ownerUserId, string conversationId, string? title,
        long occurredAtMs, long memberRevision, long conversationRevision)
    {
        await using var db = await contextFactory.CreateDbContextAsync(None);
        var now = DateTime.UtcNow;
        var existing = await db.GroupStates
            .FirstOrDefaultAsync(g => g.OwnerUserId == ownerUserId && g.ConversationId == conversationId, None);
        if (existing is not null)
        {
            if (existing.LastEventAtMs >= occurredAtMs)
                return false; // 重放/乱序：更早事件不覆盖
            existing.Title = title;
            existing.MemberRevision = memberRevision;
            existing.ConversationRevision = conversationRevision;
            existing.LastEventAtMs = occurredAtMs;
            existing.UpdatedAt = now;
            await db.SaveChangesAsync(None);
            return true;
        }

        db.GroupStates.Add(new LocalGroupState
        {
            OwnerUserId = ownerUserId,
            ConversationId = conversationId,
            Title = title,
            MemberRevision = memberRevision,
            ConversationRevision = conversationRevision,
            LastEventAtMs = occurredAtMs,
            CreatedAt = now,
            UpdatedAt = now
        });
        await db.SaveChangesAsync(None);
        return true;
    }

    /// <summary>群解散 tombstone：DissolvedAtMs 单调，仅接受更晚的事件。</summary>
    public Task<bool> MarkGroupDissolvedAsync(long ownerUserId, string conversationId, long occurredAtMs, long revision) =>
        WriteAsync(() => MarkGroupDissolvedAsyncImpl(ownerUserId, conversationId, occurredAtMs, revision));

    private async Task<bool> MarkGroupDissolvedAsyncImpl(long ownerUserId, string conversationId, long occurredAtMs, long revision)
    {
        await using var db = await contextFactory.CreateDbContextAsync(None);
        var now = DateTime.UtcNow;
        var existing = await db.GroupStates
            .FirstOrDefaultAsync(g => g.OwnerUserId == ownerUserId && g.ConversationId == conversationId, None);
        if (existing is not null)
        {
            if (existing.DissolvedAtMs is > 0 && existing.DissolvedAtMs >= occurredAtMs)
                return false;
            existing.DissolvedAtMs = occurredAtMs;
            existing.ConversationRevision = revision;
            existing.LastEventAtMs = occurredAtMs;
            existing.UpdatedAt = now;
            await db.SaveChangesAsync(None);
            return true;
        }

        db.GroupStates.Add(new LocalGroupState
        {
            OwnerUserId = ownerUserId,
            ConversationId = conversationId,
            LastEventAtMs = occurredAtMs,
            ConversationRevision = revision,
            DissolvedAtMs = occurredAtMs,
            CreatedAt = now,
            UpdatedAt = now
        });
        await db.SaveChangesAsync(None);
        return true;
    }

    /// <summary>查询群状态。</summary>
    public Task<LocalGroupState?> GetGroupStateAsync(long ownerUserId, string conversationId) =>
        WriteAsync(() => GetGroupStateAsyncImpl(ownerUserId, conversationId));

    private async Task<LocalGroupState?> GetGroupStateAsyncImpl(long ownerUserId, string conversationId)
    {
        await using var db = await contextFactory.CreateDbContextAsync(None);
        return await db.GroupStates
            .FirstOrDefaultAsync(g => g.OwnerUserId == ownerUserId && g.ConversationId == conversationId, None);
    }

    /// <summary>
    /// 群成员搜索（活跃成员）：按 UserId 前缀 或 好友显示名/备注包含查询词 匹配。
    /// 搜索词中的 LIKE 通配符（%/_）转义，杜绝注入。
    /// </summary>
    public Task<List<LocalGroupMember>> SearchGroupMembersAsync(
        long ownerUserId, string conversationId, string query, int limit = 50) =>
        WriteAsync(() => SearchGroupMembersAsyncImpl(ownerUserId, conversationId, query, limit));

    private async Task<List<LocalGroupMember>> SearchGroupMembersAsyncImpl(
        long ownerUserId, string conversationId, string query, int limit)
    {
        var q = query.Trim();
        if (q.Length == 0 || limit <= 0)
            return new List<LocalGroupMember>();

        // LIKE 转义：\% \_ \\（SQLite 默认 ESCAPE '\'）
        var escaped = q
            .Replace("\\", "\\\\")
            .Replace("%", "\\%")
            .Replace("_", "\\_");
        var prefix = escaped + "%";
        var contains = "%" + escaped + "%";

        await using var db = await contextFactory.CreateDbContextAsync(None);
        return await db.GroupMembers
            .Where(m => m.OwnerUserId == ownerUserId
                     && m.ConversationId == conversationId
                     && m.RemovedAtMs == null)
            .Where(m => EF.Functions.Like(m.UserId.ToString(), prefix)
                     || db.Friends.Any(f => f.OwnerUserId == ownerUserId
                                         && f.FriendId == m.UserId
                                         && (EF.Functions.Like(f.DisplayName ?? string.Empty, contains)
                                             || EF.Functions.Like(f.FriendName, contains))))
            .OrderBy(m => m.JoinedAtMs)
            .Take(limit)
            .ToListAsync(None);
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
            existingOutbox.MessageId = outbox.MessageId;
            existingOutbox.ConversationId = outbox.ConversationId;
            existingOutbox.TargetUserId = outbox.TargetUserId;
            existingOutbox.Content = outbox.Content;
            existingOutbox.AttachmentIdsJson = outbox.AttachmentIdsJson;
            existingOutbox.ReplyToMessageId = outbox.ReplyToMessageId;
            existingOutbox.ReplyToSenderUserId = outbox.ReplyToSenderUserId;
            existingOutbox.ReplyToPreview = outbox.ReplyToPreview;
            existingOutbox.ForwardedFromMessageId = outbox.ForwardedFromMessageId;
            existingOutbox.ForwardedFromSenderUserId = outbox.ForwardedFromSenderUserId;
            existingOutbox.ForwardedFromPreview = outbox.ForwardedFromPreview;
            existingOutbox.Status = outbox.Status;
            existingOutbox.FailureReason = outbox.FailureReason;
            existingOutbox.QueuedAt = outbox.QueuedAt;
            existingOutbox.SentAt = outbox.SentAt;
            existingOutbox.NextRetryAt = outbox.NextRetryAt;
            existingOutbox.AttemptId = outbox.AttemptId;
            existingOutbox.AttemptStartedAt = outbox.AttemptStartedAt;
            existingOutbox.LeaseUntil = outbox.LeaseUntil;
            existingOutbox.LastErrorCode = outbox.LastErrorCode;
            existingOutbox.FailureKind = outbox.FailureKind;
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
            existingMessage.Content = message.Content;
            existingMessage.ReceivedAtMs = message.ReceivedAtMs;
            existingMessage.DeliveredAtMs = message.DeliveredAtMs;
            existingMessage.ReadAtMs = message.ReadAtMs;
            existingMessage.RecalledAtMs = message.RecalledAtMs;
            existingMessage.EditVersion = message.EditVersion;
            existingMessage.EditedAtMs = message.EditedAtMs;
            existingMessage.AttachmentsJson = message.AttachmentsJson;
            existingMessage.Status = message.Status;
            existingMessage.FailureReason = message.FailureReason;
            existingMessage.UpdatedAt = message.UpdatedAt;
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
        existing.Status = status;
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
                existing.AfterMessageId = cursor.AfterMessageId;
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
            existing.LastReadAtMs = readState.LastReadAtMs;
            existing.UnreadCount = readState.UnreadCount;
            existing.UpdatedAt = readState.UpdatedAt;
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
        // 严格状态机：仅 Sent/Delivered 可推进为 Read，覆盖不了 Queued/Sending/Failed/Recalled。
        var query = db.Messages.Where(m => m.OwnerUserId == ownerUserId
                                         && m.ConversationId == conversationId
                                         && m.SenderUserId == ownerUserId
                                         && (m.Status == MessageStatus.Sent || m.Status == MessageStatus.Delivered));
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
        }
        if (existing is not null)
        {
            existing.AttachmentId = attachment.AttachmentId ?? existing.AttachmentId;
            existing.ClientAttachmentId = attachment.ClientAttachmentId ?? existing.ClientAttachmentId;
            existing.MessageId = attachment.MessageId ?? existing.MessageId;
            existing.ConversationId = attachment.ConversationId ?? existing.ConversationId;
            existing.FileName = attachment.FileName ?? existing.FileName;
            existing.ContentType = attachment.ContentType;
            existing.SizeBytes = attachment.SizeBytes;
            existing.Sha256 = attachment.Sha256 ?? existing.Sha256;
            existing.DownloadPath = attachment.DownloadPath ?? existing.DownloadPath;
            existing.ObjectKey = attachment.ObjectKey ?? existing.ObjectKey;
            existing.ThumbnailPath = attachment.ThumbnailPath ?? existing.ThumbnailPath;
            existing.LocalCachePath = attachment.LocalCachePath ?? existing.LocalCachePath;
            existing.LocalThumbnailPath = attachment.LocalThumbnailPath ?? existing.LocalThumbnailPath;
            existing.LocalUploadingPath = attachment.LocalUploadingPath ?? existing.LocalUploadingPath;
            existing.RetryCount = attachment.RetryCount;
            existing.Status = attachment.Status;
            existing.FailureReason = attachment.FailureReason ?? existing.FailureReason;
            existing.UpdatedAt = DateTime.UtcNow;
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

    /// <summary>查询可恢复的附件：上传中（Uploading）与可重试失败（Failed）均需恢复，放弃（Abandoned）除外。</summary>
    public async Task<List<LocalAttachment>> GetRecoverableAttachmentsAsync(long ownerUserId)
    {
        await using var db = await contextFactory.CreateDbContextAsync(None);
        return await db.Attachments
            .AsNoTracking()
            .Where(a => a.OwnerUserId == ownerUserId
                && (a.Status == AttachmentStatus.Uploading || a.Status == AttachmentStatus.Failed))
            .OrderBy(a => a.CreatedAt)
            .ToListAsync(None);
    }
}
