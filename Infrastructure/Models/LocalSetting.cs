using System;

namespace Chat_App.Infrastructure.Models;

/// <summary>
/// 本地设置键值行：按账户隔离，(OwnerUserId, Key) 唯一。
/// Value 为字符串化后的设置值；未设置的行表示使用默认值。
/// </summary>
public sealed class LocalSetting
{
    public long Id { get; set; }

    /// <summary>归属账户（账户隔离）。</summary>
    public long OwnerUserId { get; set; }

    /// <summary>设置键（如 notification_preview_enabled）。</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>字符串化的设置值。</summary>
    public string? Value { get; set; }

    /// <summary>最后更新时间（Unix ms）。</summary>
    public long UpdatedAtMs { get; set; }
}