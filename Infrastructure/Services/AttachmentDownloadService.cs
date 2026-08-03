using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Core.Interfaces;
using Chat_App.Infrastructure.Persistence;
using Serilog;

namespace Chat_App.Infrastructure.Services;

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

    // key = {ownerUserId}:{attachmentId}, value = 惰性合并下载任务。
    // Lazy 保证只有字典中的 winner 才真正调用下载方法（GetOrAdd 前不启动任何网络请求），
    // 且下载任务不受任何调用方的 CancellationToken 控制（调用方只能取消自己的等待）。
    private static readonly ConcurrentDictionary<string, Lazy<Task<string?>>> _inFlight = new();

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

        // 2. 惰性 single-flight 合并：只有字典 winner 才执行 DownloadAndCacheAsync。
        //    共享下载不受调用方取消影响（调用方取消只中断自己的等待）。
        var lazy = _inFlight.GetOrAdd(key,
            _ => new Lazy<Task<string?>>(
                () => DownloadAndCacheAsync(attachmentId, fileName, downloadApiHint, CancellationToken.None),
                LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            return await lazy.Value.WaitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            // 条件移除：仅移除本次创建的任务，避免误删后续发起的新任务。
            _inFlight.TryRemove(new KeyValuePair<string, Lazy<Task<string?>>>(key, lazy));
        }
    }

    private async Task<string?> DownloadAndCacheAsync(
        string attachmentId, string fileName, string? downloadApiHint, CancellationToken ct)
    {
        var hint = !string.IsNullOrWhiteSpace(downloadApiHint) ? downloadApiHint : attachmentId;
        try
        {
            var expectedSha256 = await TryGetAttachmentSha256Async(attachmentId).ConfigureAwait(false);

            // 断点续传：已有 .partial 则按已写字节数发起 Range 请求续传。
            var partialPath = _storage.GetPartialDownloadPath(attachmentId, fileName);
            var append = false;
            long? resumeFrom = null;
            if (partialPath is not null)
            {
                resumeFrom = new FileInfo(partialPath).Length;
                if (resumeFrom > 0)
                    append = true;
            }

            var result = await _attachments.DownloadAsync(hint, rangeFrom: resumeFrom, ct: ct).ConfigureAwait(false);
            try
            {
                // 请求了 Range 但服务端返回 200 全量：以新内容为准，从头写入。
                if (!result.IsPartialContent)
                    append = false;

                var path = await _storage.WriteToDownloadsAsync(
                        attachmentId, fileName, result.Content, ct, expectedSha256, append)
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
        catch (HttpRequestException ex)
            when (ex.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            // 服务端无该 Range（本地 partial 长于远端文件）：清掉残留，整文件重下。
            Log.Warning(ex, "断点续传范围无效，重置后整文件重下 AttachmentId={AttachmentId}", attachmentId);
            try
            {
                var stalePartial = _storage.GetPartialDownloadPath(attachmentId, fileName);
                if (stalePartial is not null)
                    File.Delete(stalePartial);
            }
            catch (Exception cleanupEx)
            {
                Log.Debug(cleanupEx, "清理无效 partial 失败");
            }
            return await DownloadFreshAsync(attachmentId, fileName, hint, ct).ConfigureAwait(false);
        }
        catch (IOException ex) when (ex.Message.Contains("哈希校验失败", StringComparison.Ordinal))
        {
            // 续传拼接后哈希不匹配（本地 partial 已损坏）：清掉后整文件重下。
            Log.Warning(ex, "续传结果哈希校验失败，重置后整文件重下 AttachmentId={AttachmentId}", attachmentId);
            try
            {
                var stalePartial = _storage.GetPartialDownloadPath(attachmentId, fileName);
                if (stalePartial is not null)
                    File.Delete(stalePartial);
            }
            catch (Exception cleanupEx)
            {
                Log.Debug(cleanupEx, "清理损坏 partial 失败");
            }
            return await DownloadFreshAsync(attachmentId, fileName, hint, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "下载附件失败 AttachmentId={AttachmentId}", attachmentId);
            return null;
        }
    }

    /// <summary>不带 Range 的整文件下载（续传校验失败后的兜底路径）。</summary>
    private async Task<string?> DownloadFreshAsync(
        string attachmentId, string fileName, string hint, CancellationToken ct)
    {
        var expectedSha256 = await TryGetAttachmentSha256Async(attachmentId).ConfigureAwait(false);
        var result = await _attachments.DownloadAsync(hint, ct: ct).ConfigureAwait(false);
        try
        {
            var path = await _storage.WriteToDownloadsAsync(
                    attachmentId, fileName, result.Content, ct, expectedSha256, append: false)
                .ConfigureAwait(false);

            await TryUpdateAttachmentCachePathAsync(attachmentId, path).ConfigureAwait(false);
            return path;
        }
        finally
        {
            result.Content.Dispose();
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
