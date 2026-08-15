using System;
using Core.Accessibility;
using Core.Interfaces;
using Core.Settings;

namespace Chat_App.Infrastructure.Services;

/// <summary>
/// 无障碍选项单例持有者实现：线程安全更新当前选项并广播变更。
/// </summary>
public sealed class AccessibilityService : IAccessibilityService
{
    private readonly object _gate = new();

    private AccessibilityOptions _current = AccessibilityOptions.From(ClientSettings.Defaults());

    public AccessibilityOptions Current
    {
        get
        {
            lock (_gate)
                return _current;
        }
    }

    public event EventHandler<AccessibilityOptions>? OptionsChanged;

    /// <summary>应用一组设置并广播解析后的选项。</summary>
    public void Apply(ClientSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var options = AccessibilityOptions.From(settings);

        lock (_gate)
        {
            var changed =
                options.FontScale != _current.FontScale
                || options.ReduceMotion != _current.ReduceMotion
                || options.HighContrast != _current.HighContrast;
            _current = options;

            if (changed)
                OptionsChanged?.Invoke(this, options);
        }
    }
}