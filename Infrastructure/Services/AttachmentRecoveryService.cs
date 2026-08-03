using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Chat_App.Infrastructure.Persistence;
using Core.Contracts.Attachments;
using Core.Interfaces;
using Chat_App.Infrastructure.Models;
using Serilog;

namespace Chat_App.Infrastructure.Services;

/// <summary>
/// 恢复失败的附件上传：鉴权成功事件触发，也支持启动时（已登录）主动调用。
/// 恢复路径只使用调用方显式传入的 ownerUserId，不读可变全局上下文；
/// 同用户并发恢复由 per-user 去重防止重复，不同用户可并行恢复。
/// </summary>
public sealed class AttachmentRecoveryService
{
    private readonly IDatabaseService _db;
    private readonly IAttachmentClientService _attachments;
    private readonly IAttachmentStorageService _storage;
    private readonly IChatSessionClient _chatSession;
    private readonly ConcurrentDictionary<long, byte> _inProgress = new();

    public AttachmentRecoveryService(
        IDatabaseService db,
        IAttachmentClientService attachments,
        IAttachmentStorageService storage,
        IChatSessionClient chatSession)
    {
        _db = db;
        _attachments = attachments;
        _storage = storage;
        _chatSession = chatSession;

        // 恢复任务由鉴权成功事件触发：不再依赖启动时固定延迟，未登录会重试。
        _chatSession.Authenticated += OnAuthenticated;
    }

    private void OnAuthenticated(object? sender, long userId)
    {
        // 鉴权成功（含重连后重新鉴权）即触发一次恢复，fire-and-forget。
        _ = Task.Run(() => RecoverFailedUploadsAsync(userId));
    }

    /// <summary>恢复指定用户的失败/上传中附件任务。</summary>
    public async Task RecoverFailedUploadsAsync(long ownerUserId, CancellationToken ct = default)
    {
        // 按用户去重：同一用户的并发恢复直接跳过，不同用户互不阻塞。
        if (!_inProgress.TryAdd(ownerUserId, 0))
            return;
        try
        {
            await RecoverInternalAsync(ownerUserId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 正常取消
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "附件恢复任务执行失败 OwnerUserId={OwnerUserId}", ownerUserId);
        }
        finally
        {
            _inProgress.TryRemove(ownerUserId, out _);
        }
    }

    private async Task RecoverInternalAsync(long ownerUserId, CancellationToken ct)
    {
        List<LocalAttachment> failed;
        try
        {
            failed = await _db.GetRecoverableAttachmentsAsync(ownerUserId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to query recoverable attachments for recovery OwnerUserId={OwnerUserId}", ownerUserId);
            return;
        }

        if (failed.Count == 0)
            return;

        Log.Information("Found {Count} attachment upload(s) to recover for OwnerUserId={OwnerUserId}", failed.Count, ownerUserId);

        foreach (var att in failed)
        {
            if (ct.IsCancellationRequested)
                break;

            try
            {
                await RecoverSingleAsync(att, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Recovery failed for ClientAttachmentId={ClientAttachmentId}", att.ClientAttachmentId);
            }
        }
    }
    private async Task RecoverSingleAsync(LocalAttachment att, CancellationToken ct)
    {
        // No local file path: abandon
        if (string.IsNullOrWhiteSpace(att.LocalUploadingPath))
        {
            await _db.UpdateAttachmentStatusAsync(att.OwnerUserId, att.AttachmentId, att.ClientAttachmentId, AttachmentStatus.Abandoned, null, "Local file lost").ConfigureAwait(false);
            return;
        }

        // Verify local file exists
        var fullPath = _storage.ResolvePath(att.OwnerUserId, Path.Combine("uploading", att.LocalUploadingPath));
        if (!File.Exists(fullPath))
        {
            await _db.UpdateAttachmentStatusAsync(att.OwnerUserId, att.AttachmentId, att.ClientAttachmentId, AttachmentStatus.Abandoned, null, "Local file lost").ConfigureAwait(false);
            return;
        }

        // Re-upload from local file
        AttachmentUploadResult result;
        await using (var stream = _storage.OpenUploadingRead(att.OwnerUserId, att.LocalUploadingPath))
        {
            result = await _attachments.UploadAndConfirmAsync(
                    stream, att.ContentType, att.SizeBytes, att.FileName,
                    att.ClientAttachmentId, null, maxAttempts: 3, att.Sha256, ct)
                .ConfigureAwait(false);
        }

        // Success: update metadata to Available
        att.AttachmentId = result.AttachmentId;
        att.Status = AttachmentStatus.Available;
        att.DownloadPath = result.DownloadPath;
        att.ObjectKey = result.ObjectKey;
        att.UpdatedAt = DateTime.UtcNow;

        // 九2: 上传成功后将临时文件转为下载缓存，避免再次打开仍需网络下载。
        try
        {
            att.LocalCachePath = _storage.MoveToDownloads(att.OwnerUserId, att.LocalUploadingPath!, result.AttachmentId, att.FileName ?? "file");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to move recovered file to downloads cache");
            _storage.DeleteUploadingFile(att.OwnerUserId, att.LocalUploadingPath);
        }

        await _db.UpsertAttachmentAsync(att).ConfigureAwait(false);
        await _db.UpdateAttachmentUploadPathAsync(att.OwnerUserId, att.ClientAttachmentId, localUploadingPath: null, retryCount: att.RetryCount + 1).ConfigureAwait(false);

        Log.Information("Attachment upload recovered AttachmentId={AttachmentId}", result.AttachmentId);
    }
}

