using Core.Contracts.Attachments;

namespace Core.Interfaces;

/// <summary>HTTP 附件：预签名 → 上传 → 确认 → 鉴权下载；可选放弃。</summary>
public interface IAttachmentClientService
{
    Task<AttachmentPresignResponseDto> PresignAsync(
        AttachmentPresignRequestDto request,
        CancellationToken ct = default);

    /// <summary>
    /// 上传文件内容。相对 UploadUrl 走鉴权 API；绝对 URL（S3 预签名）不带 Bearer。
    /// </summary>
    Task UploadAsync(
        AttachmentPresignResponseDto ticket,
        Stream content,
        string contentType,
        long contentLength,
        IProgress<AttachmentUploadProgress>? progress = null,
        CancellationToken ct = default);

    Task<ConfirmAttachmentResponseDto> ConfirmAsync(
        ConfirmAttachmentRequestDto request,
        CancellationToken ct = default);

    /// <summary>
    /// 完整流水线：presign → upload（可重试）→ confirm。取消时尽量 abandon。
    /// </summary>
    Task<AttachmentUploadResult> UploadAndConfirmAsync(
        Stream content,
        string contentType,
        long contentLength,
        string? originalName = null,
        string? clientAttachmentId = null,
        IProgress<AttachmentUploadProgress>? progress = null,
        int maxAttempts = 3,
        string? sha256 = null,
        CancellationToken ct = default);

    Task AbandonAsync(string attachmentId, CancellationToken ct = default);

    Task<AttachmentDownloadResult> DownloadAsync(
        string attachmentIdOrHint,
        long? rangeFrom = null,
        long? rangeTo = null,
        CancellationToken ct = default);
}
