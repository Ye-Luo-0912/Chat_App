namespace Core.Models;

/// <summary>
/// 消息生命周期状态。用于 LocalMessage.Status、Models.Message.Status。
/// 数值映射：Queued=0, Sending=1, Sent=2, Delivered=3, Read=4, Failed=5, Recalled=6。
/// </summary>
public enum MessageStatus : byte
{
    /// <summary>已入队待发送（outbox 初始状态）。</summary>
    Queued = 0,
    /// <summary>正在发送中。</summary>
    Sending = 1,
    /// <summary>已发送至服务端（收到 MessageAck.Accepted）。</summary>
    Sent = 2,
    /// <summary>已投递至对端（收到下行回执）。</summary>
    Delivered = 3,
    /// <summary>对端已读。</summary>
    Read = 4,
    /// <summary>发送失败或被拒绝。</summary>
    Failed = 5,
    /// <summary>已撤回。</summary>
    Recalled = 6
}