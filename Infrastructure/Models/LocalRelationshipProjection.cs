namespace Chat_App.Infrastructure.Models;

/// <summary>
/// 客户端本地关系列表投影。列表由服务端只读同步填充，关系变更仍由 HTTP mutation 负责。
/// </summary>
public sealed class LocalRelationshipProjection
{
    public long Id { get; set; }
    public long OwnerUserId { get; set; }
    public byte ListType { get; set; }
    public string ResourceId { get; set; } = string.Empty;
    public long UserId { get; set; }
    public string? Status { get; set; }
    public string? Message { get; set; }
    public long CreatedAtMs { get; set; }
    public long OccurredAtMs { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime UpdatedAt { get; set; }
}
