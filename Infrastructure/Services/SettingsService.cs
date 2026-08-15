using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Chat_App.Infrastructure.Models;
using Chat_App.Infrastructure.Models.Context;
using Core.Interfaces;
using Core.Settings;
using Microsoft.EntityFrameworkCore;

namespace Chat_App.Infrastructure.Services;

/// <summary>
/// 设备与安全设置服务：按账户键值持久化到本地 SQLite。
/// 未显式设置或非法持久化值回退默认值；写入仅在值偏离默认/已存在时落盘，
/// 保证表内不积压默认值行。
/// </summary>
public sealed class SettingsService : ISettingsService
{
    private const string KeyNotificationPreview = "notification_preview_enabled";
    private const string KeyAutoDownload = "auto_download_attachments";
    private const string KeyAutoLockOnIdle = "auto_lock_on_idle";
    private const string KeyAutoLockIdleMinutes = "auto_lock_idle_minutes";

    private readonly IDbContextFactory<ClientDbContext> _contextFactory;

    public SettingsService(IDbContextFactory<ClientDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    public async Task<ClientSettings> GetAsync(long ownerUserId, CancellationToken ct = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await db.Settings
            .AsNoTracking()
            .Where(s => s.OwnerUserId == ownerUserId)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var settings = ClientSettings.Defaults();
        var map = rows.ToDictionary(s => s.Key, s => s.Value, StringComparer.Ordinal);

        if (TryParseBool(map, KeyNotificationPreview, out var preview))
            settings.NotificationPreviewEnabled = preview;
        if (TryParseBool(map, KeyAutoDownload, out var autoDownload))
            settings.AutoDownloadAttachments = autoDownload;
        if (TryParseBool(map, KeyAutoLockOnIdle, out var autoLock))
            settings.AutoLockOnIdle = autoLock;
        if (TryParseInt(map, KeyAutoLockIdleMinutes, out var idleMinutes))
            settings.AutoLockIdleMinutes = idleMinutes;

        settings.Normalize();
        return settings;
    }

    public async Task SetAsync(long ownerUserId, ClientSettings settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var normalized = ClientSettings.Defaults();
        normalized.NotificationPreviewEnabled = settings.NotificationPreviewEnabled;
        normalized.AutoDownloadAttachments = settings.AutoDownloadAttachments;
        normalized.AutoLockOnIdle = settings.AutoLockOnIdle;
        normalized.AutoLockIdleMinutes = settings.AutoLockIdleMinutes;
        normalized.Normalize();

        await using var db = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var existing = await db.Settings
            .Where(s => s.OwnerUserId == ownerUserId)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var existingByKey = existing.ToDictionary(s => s.Key, StringComparer.Ordinal);

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var writes = new (string Key, string? Value)[]
        {
            (KeyNotificationPreview, FormatBool(normalized.NotificationPreviewEnabled)),
            (KeyAutoDownload, FormatBool(normalized.AutoDownloadAttachments)),
            (KeyAutoLockOnIdle, FormatBool(normalized.AutoLockOnIdle)),
            (KeyAutoLockIdleMinutes, normalized.AutoLockIdleMinutes.ToString()),
        };

        foreach (var (key, value) in writes)
        {
            if (existingByKey.TryGetValue(key, out var row))
            {
                if (row.Value == value)
                    continue;
                row.Value = value;
                row.UpdatedAtMs = now;
                db.Settings.Update(row);
            }
            else
            {
                db.Settings.Add(new LocalSetting
                {
                    OwnerUserId = ownerUserId,
                    Key = key,
                    Value = value,
                    UpdatedAtMs = now
                });
            }
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task UpdateAsync(long ownerUserId, Action<ClientSettings> mutate, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        var current = await GetAsync(ownerUserId, ct).ConfigureAwait(false);
        mutate(current);
        await SetAsync(ownerUserId, current, ct).ConfigureAwait(false);
    }

    private static bool TryParseBool(IReadOnlyDictionary<string, string?> map, string key, out bool value)
    {
        if (map.TryGetValue(key, out var raw) && bool.TryParse(raw, out value))
            return true;
        value = default;
        return false;
    }

    private static bool TryParseInt(IReadOnlyDictionary<string, string?> map, string key, out int value)
    {
        if (map.TryGetValue(key, out var raw) && int.TryParse(raw, out value))
            return true;
        value = default;
        return false;
    }

    private static string FormatBool(bool value) => value ? "true" : "false";
}