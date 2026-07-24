using Core.Contracts.Friends.Enums;

namespace Core.Contracts.Friends;

public class FriendDto
{
    public long FriendId { get; set; }
    public string? FriendName { get; set; }
    public string? Note { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? AvatarUrl { get; set; }
    public int? GroupId { get; set; }
    public string? GroupName { get; set; }
    public DateTime? LastInteractionAt { get; set; }
    public DateTimeOffset? LastSeenAt { get; set; }
}

public class BlockedUserDto
{
    public long UserId { get; set; }
    public string? UserName { get; set; }
    public string? AvatarUrl { get; set; }
    public DateTime BlockedAt { get; set; }
}

public class FriendRequestDto
{
    public long RequestId { get; set; }
    public long RequesterId { get; set; }
    public long TargetUserId { get; set; }
    public string? Message { get; set; }
    public RequestStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
}
