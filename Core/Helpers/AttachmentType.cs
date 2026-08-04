using System;

namespace Core.Helpers;

/// <summary>附件类型判定（唯一入口，避免图片判定逻辑散落各处）。</summary>
public static class AttachmentType
{
    /// <summary>按 MIME 判定图片附件（image/*），如 image/jpeg、image/png、image/gif、image/webp。</summary>
    public static bool IsImage(string? contentType) =>
        !string.IsNullOrWhiteSpace(contentType)
        && contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
}
