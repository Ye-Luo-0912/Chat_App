namespace Core.Contracts.Attachments;

public sealed class AttachmentPresignRequestDto
{
    public string ContentType { get; set; } = string.Empty;
    public long ContentLength { get; set; }
    public string? OriginalName { get; set; }
    public string? ClientAttachmentId { get; set; }

    /// <summary>文件 SHA256 哈希（十六进制小写）。服务端可用于秒传去重。</summary>
    public string? Sha256 { get; set; }
}

public sealed class AttachmentPresignResponseDto
{
    public string AttachmentId { get; set; } = string.Empty;
    public string UploadUrl { get; set; } = string.Empty;
    public string DownloadPath { get; set; } = string.Empty;
    public string ObjectKey { get; set; } = string.Empty;
    public string Ticket { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>是否秒传命中（服务端已有同 hash 文件，无需上传）。</summary>
    public bool Deduplicated { get; set; }
}

public sealed class ConfirmAttachmentRequestDto
{
    public string ObjectKey { get; set; } = string.Empty;
    public string? Ticket { get; set; }
    public string? AttachmentId { get; set; }
}

public sealed class ConfirmAttachmentResponseDto
{
    public string AttachmentId { get; set; } = string.Empty;
    public string DownloadPath { get; set; } = string.Empty;
    public string ObjectKey { get; set; } = string.Empty;
}

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
}
