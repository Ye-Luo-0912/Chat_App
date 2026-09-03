using System;

namespace Core.Interfaces;

/// <summary>音频播放进度（VOICE-MSG-2）。</summary>
public sealed record AudioPlaybackProgress(string Key, TimeSpan Position, TimeSpan Duration);

/// <summary>
/// 系统音频输出设备描述（VOICE-MSG-3）。DeviceId 为后端相关的稳定字符串
/// （WaveOut 后端 = 设备序号十进制字符串；null/空 统一表示"系统默认"）。
/// </summary>
public sealed record AudioOutputDevice(string DeviceId, string Name);

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

    // ---- 输出设备路由（VOICE-MSG-3）：耳机 ⇄ 扬声器切换 ----

    /// <summary>
    /// 当前生效的输出设备 Id（null = 系统默认）。与 <see cref="SelectOutputDevice"/> 一致，
    /// 切换仅在本属性变化时生效。
    /// </summary>
    string? SelectedOutputDeviceId { get; }

    /// <summary>
    /// 枚举系统音频输出设备。无音频设备/平台不支持（如 CI/Linux）时返回空列表，绝不抛异常。
    /// </summary>
    IReadOnlyList<AudioOutputDevice> GetOutputDevices();

    /// <summary>
    /// 选择输出设备（deviceId = null/空白 = 系统默认）。语义：<b>只影响下一次 Play</b>，
    /// 正在进行的播放不热切换（避免播放中重建渲染导致进度/停止事件流断裂）。
    /// deviceId 非法或对应设备不存在时静默回退系统默认。
    /// </summary>
    void SelectOutputDevice(string? deviceId);
}