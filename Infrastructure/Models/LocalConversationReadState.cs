namespace Infrastructure.Models;

/// <summary>
/// 会话已读状态实体（P0-6 持久化聊天系统）。
/// 与 LocalConversation 分离，专门跟踪已读水位。
/// </summary>
public class LocalConversationReadState
{
    public long Id { get; set; }

    /// <summary>账户隔离键。</summary>
    public long OwnerUserId { get; set; }

    /// <summary>会话 Id，非空。</summary>
    public string ConversationId { get; set; } = string.Empty;

    public string? LastReadMessageId { get; set; }

    public long? LastReadAtMs { get; set; }

    public int UnreadCount { get; set; }

    public DateTime UpdatedAt { get; set; }
}
