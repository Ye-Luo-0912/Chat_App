using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Chat_App.Presentation.Converters;

/// <summary>
/// 语音时长格式化（VOICE-MSG-2）：将毫秒数（long?）格式化为 "mm:ss" 字符串。
/// null / 非正数 / 无法解析时返回 "0:00"。
/// </summary>
public sealed class VoiceDurationConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var ms = value switch
        {
            long l => l,
            int i => i,
            double d => (long)d,
            _ => 0L
        };

        if (ms <= 0)
            return "0:00";

        var time = TimeSpan.FromMilliseconds(ms);
        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
            : $"{time.Minutes}:{time.Seconds:00}";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}