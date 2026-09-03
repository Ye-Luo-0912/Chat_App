using System;
using System.Collections.Generic;
using System.Globalization;
using Chat_App.Infrastructure.Models;
using Chat_App.Models;
using Chat_App.Presentation.Converters;
using Chat_App.Presentation.ViewModels.Chat;
using Core.Models;
using Core.Services;
using Xunit;

namespace UnitTests;

/// <summary>
/// 聊天主界面 UI 打磨轮次的可测支撑点：
/// 消息状态字形区分（已读 vs 已送达）、未读徽标免打扰降级、
/// 空态绑定属性 <see cref="MessageViewModel.IsMessageListEmpty"/>、
/// 空态/语音播放转换器。ViewModel 用例复用
/// <see cref="VoiceDegradationViewModelTests"/> 中的最小桩。
/// </summary>
public sealed class ChatUiStateTests
{
    // ── 消息状态字形（Models/Message.cs） ──────────────────────────

    [Fact]
    public void StatusGlyph_ReadAndDeliveredShareGlyphButDifferByColor()
    {
        var read = NewMessage(MessageStatus.Read);
        var delivered = NewMessage(MessageStatus.Delivered);

        Assert.Equal("✓✓", read.StatusGlyphText);
        Assert.Equal("✓✓", delivered.StatusGlyphText);
        // 已读必须与已送达在视觉上可区分（同一字形只能靠颜色）。
        Assert.NotEqual(delivered.StatusGlyphColor, read.StatusGlyphColor);
        Assert.Equal("#3B82F6", read.StatusGlyphColor);
        Assert.Equal("#94A3B8", delivered.StatusGlyphColor);
    }

    [Fact]
    public void StatusGlyph_FailedIsAlerting_PendingAndSentAreMuted()
    {
        Assert.Equal("⚠", NewMessage(MessageStatus.Failed).StatusGlyphText);
        Assert.Equal("#EF4444", NewMessage(MessageStatus.Failed).StatusGlyphColor);
        Assert.Equal("#94A3B8", NewMessage(MessageStatus.Sent).StatusGlyphColor);
        Assert.Equal("#94A3B8", NewMessage(MessageStatus.Queued).StatusGlyphColor);
        Assert.Equal("#94A3B8", NewMessage(MessageStatus.Sending).StatusGlyphColor);
    }

    [Fact]
    public void StatusGlyph_VisibleOnlyForOwnUnrecalledMessages()
    {
        var mine = NewMessage(MessageStatus.Sent, isSentByMe: true);
        var theirs = NewMessage(MessageStatus.Read, isSentByMe: false);
        var recalled = NewMessage(MessageStatus.Recalled, isSentByMe: true);

        Assert.True(mine.StatusGlyphVisibility);
        Assert.False(theirs.StatusGlyphVisibility);
        Assert.False(recalled.StatusGlyphVisibility);
    }

    // ── 未读徽标层级（Infrastructure/Models/LocalConversation.cs） ──

    [Fact]
    public void UnreadBadgeColor_RedNormally_GrayWhenMuted()
    {
        var conversation = new LocalConversation { ConversationId = "c1" };

        Assert.False(conversation.UnreadBadgeVisibility);

        conversation.UnreadCount = 3;
        Assert.True(conversation.UnreadBadgeVisibility);
        Assert.Equal("#EF4444", conversation.UnreadBadgeColor);

        // 免打扰会话不使用最醒目的红色，避免打扰式提醒。
        conversation.IsMuted = true;
        Assert.Equal("#9CA3AF", conversation.UnreadBadgeColor);
    }

    // ── 消息列表空态（MessageViewModel.IsMessageListEmpty） ─────────

    [Fact]
    public void IsMessageListEmpty_DefaultsTrue_AndFollowsCollectionChanges()
    {
        using var vm = CreateVm();

        Assert.True(vm.IsMessageListEmpty);

        vm.Messages.Add(new Message { Content = "hi", Sender = new User() });
        Assert.False(vm.IsMessageListEmpty);

        vm.Messages.Clear();
        Assert.True(vm.IsMessageListEmpty);
    }

    // ── 转换器 ─────────────────────────────────────────────────────

    [Fact]
    public void CountIsEmptyConverter_TrueOnlyForEmptyCounts()
    {
        var converter = new CountIsEmptyConverter();

        Assert.True(IsTrue(converter.Convert(0, typeof(bool), null, CultureInfo.InvariantCulture)));
        Assert.True(IsTrue(converter.Convert(null, typeof(bool), null, CultureInfo.InvariantCulture)));
        Assert.False(IsTrue(converter.Convert(5, typeof(bool), null, CultureInfo.InvariantCulture)));
        Assert.False(IsTrue(converter.Convert(42L, typeof(bool), null, CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void VoicePlaybackStateConverter_IsNotThisPlaying_MirrorsIsThisPlaying()
    {
        var converter = new VoicePlaybackStateConverter();
        var playingOther = new List<object?> { true, "voice-a", "voice-b", 0.5d };
        var playingThis = new List<object?> { true, "voice-a", "voice-a", 0.5d };
        var idle = new List<object?> { false, null, "voice-a" };

        Assert.True(IsTrue(converter.Convert(
            playingOther, typeof(bool), "IsNotThisPlaying", CultureInfo.InvariantCulture)));
        Assert.False(IsTrue(converter.Convert(
            playingThis, typeof(bool), "IsNotThisPlaying", CultureInfo.InvariantCulture)));
        // 空闲（无播放）时静态时长可见。
        Assert.True(IsTrue(converter.Convert(
            idle, typeof(bool), "IsNotThisPlaying", CultureInfo.InvariantCulture)));
        Assert.True(IsTrue(converter.Convert(
            playingThis, typeof(bool), "IsThisPlaying", CultureInfo.InvariantCulture)));
    }

    // ── 工具 ───────────────────────────────────────────────────────

    private static Message NewMessage(MessageStatus status, bool isSentByMe = true) => new()
    {
        Content = "hello",
        IsSentByMe = isSentByMe,
        Sender = new User(),
        Status = status
    };

    private static bool IsTrue(object? value) => value is true;

    private static MessageViewModel CreateVm() => new(
        new VoiceDegradationViewModelTests.FakeNotifications(),
        new VoiceDegradationViewModelTests.SessionStub(),
        new VoiceDegradationViewModelTests.FakeAttachmentClient(),
        null!,          // IMessageStore：空态用例不触达
        new InMemoryEventBus(),
        null!,          // IDatabaseService
        null!,          // ICurrentUserContext
        null!,          // IAttachmentStorageService
        new VoiceDegradationViewModelTests.FakeDownload(),
        null!,          // IAttachmentThumbnailService
        new VoiceDegradationViewModelTests.FakeVoiceRecorder(),
        new VoiceDegradationViewModelTests.FakeAudioPlayer());
}
