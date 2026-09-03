using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Chat_App.Presentation.Converters;

/// <summary>
/// 语音波形峰值包络转换（VOICE-MSG-2 渲染）：peaks byte[]（0–255 归一化，来自
/// TcpAttachmentRef.VoiceWaveformPeaks）→ 气泡柱状波形。
/// ConverterParameter：HasPeaks | LacksPeaks | Bars（默认）。
/// - Bars：降采样到固定 <see cref="RenderedBarCount"/> 根柱，返回像素高度集合
///   （IReadOnlyList&lt;double&gt;，ItemTemplate 直接 Height="{Binding}"）。
/// - 降采样取块内最大值（保留峰值），柱高线性映射 [0,255] → [<see cref="MinBarHeight"/>, <see cref="MaxBarHeight"/>] 像素。
/// - peaks 为 null/空：HasPeaks=false、Bars 为空集合——调用端据此降级为进度条+时长（无波形）。
/// 纯静态核心（<see cref="BuildBarHeights"/>）便于单测。
/// </summary>
public sealed class VoiceWaveformConverter : IValueConverter
{
    /// <summary>气泡内实际渲染的柱数（与包络桶数 48 解耦，避免气泡过宽）。</summary>
    public const int RenderedBarCount = 24;

    /// <summary>柱最大高度（像素）：peak=255（满幅）。</summary>
    public const double MaxBarHeight = 22.0;

    /// <summary>柱最小高度（像素）：静音/近静音保留 2px 基线刻度，波形不至于视觉断裂。</summary>
    public const double MinBarHeight = 2.0;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var peaks = value as byte[];
        return parameter switch
        {
            "HasPeaks" => HasPeaks(peaks),
            "LacksPeaks" => !HasPeaks(peaks),
            _ => BuildBarHeights(peaks) // 默认 Bars
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    public static bool HasPeaks(byte[]? peaks)
        => peaks is { Length: > 0 };

    /// <summary>
    /// peaks → 渲染柱高集合：块内最大降采样到 <paramref name="barCount"/> 根，
    /// 线性映射到 [MinBarHeight, MaxBarHeight]。null/空输入返回空集合（无波形降级）。
    /// 确定性：同输入同输出，无随机/时间依赖。
    /// </summary>
    public static IReadOnlyList<double> BuildBarHeights(byte[]? peaks, int barCount = RenderedBarCount)
    {
        if (!HasPeaks(peaks))
            return [];

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(barCount);
        var source = peaks!;
        var rendered = Math.Min(barCount, source.Length);
        var heights = new double[rendered];
        for (var bar = 0; bar < rendered; bar++)
        {
            // 等分块：bar i 覆盖 [i*len/rendered, (i+1)*len/rendered)，取块内最大值保留峰。
            var start = (int)((long)bar * source.Length / rendered);
            var end = (int)((long)(bar + 1) * source.Length / rendered);
            var max = 0;
            for (var i = start; i < end; i++)
                max = Math.Max(max, source[i]);
            heights[bar] = MinBarHeight + (MaxBarHeight - MinBarHeight) * max / 255d;
        }

        return heights;
    }
}
