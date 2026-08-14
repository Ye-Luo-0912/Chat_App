using System;

namespace Core.Services.Voice;

/// <summary>
/// 16-bit 有符号 PCM 采样源（VOICE-MSG-2）。
/// 抽象麦克风采集的输入边界：真实设备采集（如 Windows NAudio）与跨平台确定性
/// fallback（如正弦波）都实现本接口，供 <see cref="VoiceRecorderService"/> 消费。
/// 采样源为连续流：Start 后持续产出，直到 Stop 或达到外部设定的时长上限。
/// </summary>
public interface IWaveSampleSource : IDisposable
{
    /// <summary>采样率（Hz）。</summary>
    int SampleRateHz { get; }

    /// <summary>声道数。</summary>
    short Channels { get; }

    /// <summary>开始采集。</summary>
    void Start();

    /// <summary>
    /// 将 PCM 数据读入 <paramref name="pcm16"/>。返回实际读取的字节数。
    /// 在采集结束前可能阻塞（等待采样）。返回 0 表示数据源已结束。
    /// </summary>
    int Read(Span<byte> pcm16);

    /// <summary>停止采集并释放资源。</summary>
    void Stop();
}