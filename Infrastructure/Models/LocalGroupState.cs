namespace Chat_App.Infrastructure.Models;

/// <summary>
/// 群聊本地状态实体（账户隔离：OwnerUserId 键）。
/// 记录群标题与成员/会话修订版本，供成员事件与群消息的版本比较与 UI 投影。
/// </summary>
public class LocalGroupState
{
    public long Id { get; set; }

    /// <summary>账户隔离键。</summary>
    public long OwnerUserId { get; set; }

    public string ConversationId { get; set; } = string.Empty;

    public string? Title { get; set; }

    /// <summary>成员列表修订版本（服务端预留；当前以最近事件 OccurredAtMs 为单调键）。</summary>
    public long MemberRevision { get; set; }

    /// <summary>会话修订版本（服务端预留）。</summary>
    public long ConversationRevision { get; set; }

    /// <summary>最近一次群事件时间（Unix 毫秒）——重放/乱序保护的单调键。</summary>
    public long LastEventAtMs { get; set; }

    /// <summary>群解散时间（Unix 毫秒）；null 表示未解散。</summary>
    public long? DissolvedAtMs { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
