using Chat_App.Infrastructure.Services;
using Core.Accessibility;
using Core.Interfaces;
using Core.Settings;
using Xunit;

namespace UnitTests;

/// <summary>
/// 无障碍渲染选项解析与广播测试：档位→缩放倍率映射、选项解析、
/// 以及 <see cref="IAccessibilityService"/> 的变更广播与幂等。
/// </summary>
public class AccessibilityOptionsTests
{
    [Theory]
    [InlineData(AccessibilityFontSize.Standard, AccessibilityFontSizeExtensions.StandardScale)]
    [InlineData(AccessibilityFontSize.Large, AccessibilityFontSizeExtensions.LargeScale)]
    [InlineData(AccessibilityFontSize.ExtraLarge, AccessibilityFontSizeExtensions.ExtraLargeScale)]
    public void FontSize_ToScale_Maps_Each_Level(AccessibilityFontSize size, double expected)
    {
        Assert.Equal(expected, size.ToScale());
    }

    [Fact]
    public void FontSize_Invalid_Value_Coerces_To_Standard()
    {
        Assert.Equal(AccessibilityFontSize.Standard, AccessibilityFontSizeExtensions.Coerce(99));
        Assert.Equal(AccessibilityFontSize.Standard, AccessibilityFontSizeExtensions.Coerce(-1));
    }

    [Fact]
    public void Options_From_Settings_Resolves_All_Fields()
    {
        var settings = new ClientSettings
        {
            FontSize = AccessibilityFontSize.ExtraLarge,
            ReduceMotion = true,
            HighContrast = true
        };

        var options = AccessibilityOptions.From(settings);

        Assert.Equal(AccessibilityFontSizeExtensions.ExtraLargeScale, options.FontScale);
        Assert.True(options.ReduceMotion);
        Assert.True(options.HighContrast);
    }

    [Fact]
    public void Options_ScaleFont_Applies_Multiplier()
    {
        var options = AccessibilityOptions.From(new ClientSettings { FontSize = AccessibilityFontSize.Large });
        Assert.Equal(13 * AccessibilityFontSizeExtensions.LargeScale, options.ScaleFont(13));
    }

    [Fact]
    public void Defaults_Resolve_Standard_Scale()
    {
        var options = AccessibilityOptions.From(ClientSettings.Defaults());
        Assert.Equal(AccessibilityFontSizeExtensions.StandardScale, options.FontScale);
        Assert.False(options.ReduceMotion);
        Assert.False(options.HighContrast);
    }

    [Fact]
    public void Service_Applies_And_Broadcasts_Options()
    {
        var service = new AccessibilityService();
        AccessibilityOptions? last = null;
        service.OptionsChanged += (_, o) => last = o;

        service.Apply(new ClientSettings
        {
            FontSize = AccessibilityFontSize.ExtraLarge,
            ReduceMotion = true
        });

        var current = service.Current;
        Assert.Equal(AccessibilityFontSizeExtensions.ExtraLargeScale, current.FontScale);
        Assert.True(current.ReduceMotion);
        Assert.NotNull(last);
        Assert.Equal(current.FontScale, last!.FontScale);
    }

    [Fact]
    public void Service_Same_Options_Does_Not_ReBroadcast()
    {
        var service = new AccessibilityService();
        var count = 0;
        service.OptionsChanged += (_, _) => count++;

        var settings = new ClientSettings { HighContrast = true };
        service.Apply(settings);
        service.Apply(settings);

        Assert.Equal(1, count);
    }
}