using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Core.Interfaces;

namespace Core.Services;

/// <summary>
/// 附件本地磁盘存储管理实现。
/// </summary>
public sealed class AttachmentStorageService : IAttachmentStorageService
{
    private readonly ICurrentUserContext _currentUserContext;
    private readonly string _basePath;

    // 下载缓存治理（九4）：容量上限与并发下载合并。
    private const long MaxCacheBytes = 512L * 1024 * 1024; // 512MB
    private const int CacheVersion = 1;
    private const string CacheVersionFile = "cache.version";
    // 同一 AttachmentId 的并发下载合并：key = attachmentId, value = 进行中的写入任务。
    private static readonly ConcurrentDictionary<string, Task<string>> _inFlightDownloads = new();

    public AttachmentStorageService(ICurrentUserContext currentUserContext)
    {
        _currentUserContext = currentUserContext;
        _basePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ChatApp",
            "Attachments");
        EnsureCacheVersion();
    }

    /// <summary>缓存版本失效（九4）：版本不匹配时清空下载缓存重建。</summary>
    private void EnsureCacheVersion()
    {
        try
        {
            var dir = GetDownloadsDir();
            var versionPath = Path.Combine(dir, CacheVersionFile);
            int? current = null;
            if (File.Exists(versionPath) && int.TryParse(File.ReadAllText(versionPath).Trim(), out var v))
                current = v;
            if (current != CacheVersion)
            {
                foreach (var f in Directory.GetFiles(dir))
                {
                    try { File.Delete(f); } catch { /* 忽略 */ }
                }
                File.WriteAllText(versionPath, CacheVersion.ToString());
            }
        }
        catch
        {
            // 版本校验失败不阻塞主流程
        }
    }

    private string OwnerDir
    {
        get
        {
            var owner = _currentUserContext.UserId ?? 0;
            var dir = Path.Combine(_basePath, owner.ToString());
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public string GetAttachmentsRoot() => OwnerDir;

    public string GetUploadingDir()
    {
        var dir = Path.Combine(OwnerDir, "uploading");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public string GetDownloadsDir()
    {
        var dir = Path.Combine(OwnerDir, "downloads");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public string GetThumbnailsDir()
    {
        var dir = Path.Combine(OwnerDir, "thumbnails");
        Directory.CreateDirectory(dir);
        return dir;
    }
    public string CopyToUploading(string sourceFilePath, string fileName)
    {
        var uploadingDir = GetUploadingDir();
        var safeName = SanitizeFileName(fileName);
        var uniqueName = $"{Guid.NewGuid():N}_{safeName}";
        var fullPath = Path.Combine(uploadingDir, uniqueName);
        File.Copy(sourceFilePath, fullPath, overwrite: true);
        return Path.GetFileName(fullPath);
    }

    public async Task<string> WriteToUploadingAsync(Stream content, string fileName, CancellationToken ct = default)
    {
        var uploadingDir = GetUploadingDir();
        var safeName = SanitizeFileName(fileName);
        var uniqueName = $"{Guid.NewGuid():N}_{safeName}";
        var fullPath = Path.Combine(uploadingDir, uniqueName);
        await using var fs = File.Create(fullPath);
        await content.CopyToAsync(fs, ct);
        return Path.GetFileName(fullPath);
    }

    /// <summary>
    /// 将源流写入上传临时目录，同时在同一次读取中增量计算 SHA-256（九3）。
    /// 源流仅读取一次，避免“先算 hash 再复制”的重复 IO。
    /// </summary>
    public async Task<(string relativePath, string sha256)> WriteToUploadingWithHashAsync(Stream content, string fileName, CancellationToken ct = default)
    {
        var uploadingDir = GetUploadingDir();
        var safeName = SanitizeFileName(fileName);
        var uniqueName = $"{Guid.NewGuid():N}_{safeName}";
        var fullPath = Path.Combine(uploadingDir, uniqueName);

        using var sha = SHA256.Create();
        await using var fs = File.Create(fullPath);
        var buffer = new byte[65536];
        int read;
        while ((read = await content.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            // 同一缓冲既落盘又喂给哈希，单次读取完成复制+哈希（九3）。
            await fs.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            sha.TransformBlock(buffer, 0, read, buffer, 0);
        }
        sha.TransformFinalBlock([], 0, 0);

        var sb = new StringBuilder(sha.Hash!.Length * 2);
        foreach (var b in sha.Hash)
            sb.Append(b.ToString("x2"));

        return (Path.GetFileName(fullPath), sb.ToString());
    }

    public string ResolvePath(string relativePath)
    {
        return Path.Combine(OwnerDir, relativePath);
    }

    public Stream OpenUploadingRead(string relativePath)
    {
        var fullPath = Path.Combine(GetUploadingDir(), relativePath);
        return new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
    }

    public void DeleteUploadingFile(string relativePath)
    {
        try
        {
            var fullPath = Path.Combine(GetUploadingDir(), relativePath);
            if (File.Exists(fullPath))
                File.Delete(fullPath);
        }
        catch
        {
            // 忽略删除失败
        }
    }
    public string MoveToDownloads(string uploadingRelativePath, string attachmentId, string fileName)
    {
        var downloadsDir = GetDownloadsDir();
        var safeName = SanitizeFileName(fileName);
        var destName = $"{attachmentId}_{safeName}";
        var destPath = Path.Combine(downloadsDir, destName);
        var srcPath = Path.Combine(GetUploadingDir(), uploadingRelativePath);
        if (File.Exists(srcPath))
        {
            File.Move(srcPath, destPath, overwrite: true);
        }

        return Path.GetFileName(destPath);
    }

    public string? GetDownloadCachePath(string attachmentId, string fileName)
    {
        var downloadsDir = GetDownloadsDir();
        var safeName = SanitizeFileName(fileName);
        var path = Path.Combine(downloadsDir, $"{attachmentId}_{safeName}");
        return File.Exists(path) ? path : null;
    }

    public async Task<string> WriteToDownloadsAsync(string attachmentId, string fileName, Stream content, CancellationToken ct = default)
    {
        // 同一 AttachmentId 的并发下载合并（九4）：复用进行中的写入任务，避免重复下载。
        var coalesceKey = $"{_currentUserContext.UserId ?? 0}:{attachmentId}";
        var existing = _inFlightDownloads.GetValueOrDefault(coalesceKey);
        if (existing is not null)
        {
            try { return await existing.ConfigureAwait(false); }
            catch { /* 上一次失败，继续本次写入 */ }
        }

        var downloadsDir = GetDownloadsDir();
        var safeName = SanitizeFileName(fileName);
        var destName = $"{attachmentId}_{safeName}";
        var fullPath = Path.Combine(downloadsDir, destName);

        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _inFlightDownloads[coalesceKey] = tcs.Task;

        try
        {
            // 下载完整性校验 + 原子 rename（九4）：先写 .partial 临时文件，
            // 写入完成后再原子 rename 到目标路径，避免半成品文件被当作完整缓存。
            var partialPath = fullPath + ".partial";
            await using (var fs = File.Create(partialPath))
            {
                await content.CopyToAsync(fs, ct).ConfigureAwait(false);
                await fs.FlushAsync(ct).ConfigureAwait(false);
            }
            if (File.Exists(fullPath))
                File.Delete(fullPath);
            File.Move(partialPath, fullPath, overwrite: false);

            // 写入成功后更新访问时间并触发 LRU 清理（九4）。
            File.SetLastAccessTimeUtc(fullPath, DateTime.UtcNow);
            EvictIfOverCapacity();

            tcs.TrySetResult(fullPath);
            return fullPath;
        }
        catch
        {
            tcs.TrySetException(new IOException("下载缓存写入失败"));
            throw;
        }
        finally
        {
            _inFlightDownloads.TryRemove(coalesceKey, out _);
        }
    }

    /// <summary>LRU 清理（九4）：当下载缓存总大小超过上限时，按最后访问时间最久未用优先删除。</summary>
    private void EvictIfOverCapacity()
    {
        try
        {
            var dir = GetDownloadsDir();
            var files = Directory.GetFiles(dir)
                .Where(f => !Path.GetFileName(f).Equals(CacheVersionFile, StringComparison.Ordinal))
                .Select(f => new FileInfo(f))
                .Where(f => f.Exists)
                .OrderByDescending(f => f.LastAccessTimeUtc)
                .ToList();
            var total = files.Sum(f => f.Length);
            if (total <= MaxCacheBytes)
                return;
            for (var i = files.Count - 1; i >= 0 && total > MaxCacheBytes; i--)
            {
                try
                {
                    total -= files[i].Length;
                    files[i].Delete();
                }
                catch { /* 忽略单个删除失败 */ }
            }
        }
        catch
        {
            // 清理失败不阻塞下载
        }
    }

    public long? GetAvailableDiskSpace()
    {
        try
        {
            var root = Path.GetPathRoot(OwnerDir);
            if (string.IsNullOrWhiteSpace(root))
                return null;
            var drive = new DriveInfo(root);
            return drive.AvailableFreeSpace;
        }
        catch
        {
            return null;
        }
    }

    private static string SanitizeFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return "file";
        var invalid = Path.GetInvalidFileNameChars();
        var name = fileName;
        foreach (var c in invalid)
            name = name.Replace(c, '_');
        return name.Length > 200 ? name[..200] : name;
    }
}
