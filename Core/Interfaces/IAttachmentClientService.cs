using Core.Contracts.Attachments;
using ChatApp.Contracts.Http.Attachments;

namespace Core.Interfaces;

/// <summary>HTTP 附件：预签名 → 上传 → 确认 → 鉴权下载；可选放弃。</summary>
public interface IAttachmentClientService
{
    /// <summary>请求上传预签名票据（含 UploadUrl、AttachmentId 等）。</summary>
    Task<AttachmentPresignResponse> PresignAsync(
        AttachmentPresignRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// 上传文件内容。相对 UploadUrl 走鉴权 API；绝对 URL（S3 预签名）不带 Bearer。
    /// </summary>
    Task UploadAsync(
        AttachmentPresignResponse ticket,
        Stream content,
        string contentType,
        long contentLength,
        IProgress<AttachmentUploadProgress>? progress = null,
        CancellationToken ct = default);

    /// <summary>确认上传完成，服务端校验文件完整性并标记为可用。</summary>
    Task<ConfirmAttachmentResponse> ConfirmAsync(
        ConfirmAttachmentRequest request,
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

    /// <summary>放弃未完成的上传，释放服务端临时资源。</summary>
    Task AbandonAsync(string attachmentId, CancellationToken ct = default);

    /// <summary>
    /// 下载附件内容。支持 Range 断点续传（<paramref name="rangeFrom"/>/<paramref name="rangeTo"/>）。
    /// 返回结果包含流与元信息。
    /// </summary>
    /// <param name="attachmentIdOrHint">附件 Id 或下载提示。</param>
    /// <param name="rangeFrom">Range 起始字节（可选）。</param>
    /// <param name="rangeTo">Range 结束字节（可选）。</param>
    Task<AttachmentDownloadResult> DownloadAsync(
        string attachmentIdOrHint,
        long? rangeFrom = null,
        long? rangeTo = null,
        CancellationToken ct = default);
}
