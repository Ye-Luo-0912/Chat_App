using System;

namespace Core.Accessibility;

/// <summary>
/// 界面字体缩放档位。标准/大/特大三档，映射到 UI 文本缩放倍率。
/// 数值为持久化 wire 值（0=标准，1=大，2=特大），新档位向后追加。
/// </summary>
public enum AccessibilityFontSize : byte
{
    /// <summary>标准（缩放 1.00）。</summary>
    Standard = 0,

    /// <summary>大（缩放 1.15）。</summary>
    Large = 1,

    /// <summary>特大（缩放 1.30）。</summary>
    ExtraLarge = 2
}

/// <summary>字体档位 ↔ UI 缩放倍率映射。</summary>
public static class AccessibilityFontSizeExtensions
{
    /// <summary>标准档位默认缩放倍率。</summary>
    public const double StandardScale = 1.00;

    /// <summary>大档位缩放倍率。</summary>
    public const double LargeScale = 1.15;

    /// <summary>特大档位缩放倍率。</summary>
    public const double ExtraLargeScale = 1.30;

    /// <summary>返回该档位对应的文本缩放倍率（非法值回退标准）。</summary>
    public static double ToScale(this AccessibilityFontSize size)
    {
        return size switch
        {
            AccessibilityFontSize.Large => LargeScale,
            AccessibilityFontSize.ExtraLarge => ExtraLargeScale,
            _ => StandardScale
        };
    }

    /// <summary>返回该档位的本地化显示名（非法值回退标准）。</summary>
    public static string ToDisplayName(this AccessibilityFontSize size)
    {
        return size switch
        {
            AccessibilityFontSize.Large => "大",
            AccessibilityFontSize.ExtraLarge => "特大",
            _ => "标准"
        };
    }

    /// <summary>格式化非法持久化值：仅接受已定义档位，否则回退标准。</summary>
    public static AccessibilityFontSize Coerce(int value)
    {
        var candidate = (AccessibilityFontSize)(byte)value;
        return Enum.IsDefined(candidate) ? candidate : AccessibilityFontSize.Standard;
    }
}