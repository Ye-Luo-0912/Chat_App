using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Security;
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

    // 下载缓存治理：容量上限与并发下载合并。
    private const long MaxCacheBytes = 512L * 1024 * 1024; // 512MB
    private const int CacheVersion = 2;
    private const string CacheVersionFile = "cache.version";
    // 同一 AttachmentId 的并发下载合并：key = attachmentId, value = 进行中的写入任务。
    private static readonly ConcurrentDictionary<string, Task<string>> _inFlightDownloads = new();
    // 每账户缓存版本只校验一次：登录前 UserId=0 的目录不能抢占首次校验。
    private readonly ConcurrentDictionary<long, byte> _cacheVersionChecked = new();

    public AttachmentStorageService(ICurrentUserContext currentUserContext)
    {
        _currentUserContext = currentUserContext;
        _basePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ChatApp",
            "Attachments");
    }

    /// <summary>缓存版本失效：版本不匹配时清空下载缓存重建，并清扫崩溃残留的 .partial 半成品。</summary>
    private void EnsureCacheVersion(string downloadsDir)
    {
        try
        {
            var versionPath = Path.Combine(downloadsDir, CacheVersionFile);
            int? current = null;
            if (File.Exists(versionPath) && int.TryParse(File.ReadAllText(versionPath).Trim(), out var v))
                current = v;
            if (current != CacheVersion)
            {
                foreach (var f in Directory.GetFiles(downloadsDir))
                {
                    try { File.Delete(f); } catch { /* 忽略 */ }
                }
                File.WriteAllText(versionPath, CacheVersion.ToString());
            }

            // .partial 写入中断（进程崩溃）可能残留，随版本校验一并清扫。
            foreach (var f in Directory.GetFiles(downloadsDir, "*.partial"))
            {
                try { File.Delete(f); } catch { /* 忽略 */ }
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
        // 版本校验延迟到首次使用（登录后）执行：登录前 UserId 为 0，
        // 若在构造时校验会针对用户 0 目录误操作真实用户缓存。
        if (_cacheVersionChecked.TryAdd(_currentUserContext.UserId ?? 0, 0))
            EnsureCacheVersion(dir);
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
    /// 将源流写入上传临时目录，同时在同一次读取中增量计算 SHA-256。
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
            // 同一缓冲既落盘又喂给哈希，单次读取完成复制+哈希。
            await fs.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            sha.TransformBlock(buffer, 0, read, buffer, 0);
        }
        sha.TransformFinalBlock([], 0, 0);

        return (Path.GetFileName(fullPath), ToHexLower(sha.Hash!));
    }

    public string ResolvePath(string relativePath)
    {
        return SafeResolve(OwnerDir, relativePath);
    }

    public Stream OpenUploadingRead(string relativePath)
    {
        var fullPath = SafeResolve(GetUploadingDir(), relativePath);
        return new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
    }

    public void DeleteUploadingFile(string relativePath)
    {
        try
        {
            var fullPath = SafeResolve(GetUploadingDir(), relativePath);
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
        var destName = $"{HashAttachmentName(attachmentId)}_{safeName}";
        var destPath = Path.Combine(downloadsDir, destName);
        var srcPath = SafeResolve(GetUploadingDir(), uploadingRelativePath);
        if (File.Exists(srcPath))
        {
            File.Move(srcPath, destPath, overwrite: true);
        }

        // 移动即访问：更新元数据并触发容量淘汰，避免新缓存被立即淘汰。
        try { File.SetLastAccessTimeUtc(destPath, DateTime.UtcNow); } catch { /* 忽略 */ }
        EvictIfOverCapacity();

        return Path.GetFileName(destPath);
    }

    public string? GetDownloadCachePath(string attachmentId, string fileName)
    {
        var downloadsDir = GetDownloadsDir();
        var safeName = SanitizeFileName(fileName);
        var path = Path.Combine(downloadsDir, $"{HashAttachmentName(attachmentId)}_{safeName}");
        if (File.Exists(path))
        {
            // 缓存命中即更新访问元数据，保证 LRU 淘汰按真实使用顺序执行。
            try { File.SetLastAccessTimeUtc(path, DateTime.UtcNow); } catch { /* 忽略 */ }
            return path;
        }
        return null;
    }

    public async Task<string> WriteToDownloadsAsync(string attachmentId, string fileName, Stream content, CancellationToken ct = default, string? expectedSha256 = null)
    {
        // 同一 AttachmentId 的并发下载合并：复用进行中的写入任务，避免重复下载。
        var coalesceKey = $"{_currentUserContext.UserId ?? 0}:{attachmentId}";
        var existing = _inFlightDownloads.GetValueOrDefault(coalesceKey);
        if (existing is not null)
        {
            try { return await existing.ConfigureAwait(false); }
            catch { /* 上一次失败，继续本次写入 */ }
        }

        var downloadsDir = GetDownloadsDir();
        var safeName = SanitizeFileName(fileName);
        var destName = $"{HashAttachmentName(attachmentId)}_{safeName}";
        var fullPath = Path.Combine(downloadsDir, destName);

        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _inFlightDownloads[coalesceKey] = tcs.Task;

        try
        {
            // 下载完整性校验 + 原子 rename：先写 .partial 临时文件，
            // 写入完成后再原子 rename 到目标路径，避免半成品文件被当作完整缓存。
            var partialPath = fullPath + ".partial";
            var written = 0L;
            using var sha = expectedSha256 is null ? null : SHA256.Create();
            await using (var fs = File.Create(partialPath))
            {
                var buffer = new byte[65536];
                int read;
                while ((read = await content.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await fs.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    written += read;
                    sha?.TransformBlock(buffer, 0, read, buffer, 0);
                }
                sha?.TransformFinalBlock([], 0, 0);
                await fs.FlushAsync(ct).ConfigureAwait(false);
            }

            // 写入长度校验：禁止空文件落盘为完整缓存。
            if (written <= 0)
                throw new IOException("下载内容为空");

            // 内容哈希校验：调用方持有期望值时比对，不一致视为损坏。
            if (expectedSha256 is not null
                && !string.Equals(ToHexLower(sha!.Hash!), expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("下载内容哈希校验失败");
            }

            if (File.Exists(fullPath))
                File.Delete(fullPath);
            File.Move(partialPath, fullPath, overwrite: false);

            // 写入成功后更新访问时间并触发 LRU 清理。
            File.SetLastAccessTimeUtc(fullPath, DateTime.UtcNow);
            EvictIfOverCapacity();

            tcs.TrySetResult(fullPath);
            return fullPath;
        }
        catch
        {
            // 失败时清理 .partial，避免残留半成品。
            try
            {
                var partialPath = fullPath + ".partial";
                if (File.Exists(partialPath))
                    File.Delete(partialPath);
            }
            catch
            {
                // 忽略清理失败
            }
            tcs.TrySetException(new IOException("下载缓存写入失败"));
            throw;
        }
        finally
        {
            _inFlightDownloads.TryRemove(coalesceKey, out _);
        }
    }

    /// <summary>LRU 清理：当下载缓存总大小超过上限时，按最后访问时间最久未用优先删除。</summary>
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

    /// <summary>
    /// 远端 Id 派生本地文件名主段：SHA256(ownerId:attachmentId)。
    /// 远端 Id 绝不可直接用作路径段（可含 ../ 等造成路径穿越）。
    /// </summary>
    private string HashAttachmentName(string attachmentId)
    {
        var input = $"{_currentUserContext.UserId ?? 0}:{attachmentId}";
        return ToHexLower(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
    }

    /// <summary>
    /// 安全路径解析：GetFullPath 规范化后必须位于 rootDir 之内（大小写不敏感前缀），
    /// 否则抛出 SecurityException，杜绝任何相对路径逃逸。
    /// </summary>
    private static string SafeResolve(string rootDir, string relativePath)
    {
        var full = Path.GetFullPath(Path.Combine(rootDir, relativePath));
        var root = Path.GetFullPath(rootDir);
        var rootPrefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            throw new SecurityException($"非法附件路径: {relativePath}");
        return full;
    }

    private static string ToHexLower(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes)
            sb.Append(b.ToString("x2"));
        return sb.ToString();
    }
}
