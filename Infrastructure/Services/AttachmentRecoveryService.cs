using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Chat_App.Infrastructure.Persistence;
using Core.Contracts.Attachments;
using Core.Interfaces;
using Infrastructure.Models;
using Serilog;

namespace Infrastructure.Services;

/// <summary>
/// Recovers failed attachment uploads on app startup and whenever authentication succeeds.
/// This file depends on Infrastructure.Models, so it is compiled by Infrastructure.csproj
/// (excluded from Core.csproj) to avoid a circular dependency.
/// </summary>
public sealed class AttachmentRecoveryService
{
    private readonly IDatabaseService _db;
    private readonly IAttachmentClientService _attachments;
    private readonly IAttachmentStorageService _storage;
    private readonly ICurrentUserContext _currentUserContext;
    private readonly IChatSessionClient _chatSession;
    private readonly SemaphoreSlim _recoveryLock = new(1, 1);
    private int _recovering;

    public AttachmentRecoveryService(
        IDatabaseService db,
        IAttachmentClientService attachments,
        IAttachmentStorageService storage,
        ICurrentUserContext currentUserContext,
        IChatSessionClient chatSession)
    {
        _db = db;
        _attachments = attachments;
        _storage = storage;
        _currentUserContext = currentUserContext;
        _chatSession = chatSession;

        // 恢复任务由鉴权成功事件触发（九1）：不再依赖启动时固定延迟，未登录会重试。
        _chatSession.Authenticated += OnAuthenticated;
    }

    private void OnAuthenticated(object? sender, long userId)
    {
        // 鉴权成功（含重连后重新鉴权）即触发一次恢复，fire-and-forget。
        _ = Task.Run(() => RecoverFailedUploadsAsync());
    }

    /// <summary>Recover all failed/uploading attachment tasks for the current user.</summary>
    public async Task RecoverFailedUploadsAsync(CancellationToken ct = default)
    {
        if (!_currentUserContext.IsAuthenticated)
            return;

        // 防止鉴权事件多次触发导致并发重复恢复（九1）。
        if (Interlocked.CompareExchange(ref _recovering, 1, 0) != 0)
            return;
        try
        {
            await _recoveryLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await RecoverInternalAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                _recoveryLock.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // 正常取消
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "附件恢复任务执行失败");
        }
        finally
        {
            Interlocked.Exchange(ref _recovering, 0);
        }
    }

    private async Task RecoverInternalAsync(CancellationToken ct)
    {
        if (!_currentUserContext.IsAuthenticated)
            return;

        var owner = _currentUserContext.UserId;
        if (owner is null)
            return;

        List<LocalAttachment> failed;
        try
        {
            failed = await _db.GetUploadingAttachmentsAsync(owner.Value).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to query uploading attachments for recovery");
            return;
        }

        if (failed.Count == 0)
            return;

        Log.Information("Found {Count} attachment upload(s) to recover", failed.Count);

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
            await _db.UpdateAttachmentStatusAsync(att.OwnerUserId, att.AttachmentId, att.ClientAttachmentId, 3, null, "Local file lost").ConfigureAwait(false);
            return;
        }

        // Verify local file exists
        var fullPath = _storage.ResolvePath(Path.Combine("uploading", att.LocalUploadingPath));
        if (!File.Exists(fullPath))
        {
            await _db.UpdateAttachmentStatusAsync(att.OwnerUserId, att.AttachmentId, att.ClientAttachmentId, 3, null, "Local file lost").ConfigureAwait(false);
            return;
        }

        // Re-upload from local file
        AttachmentUploadResult result;
        await using (var stream = _storage.OpenUploadingRead(att.LocalUploadingPath))
        {
            result = await _attachments.UploadAndConfirmAsync(
                    stream, att.ContentType, att.SizeBytes, att.FileName,
                    att.ClientAttachmentId, null, maxAttempts: 3, att.Sha256, ct)
                .ConfigureAwait(false);
        }

        // Success: update metadata to Available
        att.AttachmentId = result.AttachmentId;
        att.Status = 1;
        att.DownloadPath = result.DownloadPath;
        att.ObjectKey = result.ObjectKey;
        att.UpdatedAt = DateTime.UtcNow;

        // 九2: 上传成功后将临时文件转为下载缓存，避免再次打开仍需网络下载。
        try
        {
            att.LocalCachePath = _storage.MoveToDownloads(att.LocalUploadingPath!, result.AttachmentId, att.FileName ?? "file");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to move recovered file to downloads cache");
            _storage.DeleteUploadingFile(att.LocalUploadingPath);
        }

        await _db.UpsertAttachmentAsync(att).ConfigureAwait(false);
        await _db.UpdateAttachmentUploadPathAsync(att.OwnerUserId, att.ClientAttachmentId, localUploadingPath: null, retryCount: att.RetryCount + 1).ConfigureAwait(false);

        Log.Information("Attachment upload recovered AttachmentId={AttachmentId}", result.AttachmentId);
    }
}
