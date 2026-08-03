using Chat_App.Infrastructure.Services;
using Chat_App.Infrastructure.Models;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Chat_App.Presentation.ViewModels.Chat;

/// <summary>
/// 会话列表好友加载器：委托共享 <see cref="IFriendStore"/>，
/// 与通讯录页共享同一份好友投影，本地立即展示 + 后台增量同步。
/// </summary>
public sealed class ChatFriendLoader : IChatFriendLoader
{
    private readonly IFriendStore _friendStore;

    public ChatFriendLoader(IFriendStore friendStore)
    {
        _friendStore = friendStore;
    }

    public Task<IReadOnlyList<LocalFriend>> LoadAsync(CancellationToken ct = default)
        => _friendStore.LoadAsync(ct);

    public Task<IReadOnlyList<LocalFriend>> SyncFromServerAsync(CancellationToken ct = default)
        => _friendStore.SyncFromServerAsync(ct);
}
