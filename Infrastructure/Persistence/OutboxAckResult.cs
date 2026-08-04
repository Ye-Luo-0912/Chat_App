namespace Chat_App.Infrastructure.Persistence;

/// <summary>
/// ACK 单事务处理结果。
/// </summary>
/// <param name="OutboxUpdated">状态机允许转换并已更新；false 表示重复/乱序 ACK 被拒绝。</param>
/// <param name="ConversationId">对应 outbox 记录的会话 Id（事件广播用）。</param>
/// <param name="ServerMessageId">ACK 接受时回填的服务端消息 Id。</param>
/// <param name="AlreadySent">重复 ACK：outbox 已是 Sent（幂等，无需更新、不发布事件、不告警）。</param>
/// <param name="QueuedAtUtc">ack 对应 outbox 记录的入队时刻（ACK 端到端延迟指标用，UTC）。</param>
public readonly record struct OutboxAckResult(
    bool OutboxUpdated,
    string? ConversationId,
    string? ServerMessageId,
    bool AlreadySent = false,
    DateTime? QueuedAtUtc = null);
