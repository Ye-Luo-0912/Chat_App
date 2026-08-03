using Core.Models.DTO;

namespace Chat_App.Infrastructure.Models;

/// <summary>
/// 群成员本地领域实体（账户隔离：OwnerUserId 键）。
/// 由群成员网络事件经协调器有序持久化，版本（OccurredAtMs）单调比较防重放/乱序。
/// </summary>
public class LocalGroupMember
{
    public long Id { get; set; }

    /// <summary>账户隔离键。</summary>
    public long OwnerUserId { get; set; }

    public string ConversationId { get; set; } = string.Empty;

    public long UserId { get; set; }

    /// <summary>当前角色（见 ConversationMemberRole）。</summary>
    public byte Role { get; set; }

    /// <summary>加入时间（Unix 毫秒）——版本单调键：仅接受更晚的事件。</summary>
    public long JoinedAtMs { get; set; }

    /// <summary>被移除/退出时间（Unix 毫秒）；null 表示仍在群内。</summary>
    public long? RemovedAtMs { get; set; }

    /// <summary>成员列表版本（服务端预留；当前以 OccurredAtMs 作单调键）。</summary>
    public long Revision { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public bool IsActive => RemovedAtMs is null;
}
