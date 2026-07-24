namespace Core.Models.DTO;

public sealed class MessageEditRequestDto
{
    public string? RequestId { get; set; }
    public required string MessageId { get; set; }
    public required string Content { get; set; }
}

public sealed class MessageEditAcknowledgementDto
{
    public string RequestId { get; set; } = string.Empty;
    public string? MessageId { get; set; }
    public bool Succeeded { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public string? ConversationId { get; set; }
    public string? Content { get; set; }
    public int? EditVersion { get; set; }
    public long? EditedAtMs { get; set; }
}

public sealed class MessageEditedUpdateDto
{
    public string MessageId { get; set; } = string.Empty;
    public string? ConversationId { get; set; }
    public long SenderUserId { get; set; }
    public long ReceiverUserId { get; set; }
    public string Content { get; set; } = string.Empty;
    public int EditVersion { get; set; }
    public long EditedAtMs { get; set; }
}
