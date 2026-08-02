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
}