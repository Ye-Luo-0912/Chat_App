namespace Core.Models.DTO;

public sealed class ConversationListRequestDto : IRequestDto
{
    public string? RequestId { get; set; }
    public bool? BeforeIsPinned { get; set; }
    public long? BeforePinnedAtMs { get; set; }
    public long? BeforeLastMessageAtMs { get; set; }
    public string? BeforeConversationId { get; set; }
    public int Limit { get; set; } = 50;
}

public sealed class ConversationListResponseDto
{
    public string RequestId { get; set; } = string.Empty;
    public bool Succeeded { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public IReadOnlyList<ConversationListItemDto> Items { get; set; } = [];
    public ConversationListCursorDto? NextCursor { get; set; }
    public bool HasMore { get; set; }
}

public sealed class ConversationChangedDto
{
    public string ConversationId { get; set; } = string.Empty;
    public ConversationTypeDto Type { get; set; }
    public long? PeerUserId { get; set; }
    public string? Title { get; set; }
    public string? LastMessageId { get; set; }
    public string? LastMessagePreview { get; set; }
    public long? LastMessageAtMs { get; set; }
    public long? LastSenderUserId { get; set; }
    public bool? IsPinned { get; set; }
    public bool? IsMuted { get; set; }
    public long? MutedUntilMs { get; set; }
}
