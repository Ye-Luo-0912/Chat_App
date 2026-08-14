using System;

namespace Core.Services.Voice;

/// <summary>
/// 跨平台确定性 fallback 采样源：固定频率/幅值的 16-bit PCM 正弦波。
/// 不依赖任何麦克风/原生音频库，可在任意平台与测试环境稳定产出有效音频，
/// 用于打通「录音→WAV→上传→发送→UI」全链路；真实设备采集后续经
/// <see cref="IWaveSampleSource"/> 注入替换。
/// 采样相位由帧序号决定，给定相同参数产物字节级一致（可复现测试）。
/// </summary>
public sealed class SineToneSampleSource : IWaveSampleSource
{
    private readonly double _phasePerSample;
    private readonly double _amplitude; // 0..1 归一化幅值
    private readonly long _maxFrames;   // 总采样帧数上限（超过后数据源结束）
    private long _frameIndex;

    public SineToneSampleSource(
        int sampleRateHz,
        short channels,
        double frequencyHz = 440,
        double amplitude = 0.5,
        TimeSpan? maxDuration = null)
    {
        if (sampleRateHz <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRateHz));
        if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));
        if (frequencyHz <= 0) throw new ArgumentOutOfRangeException(nameof(frequencyHz));
        if (amplitude is <= 0 or > 1) throw new ArgumentOutOfRangeException(nameof(amplitude));

        SampleRateHz = sampleRateHz;
        Channels = channels;
        _phasePerSample = 2.0 * Math.PI * frequencyHz / sampleRateHz;
        _amplitude = amplitude;
        _maxFrames = maxDuration is { } d
            ? (long)(d.TotalSeconds * sampleRateHz)
            : long.MaxValue;
    }

    public int SampleRateHz { get; }
    public short Channels { get; }

    public void Start()
    {
        // 重启时重置采样相位，使同一实例可跨 Start/Stop 复用（VoiceRecorderService 支持多次录音）。
        _frameIndex = 0;
    }

    public int Read(Span<byte> pcm16)
    {
        var frames = Math.Min(pcm16.Length / (Channels * 2), _maxFrames - _frameIndex);
        if (frames <= 0)
            return 0;

        var written = 0;
        for (var f = 0; f < frames; f++)
        {
            // 每帧所有声道写入同一采样值（单声道正弦）。
            var sample = (short)(Math.Sin((_frameIndex + f) * _phasePerSample) * _amplitude * short.MaxValue);
            for (var c = 0; c < Channels; c++)
            {
                pcm16[written] = (byte)sample;
                pcm16[written + 1] = (byte)(sample >> 8);
                written += 2;
            }
        }

        _frameIndex += frames;
        return written;
    }

    public void Stop()
    {
        // 无状态源：无需清理。
    }

    public void Dispose()
    {
        // 无外部资源。
    }
}