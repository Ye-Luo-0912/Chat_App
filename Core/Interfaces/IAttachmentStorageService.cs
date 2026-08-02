using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Core.Interfaces;

/// <summary>
/// 附件本地磁盘存储管理。负责文件落盘、路径解析、缓存清理。
/// 目录结构：{AppData}/ChatApp/Attachments/{ownerUserId}/
/// ├── uploading/ — 上传中临时文件
/// ├── downloads/ — 下载缓存
/// └── thumbnails/ — 缩略图缓存
/// </summary>
public interface IAttachmentStorageService
{
    /// <summary>附件根目录（如 AppData/ChatApp/Attachments/{ownerUserId}/）。</summary>
    string GetAttachmentsRoot();

    /// <summary>上传临时文件目录（如 .../uploading/）。</summary>
    string GetUploadingDir();

    /// <summary>下载缓存目录（如 .../downloads/）。</summary>
    string GetDownloadsDir();

    /// <summary>缩略图缓存目录（如 .../thumbnails/）。</summary>
    string GetThumbnailsDir();

    /// <summary>将源文件复制到上传临时目录，返回相对路径（仅文件名）。</summary>
    string CopyToUploading(string sourceFilePath, string fileName);

    /// <summary>将源流写入上传临时目录，返回相对路径（仅文件名）。</summary>
    Task<string> WriteToUploadingAsync(Stream content, string fileName, CancellationToken ct = default);

    /// <summary>
    /// 将源流写入上传临时目录，同时在同一次读取中增量计算 SHA-256。
    /// 返回 (相对路径, sha256 十六进制小写)。源流仅读取一次，避免重复 IO。
    /// </summary>
    Task<(string relativePath, string sha256)> WriteToUploadingWithHashAsync(Stream content, string fileName, CancellationToken ct = default);

    /// <summary>根据相对路径获取完整路径。</summary>
    string ResolvePath(string relativePath);

    /// <summary>打开上传临时文件用于读取。</summary>
    Stream OpenUploadingRead(string relativePath);

    /// <summary>删除上传临时文件。</summary>
    void DeleteUploadingFile(string relativePath);

    /// <summary>将上传完成的文件移动到持久目录，返回新的相对路径。</summary>
    string MoveToDownloads(string uploadingRelativePath, string attachmentId, string fileName);

    /// <summary>检查下载缓存中是否已有该附件，返回完整路径或 null。</summary>
    string? GetDownloadCachePath(string attachmentId, string fileName);

    /// <summary>将下载流写入缓存，返回完整路径。</summary>
    Task<string> WriteToDownloadsAsync(string attachmentId, string fileName, Stream content, CancellationToken ct = default);

    /// <summary>获取磁盘可用空间（字节）。返回 null 表示无法确定。</summary>
    long? GetAvailableDiskSpace();
}