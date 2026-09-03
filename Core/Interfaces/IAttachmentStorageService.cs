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
/// 所有路径方法显式携带 ownerUserId：恢复 A 账户的记录时绝不会读取 B 账户的附件目录
/// （不依赖全局当前用户上下文）。
/// </summary>
public interface IAttachmentStorageService
{
    /// <summary>附件根目录（如 AppData/ChatApp/Attachments/{ownerUserId}/）。</summary>
    string GetAttachmentsRoot(long ownerUserId);

    /// <summary>上传临时文件目录（如 .../uploading/）。</summary>
    string GetUploadingDir(long ownerUserId);

    /// <summary>下载缓存目录（如 .../downloads/）。</summary>
    string GetDownloadsDir(long ownerUserId);

    /// <summary>缩略图缓存目录（如 .../thumbnails/）。</summary>
    string GetThumbnailsDir(long ownerUserId);

    /// <summary>将源文件复制到上传临时目录，返回相对路径（仅文件名）。</summary>
    string CopyToUploading(long ownerUserId, string sourceFilePath, string fileName);

    /// <summary>将源流写入上传临时目录，返回相对路径（仅文件名）。</summary>
    Task<string> WriteToUploadingAsync(long ownerUserId, Stream content, string fileName, CancellationToken ct = default);

    /// <summary>
    /// 将源流写入上传临时目录，同时在同一次读取中增量计算 SHA-256。
    /// 返回 (相对路径, sha256 十六进制小写)。源流仅读取一次，避免重复 IO。
    /// </summary>
    Task<(string relativePath, string sha256)> WriteToUploadingWithHashAsync(long ownerUserId, Stream content, string fileName, CancellationToken ct = default);

    /// <summary>根据相对路径获取完整路径（路径逃逸抛 SecurityException）。</summary>
    string ResolvePath(long ownerUserId, string relativePath);

    /// <summary>打开上传临时文件用于读取（路径逃逸抛 SecurityException）。</summary>
    Stream OpenUploadingRead(long ownerUserId, string relativePath);

    /// <summary>删除上传临时文件。</summary>
    void DeleteUploadingFile(long ownerUserId, string relativePath);

    /// <summary>将上传完成的文件移动到持久目录，返回新的相对路径。</summary>
    string MoveToDownloads(long ownerUserId, string uploadingRelativePath, string attachmentId, string fileName);

    /// <summary>检查下载缓存中是否已有该附件，返回完整路径或 null。</summary>
    string? GetDownloadCachePath(long ownerUserId, string attachmentId, string fileName);

    /// <summary>检查是否存在未完成的 .partial 下载缓存，返回完整路径或 null（空文件视为无效）。</summary>
    string? GetPartialDownloadPath(long ownerUserId, string attachmentId, string fileName);

    /// <summary>
    /// 将下载流写入缓存，返回完整路径。
    /// expectedSha256 非空时校验内容哈希（append=true 时对落盘后的完整文件计算），不一致则删除并抛异常。
    /// append=false：写 .partial 临时文件后原子 rename；append=true：向已有 .partial 追加续传。
    /// </summary>
    Task<string> WriteToDownloadsAsync(long ownerUserId, string attachmentId, string fileName, Stream content, CancellationToken ct = default, string? expectedSha256 = null, bool append = false);

    /// <summary>获取磁盘可用空间（字节）。返回 null 表示无法确定。</summary>
    long? GetAvailableDiskSpace();

    // ---- 缓存治理（VOICE-MSG-3）：占用统计与手动清理 ----

    /// <summary>下载缓存容量上限（字节）。用于设置页"已用/上限"展示与容量告警。</summary>
    long MaxCacheBytes { get; }

    /// <summary>
    /// 统计下载缓存当前占用（字节）。口径与容量淘汰一致：不含 cache.version 与 .partial。
    /// 统计失败返回 0，不抛异常。
    /// </summary>
    long GetDownloadsCacheSizeBytes(long ownerUserId);

    /// <summary>
    /// 清空下载缓存（设置页"清除语音缓存"入口）。保留 cache.version 标记与在途 .partial
    /// （断点续传不中断）；被占用（如正在播放）或删除失败的文件跳过。
    /// 返回实际释放的字节数；目录不可访问时返回 0，不抛异常。
    /// </summary>
    long ClearDownloadsCache(long ownerUserId);
}
