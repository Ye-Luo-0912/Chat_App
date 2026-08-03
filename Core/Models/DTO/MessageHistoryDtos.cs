using System.Collections.Generic;

namespace Core.Models.DTO;

/// <summary>显式历史消息拉取请求。注意：SyncBootstrap 内嵌的 catch-up 不用此 DTO。</summary>
public sealed class MessageHistoryRequestDto : IRequestDto
{
    public string? RequestId { get; set; }
    public required string ConversationId { get; set; }

    /// <summary>游标：返回此时间点之前的消息（Unix 毫秒）。null 表示从最新开始。</summary>
    public long? BeforeReceivedAtMs { get; set; }

    /// <summary>游标：返回此 MessageId 之前的消息（不含该 Id）。null 表示从最新开始。</summary>
    public string? BeforeMessageId { get; set; }

    public int Limit { get; set; } = 50;
}

/// <summary>显式历史消息分页响应。复用 MessageHistoryItemDto 与 MessageHistoryCursorDto。</summary>
public sealed class MessageHistoryPageDto
{
    public string? RequestId { get; set; }
    public bool Succeeded { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public required string ConversationId { get; set; }
    public IReadOnlyList<MessageHistoryItemDto> Items { get; set; } = [];
    public bool HasMore { get; set; }
    public MessageHistoryCursorDto? NextCursor { get; set; }
}