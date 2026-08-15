using System;
using System.Globalization;
using Chat_App.Infrastructure.Services.Voice;
using Chat_App.Presentation.Converters;
using Xunit;

namespace UnitTests;

/// <summary>
/// 语音播放链路（VOICE-MSG-2）单元测试：时长格式化转换器 + 播放器边界行为。
/// 转换器为纯逻辑，可直接断言；播放器仅验证不依赖音频设备的边界（缺失文件、空参）。
/// </summary>
public sealed class VoicePlaybackTests
{
    // ---- VoiceDurationConverter：long? 毫秒 → "mm:ss" ----

    [Theory]
    [InlineData(0L, "0:00")]
    [InlineData(-5L, "0:00")]
    [InlineData(4_500L, "0:04")]
    [InlineData(59_900L, "0:59")]
    [InlineData(60_000L, "1:00")]
    [InlineData(4_500_000L, "1:15:00")]
    [InlineData(3_660_000L, "1:01:00")]
    public void Convert_FormatsMilliseconds(long ms, string expected)
    {
        var converter = new VoiceDurationConverter();
        Assert.Equal(expected, converter.Convert(ms, typeof(string), null, CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-number")]
    public void Convert_InvalidInput_ReturnsZeroTime(object? value)
    {
        var converter = new VoiceDurationConverter();
        Assert.Equal("0:00", converter.Convert(value, typeof(string), null, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Convert_HoursFormat_UsesHms()
    {
        // 超过 1 小时：H:mm:ss
        var converter = new VoiceDurationConverter();
        Assert.Equal("1:01:30", converter.Convert(3_690_000L, typeof(string), null, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void ConvertBack_ThrowsNotSupported()
    {
        var converter = new VoiceDurationConverter();
        Assert.Throws<NotSupportedException>(() =>
            converter.ConvertBack("0:00", typeof(long), null, CultureInfo.InvariantCulture));
    }

    // ---- VoicePlaybackStateConverter：无障碍标签（"播放语音"/"暂停语音"）----

    [Theory]
    [InlineData(false, null, "att-1", "播放语音")]
    [InlineData(true, "att-1", "att-1", "暂停语音")]   // 正在播放当前附件
    [InlineData(true, "att-2", "att-1", "播放语音")]   // 正在播放其他附件
    [InlineData(true, null, "att-1", "播放语音")]      // 播放中但无当前附件 Id
    public void Convert_Label_Reflects_Playback_State(
        bool isPlaying, string? playingId, string? attachmentId, string expected)
    {
        var converter = new VoicePlaybackStateConverter();
        IList<object?> values = [isPlaying, playingId, attachmentId];
        var result = converter.Convert(values, typeof(string), "Label", CultureInfo.InvariantCulture);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Convert_Label_And_Icon_Are_Consistent()
    {
        var converter = new VoicePlaybackStateConverter();
        IList<object?> playing = [true, "att-1", "att-1"];
        IList<object?> idle = [false, null, "att-1"];

        Assert.Equal("暂停语音", converter.Convert(playing, typeof(string), "Label", CultureInfo.InvariantCulture));
        Assert.Equal("暂停", converter.Convert(playing, typeof(string), "Icon", CultureInfo.InvariantCulture));
        Assert.Equal("播放语音", converter.Convert(idle, typeof(string), "Label", CultureInfo.InvariantCulture));
        Assert.Equal("播放", converter.Convert(idle, typeof(string), "Icon", CultureInfo.InvariantCulture));
    }

    [Fact]
    public void ConvertBack_ThrowsNotSupported_For_StateConverter()
    {
        var converter = new VoicePlaybackStateConverter();
        IList<object?> values = ["播放语音"];
        Assert.Throws<NotSupportedException>(() =>
            converter.ConvertBack(values, [typeof(string)], "Label", CultureInfo.InvariantCulture));
    }

    // ---- PcmAudioPlayer：不依赖音频设备的边界行为 ----

    [Fact]
    public void Play_MissingFile_ThrowsFileNotFound()
    {
        using var player = new PcmAudioPlayer();
        Assert.Throws<System.IO.FileNotFoundException>(() =>
            player.Play("key-1", @"Z:\nonexistent\voice.wav"));
    }

    [Theory]
    [InlineData(null, @"C:\x.wav")]
    [InlineData("", @"C:\x.wav")]
    [InlineData("  ", @"C:\x.wav")]
    [InlineData("key-1", null)]
    [InlineData("key-1", "")]
    public void Play_NullOrEmptyArgument_ThrowsArgumentNull(string? key, string? path)
    {
        using var player = new PcmAudioPlayer();
        Assert.Throws<ArgumentNullException>(() => player.Play(key!, path!));
    }
}