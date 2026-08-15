using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Chat_App.Presentation.Converters;

/// <summary>
/// 语音气泡播放状态（VOICE-MSG-2）：根据全局播放状态与当前附件是否一致，
/// 派生播放图标、进度条值、"是否正在播放"标记与无障碍标签。
/// MultiBinding 值顺序：[IsVoicePlaying, PlayingVoiceAttachmentId, 当前附件 AttachmentId, VoicePlaybackProgress]
/// ConverterParameter：Icon | Progress | IsThisPlaying | Label
/// </summary>
public sealed class VoicePlaybackStateConverter : IMultiValueConverter
{
    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        var isPlaying = values.Count > 0 && values[0] is true;
        var playingId = values.Count > 1 ? values[1] as string : null;
        var attachmentId = values.Count > 2 ? values[2] as string : null;
        var progress = values.Count > 3 && values[3] is double d ? d : 0d;

        var isThisPlaying = isPlaying && playingId is not null && playingId == attachmentId;

        return parameter switch
        {
            "IsThisPlaying" => isThisPlaying,
            "Progress" => isThisPlaying ? progress : 0d,
            "Label" => isThisPlaying ? "暂停语音" : "播放语音", // 无障碍标签
            _ => isThisPlaying ? "暂停" : "播放" // 默认 Icon
        };
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}