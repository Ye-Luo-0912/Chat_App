using Core.Settings;

namespace Core.Accessibility;

/// <summary>
/// 从 <see cref="ClientSettings"/> 解析出的、可直接用于 UI 渲染的无障碍选项。
/// 让设置模型与渲染层解耦：渲染层只消费本值对象，不直接读取持久化键。
/// </summary>
public sealed class AccessibilityOptions
{
    /// <summary>文本缩放倍率（1.00 / 1.15 / 1.30）。</summary>
    public double FontScale { get; init; }

    /// <summary>是否减少动效（禁用过渡/滚动动画）。</summary>
    public bool ReduceMotion { get; init; }

    /// <summary>是否启用高对比度文本。</summary>
    public bool HighContrast { get; init; }

    /// <summary>按基础字号计算缩放后的 UI 文本像素值。</summary>
    public double ScaleFont(double baseSize) => baseSize * FontScale;

    /// <summary>从设置解析出渲染选项。</summary>
    public static AccessibilityOptions From(ClientSettings settings)
    {
        return new AccessibilityOptions
        {
            FontScale = settings.FontSize.ToScale(),
            ReduceMotion = settings.ReduceMotion,
            HighContrast = settings.HighContrast
        };
    }
}