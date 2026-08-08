using System;
using System.Collections.Concurrent;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;
using Serilog;

namespace Chat_App.Presentation.Converters;

/// <summary>
/// 本地文件路径 → Bitmap。缩略图为 ≤512px JPEG，UI 线程解码开销小（毫秒级）；
/// 进程内缓存避免虚拟化列表滚动时重复解码。
/// 每个转换器实例独立缓存（共享 PathToBitmap 用 256 项上限服务缩略图流；
/// 大图预览用独立小缓存实例，避免整幅原图驻留）。
/// </summary>
public class PathToBitmapConverter : IValueConverter
{
    private const int DefaultMaxCacheEntries = 256;

    private readonly ConcurrentDictionary<string, Bitmap> _cache = new(StringComparer.Ordinal);

    /// <summary>缓存项上限，超限清空重建（缩略图文件小，重建代价低）。</summary>
    public int MaxCacheEntries { get; set; } = DefaultMaxCacheEntries;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || string.IsNullOrWhiteSpace(path))
            return null;

        if (_cache.TryGetValue(path, out var cached))
            return cached;

        try
        {
            var bitmap = new Bitmap(path);
            if (_cache.Count >= MaxCacheEntries)
                _cache.Clear();
            _cache[path] = bitmap;
            return bitmap;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "加载图片失败 Path={Path}", path);
            return null;
        }
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}
