using System;
using Core.Accessibility;

namespace Core.Settings;

/// <summary>
/// 客户端设备与安全设置的类型化模型（带默认值与非法值规整）。
/// UI 层直接绑定这些属性；持久化层将其扁平化为按 key 存储的键值行，
/// 未显式设置的项回退到默认值。
/// </summary>
public sealed class ClientSettings
{
    /// <summary>通知是否显示消息预览。</summary>
    public bool NotificationPreviewEnabled { get; set; } = true;

    /// <summary>是否自动下载收到的媒体附件。</summary>
    public bool AutoDownloadAttachments { get; set; } = true;

    /// <summary>空闲一段时间后是否自动锁定应用。</summary>
    public bool AutoLockOnIdle { get; set; }

    /// <summary>空闲自动锁定阈值（分钟），需 ≥0。</summary>
    public int AutoLockIdleMinutes { get; set; } = DefaultAutoLockIdleMinutes;

    // ---- 无障碍体验 ----
    /// <summary>界面字体缩放档位。</summary>
    public AccessibilityFontSize FontSize { get; set; } = AccessibilityFontSize.Standard;

    /// <summary>是否减少动效（禁用过渡/滚动动画）。</summary>
    public bool ReduceMotion { get; set; }

    /// <summary>是否启用高对比度文本。</summary>
    public bool HighContrast { get; set; }

    // ---- 语音播放（VOICE-MSG-3）----
    /// <summary>
    /// 语音播放输出设备偏好（WaveOut 设备序号的十进制字符串）。
    /// null/空白 = 系统默认；设备被拔出/序号漂移时由播放器回退系统默认。
    /// </summary>
    public string? AudioOutputDeviceId { get; set; }

    /// <summary>默认空闲自动锁定阈值（分钟）。</summary>
    public const int DefaultAutoLockIdleMinutes = 5;

    /// <summary>空闲锁定阈值下限/上限（分钟），用于规整非法持久化值。</summary>
    public const int MinAutoLockIdleMinutes = 0;
    public const int MaxAutoLockIdleMinutes = 480;

    /// <summary>将每个属性规整到合法范围（非法持久化值回退默认）。</summary>
    public void Normalize()
    {
        if (AutoLockIdleMinutes < MinAutoLockIdleMinutes || AutoLockIdleMinutes > MaxAutoLockIdleMinutes)
            AutoLockIdleMinutes = DefaultAutoLockIdleMinutes;
        FontSize = AccessibilityFontSizeExtensions.Coerce((int)FontSize);
        AudioOutputDeviceId = string.IsNullOrWhiteSpace(AudioOutputDeviceId)
            ? null
            : AudioOutputDeviceId.Trim();
    }

    /// <summary>全部采用默认值的设置。</summary>
    public static ClientSettings Defaults() => new();
}