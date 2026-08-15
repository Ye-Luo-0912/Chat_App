using System;
using System.Buffers;
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
    private readonly string _basePath;
    private readonly long _maxCacheBytes;

    // 下载缓存治理：容量上限（默认 512MB，可注入以便测试 LRU 淘汰）与并发下载合并。
    private const long DefaultMaxCacheBytes = 512L * 1024 * 1024; // 512MB
    private const int CacheVersion = 2;
    private const string CacheVersionFile = "cache.version";
    // 同一 (Owner, AttachmentId, append) 的并发下载合并：key = 合并键, value = 进行中的写入任务。
    private static readonly ConcurrentDictionary<string, Task<string>> _inFlightDownloads = new();
    // 每账户缓存版本只校验一次。
    private readonly ConcurrentDictionary<long, byte> _cacheVersionChecked = new();

    public AttachmentStorageService(ICurrentUserContext currentUserContext, string? basePath = null, long? maxCacheBytes = null)
    {
        _basePath = basePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ChatApp",
            "Attachments");
        _maxCacheBytes = maxCacheBytes ?? DefaultMaxCacheBytes;
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

    /// <summary>指定 owner 的附件根目录（路径方法全部显式携带 owner，不依赖全局当前用户）。</summary>
    private static string OwnerDirOf(string basePath, long ownerUserId)
    {
        var dir = Path.Combine(basePath, ownerUserId.ToString());
        Directory.CreateDirectory(dir);
        return dir;
    }

    public string GetAttachmentsRoot(long ownerUserId) => OwnerDirOf(_basePath, ownerUserId);

    public string GetUploadingDir(long ownerUserId)
    {
        var dir = Path.Combine(OwnerDirOf(_basePath, ownerUserId), "uploading");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public string GetDownloadsDir(long ownerUserId)
    {
        var dir = Path.Combine(OwnerDirOf(_basePath, ownerUserId), "downloads");
        Directory.CreateDirectory(dir);
        if (_cacheVersionChecked.TryAdd(ownerUserId, 0))
            EnsureCacheVersion(dir);
        return dir;
    }

    public string GetThumbnailsDir(long ownerUserId)
    {
        var dir = Path.Combine(OwnerDirOf(_basePath, ownerUserId), "thumbnails");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public string CopyToUploading(long ownerUserId, string sourceFilePath, string fileName)
    {
        var uploadingDir = GetUploadingDir(ownerUserId);
        var safeName = SanitizeFileName(fileName);
        var uniqueName = $"{Guid.NewGuid():N}_{safeName}";
        var fullPath = Path.Combine(uploadingDir, uniqueName);
        File.Copy(sourceFilePath, fullPath, overwrite: true);
        return Path.GetFileName(fullPath);
    }

    public async Task<string> WriteToUploadingAsync(long ownerUserId, Stream content, string fileName, CancellationToken ct = default)
    {
        var uploadingDir = GetUploadingDir(ownerUserId);
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
    public async Task<(string relativePath, string sha256)> WriteToUploadingWithHashAsync(long ownerUserId, Stream content, string fileName, CancellationToken ct = default)
    {
        var uploadingDir = GetUploadingDir(ownerUserId);
        var safeName = SanitizeFileName(fileName);
        var uniqueName = $"{Guid.NewGuid():N}_{safeName}";
        var fullPath = Path.Combine(uploadingDir, uniqueName);

        using var sha = SHA256.Create();
        await using var fs = File.Create(fullPath);
        var buffer = ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            int read;
            while ((read = await content.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                // 同一缓冲既落盘又喂给哈希，单次读取完成复制+哈希。
                await fs.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                sha.TransformBlock(buffer, 0, read, buffer, 0);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
        sha.TransformFinalBlock([], 0, 0);

        return (Path.GetFileName(fullPath), ToHexLower(sha.Hash!));
    }

    public string ResolvePath(long ownerUserId, string relativePath)
    {
        return SafeResolve(OwnerDirOf(_basePath, ownerUserId), relativePath);
    }

    public Stream OpenUploadingRead(long ownerUserId, string relativePath)
    {
        var fullPath = SafeResolve(GetUploadingDir(ownerUserId), relativePath);
        return new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
    }

    public void DeleteUploadingFile(long ownerUserId, string relativePath)
    {
        try
        {
            var fullPath = SafeResolve(GetUploadingDir(ownerUserId), relativePath);
            if (File.Exists(fullPath))
                File.Delete(fullPath);
        }
        catch
        {
            // 忽略删除失败
        }
    }
    public string MoveToDownloads(long ownerUserId, string uploadingRelativePath, string attachmentId, string fileName)
    {
        var downloadsDir = GetDownloadsDir(ownerUserId);
        var safeName = SanitizeFileName(fileName);
        var destName = $"{HashAttachmentName(ownerUserId, attachmentId)}_{safeName}";
        var destPath = Path.Combine(downloadsDir, destName);
        var srcPath = SafeResolve(GetUploadingDir(ownerUserId), uploadingRelativePath);
        if (File.Exists(srcPath))
        {
            File.Move(srcPath, destPath, overwrite: true);
        }

        // 移动即访问：更新元数据并触发容量淘汰，避免新缓存被立即淘汰。
        try { File.SetLastAccessTimeUtc(destPath, DateTime.UtcNow); } catch { /* 忽略 */ }
        EvictIfOverCapacity(ownerUserId);

        return Path.GetFileName(destPath);
    }

    public string? GetDownloadCachePath(long ownerUserId, string attachmentId, string fileName)
    {
        var downloadsDir = GetDownloadsDir(ownerUserId);
        var safeName = SanitizeFileName(fileName);
        var path = Path.Combine(downloadsDir, $"{HashAttachmentName(ownerUserId, attachmentId)}_{safeName}");
        if (File.Exists(path))
        {
            // 缓存命中即更新访问元数据，保证 LRU 淘汰按真实使用顺序执行。
            try { File.SetLastAccessTimeUtc(path, DateTime.UtcNow); } catch { /* 忽略 */ }
            return path;
        }
        return null;
    }

    public string? GetPartialDownloadPath(long ownerUserId, string attachmentId, string fileName)
    {
        var downloadsDir = GetDownloadsDir(ownerUserId);
        var safeName = SanitizeFileName(fileName);
        var path = Path.Combine(downloadsDir, $"{HashAttachmentName(ownerUserId, attachmentId)}_{safeName}.partial");
        if (!File.Exists(path))
            return null;
        var info = new FileInfo(path);
        // 空 partial 无续传价值（上次写入可能从零开始即失败），返回 null 走全新下载。
        return info.Length > 0 ? path : null;
    }

    public async Task<string> WriteToDownloadsAsync(
        long ownerUserId, string attachmentId, string fileName, Stream content, CancellationToken ct = default,
        string? expectedSha256 = null, bool append = false)
    {
        // 同一 (Owner, AttachmentId, append) 的并发下载合并：复用进行中的写入任务，避免重复下载。
        var coalesceKey = $"{ownerUserId}:{attachmentId}:{(append ? 1 : 0)}";
        var existing = _inFlightDownloads.GetValueOrDefault(coalesceKey);
        if (existing is not null)
        {
            try { return await existing.ConfigureAwait(false); }
            catch { /* 上一次失败，继续本次写入 */ }
        }

        var downloadsDir = GetDownloadsDir(ownerUserId);
        var safeName = SanitizeFileName(fileName);
        var destName = $"{HashAttachmentName(ownerUserId, attachmentId)}_{safeName}";
        var fullPath = Path.Combine(downloadsDir, destName);
        var partialPath = fullPath + ".partial";

        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _inFlightDownloads[coalesceKey] = tcs.Task;

        try
        {
            // 下载完整性校验 + 原子 rename：先写 .partial 临时文件，
            // 写入完成后再原子 rename 到目标路径，避免半成品文件被当作完整缓存。
            var written = 0L;
            if (append && !File.Exists(partialPath))
                throw new IOException("续传目标 partial 不存在");
            var mode = append ? FileMode.Append : FileMode.Create;
            await using (var fs = new FileStream(partialPath, mode, FileAccess.Write, FileShare.None, 65536, useAsync: true))
            {
                var buffer = ArrayPool<byte>.Shared.Rent(65536);
                try
                {
                    int read;
                    while ((read = await content.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                    {
                        await fs.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                        written += read;
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
                await fs.FlushAsync(ct).ConfigureAwait(false);
            }

            // 全新下载禁止空文件落盘为完整缓存（append 续传 0 字节表示已完整，跳过该检查）。
            if (!append && written <= 0)
            {
                // 空 partial 无续传价值，直接删除。
                try { if (File.Exists(partialPath)) File.Delete(partialPath); } catch { /* 忽略 */ }
                throw new IOException("下载内容为空");
            }

            // 内容哈希校验：调用方持有期望值时比对，不一致视为损坏。
            // append 模式下载流只是文件尾部，改为对落盘后的完整文件计算哈希。
            if (expectedSha256 is not null)
            {
                var actual = await ComputeFileSha256Async(partialPath, ct).ConfigureAwait(false);
                if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException("下载内容哈希校验失败");
                }
            }

            if (File.Exists(fullPath))
                File.Delete(fullPath);
            File.Move(partialPath, fullPath, overwrite: false);

            // 写入成功后更新访问时间并触发 LRU 清理。
            File.SetLastAccessTimeUtc(fullPath, DateTime.UtcNow);
            EvictIfOverCapacity(ownerUserId);

            tcs.TrySetResult(fullPath);
            return fullPath;
        }
        catch
        {
            // 失败时保留 .partial 半成品供断点续传复用（服务层负责后续重试/清理）；
            // 仅当写入完全未开始时无文件可留，无需处理。
            tcs.TrySetException(new IOException("下载缓存写入失败"));
            throw;
        }
        finally
        {
            _inFlightDownloads.TryRemove(coalesceKey, out _);
        }
    }

    private static async Task<string> ComputeFileSha256Async(string filePath, CancellationToken ct)
    {
        using var sha = SHA256.Create();
        await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
        var buffer = ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            int read;
            while ((read = await fs.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
            {
                sha.TransformBlock(buffer, 0, read, buffer, 0);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
        sha.TransformFinalBlock([], 0, 0);
        return ToHexLower(sha.Hash!);
    }

    /// <summary>
    /// LRU 清理：当下载缓存总大小超过上限时，按最后访问时间最久未用优先删除。
    /// 活跃 .partial（断点续传在途）不参与淘汰，避免正在下载的文件被删。
    /// 注：LRU 元数据（缓存路径/大小/访问时间）已可落入 SQLite（LocalAttachment.CachePath），
    /// 目录扫描仅在超容量时触发一次，作为兜底保持目录与实际一致。
    /// </summary>
    private void EvictIfOverCapacity(long ownerUserId)
    {
        try
        {
            var dir = GetDownloadsDir(ownerUserId);
            var files = Directory.GetFiles(dir)
                .Where(f => !Path.GetFileName(f).Equals(CacheVersionFile, StringComparison.Ordinal))
                .Where(f => !Path.GetFileName(f).EndsWith(".partial", StringComparison.OrdinalIgnoreCase))
                .Select(f => new FileInfo(f))
                .Where(f => f.Exists)
                .OrderByDescending(f => f.LastAccessTimeUtc)
                .ToList();
            var total = files.Sum(f => f.Length);
            if (total <= _maxCacheBytes)
                return;
            for (var i = files.Count - 1; i >= 0 && total > _maxCacheBytes; i--)
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
            var root = Path.GetPathRoot(_basePath);
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
    private static string HashAttachmentName(long ownerUserId, string attachmentId)
    {
        var input = $"{ownerUserId}:{attachmentId}";
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


