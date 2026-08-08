using System.ComponentModel;
using System.Runtime.CompilerServices;
using Core.Models.DTO;

namespace Chat_App.Models;

/// <summary>
/// 图片附件缩略图项：附件引用 + 异步生成的本地缩略图路径。
/// ThumbnailPath 就绪后（后台预取完成）通知 UI 展示；失败则保持 null，
/// 消息气泡回退为附件链接（下载/保存行为不变）。
/// </summary>
public sealed class ImageThumbnailItem : INotifyPropertyChanged
{
    private string? _thumbnailPath;
    private string? _fullPath;

    public AttachmentRefDto Attachment { get; }

    public ImageThumbnailItem(AttachmentRefDto attachment)
    {
        Attachment = attachment;
    }

    /// <summary>本地缩略图完整路径（thumbnails/ 目录下 ≤512px JPEG）。</summary>
    public string? ThumbnailPath
    {
        get => _thumbnailPath;
        set
        {
            if (_thumbnailPath == value)
                return;
            _thumbnailPath = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasThumbnail));
        }
    }

    public bool HasThumbnail => !string.IsNullOrWhiteSpace(_thumbnailPath);

    /// <summary>本地原图完整路径（下载缓存），点击缩略图大图预览时使用；未就绪为 null。</summary>
    public string? FullPath
    {
        get => _fullPath;
        set
        {
            if (_fullPath == value)
                return;
            _fullPath = value;
            OnPropertyChanged();
        }
    }

    public string DisplayName => Attachment.DisplayName;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
