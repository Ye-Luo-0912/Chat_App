namespace Core.Models;

/// <summary>
/// 不可变会话标识：所有异步链路（入站队列、Outbox、同步、附件恢复）必须携带，
/// 消费时用 Generation 校验连接代际，防止跨账户/跨连接写入。
/// </summary>
public readonly record struct SessionStamp(long OwnerUserId, long Generation, Guid ConnectionId)
{
    /// <summary>无效会话（未登录）。</summary>
    public static readonly SessionStamp None = new(0, 0, Guid.Empty);

    public bool IsValid => OwnerUserId > 0 && ConnectionId != Guid.Empty;

    public bool SameOwner(long userId) => OwnerUserId == userId;
}
