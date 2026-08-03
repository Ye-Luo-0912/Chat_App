using Chat_App.Infrastructure.Services;
using Core.Contracts.Friends;
using System.Collections.Generic;
using System.Threading;

namespace Chat_App.Services;

/// <summary>
/// 将主应用的 IFriendshipService 适配为 Infrastructure 的 IFriendFetcher，
/// 使 FriendStore（统一好友数据源）可在 Infrastructure 层独立使用。
/// </summary>
public sealed class FriendFetcherAdapter : IFriendFetcher
{
    private readonly IFriendshipService _api;

    public FriendFetcherAdapter(IFriendshipService api)
    {
        _api = api;
    }

    public IAsyncEnumerable<FriendDto> GetAllFriendsAsync(CancellationToken cancellationToken = default)
        => _api.GetAllFriendsAsync(cancellationToken);
}
