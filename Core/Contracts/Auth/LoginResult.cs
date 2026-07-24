using Core.Contracts.Common;

namespace Core.Contracts.Auth;

public class LoginResult
{
    // ---------- 流程状态 ----------
    public bool IsSuccess { get; init; }
    public LoginCheckStatus LoginCheckStatus { get; init; }
    public string? ErrorMessage { get; init; }

    // ---------- 令牌对 ----------
    public string? AccessToken { get; init; }
    public DateTime AccessTokenExpiresAtUtc { get; init; }
    public string? RefreshToken { get; init; }
    public DateTime RefreshTokenExpiresAtUtc { get; init; }

    // ---------- 会话元信息 ----------
    /// <summary>服务端登录完成时间（UTC）。</summary>
    public DateTimeOffset LoginAt { get; init; }
    /// <summary>上次成功登录时间（UTC），为空表示首次登录。</summary>
    public DateTimeOffset? PreviousLoginDate { get; init; }
    /// <summary>本次登录的会话唯一标识；TCP 握手时携带，用于会话关联。</summary>
    public string? SessionId { get; init; }
    /// <summary>
    /// 设备指纹 64 位哈希（服务端计算后直接下发）。
    /// TCP 握手时原样携带，服务端做整数比对，无需客户端重新计算。
    /// </summary>
    public ulong? DeviceIdHash { get; init; }

    // ---------- 用户画像快照 ----------
    public long? UserId { get; init; }
    public string? UserName { get; init; }
    public string? Email { get; init; }
    public string? AvatarUrl { get; init; }
    public string? Signature { get; init; }
    public bool Gender { get; init; }
    public string? Region { get; init; }
    public UserStatus Status { get; init; }

    // ---------- 实时通信连接端点 ----------
    public ServerEndPoint? Server { get; init; }
}

public enum LoginCheckStatus : byte
{
    Success = 1,
    InvalidCredentials = 2,
    LockedOut = 3,
    NotAllowed = 4,
    RequiresTwoFactor = 5
}

public enum UserStatus : byte
{
    Offline = 0,
    Online = 1,
    Away = 2,
    Busy = 3,
    Invisible = 4
}

