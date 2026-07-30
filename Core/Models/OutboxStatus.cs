namespace Core.Models;

/// <summary>
/// Outbox 发送状态。用于 LocalOutboxMessage.Status。
/// 与 MessageStatus 部分重叠但独立维护：Outbox 不需要 Read/Recalled。
/// </summary>
public enum OutboxStatus : byte
{
    /// <summary>已入队。</summary>
    Queued = 0,
    /// <summary>发送中。</summary>
    Sending = 1,
    /// <summary>已发送（ack 成功）。</summary>
    Sent = 2,
    /// <summary>发送失败，可重试。</summary>
    Failed = 3,
    /// <summary>已取消（用户主动撤销）。</summary>
    Cancelled = 4
}