using Core.Models;

namespace Chat_App.Infrastructure.Models;

/// <summary>
/// 发送 Outbox 实体（持久化聊天系统）。
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

    /// <summary>会话类型：0=直聊，1=群聊（见 ConversationTypeDto）。发送按 ConversationId 寻址。</summary>
    public byte ConversationType { get; set; }

    /// <summary>直聊对端用户 Id：直聊必填，群聊为空（群消息按会话寻址，无对端用户）。</summary>
    public long? TargetUserId { get; set; }

    public string? Content { get; set; }

    /// <summary>附件 Id 列表的 JSON。</summary>
    public string? AttachmentIdsJson { get; set; }

    public string? ReplyToMessageId { get; set; }

    public long? ReplyToSenderUserId { get; set; }

    public string? ReplyToPreview { get; set; }

    public string? ForwardedFromMessageId { get; set; }

    public long? ForwardedFromSenderUserId { get; set; }

    public string? ForwardedFromPreview { get; set; }

    /// <summary>Outbox 发送状态。见 <see cref="Core.Models.OutboxStatus"/>。</summary>
    public OutboxStatus Status { get; set; }

    public string? FailureReason { get; set; }

    public int RetryCount { get; set; }

    public DateTime QueuedAt { get; set; }

    public DateTime? SentAt { get; set; }

    public DateTime? NextRetryAt { get; set; }

    // ---- 发送租约----
    /// <summary>当前发送尝试 Id（Guid）：认领时生成，用于检测陈旧 Sending。</summary>
    public string? AttemptId { get; set; }

    /// <summary>当前尝试开始时间（UTC）。</summary>
    public DateTime? AttemptStartedAt { get; set; }

    /// <summary>租约到期时间（UTC）：到期后 Sending 可被回收重新入队。</summary>
    public DateTime? LeaseUntil { get; set; }

    /// <summary>最近一次失败的服务端错误码/异常类型标识。</summary>
    public string? LastErrorCode { get; set; }

    /// <summary>失败分类：可重试/永久。</summary>
    public OutboxFailureKind FailureKind { get; set; }
}
