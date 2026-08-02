namespace Chat_App.Infrastructure.Models;

/// <summary>
/// 本地好友申请记录（收到的 / 发出的）
/// </summary>
public class LocalFriendRequest
{
    public long RequesterId { get; set; }
    public string RequesterName { get; set; } = string.Empty;
    public string? RequesterAvatarUrl { get; set; }

    public long TargetUserId { get; set; }
    public string TargetUserName { get; set; } = string.Empty;
    public string? TargetAvatarUrl { get; set; }

    public string? Message { get; set; }
    public DateTime CreatedAt { get; set; }

    public string RequesterInitial =>
        !string.IsNullOrEmpty(RequesterName) ? RequesterName[..1] : "?";
    public string TargetInitial =>
        !string.IsNullOrEmpty(TargetUserName) ? TargetUserName[..1] : "?";
}

/// <summary>
/// 本地黑名单记录
/// </summary>
public class LocalBlockedUser
{
    public long BlockedUserId { get; set; }
    public string BlockedUserName { get; set; } = string.Empty;
    public string? AvatarUrl { get; set; }
    public DateTime BlockedAt { get; set; }

    public string Initial =>
        !string.IsNullOrEmpty(BlockedUserName) ? BlockedUserName[..1] : "?";
}

