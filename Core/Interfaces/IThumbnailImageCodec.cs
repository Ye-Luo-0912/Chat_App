using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Core.Interfaces;

/// <summary>
/// 缩略图图像编解码器：将源图片文件解码、等比缩小（最长边不超过 maxDimension 像素）
/// 并编码写入目标文件。由宿主实现（如 SkiaSharp），服务层不依赖具体图像库。
/// 返回 false 表示解码/编码失败（调用方回退为附件链接展示）。
/// </summary>
public interface IThumbnailImageCodec
{
    Task<bool> TryCreateThumbnailAsync(
        string sourceFullPath,
        string destinationPath,
        int maxDimension,
        CancellationToken ct = default);
}
