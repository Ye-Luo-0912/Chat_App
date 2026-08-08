using Core.Contracts.Friends;
using ChatApp.Contracts.Http.Friends;
using System.Collections.Generic;
using System.Threading;

namespace Chat_App.Infrastructure.Services;

/// <summary>
/// 好友数据获取抽象：供 Infrastructure 内部（如 FriendStore）发起服务端好友列表拉取，
/// 具体实现（适配 IFriendshipService）由宿主应用注入。
/// </summary>
public interface IFriendFetcher
{
    IAsyncEnumerable<FriendDto> GetAllFriendsAsync(CancellationToken cancellationToken = default);
}
