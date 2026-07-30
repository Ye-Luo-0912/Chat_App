using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Core.Interfaces;

namespace Core.Services;

/// <summary>
/// 附件本地磁盘存储管理实现。
/// </summary>
public sealed class AttachmentStorageService : IAttachmentStorageService
{
    private readonly ICurrentUserContext _currentUserContext;
    private readonly string _basePath;

    public AttachmentStorageService(ICurrentUserContext currentUserContext)
    {
        _currentUserContext = currentUserContext;
        _basePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ChatApp",
            "Attachments");
    }

    private string OwnerDir
    {
        get
        {
            var owner = _currentUserContext.UserId ?? 0;
            var dir = Path.Combine(_basePath, owner.ToString());
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public string GetAttachmentsRoot() => OwnerDir;

    public string GetUploadingDir()
    {
        var dir = Path.Combine(OwnerDir, "uploading");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public string GetDownloadsDir()
    {
        var dir = Path.Combine(OwnerDir, "downloads");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public string GetThumbnailsDir()
    {
        var dir = Path.Combine(OwnerDir, "thumbnails");
        Directory.CreateDirectory(dir);
        return dir;
    }
    public string CopyToUploading(string sourceFilePath, string fileName)
    {
        var uploadingDir = GetUploadingDir();
        var safeName = SanitizeFileName(fileName);
        var uniqueName = $"{Guid.NewGuid():N}_{safeName}";
        var fullPath = Path.Combine(uploadingDir, uniqueName);
        File.Copy(sourceFilePath, fullPath, overwrite: true);
        return Path.GetFileName(fullPath);
    }

    public async Task<string> WriteToUploadingAsync(Stream content, string fileName, CancellationToken ct = default)
    {
        var uploadingDir = GetUploadingDir();
        var safeName = SanitizeFileName(fileName);
        var uniqueName = $"{Guid.NewGuid():N}_{safeName}";
        var fullPath = Path.Combine(uploadingDir, uniqueName);
        await using var fs = File.Create(fullPath);
        await content.CopyToAsync(fs, ct);
        return Path.GetFileName(fullPath);
    }

    public string ResolvePath(string relativePath)
    {
        return Path.Combine(OwnerDir, relativePath);
    }

    public Stream OpenUploadingRead(string relativePath)
    {
        var fullPath = Path.Combine(GetUploadingDir(), relativePath);
        return new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
    }

    public void DeleteUploadingFile(string relativePath)
    {
        try
        {
            var fullPath = Path.Combine(GetUploadingDir(), relativePath);
            if (File.Exists(fullPath))
                File.Delete(fullPath);
        }
        catch
        {
            // 忽略删除失败
        }
    }
    public string MoveToDownloads(string uploadingRelativePath, string attachmentId, string fileName)
    {
        var downloadsDir = GetDownloadsDir();
        var safeName = SanitizeFileName(fileName);
        var destName = $"{attachmentId}_{safeName}";
        var destPath = Path.Combine(downloadsDir, destName);
        var srcPath = Path.Combine(GetUploadingDir(), uploadingRelativePath);
        if (File.Exists(srcPath))
        {
            File.Move(srcPath, destPath, overwrite: true);
        }

        return Path.GetFileName(destPath);
    }

    public string? GetDownloadCachePath(string attachmentId, string fileName)
    {
        var downloadsDir = GetDownloadsDir();
        var safeName = SanitizeFileName(fileName);
        var path = Path.Combine(downloadsDir, $"{attachmentId}_{safeName}");
        return File.Exists(path) ? path : null;
    }

    public async Task<string> WriteToDownloadsAsync(string attachmentId, string fileName, Stream content, CancellationToken ct = default)
    {
        var downloadsDir = GetDownloadsDir();
        var safeName = SanitizeFileName(fileName);
        var destName = $"{attachmentId}_{safeName}";
        var fullPath = Path.Combine(downloadsDir, destName);
        await using var fs = File.Create(fullPath);
        await content.CopyToAsync(fs, ct);
        return fullPath;
    }

    public long? GetAvailableDiskSpace()
    {
        try
        {
            var root = Path.GetPathRoot(OwnerDir);
            if (string.IsNullOrWhiteSpace(root))
                return null;
            var drive = new DriveInfo(root);
            return drive.AvailableFreeSpace;
        }
        catch
        {
            return null;
        }
    }

    private static string SanitizeFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return "file";
        var invalid = Path.GetInvalidFileNameChars();
        var name = fileName;
        foreach (var c in invalid)
            name = name.Replace(c, '_');
        return name.Length > 200 ? name[..200] : name;
    }
}