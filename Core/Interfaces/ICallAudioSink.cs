using System;

namespace Core.Interfaces;

/// <summary>
/// 实时通话音频播放 sink（CALL-E2E-2）。
/// <para>
/// 区别于 <see cref="IAudioPlayer"/>（播放本地 WAV 文件），该抽象面向通话媒体面的连续
/// PCM 拉流：远端音频经 RTP/SRTP 解码后以 16-bit PCM 小端样本持续写入，实现边收边播。
/// 实现位于 Infrastructure（NAudio/WaveOut + BufferedWaveProvider）。
/// </para>
/// </summary>
public interface ICallAudioSink : IDisposable
{
    /// <summary>
    /// 打开输出设备（通话建立时调用一次）。调用前必须处于 Closed 状态。
    /// </summary>
    void Open(int sampleRateHz, short channels);

    /// <summary>写入一份 16-bit PCM（小端）样本来播放。</summary>
    void Write(ReadOnlySpan<byte> pcm16);

    /// <summary>停止播放并关闭输出设备（通话结束或暂停）。</summary>
    void Close();
}