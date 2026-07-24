using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Chat_App.Presentation.ViewModels.Chat;

namespace Chat_App.Presentation.Views.Chat;

public partial class MessageView : UserControl
{
    public MessageView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is MessageViewModel vm)
        {
            vm.PickAttachmentAsync = PickAttachmentAsync;
            vm.SaveDownloadedAttachmentAsync = SaveDownloadedAttachmentAsync;
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
