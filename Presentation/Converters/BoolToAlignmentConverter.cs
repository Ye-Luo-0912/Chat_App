using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Layout;
using Avalonia.Media;
using Core.Models;

namespace Chat_App.Presentation.Converters;

public class BoolToAlignmentConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is bool and true ? HorizontalAlignment.Right : HorizontalAlignment.Left;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class BoolToColumnConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is bool and true ? 1 : 0;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class BoolToBrushConverter : IValueConverter
{
    public IBrush? TrueBrush { get; set; }
    public IBrush? FalseBrush { get; set; }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return (value is bool boolValue && boolValue) ? TrueBrush : FalseBrush;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class LongToBoolConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is long l && l > 0;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}

/// <summary>
/// 集合计数为空时返回 true（用于空态占位可见性）。绑定 ObservableCollection.Count；
/// null / 无法识别的值一律视为空（显示空态而非空白）。
/// </summary>
public class CountIsEmptyConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value switch
        {
            int count => count == 0,
            long count => count == 0,
            _ => true
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>消息状态匹配：parameter 为逗号分隔的状态名（如 "Failed" / "Queued,Sending"），命中任一返回 true。</summary>
public class MessageStatusMatchConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not MessageStatus status || parameter is not string names)
            return false;
        foreach (var name in names.Split(','))
        {
            if (Enum.TryParse(name.Trim(), ignoreCase: true, out MessageStatus target) && status == target)
                return true;
        }
        return false;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}