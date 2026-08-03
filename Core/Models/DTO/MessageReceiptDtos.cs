using System;
using System.Collections.Generic;

namespace Core.Models.DTO;

/// <summary>已读回执（103）。对端已读我的消息时，服务端下发；也用于上行告知服务端我已读。</summary>
public sealed class MessageReceiptDto : IRequestDto
{
    /// <summary>请求 Id（上行时携带，用于匹配 MessageReceiptAck）。</summary>
    public string? RequestId { get; set; }

    /// <summary>会话 Id。</summary>
    public string? ConversationId { get; set; }

    /// <summary>已读到的消息 Id（水位）。</summary>
    public string? LastReadMessageId { get; set; }

    /// <summary>已读时间（Unix 毫秒）。</summary>
    public long? LastReadAtMs { get; set; }

    /// <summary>已读消息发送方（通常是当前用户）。</summary>
    public long? ReaderUserId { get; set; }

    /// <summary>接收方（通常是对端）。</summary>
    public long? ReceiverUserId { get; set; }
}

/// <summary>已读回执 ACK（104）。服务端对我们上行 MessageReceipt 的确认。</summary>
public sealed class MessageReceiptAckDto
{
    public string? RequestId { get; set; }
    public bool Accepted { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
}

/// <summary>已读状态更新推送（105）。服务端推送批量已读水位变更。</summary>
public sealed class MessageReceiptUpdatedDto
{
    public string? ConversationId { get; set; }

    /// <summary>新的已读水位消息 Id。</summary>
    public string? LastReadMessageId { get; set; }

    /// <summary>新的已读时间（Unix 毫秒）。</summary>
    public long? LastReadAtMs { get; set; }

    /// <summary>已读方用户 Id。</summary>
    public long? ReaderUserId { get; set; }
}