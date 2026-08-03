namespace Core.Models.DTO;

public sealed class MessageRecallRequestDto : IRequestDto
{
    public string? RequestId { get; set; }
    public required string MessageId { get; set; }
}

public sealed class MessageRecallAcknowledgementDto
{
    public string RequestId { get; set; } = string.Empty;
    public string? MessageId { get; set; }
    public bool Succeeded { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public string? ConversationId { get; set; }
    public long? RecalledAtMs { get; set; }
}

public sealed class MessageRecalledUpdateDto
{
    public string MessageId { get; set; } = string.Empty;
    public string? ConversationId { get; set; }
    public long SenderUserId { get; set; }
    public long ReceiverUserId { get; set; }
    public long RecalledAtMs { get; set; }
}
