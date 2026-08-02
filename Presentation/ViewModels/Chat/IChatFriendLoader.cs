using Chat_App.Infrastructure.Models;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Chat_App.Presentation.ViewModels.Chat;

public interface IChatFriendLoader
{
    Task<IReadOnlyList<LocalFriend>> LoadAsync(CancellationToken ct = default);

    /// <summary>
    /// 后台增量同步好友列表：服务端为权威，Upsert 变化、Tombstone 已删除。
    /// 返回合并后的完整列表（含 IsDeleted 项，UI 自行过滤）。
    /// </summary>
    Task<IReadOnlyList<LocalFriend>> SyncFromServerAsync(CancellationToken ct = default);
}
