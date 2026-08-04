using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Chat_App.Infrastructure.Persistence;
using Core.Diagnostics;
using Core.Helpers;
using Core.Interfaces;
using Serilog;

namespace Chat_App.Infrastructure.Services;

public interface IAttachmentThumbnailService
{
    /// <summary>
    /// 确保图片附件存在本地缩略图（thumbnails/ 目录，最长边 512px JPEG）：
    /// 已缓存直接返回；未缓存则用注入的 <see cref="IThumbnailImageCodec"/> 生成后原子落盘，
    /// 并回填 LocalAttachment.LocalThumbnailPath。非图片/源文件缺失/生成失败返回 null。
    /// </summary>
    Task<string?> EnsureThumbnailAsync(
        long ownerUserId, string attachmentId, string fileName, string? contentType,
        string sourceFullPath, CancellationToken ct = default);
}

/// <summary>
/// 附件缩略图服务：缓存优先 + 原子落盘（.tmp → rename）+ LRU 容量治理 + DB 路径回填。
/// 生成工作由注入的 codec 执行（宿主实现，如 SkiaSharp），本服务不依赖具体图像库。
/// </summary>
public sealed class AttachmentThumbnailService : IAttachmentThumbnailService, IMetricsSource
{
    private const int MaxDimension = 512;
    private const long MaxThumbnailsBytes = 64L * 1024 * 1024;

    private readonly IAttachmentStorageService _storage;
    private readonly IThumbnailImageCodec _codec;
    private readonly IDatabaseService _db;
    private readonly ICurrentUserContext _currentUserContext;

    // key = {ownerUserId}:{attachmentId}, value = 进行中的生成任务。
    // 同一附件的并发 EnsureThumbnailAsync 共享同一次生成（与下载 single-flight 同模式）。
    private readonly ConcurrentDictionary<string, Lazy<Task<string?>>> _inFlight = new();

    private long _cacheHits;
    private long _generated;
    private long _failed;

    public string Name => "attachment_thumbnail";

    public IReadOnlyDictionary<string, long> Counters => new Dictionary<string, long>
    {
        // 缓存命中率 = cache_hits / (cache_hits + generated + failed)
        ["cache_hits"] = Volatile.Read(ref _cacheHits),
        ["generated"] = Volatile.Read(ref _generated),
        ["failed"] = Volatile.Read(ref _failed)
    };

    public IReadOnlyDictionary<string, HistogramSnapshot> Histograms =>
        new Dictionary<string, HistogramSnapshot>();

    public AttachmentThumbnailService(
        IAttachmentStorageService storage,
        IThumbnailImageCodec codec,
        IDatabaseService db,
        ICurrentUserContext currentUserContext)
    {
        _storage = storage;
        _codec = codec;
        _db = db;
        _currentUserContext = currentUserContext;
    }

    public async Task<string?> EnsureThumbnailAsync(
        long ownerUserId, string attachmentId, string fileName, string? contentType,
        string sourceFullPath, CancellationToken ct = default)
    {
        // 非图片附件不做缩略图。
        if (!AttachmentType.IsImage(contentType))
            return null;
        if (!File.Exists(sourceFullPath))
            return null;

        var thumbPath = GetThumbnailPath(ownerUserId, attachmentId);

        // 1. 缓存优先（命中即刷新 LRU 访问元数据）。
        try
        {
            if (File.Exists(thumbPath))
            {
                try { File.SetLastAccessTimeUtc(thumbPath, DateTime.UtcNow); } catch { /* 忽略 */ }
                Interlocked.Increment(ref _cacheHits);
                return thumbPath;
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "查询缩略图缓存失败 AttachmentId={AttachmentId}", attachmentId);
        }

        var key = $"{ownerUserId}:{attachmentId}";
        var lazy = _inFlight.GetOrAdd(key,
            _ => new Lazy<Task<string?>>(
                () => GenerateAndCacheAsync(ownerUserId, attachmentId, sourceFullPath, thumbPath, ct),
                LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            return await lazy.Value.ConfigureAwait(false);
        }
        finally
        {
            _inFlight.TryRemove(new KeyValuePair<string, Lazy<Task<string?>>>(key, lazy));
        }
    }

    private async Task<string?> GenerateAndCacheAsync(
        long ownerUserId, string attachmentId, string sourceFullPath, string thumbPath, CancellationToken ct)
    {
        var tmpPath = thumbPath + ".tmp";
        try
        {
            if (await _codec.TryCreateThumbnailAsync(sourceFullPath, tmpPath, MaxDimension, ct).ConfigureAwait(false))
            {
                // 原子落盘：临时文件就绪后 rename，避免半成品被当作有效缓存。
                File.Move(tmpPath, thumbPath, overwrite: true);
                try { File.SetLastAccessTimeUtc(thumbPath, DateTime.UtcNow); } catch { /* 忽略 */ }

                Interlocked.Increment(ref _generated);
                EvictIfOverCapacity(ownerUserId);
                await TryBackfillThumbnailPathAsync(ownerUserId, attachmentId, Path.GetFileName(thumbPath))
                    .ConfigureAwait(false);
                return thumbPath;
            }

            Interlocked.Increment(ref _failed);
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _failed);
            Log.Warning(ex, "生成附件缩略图失败 AttachmentId={AttachmentId}", attachmentId);
            return null;
        }
        finally
        {
            try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { /* 忽略 */ }
        }
    }

    private async Task TryBackfillThumbnailPathAsync(long ownerUserId, string attachmentId, string thumbFileName)
    {
        try
        {
            var existing = await _db.GetAttachmentByAttachmentIdAsync(ownerUserId, attachmentId).ConfigureAwait(false);
            if (existing is not null && string.IsNullOrEmpty(existing.LocalThumbnailPath))
            {
                existing.LocalThumbnailPath = thumbFileName;
                existing.UpdatedAt = DateTime.UtcNow;
                await _db.UpsertAttachmentAsync(existing).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "回填附件缩略图路径失败 AttachmentId={AttachmentId}", attachmentId);
        }
    }

    /// <summary>
    /// 缩略图 LRU 清理：总量超上限时按最后访问时间最久未用优先删除。
    /// 缩略图为小文件（≤512px JPEG），独立于 downloads 的 512MB 上限单独治理。
    /// </summary>
    private void EvictIfOverCapacity(long ownerUserId)
    {
        try
        {
            var dir = _storage.GetThumbnailsDir(ownerUserId);
            var files = Directory.GetFiles(dir)
                .Select(f => new FileInfo(f))
                .Where(f => f.Exists)
                .OrderByDescending(f => f.LastAccessTimeUtc)
                .ToList();
            var total = files.Sum(f => f.Length);
            if (total <= MaxThumbnailsBytes)
                return;
            for (var i = files.Count - 1; i >= 0 && total > MaxThumbnailsBytes; i--)
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
            // 清理失败不阻塞生成
        }
    }

    /// <summary>缩略图文件名派生：SHA256(ownerId:attachmentId) 主段 + _thumb.jpg（与下载缓存同源命名规则）。</summary>
    private string GetThumbnailPath(long ownerUserId, string attachmentId)
    {
        var input = $"{ownerUserId}:{attachmentId}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();
        return Path.Combine(_storage.GetThumbnailsDir(ownerUserId), $"{hash}_thumb.jpg");
    }
}
