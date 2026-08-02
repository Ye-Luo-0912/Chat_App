using Chat_App.Infrastructure.Persistence;
using Chat_App.Shared.Extensions;
using Chat_App.Services;
using Core.Interfaces;
using Chat_App.Infrastructure.Models;
using Serilog;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Chat_App.Presentation.ViewModels.Chat;

public sealed class ChatFriendLoader : IChatFriendLoader
{
    private readonly IDatabaseService _databaseService;
    private readonly IFriendshipService _friendshipService;
    private readonly ICurrentUserContext _currentUserContext;

    public ChatFriendLoader(
        IDatabaseService databaseService,
        IFriendshipService friendshipService,
        ICurrentUserContext currentUserContext)
    {
        _databaseService = databaseService;
        _friendshipService = friendshipService;
        _currentUserContext = currentUserContext;
    }

    public async Task<IReadOnlyList<LocalFriend>> LoadAsync(CancellationToken ct = default)
    {
        // 按当前账户过滤本地好友，避免跨账户数据污染。
        var ownerUserId = _currentUserContext.RequireUserId();
        var localFriends = await _databaseService.GetFriendsAsync(ownerUserId);
        if (localFriends.Count > 0)
        {
            Log.Information("已从本地加载 {Count} 个好友", localFriends.Count);
            return localFriends;
        }

        Log.Information("本地无好友数据，从服务器拉取");
        var remoteFriends = new List<LocalFriend>();

        await foreach (var friendDto in _friendshipService.GetAllFriendsAsync(ct).WithCancellation(ct))
        {
            remoteFriends.Add(friendDto.ToLocalFriend(ownerUserId));
        }

        if (remoteFriends.Count == 0)
        {
            Log.Warning("从服务器拉取好友列表为空");
            return remoteFriends;
        }

        await _databaseService.AddFriendAsync(remoteFriends);
        Log.Information("已从服务器拉取并缓存 {Count} 个好友", remoteFriends.Count);
        return remoteFriends;
    }

    /// <summary>
    /// 后台增量同步：以服务端列表为权威全量比对。
    /// 远端存在 → 全字段 Upsert（含备注/分组变化、Tombstone 复活）；
    /// 远端缺失 → Tombstone 标记 IsDeleted（保留行以支撑历史会话）。
    /// 返回合并后的完整列表（含已删除项，由 UI 层过滤展示）。
    /// </summary>
    public async Task<IReadOnlyList<LocalFriend>> SyncFromServerAsync(CancellationToken ct = default)
    {
        var ownerUserId = _currentUserContext.RequireUserId();
        var remoteFriends = new List<LocalFriend>();

        await foreach (var friendDto in _friendshipService.GetAllFriendsAsync(ct).WithCancellation(ct))
        {
            remoteFriends.Add(friendDto.ToLocalFriend(ownerUserId));
        }

        var merged = new List<LocalFriend>();
        if (remoteFriends.Count > 0)
        {
            await _databaseService.AddFriendAsync(remoteFriends);
            merged.AddRange(remoteFriends);
            Log.Information("好友后台同步完成: 远端 {Count} 个", remoteFriends.Count);
        }

        // 本地存在但远端已删除 → Tombstone（仅当远端列表非空时判定删除，
        // 避免服务端临时不可用返回空列表导致全部误删）。
        if (remoteFriends.Count == 0)
            return merged;

        var localFriends = await _databaseService.GetFriendsAsync(ownerUserId);
        var remoteIds = remoteFriends.Select(f => f.FriendId).ToHashSet();
        foreach (var local in localFriends)
        {
            if (local.IsDeleted) continue;
            if (!remoteIds.Contains(local.FriendId))
            {
                await _databaseService.MarkFriendDeletedAsync(ownerUserId, local.FriendId);
                local.IsDeleted = true;
                merged.Add(local);
                Log.Information("好友已被服务端移除，标记删除: {FriendId}", local.FriendId);
            }
        }

        return merged;
    }
}
