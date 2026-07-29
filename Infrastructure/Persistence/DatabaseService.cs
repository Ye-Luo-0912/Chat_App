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
}
