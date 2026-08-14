using System;

namespace Core.Interfaces;

/// <summary>音频播放进度（VOICE-MSG-2）。</summary>
public sealed record AudioPlaybackProgress(string Key, TimeSpan Position, TimeSpan Duration);

/// <summary>
/// 音频播放器抽象（VOICE-MSG-2）：播放本地 WAV 文件并报告进度/停止。
/// Key 用于标识当前播放的语音附件（UI 据此判断哪个气泡在播）。
/// 实现位于 Infrastructure（NAudio/WaveOut），UI/ViewModel 不感知具体播放后端。
/// </summary>
public interface IAudioPlayer : IDisposable
{
    /// <summary>是否正在播放。</summary>
    bool IsPlaying { get; }

    /// <summary>当前播放的附件 Key（无则 null）。</summary>
    string? CurrentKey { get; }

    /// <summary>播放进度（约每帧回调一次）。</summary>
    event Action<AudioPlaybackProgress>? Progress;

    /// <summary>播放停止（自然结束或调用 Stop）。</summary>
    event Action? Stopped;

    /// <summary>开始播放指定 WAV 文件；若正在播放会自动切换到新 Key。</summary>
    void Play(string key, string wavPath);

    /// <summary>暂停当前播放。</summary>
    void Pause();

    /// <summary>恢复暂停的播放。</summary>
    void Resume();

    /// <summary>停止并释放当前播放。</summary>
    void Stop();
}