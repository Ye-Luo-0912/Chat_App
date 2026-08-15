using System;
using Avalonia.Animation;
using Avalonia.Data.Converters;
using System.Globalization;

namespace Chat_App.Presentation.Converters;

/// <summary>
/// 减少动效的无障碍渲染：将「是否减少动效」布尔值映射为页面切换过渡。
/// <see langword="true"/> 时返回 <see langword="null"/>（无过渡动画），
/// <see langword="false"/> 时返回默认交叉淡化。
/// </summary>
public sealed class MotionTransitionConverter : IValueConverter
{
    private static readonly IPageTransition DefaultTransition = new CrossFade(TimeSpan.FromMilliseconds(220));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? null : DefaultTransition;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}