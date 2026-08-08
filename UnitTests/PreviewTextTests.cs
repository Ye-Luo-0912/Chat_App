using Core.Helpers;
using Xunit;

namespace UnitTests;

/// <summary>
/// 会话列表摘要（PreviewText.ForMessage）：
/// 文本优先截断；空正文按附件类型回退 [图片]/[附件]；全空返回空串。
/// </summary>
public class PreviewTextTests
{
    [Fact]
    public void ForMessage_Text_TruncatesTo100()
    {
        var longText = new string('汉', 120);
        var preview = PreviewText.ForMessage(longText, hasImageAttachment: false, hasAttachment: false);
        Assert.Equal(101, preview.Length);
        Assert.Equal(100, preview.AsSpan(0, 100).ToString().Length);
        Assert.EndsWith("…", preview);
    }

    [Fact]
    public void ForMessage_ImageOnly_FallsBackToImageTag()
    {
        Assert.Equal("[图片]", PreviewText.ForMessage("", hasImageAttachment: true, hasAttachment: true));
        Assert.Equal("[图片]", PreviewText.ForMessage(null, hasImageAttachment: true, hasAttachment: false));
    }

    [Fact]
    public void ForMessage_AttachmentOnly_FallsBackToAttachmentTag()
    {
        Assert.Equal("[附件]", PreviewText.ForMessage("  ", hasImageAttachment: false, hasAttachment: true));
    }

    [Fact]
    public void ForMessage_Empty_ReturnsEmptyString()
    {
        Assert.Equal(string.Empty, PreviewText.ForMessage(null, hasImageAttachment: false, hasAttachment: false));
        Assert.Equal(string.Empty, PreviewText.ForMessage("", hasImageAttachment: false, hasAttachment: false));
    }

    [Fact]
    public void ForMessage_ImageWinsOverGenericAttachment()
    {
        Assert.Equal("[图片]", PreviewText.ForMessage("", hasImageAttachment: true, hasAttachment: true));
    }
}
