using System;
using System.IO;
using System.Threading.Tasks;
using Chat_App.Services;
using SkiaSharp;
using Xunit;

namespace UnitTests;

/// <summary>
/// SkiaSharp 缩略图编解码器测试：真实解码/缩放/编码。
/// 2000×1000 PNG → 最长边 512 的 JPEG，宽高比保持。
/// </summary>
public class SkiaThumbnailImageCodecTests
{
    [Fact]
    public async Task Large_Image_Is_Scaled_To_Max_Dimension_Keeping_Aspect()
    {
        var source = Path.Combine(Path.GetTempPath(), $"chat_src_{Guid.NewGuid():N}.png");
        var dest = Path.Combine(Path.GetTempPath(), $"chat_thumb_{Guid.NewGuid():N}.jpg");
        try
        {
            // 2000×1000 红色渐变测试图
            using (var bitmap = new SKBitmap(2000, 1000))
            {
                for (var y = 0; y < 1000; y++)
                {
                    for (var x = 0; x < 2000; x++)
                    {
                        bitmap.SetPixel(x, y, new SKColor((byte)(x / 8), (byte)(y / 4), 128));
                    }
                }
                using var image = SKImage.FromBitmap(bitmap);
                using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                using var fs = File.Create(source);
                data.SaveTo(fs);
            }

            var codec = new SkiaThumbnailImageCodec();
            var ok = await codec.TryCreateThumbnailAsync(source, dest, maxDimension: 512);

            Assert.True(ok);
            Assert.True(File.Exists(dest));
            Assert.EndsWith(".jpg", dest, StringComparison.OrdinalIgnoreCase);

            using var decoded = SKBitmap.Decode(dest);
            Assert.NotNull(decoded);
            Assert.True(decoded!.Width <= 512);
            Assert.True(decoded.Height <= 512);
            // 宽高比 2:1 保持
            Assert.Equal(2.0, decoded.Width / (double)decoded.Height, precision: 1);
        }
        finally
        {
            TryDelete(source);
            TryDelete(dest);
        }
    }

    [Fact]
    public async Task Small_Image_Is_Not_Upscaled()
    {
        var source = Path.Combine(Path.GetTempPath(), $"chat_src_{Guid.NewGuid():N}.png");
        var dest = Path.Combine(Path.GetTempPath(), $"chat_thumb_{Guid.NewGuid():N}.jpg");
        try
        {
            using (var bitmap = new SKBitmap(100, 80))
            {
                bitmap.Erase(SKColors.CornflowerBlue);
                using var image = SKImage.FromBitmap(bitmap);
                using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                using var fs = File.Create(source);
                data.SaveTo(fs);
            }

            var codec = new SkiaThumbnailImageCodec();
            var ok = await codec.TryCreateThumbnailAsync(source, dest, maxDimension: 512);

            Assert.True(ok);
            using var decoded = SKBitmap.Decode(dest);
            Assert.Equal(100, decoded!.Width);
            Assert.Equal(80, decoded.Height);
        }
        finally
        {
            TryDelete(source);
            TryDelete(dest);
        }
    }

    [Fact]
    public async Task Corrupt_Source_Returns_False()
    {
        var source = Path.Combine(Path.GetTempPath(), $"chat_src_{Guid.NewGuid():N}.png");
        var dest = Path.Combine(Path.GetTempPath(), $"chat_thumb_{Guid.NewGuid():N}.jpg");
        try
        {
            File.WriteAllBytes(source, [0, 1, 2, 3, 4, 5]);

            var codec = new SkiaThumbnailImageCodec();
            var ok = await codec.TryCreateThumbnailAsync(source, dest, maxDimension: 512);

            Assert.False(ok);
            Assert.False(File.Exists(dest));
        }
        finally
        {
            TryDelete(source);
            TryDelete(dest);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // 忽略
        }
    }
}
