using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Chat_App.Infrastructure.Persistence;
using Core.Contracts.Attachments;
using Core.Interfaces;
using Infrastructure.Models;
using Serilog;

namespace Core.Services;

/// <summary>
/// Recovers failed attachment uploads on app startup.
/// This file depends on Infrastructure.Models, so it is compiled by Infrastructure.csproj
/// (excluded from Core.csproj) to avoid a circular dependency.
/// </summary>
public sealed class AttachmentRecoveryService
{
    private readonly IDatabaseService _db;
    private readonly IAttachmentClientService _attachments;
    private readonly IAttachmentStorageService _storage;
    private readonly ICurrentUserContext _currentUserContext;

    public AttachmentRecoveryService(
        IDatabaseService db,
        IAttachmentClientService attachments,
        IAttachmentStorageService storage,
        ICurrentUserContext currentUserContext)
    {
        _db = db;
        _attachments = attachments;
        _storage = storage;
        _currentUserContext = currentUserContext;
    }
    /// <summary>Recover all failed/uploading attachment tasks for the current user.</summary>
    public async Task RecoverFailedUploadsAsync(CancellationToken ct = default)
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
        await _db.UpsertAttachmentAsync(att).ConfigureAwait(false);
        await _db.UpdateAttachmentUploadPathAsync(att.OwnerUserId, att.ClientAttachmentId, localUploadingPath: null, retryCount: att.RetryCount + 1).ConfigureAwait(false);

        _storage.DeleteUploadingFile(att.LocalUploadingPath);
        Log.Information("Attachment upload recovered AttachmentId={AttachmentId}", result.AttachmentId);
    }
}
