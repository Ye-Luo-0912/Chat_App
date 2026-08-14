namespace Chat_App.Infrastructure.Models;

/// <summary>按账户和列表类型隔离的关系增量 opaque 水位。</summary>
public sealed class LocalRelationshipWatermark
{
    public long Id { get; set; }
    public long OwnerUserId { get; set; }
    public byte ListType { get; set; }
    public long AfterSequence { get; set; }
    public DateTime UpdatedAt { get; set; }
}
