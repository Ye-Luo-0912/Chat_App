namespace Core.Models.DTO;

/// <summary>群成员角色（与服务端 ConversationMemberRole 一致）。</summary>
public enum ConversationMemberRole : byte
{
    Owner = 1,
    Admin = 2,
    Member = 3
}

/// <summary>群成员条目。</summary>
public sealed class ConversationMemberItemDto
{
    public long UserId { get; set; }
    public ConversationMemberRole Role { get; set; } = ConversationMemberRole.Member;
    public long JoinedAtMs { get; set; }
}

public sealed class CreateGroupRequestDto : IRequestDto
{
    public string? RequestId { get; set; }
    public string Title { get; set; } = string.Empty;
    public IReadOnlyList<long>? MemberUserIds { get; set; }
}

public sealed class CreateGroupResponseDto : IRequestDto
{
    public string? RequestId { get; set; }
    public bool Succeeded { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public string? ConversationId { get; set; }
    public string? Title { get; set; }
    public IReadOnlyList<ConversationMemberItemDto>? Members { get; set; }
}

public sealed class AddGroupMembersRequestDto : IRequestDto
{
    public string? RequestId { get; set; }
    public string ConversationId { get; set; } = string.Empty;
    public IReadOnlyList<long> MemberUserIds { get; set; } = [];
}

public sealed class AddGroupMembersResponseDto : IRequestDto
{
    public string? RequestId { get; set; }
    public bool Succeeded { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public string? ConversationId { get; set; }
    public IReadOnlyList<ConversationMemberItemDto>? Members { get; set; }
}

public sealed class RemoveGroupMemberRequestDto : IRequestDto
{
    public string? RequestId { get; set; }
    public string ConversationId { get; set; } = string.Empty;
    public long TargetUserId { get; set; }
}

public sealed class RemoveGroupMemberResponseDto : IRequestDto
{
    public string? RequestId { get; set; }
    public bool Succeeded { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public string? ConversationId { get; set; }
}

public sealed class LeaveGroupRequestDto : IRequestDto
{
    public string? RequestId { get; set; }
    public string ConversationId { get; set; } = string.Empty;
}

public sealed class LeaveGroupResponseDto : IRequestDto
{
    public string? RequestId { get; set; }
    public bool Succeeded { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public string? ConversationId { get; set; }
}

public sealed class ChangeMemberRoleRequestDto : IRequestDto
{
    public string? RequestId { get; set; }
    public string ConversationId { get; set; } = string.Empty;
    public long TargetUserId { get; set; }
    public ConversationMemberRole NewRole { get; set; } = ConversationMemberRole.Member;
}

public sealed class ChangeMemberRoleResponseDto : IRequestDto
{
    public string? RequestId { get; set; }
    public bool Succeeded { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public string? ConversationId { get; set; }
}

public sealed class ListGroupMembersRequestDto : IRequestDto
{
    public string? RequestId { get; set; }
    public string ConversationId { get; set; } = string.Empty;
    public int? PageSize { get; set; }
    public string? Cursor { get; set; }
}

public sealed class ListGroupMembersResponseDto : IRequestDto
{
    public string? RequestId { get; set; }
    public bool Succeeded { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public string? ConversationId { get; set; }
    public IReadOnlyList<ConversationMemberItemDto>? Members { get; set; }
    public string? NextCursor { get; set; }
    public bool HasMore { get; set; }
}

/// <summary>S2C：成员加入群聊通知。</summary>
public sealed class MemberJoinedUpdateDto
{
    public string ConversationId { get; set; } = string.Empty;
    public long UserId { get; set; }
    public ConversationMemberRole Role { get; set; } = ConversationMemberRole.Member;
    public long ActorUserId { get; set; }
    public string? Title { get; set; }
    public long OccurredAtMs { get; set; }
}

/// <summary>S2C：成员主动退出群聊通知。</summary>
public sealed class MemberLeftUpdateDto
{
    public string ConversationId { get; set; } = string.Empty;
    public long UserId { get; set; }
    public long OccurredAtMs { get; set; }
}

/// <summary>S2C：成员被移出群聊通知。</summary>
public sealed class MemberRemovedUpdateDto
{
    public string ConversationId { get; set; } = string.Empty;
    public long UserId { get; set; }
    public long ActorUserId { get; set; }
    public long OccurredAtMs { get; set; }
}

/// <summary>S2C：成员角色变更通知。</summary>
public sealed class RoleChangedUpdateDto
{
    public string ConversationId { get; set; } = string.Empty;
    public long UserId { get; set; }
    public ConversationMemberRole NewRole { get; set; }
    public ConversationMemberRole? PreviousRole { get; set; }
    public long ActorUserId { get; set; }
    public long OccurredAtMs { get; set; }
}

/// <summary>S2C：成员批量加入通知（替代逐成员 MemberJoined 的聚合事件）。</summary>
public sealed class MembersAddedUpdateDto
{
    public string ConversationId { get; set; } = string.Empty;
    public long[] AddedUserIds { get; set; } = [];
    public long ActorUserId { get; set; }
    public string? Title { get; set; }
    public long OccurredAtMs { get; set; }
}

/// <summary>S2C：会话解散通知。</summary>
public sealed class ConversationDissolvedUpdateDto
{
    public string ConversationId { get; set; } = string.Empty;
    public long ActorUserId { get; set; }
    public long OccurredAtMs { get; set; }
}
