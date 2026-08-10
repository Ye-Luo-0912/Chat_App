namespace Chat_App.Infrastructure.Models;

/// <summary>
/// 同步水位实体（持久化聊天系统）。
/// </summary>
public class LocalSyncCursor
{
    public long Id { get; set; }

    /// <summary>账户隔离键。</summary>
    public long OwnerUserId { get; set; }

    /// <summary>会话 Id，非空。</summary>
    public string ConversationId { get; set; } = string.Empty;

    /// <summary>
    /// v1 schema/协议字段名；当前语义是服务端 changed_at_ms 水位，而不是原始接收时间。
    /// </summary>
    public long AfterReceivedAtMs { get; set; }

    /// <summary>非空。</summary>
    public string AfterMessageId { get; set; } = string.Empty;

    public DateTime UpdatedAt { get; set; }
}
