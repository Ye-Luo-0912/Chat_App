using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Chat_App.Infrastructure.Persistence;
using Chat_App.Shared.Extensions;
using Core.Contracts.Friends;
using Core.Interfaces;
using Infrastructure.Models;
using Serilog;

namespace Chat_App.Services;

/// <summary>
/// 通讯录页面的应用服务实现。
/// 负责：DTO -> Local 转换、服务端调用、本地缓存更新、返回 UI 需要的数据。
/// </summary>
public class FriendsPageService : IFriendsPageService
{
    private readonly IFriendshipService _api;
    private readonly IDatabaseService _db;
    private readonly ICurrentUserContext _currentUser;

    // 内存缓存
    private List<LocalFriend> _friendsCache = [];
    private List<LocalFriendRequest> _incomingRequestsCache = [];
    private List<LocalFriendRequest> _outgoingRequestsCache = [];
    private List<LocalBlockedUser> _blockedUsersCache = [];

    public FriendsPageService(
        IFriendshipService api,
        IDatabaseService db,
        ICurrentUserContext currentUser)
    {
        _api = api;
        _db = db;
        _currentUser = currentUser;
    }

    // ── 数据加载 ──────────────────────────────────────

    public async Task<IReadOnlyList<LocalFriend>> LoadFriendsAsync(CancellationToken ct = default)
    {
        try
        {
            var ownerUserId = _currentUser.RequireUserId();

            // 从服务端获取
            var friendDtos = new List<FriendDto>();
            await foreach (var dto in _api.GetAllFriendsAsync(ct).ConfigureAwait(false))
            {
                friendDtos.Add(dto);
            }

            // 使用扩展方法批量转换为本地模型
            _friendsCache = friendDtos.ToLocalFriends(ownerUserId);

            // 更新本地数据库
            if (_friendsCache.Count > 0)
            {
                await _db.AddFriendAsync(_friendsCache).ConfigureAwait(false);
            }

            Log.Information("加载好友列表完成，共 {Count} 人", _friendsCache.Count);
            return _friendsCache.AsReadOnly();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "加载好友列表失败");
            // 尝试从本地数据库加载
            var local = await _db.GetFriendsAsync().ConfigureAwait(false);
            _friendsCache = local;
            return local.AsReadOnly();
        }
    }

    public async Task<IReadOnlyList<LocalFriendRequest>> LoadIncomingRequestsAsync(CancellationToken ct = default)
    {
        var result = await _api.GetIncomingRequestsAsync(ct).ConfigureAwait(false);

        if (result.IsSuccess && result.Data is not null)
        {
            _incomingRequestsCache = result.Data.ToLocalFriendRequests();
            Log.Information("加载收到的好友申请完成，共 {Count} 条", _incomingRequestsCache.Count);
        }
        else
        {
            Log.Warning("加载收到的好友申请失败: {Message}", result.Message);
        }

        return _incomingRequestsCache.AsReadOnly();
    }

    public async Task<IReadOnlyList<LocalFriendRequest>> LoadOutgoingRequestsAsync(CancellationToken ct = default)
    {
        var result = await _api.GetOutgoingRequestsAsync(ct).ConfigureAwait(false);

        if (result.IsSuccess && result.Data is not null)
        {
            _outgoingRequestsCache = result.Data.ToLocalFriendRequests();
            Log.Information("加载发出的好友申请完成，共 {Count} 条", _outgoingRequestsCache.Count);
        }
        else
        {
            Log.Warning("加载发出的好友申请失败: {Message}", result.Message);
        }

        return _outgoingRequestsCache.AsReadOnly();
    }

    public async Task<IReadOnlyList<LocalBlockedUser>> LoadBlockedUsersAsync(CancellationToken ct = default)
    {
        var result = await _api.GetBlockedUsersAsync(ct).ConfigureAwait(false);

        if (result.IsSuccess && result.Data is not null)
        {
            _blockedUsersCache = result.Data.ToBlockedUsers();
            Log.Information("加载黑名单完成，共 {Count} 人", _blockedUsersCache.Count);
        }
        else
        {
            Log.Warning("加载黑名单失败: {Message}", result.Message);
        }

        return _blockedUsersCache.AsReadOnly();
    }

    // ── 操作 ──────────────────────────────────────────

    public async Task<SendFriendRequestResult> SendFriendRequestAsync(
        long targetUserId, string? message, CancellationToken ct = default)
    {
        var result = await _api.SendFriendRequestAsync(targetUserId, message, ct).ConfigureAwait(false);

        if (result.IsSuccess)
        {
            Log.Information("发送好友申请成功 -> {TargetUserId}", targetUserId);
        }
        else
        {
            Log.Warning("发送好友申请失败: {Message}", result.Message);
        }

        return result;
    }

    public async Task<OperationResult<FriendDto>> AcceptRequestAsync(
        long requesterId, CancellationToken ct = default)
    {
        var result = await _api.AcceptRequestAsync(requesterId, ct).ConfigureAwait(false);

        if (result.IsSuccess && result.Data is not null)
        {
            // 从申请列表移除
            _incomingRequestsCache.RemoveAll(r => r.RequesterId == requesterId);

            // 使用扩展方法转换并添加到好友列表
            var ownerUserId = _currentUser.RequireUserId();
            var newFriend = result.Data.ToLocalFriend(ownerUserId);
            _friendsCache.Add(newFriend);

            // 更新本地数据库
            await _db.AddFriendAsync([newFriend]).ConfigureAwait(false);

            Log.Information("接受好友申请成功，新增好友: {FriendName}", newFriend.FriendName);
        }
        else
        {
            Log.Warning("接受好友申请失败: {Message}", result.Message);
        }

        return result;
    }

    public async Task<OperationResult> DeclineRequestAsync(
        long requesterId, CancellationToken ct = default)
    {
        var result = await _api.DeclineRequestAsync(requesterId, ct).ConfigureAwait(false);

        if (result.IsSuccess)
        {
            _incomingRequestsCache.RemoveAll(r => r.RequesterId == requesterId);
            Log.Information("拒绝好友申请成功: {RequesterId}", requesterId);
        }
        else
        {
            Log.Warning("拒绝好友申请失败: {Message}", result.Message);
        }

        return result;
    }

    public async Task<OperationResult> DeleteFriendAsync(
        long friendId, CancellationToken ct = default)
    {
        var result = await _api.DeleteFriendAsync(friendId, ct).ConfigureAwait(false);

        if (result.IsSuccess)
        {
            _friendsCache.RemoveAll(f => f.FriendId == friendId);
            await _db.DeleteFriendAsync(friendId).ConfigureAwait(false);
            Log.Information("删除好友成功: {FriendId}", friendId);
        }
        else
        {
            Log.Warning("删除好友失败: {Message}", result.Message);
        }

        return result;
    }

    public async Task<OperationResult> UnblockUserAsync(
        long blockedUserId, CancellationToken ct = default)
    {
        var result = await _api.UnblockUserAsync(blockedUserId, ct).ConfigureAwait(false);

        if (result.IsSuccess)
        {
            _blockedUsersCache.RemoveAll(u => u.BlockedUserId == blockedUserId);
            Log.Information("解除拉黑成功: {BlockedUserId}", blockedUserId);
        }
        else
        {
            Log.Warning("解除拉黑失败: {Message}", result.Message);
        }

        return result;
    }
}
