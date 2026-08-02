using System;
using System.Threading;
using Core.Interfaces;
using Core.Models;

namespace Chat_App.Infrastructure.Services;

/// <summary>
/// 当前用户上下文：原子替换的不可变快照实现。
/// 登录/退出/切换账户整体替换快照并递增 Generation，
/// 任何读方在任意时刻都能拿到一致状态，不存在字段间不一致的中间态。
/// </summary>
public sealed class CurrentUserContext : ICurrentUserState
{
    private UserSessionSnapshot _snapshot = UserSessionSnapshot.Empty;

    public UserSessionSnapshot Snapshot => Volatile.Read(ref _snapshot);

    public long Generation => Snapshot.Generation;

    public long? UserId
    {
        get
        {
            var s = Snapshot;
            return s.IsEmpty ? null : s.OwnerUserId;
        }
    }

    public string? UserName => Snapshot.UserName;

    public bool IsAuthenticated => !Snapshot.IsEmpty;

    public bool HasUserId => !Snapshot.IsEmpty;

    public bool TryGetUserId(out long userId)
    {
        var s = Snapshot;
        userId = s.OwnerUserId;
        return !s.IsEmpty;
    }

    public long RequireUserId()
    {
        if (TryGetUserId(out var userId))
            return userId;
        throw new InvalidOperationException("当前用户未登录或用户ID不可用");
    }

    public void SetCurrentUser(long userId, string? userName)
    {
        if (userId <= 0)
            throw new ArgumentOutOfRangeException(nameof(userId), "用户ID必须大于 0");

        var prev = Snapshot;
        Volatile.Write(ref _snapshot, new UserSessionSnapshot(
            userId, prev.Generation + 1, userName, prev.SessionId, prev.DeviceHash));
    }

    public void SetSession(string? sessionId, ulong? deviceHash)
    {
        var prev = Snapshot;
        if (prev.IsEmpty)
            return;
        Volatile.Write(ref _snapshot, prev with { SessionId = sessionId, DeviceHash = deviceHash });
    }

    public void Clear()
    {
        var prev = Snapshot;
        Volatile.Write(ref _snapshot, new UserSessionSnapshot(
            0, prev.Generation + 1, null, null, null));
    }
}
