using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Core.Interfaces;
using SkiaSharp;

namespace Chat_App.Services;

/// <summary>
/// SkiaSharp 缩略图编解码器：解码原图 → 等比缩放（最长边 maxDimension）→ JPEG 编码原子写入。
/// 生成在线程池执行（SkiaSharp 解码为 CPU 密集操作），不阻塞 UI 线程。
/// </summary>
public sealed class SkiaThumbnailImageCodec : IThumbnailImageCodec
{
    private const int JpegQuality = 85;

    public Task<bool> TryCreateThumbnailAsync(
        string sourceFullPath, string destinationPath, int maxDimension, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            using var source = File.OpenRead(sourceFullPath);
            using var bitmap = SKBitmap.Decode(source);
            if (bitmap is null || bitmap.Width <= 0 || bitmap.Height <= 0)
                return false;

            var scale = Math.Min(1.0, maxDimension / (double)Math.Max(bitmap.Width, bitmap.Height));
            var width = Math.Max(1, (int)(bitmap.Width * scale));
            var height = Math.Max(1, (int)(bitmap.Height * scale));

            using var resized = bitmap.Resize(
                new SKImageInfo(width, height),
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
            if (resized is null)
                return false;

            using var image = SKImage.FromBitmap(resized);
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, JpegQuality);
            if (data is null)
                return false;

            using var output = File.Create(destinationPath);
            data.SaveTo(output);
            output.Flush();
            return true;
        }, ct);
    }
}
