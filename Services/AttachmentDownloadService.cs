using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Core.Interfaces;
using Chat_App.Infrastructure.Persistence;
using Serilog;

namespace Chat_App.Services;

public interface IAttachmentDownloadService
{
    /// <summary>
    /// 获取附件本地缓存路径：命中直接返回；未命中时执行"网络下载 → 校验 → 缓存落盘"。
    /// 同一 (OwnerUserId, AttachmentId) 的并发调用共享同一次下载，网络请求只发一次。
    /// 失败返回 null。
    /// </summary>
    Task<string?> GetOrDownloadAsync(
        string attachmentId, string fileName, string? downloadApiHint, CancellationToken ct = default);
}

/// <summary>
/// 附件下载协调服务：缓存优先 + 按 (OwnerUserId, AttachmentId) 合并并发下载。
/// 合并发生在服务入口，整个"网络下载 → 哈希校验 → 缓存落盘"只执行一次，
/// 后续等待者直接复用结果，不再各自发起 HTTP 请求。
/// </summary>
public sealed class AttachmentDownloadService : IAttachmentDownloadService
{
    private readonly IAttachmentClientService _attachments;
    private readonly IAttachmentStorageService _storage;
    private readonly IDatabaseService _db;
    private readonly ICurrentUserContext _currentUserContext;

    // key = {ownerUserId}:{attachmentId}, value = 进行中的合并下载任务。
    private static readonly ConcurrentDictionary<string, Task<string?>> _inFlight = new();

    public AttachmentDownloadService(
        IAttachmentClientService attachments,
        IAttachmentStorageService storage,
        IDatabaseService db,
        ICurrentUserContext currentUserContext)
    {
        _attachments = attachments;
        _storage = storage;
        _db = db;
        _currentUserContext = currentUserContext;
    }

    public async Task<string?> GetOrDownloadAsync(
        string attachmentId, string fileName, string? downloadApiHint, CancellationToken ct = default)
    {
        // 1. 缓存优先：命中即返回（存储层已顺带更新 LRU 访问元数据）。
        try
        {
            var cached = _storage.GetDownloadCachePath(attachmentId, fileName);
            if (cached is not null)
                return cached;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "查询下载缓存失败 AttachmentId={AttachmentId}", attachmentId);
        }

        var owner = _currentUserContext.UserId ?? 0;
        var key = $"{owner}:{attachmentId}";

        // 2. AsyncLazy 合并：加入或复用进行中的下载任务，网络请求只发一次。
        var newTask = DownloadAndCacheAsync(attachmentId, fileName, downloadApiHint, ct);
        var raced = _inFlight.GetOrAdd(key, newTask);
        if (!ReferenceEquals(raced, newTask))
        {
            // 并发发起者输掉竞争：等待胜者结果。
            try { return await raced.ConfigureAwait(false); }
            catch { /* 胜者失败，本次不重试，保持与失败路径一致 */ }
            return null;
        }

        try
        {
            return await newTask.ConfigureAwait(false);
        }
        finally
        {
            // 条件移除：仅移除本次创建的任务，避免误删后续发起的新任务。
            _inFlight.TryRemove(new KeyValuePair<string, Task<string?>>(key, newTask));
        }
    }

    private async Task<string?> DownloadAndCacheAsync(
        string attachmentId, string fileName, string? downloadApiHint, CancellationToken ct)
    {
        var hint = !string.IsNullOrWhiteSpace(downloadApiHint) ? downloadApiHint : attachmentId;
        try
        {
            var expectedSha256 = await TryGetAttachmentSha256Async(attachmentId).ConfigureAwait(false);
            var result = await _attachments.DownloadAsync(hint, ct: ct).ConfigureAwait(false);
            try
            {
                var path = await _storage.WriteToDownloadsAsync(
                        attachmentId, fileName, result.Content, ct, expectedSha256)
                    .ConfigureAwait(false);

                await TryUpdateAttachmentCachePathAsync(attachmentId, path).ConfigureAwait(false);
                return path;
            }
            finally
            {
                result.Content.Dispose();
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "下载附件失败 AttachmentId={AttachmentId}", attachmentId);
            return null;
        }
    }

    private async Task TryUpdateAttachmentCachePathAsync(string attachmentId, string cachedPath)
    {
        try
        {
            var owner = _currentUserContext.RequireUserId();
            var existing = await _db.GetAttachmentByAttachmentIdAsync(owner, attachmentId).ConfigureAwait(false);
            if (existing is not null && string.IsNullOrEmpty(existing.LocalCachePath))
            {
                existing.LocalCachePath = Path.GetFileName(cachedPath);
                existing.UpdatedAt = DateTime.UtcNow;
                await _db.UpsertAttachmentAsync(existing).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "更新附件缓存路径失败");
        }
    }

    private async Task<string?> TryGetAttachmentSha256Async(string attachmentId)
    {
        if (!_currentUserContext.TryGetUserId(out var owner))
            return null;
        try
        {
            var existing = await _db.GetAttachmentByAttachmentIdAsync(owner, attachmentId).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(existing?.Sha256) ? null : existing.Sha256;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "读取附件 SHA-256 失败，跳过哈希校验");
            return null;
        }
    }
}
