using Avalonia.Styling;
using Chat_App.Presentation.Converters;
using Chat_App.Presentation.Services;
using System.Globalization;
using Xunit;

namespace UnitTests;

/// <summary>
/// 无障碍渲染层应用测试：字体缩放倍率→根字号映射、高对比度→主题变体映射、
/// 以及减少动效→页面切换过渡的转换器行为。
/// </summary>
public class AccessibilityRenderingTests
{
    // ── AccessibilityThemeApplier 纯映射逻辑 ──

    [Theory]
    [InlineData(1.00, AccessibilityThemeApplier.BaseFontSize)]
    [InlineData(1.15, 14 * 1.15)]
    [InlineData(1.30, 14 * 1.30)]
    public void ComputeFontSize_Multiplies_Base_By_Scale(double scale, double expected)
    {
        Assert.Equal(expected, AccessibilityThemeApplier.ComputeFontSize(scale), precision: 4);
    }

    [Fact]
    public void ResolveThemeVariant_HighContrast_Selects_Dark()
    {
        Assert.Same(ThemeVariant.Dark, AccessibilityThemeApplier.ResolveThemeVariant(highContrast: true));
    }

    [Fact]
    public void ResolveThemeVariant_No_HighContrast_Selects_Default()
    {
        Assert.Same(ThemeVariant.Default, AccessibilityThemeApplier.ResolveThemeVariant(highContrast: false));
    }

    // ── MotionTransitionConverter ──

    [Fact]
    public void Convert_ReduceMotion_True_Returns_Null_Transition()
    {
        var converter = new MotionTransitionConverter();
        var result = converter.Convert(true, typeof(object), null, CultureInfo.InvariantCulture);
        Assert.Null(result);
    }

    [Fact]
    public void Convert_ReduceMotion_False_Returns_Default_Transition()
    {
        var converter = new MotionTransitionConverter();
        var result = converter.Convert(false, typeof(object), null, CultureInfo.InvariantCulture);
        Assert.NotNull(result);
    }

    [Fact]
    public void Convert_ReduceMotion_Null_Treated_As_False()
    {
        var converter = new MotionTransitionConverter();
        var result = converter.Convert(null, typeof(object), null, CultureInfo.InvariantCulture);
        Assert.NotNull(result);
    }

    [Fact]
    public void ConvertBack_ThrowsNotSupported()
    {
        var converter = new MotionTransitionConverter();
        Assert.Throws<NotSupportedException>(() =>
            converter.ConvertBack(null, typeof(object), null, CultureInfo.InvariantCulture));
    }
}