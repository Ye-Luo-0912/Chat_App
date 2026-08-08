namespace Core.Contracts.Attachments;

public sealed class AttachmentUploadProgress
{
    public string AttachmentId { get; init; } = string.Empty;
    public long BytesTransferred { get; init; }
    public long TotalBytes { get; init; }
    public double Percent => TotalBytes <= 0
        ? 0
        : Math.Clamp(100.0 * BytesTransferred / TotalBytes, 0, 100);
}

public sealed class AttachmentUploadResult
{
    public required string AttachmentId { get; init; }
    public required string DownloadPath { get; init; }
    public required string ObjectKey { get; init; }
    public required string ContentType { get; init; }
    public required long SizeBytes { get; init; }
    public string? OriginalName { get; init; }
}

public sealed class AttachmentDownloadResult
{
    public required Stream Content { get; init; }
    public required string ContentType { get; init; }
    public long? ContentLength { get; init; }
    public string? FileName { get; init; }
    /// <summary>是否 206 Partial Content（服务端按 Range 返回了部分内容）。</summary>
    public bool IsPartialContent { get; init; }
}
