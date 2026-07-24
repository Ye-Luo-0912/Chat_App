namespace Core.Models.DTO;

public sealed class TypingNotifyDto
{
    public long TargetUserId { get; set; }
    public string? ConversationId { get; set; }
    public bool IsTyping { get; set; }
}

public sealed class TypingUpdateDto
{
    public long SenderUserId { get; set; }
    public string? ConversationId { get; set; }
    public bool IsTyping { get; set; }
}

public sealed class PresenceQueryRequestDto
{
    public string? RequestId { get; set; }
    public IReadOnlyList<long>? UserIds { get; set; }
}

public sealed class PresenceUnwatchRequestDto
{
    public IReadOnlyList<long>? UserIds { get; set; }
}

public sealed class PresenceSnapshotItemDto
{
    public long UserId { get; set; }
    public bool IsOnline { get; set; }
}

public sealed class PresenceSnapshotResponseDto
{
    public string RequestId { get; set; } = string.Empty;
    public IReadOnlyList<PresenceSnapshotItemDto> Items { get; set; } = [];
}

public sealed class PresenceChangedDto
{
    public long UserId { get; set; }
    public bool IsOnline { get; set; }
}
