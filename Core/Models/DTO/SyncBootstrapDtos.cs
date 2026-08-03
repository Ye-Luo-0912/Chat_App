namespace Core.Models.DTO;

public sealed class ConversationSyncWatermarkDto
{
    public required string ConversationId { get; set; }
    public long AfterReceivedAtMs { get; set; }
    public required string AfterMessageId { get; set; }
}

public sealed class SyncBootstrapRequestDto : IRequestDto
{
    public string? RequestId { get; set; }
    public int ListLimit { get; set; } = 50;
    public int HistoryLimitPerConversation { get; set; } = 20;
    public int MaxConversationsWithHistory { get; set; } = 10;
    public IReadOnlyList<ConversationSyncWatermarkDto>? Watermarks { get; set; }
}

public sealed class MessageHistoryItemDto
{
    public string MessageId { get; set; } = string.Empty;
    public string ClientMessageId { get; set; } = string.Empty;
    public long SenderUserId { get; set; }
    public long ReceiverUserId { get; set; }
    public string? ConversationId { get; set; }
    public string Content { get; set; } = string.Empty;
    public long ReceivedAtMs { get; set; }
    public long? DeliveredAtMs { get; set; }
    public long? ReadAtMs { get; set; }
    public long? RecalledAtMs { get; set; }
    public int EditVersion { get; set; } = 1;
    public long? EditedAtMs { get; set; }
    public IReadOnlyList<AttachmentRefDto>? Attachments { get; set; }
    public string? ReplyToMessageId { get; set; }
    public long? ReplyToSenderUserId { get; set; }
    public string? ReplyToPreview { get; set; }
    public string? ForwardedFromMessageId { get; set; }
    public long? ForwardedFromSenderUserId { get; set; }
    public string? ForwardedFromPreview { get; set; }
}

public sealed class MessageHistoryCursorDto
{
    public long ReceivedAtMs { get; set; }
    public string MessageId { get; set; } = string.Empty;
}

public sealed class ConversationHistoryCatchUpDto
{
    public string ConversationId { get; set; } = string.Empty;
    public IReadOnlyList<MessageHistoryItemDto> Items { get; set; } = [];
    public bool HasMore { get; set; }
    public MessageHistoryCursorDto? NextCursor { get; set; }
}

public sealed class SyncBootstrapResponseDto
{
    public string RequestId { get; set; } = string.Empty;
    public bool Succeeded { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public long ServerTimeMs { get; set; }
    public IReadOnlyList<ConversationListItemDto> Conversations { get; set; } = [];
    public ConversationListCursorDto? ConversationsNextCursor { get; set; }
    public bool ConversationsHasMore { get; set; }
    public IReadOnlyList<ConversationHistoryCatchUpDto> CatchUps { get; set; } = [];
}
