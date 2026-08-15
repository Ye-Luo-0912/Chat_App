using System;
using Avalonia.Controls;
using Avalonia.Styling;
using Core.Accessibility;
using Core.Interfaces;
using Serilog;

namespace Chat_App.Presentation.Services;

/// <summary>
/// 无障碍设置的渲染层应用（APP-OPS-1）：订阅 <see cref="IAccessibilityService"/> 的选项变更，
/// 应用到根窗口——字体缩放（根 FontSize 随继承传播）、高对比度（切暗色主题变体）、
/// 减少动效（在根上挂 reduce-motion 类供样式关停动画）。页面切换动画由 HomeView 按
/// <see cref="AccessibilityOptions.ReduceMotion"/> 关停。
/// </summary>
public sealed class AccessibilityThemeApplier
{
    /// <summary>基准字号（px），与 FluentTheme 默认一致；缩放倍率乘以此值得到根字号。</summary>
    public const double BaseFontSize = 14;

    private readonly IAccessibilityService _accessibility;
    private Window? _target;

    public AccessibilityThemeApplier(IAccessibilityService accessibility)
    {
        _accessibility = accessibility;
    }

    /// <summary>绑定根窗口并立即应用当前选项，随后订阅变更。</summary>
    public void Attach(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        _target = window;
        _accessibility.OptionsChanged += OnOptionsChanged;
        Apply(window, _accessibility.Current);
    }

    /// <summary>解绑根窗口与事件订阅（窗口关闭时调用）。</summary>
    public void Detach()
    {
        if (_target is null)
            return;
        _accessibility.OptionsChanged -= OnOptionsChanged;
        _target = null;
    }

    private void OnOptionsChanged(object? sender, AccessibilityOptions options)
    {
        if (_target is { } window)
            Apply(window, options);
    }

    private static void Apply(Window window, AccessibilityOptions options)
    {
        try
        {
            var fontSize = ComputeFontSize(options.FontScale);
            if (Math.Abs(window.FontSize - fontSize) > double.Epsilon)
                window.FontSize = fontSize;

            var variant = ResolveThemeVariant(options.HighContrast);
            if (window.RequestedThemeVariant != variant)
                window.RequestedThemeVariant = variant;

            if (options.ReduceMotion)
                window.Classes.Add("reduce-motion");
            else
                window.Classes.Remove("reduce-motion");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "应用无障碍渲染设置失败");
        }
    }

    /// <summary>由缩放倍率计算根字号（倍率已含非法档位规整，此处仅乘算）。</summary>
    public static double ComputeFontSize(double fontScale) => BaseFontSize * fontScale;

    /// <summary>高对比度开关映射到主题变体：开启切暗色（更高对比度），关闭回默认。</summary>
    public static ThemeVariant ResolveThemeVariant(bool highContrast)
        => highContrast ? ThemeVariant.Dark : ThemeVariant.Default;
}