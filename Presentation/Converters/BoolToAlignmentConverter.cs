using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Layout;
using Avalonia.Media;

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