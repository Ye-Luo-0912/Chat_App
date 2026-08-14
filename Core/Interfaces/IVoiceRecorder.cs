using System;
using System.IO;

namespace Core.Interfaces;

/// <summary>
/// 录音机的可配置参数（VOICE-MSG-2）。
/// 采样率与声道数在上行前固定，作为 WAV 头与语音元数据来源，中途不可变更。
/// </summary>
public sealed record VoiceRecorderOptions(
    int SampleRateHz = 16_000,
    short Channels = 1);

/// <summary>
/// 语音附件元数据（与 Shared TCP 契约的 5 个语音字段一一对应，外加大小）。
/// 仅当 IsVoice=true 时 Codec/Container/DurationMs/SampleRateHz/Channels 有值。
/// </summary>
public sealed record VoiceMetadata(
    string Codec,
    string Container,
    long DurationMs,
    int SampleRateHz,
    short Channels,
    long SizeBytes);

/// <summary>录音过程中的进度（当前已录时长）。</summary>
public readonly record struct VoiceRecordingProgress(TimeSpan Elapsed);

/// <summary>
/// 一次完整录音的产物：WAV 字节流 + 语音元数据。
/// 调用方负责释放 <see cref="WavStream"/>。
/// </summary>
public sealed class VoiceRecording : IDisposable
{
    public VoiceRecording(Stream wavStream, VoiceMetadata metadata)
    {
        WavStream = wavStream;
        Metadata = metadata;
    }

    /// <summary>完整 PCM WAV 字节流（头部已填充正确的 data 长度）。</summary>
    public Stream WavStream { get; }

    /// <summary>语音元数据（codec=pcm, container=wav）。</summary>
    public VoiceMetadata Metadata { get; }

    public void Dispose() => WavStream.Dispose();
}

/// <summary>
/// 录音机抽象（VOICE-MSG-2）。
/// 采集麦克风原始 PCM 到内存，结束或取消时产出确定性的 WAV 容器。
/// 采集源通过 <see cref="IVoiceRecorder"/> 的平台/测试注入点替换，本接口保证
/// Start/Stop/Cancel 语义稳定，UI 与发送链路不依赖采集实现细节。
/// </summary>
public interface IVoiceRecorder
{
    /// <summary>当前是否正在录音。</summary>
    bool IsRecording { get; }

    /// <summary>本次录音的固定参数（采样率/声道数）。</summary>
    VoiceRecorderOptions Options { get; }

    /// <summary>录音过程中周期性触发（时长递增；UI 用于显示秒数）。</summary>
    event Action<VoiceRecordingProgress>? Progress;

    /// <summary>开始录音。幂等：已在录音则无操作。</summary>
    void Start();

    /// <summary>结束录音并返回 WAV 产物。未开始录音时返回 null。</summary>
    VoiceRecording? Stop();

    /// <summary>放弃录音并丢弃已采集数据。未开始录音时无操作。</summary>
    void Cancel();
}