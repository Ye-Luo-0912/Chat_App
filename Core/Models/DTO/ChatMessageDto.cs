using System;
using System.Collections.Generic;

namespace Core.Models.DTO;

public class ChatMessageDto
{
    public string? MessageId { get; set; }

    public string? ConversationId { get; set; }

    public long TargetUserId { get; set; }

    public long SenderUserId { get; set; }

    public string? Content { get; set; }

    public DateTime SentUtc { get; set; }

    /// <summary>上行：已确认附件 Id。</summary>
    public IReadOnlyList<string>? AttachmentIds { get; set; }

    /// <summary>下行：附件元数据。</summary>
    public IReadOnlyList<AttachmentRefDto>? Attachments { get; set; }

    public string? ReplyToMessageId { get; set; }
    public long? ReplyToSenderUserId { get; set; }
    public string? ReplyToPreview { get; set; }

    public string? ForwardedFromMessageId { get; set; }
    public long? ForwardedFromSenderUserId { get; set; }
    public string? ForwardedFromPreview { get; set; }

    /// <summary>@提到的用户 Id 列表（群聊场景下使用）。</summary>
    public IReadOnlyList<long>? MentionedUserIds { get; set; }

    /// <summary>@提到的角色（如 "all"、"admin"）；目前仅供展示，无强校验。</summary>
    public IReadOnlyList<string>? MentionedRoles { get; set; }
}
