namespace Infrastructure.Models;

/// <summary>
/// 发送 Outbox 实体（P0-6 持久化聊天系统）。
/// 与 LocalMessage 一一对应，专门跟踪发送状态。
/// </summary>
public class LocalOutboxMessage
{
    public long Id { get; set; }

    /// <summary>账户隔离键。</summary>
    public long OwnerUserId { get; set; }

    /// <summary>客户端发送 Id，非空，唯一——用于匹配 MessageAck。</summary>
    public string ClientMessageId { get; set; } = string.Empty;

    /// <summary>ack 成功后写入服务端 Id。</summary>
    public string? MessageId { get; set; }

    /// <summary>所属会话 Id，非空。</summary>
    public string ConversationId { get; set; } = string.Empty;

    public long TargetUserId { get; set; }

    public string? Content { get; set; }

    /// <summary>附件 Id 列表的 JSON。</summary>
    public string? AttachmentIdsJson { get; set; }

    public string? ReplyToMessageId { get; set; }

    public long? ReplyToSenderUserId { get; set; }

    public string? ReplyToPreview { get; set; }

    public string? ForwardedFromMessageId { get; set; }

    public long? ForwardedFromSenderUserId { get; set; }

    public string? ForwardedFromPreview { get; set; }

    /// <summary>状态：0=Queued, 1=Sending, 2=Sent, 3=Failed, 4=Cancelled。</summary>
    public byte Status { get; set; }

    public string? FailureReason { get; set; }

    public int RetryCount { get; set; }

    public DateTime QueuedAt { get; set; }

    public DateTime? SentAt { get; set; }

    public DateTime? NextRetryAt { get; set; }
}
