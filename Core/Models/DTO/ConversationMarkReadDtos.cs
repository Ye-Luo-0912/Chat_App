using System.Collections.Generic;

namespace Core.Models.DTO;

/// <summary>标记会话已读请求（110）。</summary>
public sealed class ConversationMarkReadRequestDto
{
    public string? RequestId { get; set; }
    public required string ConversationId { get; set; }
    public string? LastReadMessageId { get; set; }
    public long? LastReadAtMs { get; set; }
}

/// <summary>标记会话已读响应（111）。</summary>
public sealed class ConversationMarkReadResponseDto
{
    public string? RequestId { get; set; }
    public bool Succeeded { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public required string ConversationId { get; set; }
    public int UnreadCount { get; set; }
}

/// <summary>未读数变更推送（113）。</summary>
public sealed class UnreadCountChangedDto
{
    public required string ConversationId { get; set; }
    public int UnreadCount { get; set; }
    public string? LastReadMessageId { get; set; }
    public long? LastReadAtMs { get; set; }
}