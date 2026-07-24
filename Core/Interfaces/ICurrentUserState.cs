using System;

namespace Core.Interfaces;

public interface ICurrentUserState : ICurrentUserContext
{
    void SetCurrentUser(long userId, string? userName);
    void Clear();
}
