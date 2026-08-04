using System;
using System.Collections.Concurrent;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;
using Serilog;

namespace Chat_App.Presentation.Converters;

/// <summary>
/// 本地文件路径 → Bitmap。缩略图为 ≤512px JPEG，UI 线程解码开销小（毫秒级）；
/// 进程内小缓存（256 项）避免虚拟化列表滚动时重复解码。
/// </summary>
public class PathToBitmapConverter : IValueConverter
{
    private const int MaxCacheEntries = 256;

    private static readonly ConcurrentDictionary<string, Bitmap> Cache = new(StringComparer.Ordinal);

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || string.IsNullOrWhiteSpace(path))
            return null;

        if (Cache.TryGetValue(path, out var cached))
            return cached;

        try
        {
            var bitmap = new Bitmap(path);
            // 简单封顶：超限清空重建（缩略图文件小，重建代价低）。
            if (Cache.Count >= MaxCacheEntries)
                Cache.Clear();
            Cache[path] = bitmap;
            return bitmap;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "加载缩略图失败 Path={Path}", path);
            return null;
        }
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}
