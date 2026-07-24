using System;

namespace Core.Interfaces
{
    public interface ICurrentUserContext
    {
        long? UserId { get; }
        string? UserName { get; }
        bool IsAuthenticated { get; }
        bool HasUserId { get; }
        long RequireUserId();
        bool TryGetUserId(out long userId);
    }
}
