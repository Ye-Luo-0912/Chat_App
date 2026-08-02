using Core.Models;

namespace Chat_App.Infrastructure.Models;

/// <summary>
/// 本地消息实体（持久化聊天系统）。每条消息一行。纯数据实体，无 INPC。
/// </summary>
public class LocalMessage
{
    public long Id { get; set; }

    /// <summary>账户隔离键。</summary>
    public long OwnerUserId { get; set; }

    /// <summary>服务端消息 Id，可空——outbox 未确认时为 null。</summary>
    public string? MessageId { get; set; }

    /// <summary>客户端发送 Id，用于匹配 MessageAck。</summary>
    public string? ClientMessageId { get; set; }

    /// <summary>所属会话 Id，非空。</summary>
    public string ConversationId { get; set; } = string.Empty;

    public long SenderUserId { get; set; }

    /// <summary>直聊场景的接收方。</summary>
    public long ReceiverUserId { get; set; }

    public string Content { get; set; } = string.Empty;

    /// <summary>服务端接收时间（Unix 毫秒）。</summary>
    public long ReceivedAtMs { get; set; }

    public long? DeliveredAtMs { get; set; }

    public long? ReadAtMs { get; set; }

    public long? RecalledAtMs { get; set; }

    public int EditVersion { get; set; } = 1;

    public long? EditedAtMs { get; set; }

    /// <summary>附件 JSON 序列化。</summary>
    public string? AttachmentsJson { get; set; }

    public string? ReplyToMessageId { get; set; }

    public long? ReplyToSenderUserId { get; set; }

    public string? ReplyToPreview { get; set; }

    public string? ForwardedFromMessageId { get; set; }

    public long? ForwardedFromSenderUserId { get; set; }

    public string? ForwardedFromPreview { get; set; }

    /// <summary>消息状态。见 <see cref="Core.Models.MessageStatus"/>。</summary>
    public MessageStatus Status { get; set; }

    /// <summary>失败原因。</summary>
    public string? FailureReason { get; set; }

    public int RetryCount { get; set; }

    /// <summary>本地创建时间。</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>本地最后更新时间。</summary>
    public DateTime UpdatedAt { get; set; }
}
