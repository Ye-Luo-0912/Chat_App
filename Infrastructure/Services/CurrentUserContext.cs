using Core.Interfaces;
using System;

namespace Chat_App.Infrastructure.Services;

public sealed class CurrentUserContext : ICurrentUserState
{
    private long? _userId;
    private string? _userName;

    public long? UserId => _userId;
    public string? UserName => _userName;
    public bool IsAuthenticated => HasUserId;
    public bool HasUserId => _userId.HasValue;

    public bool TryGetUserId(out long userId)
    {
        if (_userId.HasValue)
        {
            userId = _userId.Value;
            return true;
        }

        userId = default;
        return false;
    }

    public long RequireUserId()
    {
        if (TryGetUserId(out var userId))
        {
            return userId;
        }

        throw new InvalidOperationException("当前用户未登录或用户ID不可用");
    }

    public void Clear()
    {
        _userId = null;
        _userName = null;
    }

    public void SetCurrentUser(long userId, string? userName)
    {
        if (userId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(userId), "用户ID必须大于 0");
        }

        _userId = userId;
        _userName = userName;
    }
}
