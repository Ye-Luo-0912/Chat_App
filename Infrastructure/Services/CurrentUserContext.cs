using System;
using System.Threading;
using Core.Interfaces;
using Core.Models;

namespace Chat_App.Infrastructure.Services;

/// <summary>
/// 当前用户上下文：原子 CAS 替换的不可变快照实现。
/// 所有更新走 CAS 循环（CompareExchange），并发 Set/SetAuthenticatedSession/Clear 不会互相覆盖
/// （read-modify-write 原子化）。
/// 三代际：账户代际（切换递增）/连接代际（传输重连，由调用方传入）/令牌代际（刷新递增）。
/// </summary>
public sealed class CurrentUserContext : ICurrentUserState
{
    private sealed class Box(UserSessionSnapshot value)
    {
        public readonly UserSessionSnapshot Value = value;
    }

    private Box _box = new(UserSessionSnapshot.Empty);

    public UserSessionSnapshot Snapshot => Volatile.Read(ref _box).Value;

    public long Generation => Snapshot.Generation;

    public long ConnectionGeneration => Snapshot.ConnectionGeneration;

    public long TokenGeneration => Snapshot.TokenGeneration;

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

    /// <summary>
    /// 原子 read-modify-write：基于当前快照构造新快照并以 CAS 提交，
    /// 失败则重读重试，杜绝并发更新的丢失更新问题。
    /// </summary>
    private void Update(Func<UserSessionSnapshot, UserSessionSnapshot> transform)
    {
        var spin = new SpinWait();
        while (true)
        {
            var prev = Volatile.Read(ref _box);
            var next = new Box(transform(prev.Value));
            if (ReferenceEquals(Interlocked.CompareExchange(ref _box, next, prev), prev))
                return;
            spin.SpinOnce();
        }
    }

    /// <summary>
    /// 原子设置完整鉴权会话：同一账户的重连不递增账户代际（仅更新连接代际），
    /// 账户切换才递增账户代际。避免"同账户传输重连导致 generation 递增"的误判。
    /// </summary>
    public void SetAuthenticatedSession(
        long userId, string? userName, string? sessionId, ulong? deviceHash, long connectionGeneration)
    {
        if (userId <= 0)
            throw new ArgumentOutOfRangeException(nameof(userId), "用户ID必须大于 0");

        Update(prev =>
        {
            var accountGeneration = prev.OwnerUserId == userId ? prev.Generation : prev.Generation + 1;
            return new UserSessionSnapshot(
                userId, accountGeneration, userName, sessionId, deviceHash,
                connectionGeneration, prev.TokenGeneration);
        });
    }

    /// <summary>仅更新连接代际（传输重连；账户代际不变）。</summary>
    public void BumpConnectionGeneration(long connectionGeneration)
        => Update(prev => prev with { ConnectionGeneration = connectionGeneration });

    /// <summary>令牌刷新：令牌代际递增（账户代际不变）。</summary>
    public void BumpTokenGeneration()
        => Update(prev => prev with { TokenGeneration = prev.TokenGeneration + 1 });

    public void SetCurrentUser(long userId, string? userName)
    {
        if (userId <= 0)
            throw new ArgumentOutOfRangeException(nameof(userId), "用户ID必须大于 0");

        Update(prev => new UserSessionSnapshot(
            userId, prev.Generation + 1, userName, prev.SessionId, prev.DeviceHash,
            prev.ConnectionGeneration, prev.TokenGeneration));
    }

    public void SetSession(string? sessionId, ulong? deviceHash)
    {
        Update(prev =>
        {
            if (prev.IsEmpty)
                return prev;
            return prev with { SessionId = sessionId, DeviceHash = deviceHash };
        });
    }

    public void Clear()
    {
        Update(prev => new UserSessionSnapshot(
            0, prev.Generation + 1, null, null, null,
            prev.ConnectionGeneration, prev.TokenGeneration));
    }
}
