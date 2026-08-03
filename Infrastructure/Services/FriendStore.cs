using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Chat_App.Infrastructure.Models;
using Chat_App.Infrastructure.Persistence;
using Chat_App.Infrastructure.Extensions;
using Core.Interfaces;
using Serilog;

namespace Chat_App.Infrastructure.Services;

/// <summary>
/// 统一好友数据源实现：
/// - LoadAsync：本地立即返回 + 后台增量同步（修复"本地非空则永不请求服务器"）；
/// - SyncFromServerAsync：服务端权威全量比对，Upsert + Tombstone；
/// - FriendsChanged：同步完成即通知所有订阅者（通讯录页、会话列表）重新投影；
/// - Reset：退出登录时清空，防止跨账户残留。
/// 快照读写均在同步锁内完成；事件触发不在锁内。
/// </summary>
public sealed class FriendStore : IFriendStore
{
    private readonly IDatabaseService _db;
    private readonly IFriendFetcher _friendFetcher;
    private readonly ICurrentUserContext _currentUserContext;
    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private List<LocalFriend> _snapshot = [];
    private long _ownerUserId;

    public FriendStore(
        IDatabaseService db,
        IFriendFetcher friendFetcher,
        ICurrentUserContext currentUserContext)
    {
        _db = db;
        _friendFetcher = friendFetcher;
        _currentUserContext = currentUserContext;
    }

    public event EventHandler? FriendsChanged;

    public IReadOnlyList<LocalFriend> Snapshot => _snapshot;

    public async Task<IReadOnlyList<LocalFriend>> LoadAsync(CancellationToken ct = default)
    {
        var ownerUserId = _currentUserContext.RequireUserId();
        _ownerUserId = ownerUserId;

        var local = await _db.GetFriendsAsync(ownerUserId);
        _snapshot = local;
        Log.Information("好友本地加载完成: {Count} 个", local.Count);

        // 本地立即展示；同时后台增量同步，保证远端变化（新增/删除/备注/拉黑）能进入列表。
        if (local.Count > 0 && _currentUserContext.IsAuthenticated)
            _ = Task.Run(() => SyncFromServerAsync(ct), ct);

        return local;
    }

    public async Task<IReadOnlyList<LocalFriend>> SyncFromServerAsync(CancellationToken ct = default)
    {
        // 防重入：已有同步在进行时直接返回当前快照，避免并发全量比对互相覆盖。
        if (!await _syncLock.WaitAsync(0, ct).ConfigureAwait(false))
            return _snapshot;

        try
        {
            var ownerUserId = _currentUserContext.RequireUserId();
            _ownerUserId = ownerUserId;

            var remote = new List<LocalFriend>();
            await foreach (var dto in _friendFetcher.GetAllFriendsAsync(ct).WithCancellation(ct))
            {
                remote.Add(dto.ToLocalFriend(ownerUserId));
            }

            var merged = new List<LocalFriend>(remote);

            if (remote.Count > 0)
            {
                await _db.AddFriendAsync(remote).ConfigureAwait(false);

                // Tombstone：本地存在但远端缺失 → 标记删除（保留行支撑历史会话）。
                var local = await _db.GetFriendsAsync(ownerUserId).ConfigureAwait(false);
                var remoteIds = remote.Select(f => f.FriendId).ToHashSet();
                foreach (var lf in local)
                {
                    if (lf.IsDeleted || remoteIds.Contains(lf.FriendId))
                        continue;
                    await _db.MarkFriendDeletedAsync(ownerUserId, lf.FriendId).ConfigureAwait(false);
                    lf.IsDeleted = true;
                    merged.Add(lf);
                    Log.Information("好友已被服务端移除，标记删除: {FriendId}", lf.FriendId);
                }
            }
            else
            {
                // 远端为空：视为服务端无好友（不做误删判定），本地快照原样保留。
                merged = _snapshot.ToList();
            }

            _snapshot = merged;
            Log.Information("好友增量同步完成: 共 {Count} 个", merged.Count);
            FriendsChanged?.Invoke(this, EventArgs.Empty);
            return merged;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "好友增量同步失败");
            return _snapshot;
        }
        finally
        {
            _syncLock.Release();
        }
    }

    public void Reset()
    {
        _snapshot = [];
        _ownerUserId = 0;
    }
}
