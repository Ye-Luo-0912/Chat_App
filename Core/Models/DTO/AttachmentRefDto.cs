namespace Core.Models.DTO;

/// <summary>TCP 线协议附件引用（与 Gateway AttachmentRef 对齐）。</summary>
public sealed class AttachmentRefDto
{
    public int RefVersion { get; set; } = 1;

    public string AttachmentId { get; set; } = string.Empty;

    public string? FileName { get; set; }

    public string ContentType { get; set; } = "application/octet-stream";

    public long SizeBytes { get; set; }

    /// <summary>0=Scanning，1=Available。</summary>
    public short Status { get; set; }

    /// <summary>通常为 attachmentId → GET /api/attachments/{id}/download。</summary>
    public string? DownloadApiHint { get; set; }

    public string? DownloadToken { get; set; }

    public string? ThumbnailApiHint { get; set; }

    public string DisplayName =>
        string.IsNullOrWhiteSpace(FileName) ? AttachmentId : FileName;
}
