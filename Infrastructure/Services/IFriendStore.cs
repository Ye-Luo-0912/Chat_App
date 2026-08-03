using Chat_App.Infrastructure.Models;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Chat_App.Infrastructure.Services;

/// <summary>
/// 统一好友数据源：通讯录与会话列表共享同一份好友投影。
/// 本地立即展示 + 后台服务端增量同步（Upsert / Tombstone 删除）。
/// 远端新增、删除、备注、分组、拉黑变化均可进入共享列表。
/// </summary>
public interface IFriendStore
{
    /// <summary>
    /// 本地优先加载（立即返回），同时后台触发服务端增量同步
    /// （本地已有数据也不再"永不请求服务器"）。
    /// </summary>
    Task<IReadOnlyList<LocalFriend>> LoadAsync(CancellationToken ct = default);

    /// <summary>
    /// 服务端权威增量同步：远端 Upsert（含备注/分组/Tombstone 复活），
    /// 本地存在但远端缺失 → Tombstone 标记删除。完成后触发 <see cref="FriendsChanged"/>。
    /// 防重入：已有同步在进行时直接返回当前快照。
    /// </summary>
    Task<IReadOnlyList<LocalFriend>> SyncFromServerAsync(CancellationToken ct = default);

    /// <summary>好友投影变化（同步完成/本地操作后触发）。UI 订阅后重新投影。</summary>
    event EventHandler? FriendsChanged;

    /// <summary>当前账户快照（可能含 Tombstone 行，由 UI 层过滤展示）。</summary>
    IReadOnlyList<LocalFriend> Snapshot { get; }

    /// <summary>退出登录/切账户时清空快照与所有权标记。</summary>
    void Reset();
}
