namespace Core.Models.DTO;

public sealed class ConversationSetPrefsRequestDto
{
    public string? RequestId { get; set; }
    public required string ConversationId { get; set; }
    public bool? Pinned { get; set; }
    public bool? Muted { get; set; }
    public long? MutedUntilMs { get; set; }
}

public sealed class ConversationSetPrefsResponseDto
{
    public string RequestId { get; set; } = string.Empty;
    public bool Succeeded { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public string? ConversationId { get; set; }
    public bool IsPinned { get; set; }
    public bool IsMuted { get; set; }
    public long? MutedUntilMs { get; set; }
    public bool Changed { get; set; }
}
