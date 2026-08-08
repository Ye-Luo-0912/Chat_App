using System;

namespace Core.Helpers;

/// <summary>
/// 消息预览文本截断的统一入口。
/// 消除 MessageStore.BuildPreview 与 MessageViewModel.TruncatePreview 的重复逻辑，
/// 各调用方按用途传不同 maxLen：DB 存储用 100，UI 显示用 80。
/// </summary>
public static class PreviewText
{
    private const string Ellipsis = "…";

    /// <summary>截断文本到 maxLen 个字符，超长则尾部加省略号；空白返回空字符串。</summary>
    public static string Truncate(string? content, int maxLen)
    {
        var s = content?.Trim() ?? string.Empty;
        return s.Length <= maxLen ? s : string.Concat(s.AsSpan(0, maxLen), Ellipsis);
    }

    /// <summary>
    /// 构建会话列表摘要：文本消息优先截断正文；空正文且有图片附件回退
    /// <c>[图片]</c>，仅有其他附件回退 <c>[附件]</c>，否则空字符串。
    /// </summary>
    public static string ForMessage(string? content, bool hasImageAttachment, bool hasAttachment)
    {
        if (!string.IsNullOrWhiteSpace(content))
            return Truncate(content, 100);
        if (hasImageAttachment)
            return "[图片]";
        if (hasAttachment)
            return "[附件]";
        return string.Empty;
    }
}
