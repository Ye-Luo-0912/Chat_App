namespace Core.Models;

/// <summary>
/// 消息状态严格状态机：唯一允许的状态转换表。
/// Failed 是发送分支的终态（可重试回 Queued），不是比 Read 更高的成功阶段；
/// 任何进入 Recalled 的转换都是合法的；已撤回为终态。
/// 状态推进一律通过本表判断，禁止依赖枚举数值大小。
/// </summary>
public static class MessageStatusTransitions
{
    /// <summary>
    /// 判断 from → to 是否为合法转换。
    /// 说明：
    /// - Queued → Sending/Sent/Failed/Recalled（发送链路）
    /// - Sending → Sent/Failed/Recalled
    /// - Failed → Queued/Sending/Sent/Recalled（重试/重发/撤回）
    /// - Sent → Delivered/Read/Recalled
    /// - Delivered → Read/Recalled
    /// - Read → Recalled
    /// - Recalled → 无（终态）
    /// 非法示例：Read→Sent、Delivered→Failed、Sent→Queued。
    /// </summary>
    public static bool CanTransition(MessageStatus from, MessageStatus to)
    {
        if (from == to)
            return true;
        if (from == MessageStatus.Recalled)
            return false;

        return to switch
        {
            MessageStatus.Queued => from == MessageStatus.Failed,
            MessageStatus.Sending => from is MessageStatus.Queued or MessageStatus.Failed,
            MessageStatus.Sent => from is MessageStatus.Queued or MessageStatus.Sending or MessageStatus.Failed,
            MessageStatus.Delivered => from == MessageStatus.Sent,
            MessageStatus.Read => from is MessageStatus.Sent or MessageStatus.Delivered,
            MessageStatus.Failed => from is MessageStatus.Queued or MessageStatus.Sending,
            MessageStatus.Recalled => true,
            _ => false
        };
    }

    /// <summary>从目标状态推导允许的源状态集合（供 SQL WHERE 使用）。</summary>
    public static MessageStatus[] AllowedFrom(MessageStatus to)
    {
        var values = (MessageStatus[])Enum.GetValues(typeof(MessageStatus));
        var result = new List<MessageStatus>(values.Length);
        foreach (var s in values)
        {
            if (CanTransition(s, to))
                result.Add(s);
        }
        return result.ToArray();
    }
}
