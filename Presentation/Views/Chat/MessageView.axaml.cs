using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Chat_App.Presentation.ViewModels.Chat;

namespace Chat_App.Presentation.Views.Chat;

public partial class MessageView : UserControl
{
    private ScrollViewer? _scroll;
    private bool _loadingOlder;
    // 接近顶部即触发向上加载的距离阈值（八）。
    private const double TopLoadThreshold = 80;

    public MessageView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object? sender, EventArgs e)
    {
        // ListBox 模板应用后取出内部 ScrollViewer，绑定滚动事件以驱动向上加载（八）。
        MessageList.TemplateApplied += OnMessageListTemplateApplied;
        AttachScroll(MessageList.Scroll);
    }

    private void OnUnloaded(object? sender, EventArgs e)
    {
        MessageList.TemplateApplied -= OnMessageListTemplateApplied;
        if (_scroll is not null)
            _scroll.ScrollChanged -= OnScrollChanged;
        _scroll = null;
    }

    private void OnMessageListTemplateApplied(object? sender, TemplateAppliedEventArgs e)
    {
        AttachScroll(MessageList.Scroll);
    }

    private void AttachScroll(IScrollable? scrollable)
    {
        // ListBox.Scroll 返回 IScrollable，实际对象即内部 ScrollViewer，转换后订阅滚动事件（八）。
        var scroll = scrollable as ScrollViewer;
        if (scroll is null || ReferenceEquals(scroll, _scroll))
            return;
        if (_scroll is not null)
            _scroll.ScrollChanged -= OnScrollChanged;
        _scroll = scroll;
        _scroll.ScrollChanged += OnScrollChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is MessageViewModel vm)
        {
            vm.PickAttachmentAsync = PickAttachmentAsync;
            vm.SaveDownloadedAttachmentAsync = SaveDownloadedAttachmentAsync;
        }
    }

    private async void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        var scroll = _scroll;
        if (scroll is null || _loadingOlder)
            return;

        // 接近顶部时向上加载更早历史（八）。
        if (scroll.Offset.Y <= TopLoadThreshold)
        {
            if (DataContext is not MessageViewModel vm)
                return;

            _loadingOlder = true;
            try
            {
                // 视觉锚点：加载前记录内容总高与偏移，加载后按新增高度补偿偏移，保持当前可见位置稳定（八）。
                var oldExtent = scroll.Extent.Height;
                var oldOffset = scroll.Offset.Y;

                var more = await vm.LoadOlderHistoryAsync().ConfigureAwait(true);

                if (more)
                {
                    // 等待布局更新后读取新内容总高，按差值补偿偏移。
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        var delta = scroll.Extent.Height - oldExtent;
                        if (delta > 0)
                            scroll.Offset = new Vector(0, oldOffset + delta);
                    }, DispatcherPriority.Background);
                }
            }
            finally
            {
                _loadingOlder = false;
            }
        }
    }

    private async Task<PickedAttachmentFile?> PickAttachmentAsync(CancellationToken ct)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
            return null;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择附件",
            AllowMultiple = false,
            FileTypeFilter =
            [
                FilePickerFileTypes.ImageAll,
                FilePickerFileTypes.All
            ]
        }).ConfigureAwait(true);

        var file = files.FirstOrDefault();
        if (file is null)
            return null;

        await using var source = await file.OpenReadAsync().ConfigureAwait(true);
        await using var buffer = new MemoryStream();
        await source.CopyToAsync(buffer, ct).ConfigureAwait(true);
        var bytes = buffer.ToArray();
        if (bytes.Length == 0)
            return null;

        var name = file.Name;
        return new PickedAttachmentFile
        {
            FileName = name,
            ContentType = GuessContentType(name),
            ContentLength = bytes.Length,
            OpenRead = () => new MemoryStream(bytes, writable: false)
        };
    }

    private async Task<bool> SaveDownloadedAttachmentAsync(
        string suggestedFileName,
        Stream content,
        string contentType,
        CancellationToken ct)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
            return false;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "保存附件",
            SuggestedFileName = suggestedFileName,
            FileTypeChoices =
            [
                new FilePickerFileType("全部文件") { Patterns = ["*.*"] }
            ]
        }).ConfigureAwait(true);

        if (file is null)
            return false;

        await using var target = await file.OpenWriteAsync().ConfigureAwait(true);
        await content.CopyToAsync(target, ct).ConfigureAwait(true);
        await target.FlushAsync(ct).ConfigureAwait(true);
        _ = contentType;
        return true;
    }

    private static string GuessContentType(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".pdf" => "application/pdf",
            ".txt" => "text/plain",
            _ => "application/octet-stream"
        };
    }
}
