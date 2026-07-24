using Core.Contracts.Auth;
using Core.Models;
using Infrastructure.Data;
using Infrastructure.Models;
using Infrastructure.Models.Context;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Chat_App.Infrastructure.Persistence;

public class DatabaseService(ClientDbContext context) : IDatabaseService
{
    private readonly ClientDbContext _context = context;

    public async Task<List<LocalFriend>> GetFriendsAsync()
    {
        return await _context.Friends
            .AsNoTracking()
            .Select(f => new LocalFriend
            {
                FriendId = f.FriendId,
                FriendName = f.FriendName,
                Status = f.Status,
                AvatarUrl = f.AvatarUrl,
                LastSynced = f.LastSynced
            }).ToListAsync();
    }

    public async Task AddFriendAsync(List<LocalFriend> friend)
    {
        await _context.Friends.AddRangeAsync(friend);
        await _context.SaveChangesAsync();
    }

    public async Task<LocalFriend?> GetFriendByIdAsync(long id)
    {
        return await _context.Friends.AsNoTracking().FirstOrDefaultAsync(f => f.Id == id);
    }

    public async Task UpdateFriendAsync(LocalFriend updatedFriend)
    {
        var friend = await _context.Friends.FirstOrDefaultAsync(f => f.Id == updatedFriend.Id);
        if (friend != null)
        {
            _context.Friends.Update(friend);
            await _context.SaveChangesAsync();
        }
    }

    public async Task DeleteFriendAsync(long id)
    {
        await _context.Friends
            .Where(f => f.Id == id)
            .ExecuteDeleteAsync();
    }

    // ---- 用户信息 ----

    /// <summary>
    /// 保存或更新本地用户信息（登录时全量写入服务端返回数据）。
    /// </summary>
    public async Task SaveUserAsync(LocalUser user)
    {
        var existing = await _context.Users.FirstOrDefaultAsync(u => u.UserId == user.UserId);
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
            await _context.Users.AddAsync(user);
        }
        await _context.SaveChangesAsync();
    }

    public async Task<LocalUser?> GetUserAsync(long userId)
    {
        return await _context.Users.AsNoTracking().FirstOrDefaultAsync(u => u.UserId == userId);
    }

    // ---- Token ----

    public async Task SaveTokenAsync(AuthToken token)
    {
        var oldToken = await _context.Tokens.FirstOrDefaultAsync();
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
            await _context.Tokens.AddAsync(token);
        }
        await _context.SaveChangesAsync();
    }

    public async Task<Token?> GetAccessTokenAsync()
    {
        return await _context.Tokens
            .AsNoTracking()
            .Select(t => new Token
            {
                TokenExpires = t.AccessTokenExpires,
                TokenValue = t.AccessToken
            })
            .FirstOrDefaultAsync();
    }

    public async Task<int> UpdateTokenAsync(AuthToken token)
    {
        return await _context.Tokens
            .Where(f => f.UserId == token.UserId)
            .ExecuteUpdateAsync(t
                => t.SetProperty(authToken => authToken.AccessToken, token.AccessToken)
                    .SetProperty(authToken => authToken.RefreshToken, token.RefreshToken)
                    .SetProperty(authToken => authToken.AccessTokenExpires, token.AccessTokenExpires)
                    .SetProperty(authToken => authToken.RefreshTokenExpires, token.RefreshTokenExpires));
    }

    public async Task<AuthToken?> GetTokenAsync()
    {
        return await _context.Tokens.AsNoTracking().FirstOrDefaultAsync();
    }

    public async Task DeleteTokenAsync()
    {
        await _context.Tokens.ExecuteDeleteAsync();
    }

    // ---- サーバー情報 ----

    /// <summary>
    /// 保存服务器信息：若已存在相同地址+端口的记录则更新，否则新增，避免重复。
    /// </summary>
    public async Task SaveServerInfoAsync(ServerEndpoint serverInfo)
    {
        var existing = await _context.Servers
            .FirstOrDefaultAsync(s => s.ServerIpAddress == serverInfo.ServerIpAddress
                                   && s.ServerPort == serverInfo.ServerPort);
        if (existing is not null)
        {
            existing.ServerName = serverInfo.ServerName;
            existing.IsPrimary = serverInfo.IsPrimary;
            existing.LastConnected = DateTime.UtcNow;
        }
        else
        {
            serverInfo.LastConnected = DateTime.UtcNow;
            await _context.Servers.AddAsync(serverInfo);
        }
        await _context.SaveChangesAsync();
    }

    public async Task<ServerEndpoint?> GetServerInfoAsync()
    {
        return await _context.Servers.AsNoTracking().FirstOrDefaultAsync();
    }

    public async Task DeleteServerInfoAsync()
    {
        await _context.Servers.ExecuteDeleteAsync();
    }
}