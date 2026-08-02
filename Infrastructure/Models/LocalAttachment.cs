using System;
using System.ComponentModel.DataAnnotations;

namespace Chat_App.Infrastructure.Models;

/// <summary>
/// 附件状态枚举（底层 byte，与数据库列兼容）。
/// </summary>
public enum AttachmentStatus : byte
{
    /// <summary>上传中：文件已落盘到 uploading 目录，尚未得到服务端确认。</summary>
    Uploading = 0,

    /// <summary>可用：服务端已确认，可被下载/引用。</summary>
    Available = 1,

    /// <summary>失败：上传或确认过程出错，可重试。</summary>
    Failed = 2,

    /// <summary>放弃：永久失败或本地文件丢失，不再重试。</summary>
    Abandoned = 3
}

/// <summary>
/// 本地附件元数据记录。与 LocalMessage 多对一关联（通过 MessageId）。
/// 也用于上传任务跟踪（MessageId 为空时表示未关联消息的上传中附件）。
/// </summary>
public sealed class LocalAttachment
{
    public long Id { get; set; }

    /// <summary>所属账户用户 Id（数据隔离）。</summary>
    public long OwnerUserId { get; set; }

    /// <summary>服务端附件唯一 Id。上传确认后回填。</summary>
    public string? AttachmentId { get; set; }

    /// <summary>客户端临时 Id（上传前生成，用于关联上传任务）。</summary>
    public string? ClientAttachmentId { get; set; }

    /// <summary>关联的消息 Id（LocalMessage.MessageId）。上传中尚未发送时为 null。</summary>
    public string? MessageId { get; set; }

    /// <summary>关联的会话 Id。</summary>
    public string? ConversationId { get; set; }

    /// <summary>原始文件名。</summary>
    public string? FileName { get; set; }

    /// <summary>MIME 类型。</summary>
    public string ContentType { get; set; } = "application/octet-stream";

    /// <summary>文件大小（字节）。</summary>
    public long SizeBytes { get; set; }

    /// <summary>文件 SHA256 哈希（十六进制字符串）。用于秒传去重。</summary>
    public string? Sha256 { get; set; }

    /// <summary>下载 API 路径（presign confirm 后回填）。</summary>
    public string? DownloadPath { get; set; }

    /// <summary>对象存储 Key。</summary>
    public string? ObjectKey { get; set; }

    /// <summary>缩略图 API 路径（服务端提供时回填）。</summary>
    public string? ThumbnailPath { get; set; }

    /// <summary>本地缓存路径（下载后落盘的相对路径，相对于附件根目录）。</summary>
    public string? LocalCachePath { get; set; }

    /// <summary>本地缩略图缓存路径。</summary>
    public string? LocalThumbnailPath { get; set; }

    /// <summary>本地上传临时文件相对路径（相对于 uploading 目录）。上传完成后清空。</summary>
    public string? LocalUploadingPath { get; set; }

    /// <summary>上传重试次数。</summary>
    public int RetryCount { get; set; }

    /// <summary>附件状态。见 <see cref="AttachmentStatus"/>。</summary>
    public AttachmentStatus Status { get; set; }

    /// <summary>失败原因。</summary>
    public string? FailureReason { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}