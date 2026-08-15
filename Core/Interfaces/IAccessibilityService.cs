using System;
using Core.Accessibility;
using Core.Settings;

namespace Core.Interfaces;

/// <summary>
/// 无障碍选项的单例持有者：承载当前解析出的渲染选项，并广播变更。
/// 视图层以此为来源应用字体缩放/动效/高对比度，解耦于持久化层。
/// </summary>
public interface IAccessibilityService
{
    /// <summary>当前生效的无障碍渲染选项。</summary>
    AccessibilityOptions Current { get; }

    /// <summary>选项变更时触发（含初始化时赋值）。</summary>
    event EventHandler<AccessibilityOptions>? OptionsChanged;

    /// <summary>应用一组设置并广播解析后的选项。</summary>
    void Apply(ClientSettings settings);
}