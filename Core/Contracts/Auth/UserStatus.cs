namespace Core.Contracts.Auth;

/// <summary>Local presentation/persistence status. Kept out of the HTTP wire assembly intentionally.</summary>
public enum UserStatus : byte
{
    Offline = 0,
    Online = 1,
    Away = 2,
    Busy = 3,
    DoNotDisturb = 4
}
