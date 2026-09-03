using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Core.Interfaces;

namespace Core.Services.Voice;

/// <summary>
/// <see cref="IVoiceRecorder"/> 的默认实现：从 <see cref="IWaveSampleSource"/> 采集
/// 16-bit PCM 到内存，经 <see cref="WavPcmEncoder"/> 封装为确定性 WAV。
/// Start/Stop/Cancel 线程安全；Stop 产出含正确 data 长度的 WAV 与语音元数据。
/// codec=pcm、container=wav。
/// </summary>
public sealed class VoiceRecorderService : IVoiceRecorder, IDisposable
{
    /// <summary>单次录音最长时长（避免持续麦克风流导致内存无界增长）。</summary>
    private static readonly TimeSpan DefaultMaxDuration = TimeSpan.FromSeconds(60);

    private readonly object _gate = new();
    private readonly IWaveSampleSource _source;
    private readonly TimeSpan _maxDuration;
    private int _state; // 0=Idle, 1=Recording, 2=Stopped
    private MemoryStream? _captureStream;
    private long _dataBytes;
    private Task? _captureTask;
    private Stopwatch? _stopwatch;
    private volatile bool _finishRequested;
    private CancellationTokenSource _cts = new();

    public VoiceRecorderService(
        IWaveSampleSource source,
        VoiceRecorderOptions? options = null,
        TimeSpan? maxDuration = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        Options = options ?? new VoiceRecorderOptions(source.SampleRateHz, source.Channels);
        _maxDuration = maxDuration ?? DefaultMaxDuration;
    }

    public bool IsRecording => Volatile.Read(ref _state) == 1;

    public VoiceRecorderOptions Options { get; }

    public event Action<VoiceRecordingProgress>? Progress;

    /// <summary>
    /// 达到 <see cref="_maxDuration"/> 自动收尾时触发，携带已封装的合法 WAV 产物。
    /// 调用方接管产物的释放（<see cref="VoiceRecording.Dispose"/>）。
    /// 触发后录音状态已复位（<see cref="IsRecording"/> 为 false）。
    /// </summary>
    public event Action<VoiceRecording>? AutoCompleted;

    public void Start()
    {
        lock (_gate)
        {
            if (Volatile.Read(ref _state) == 1)
                return;

            _captureStream = new MemoryStream();
            // 写入 WAV 头占位（data 长度 0，收尾时回填）；capture 循环其后追加 PCM。
            _captureStream.Write(WavPcmEncoder.CreateHeader(Options.SampleRateHz, Options.Channels, 0));
            _dataBytes = 0;
            _finishRequested = false;
            _cts = new CancellationTokenSource();
            _stopwatch = Stopwatch.StartNew();
            _source.Start();

            Volatile.Write(ref _state, 1);
            _captureTask = Task.Run(CaptureLoopAsync);
        }
    }

    public VoiceRecording? Stop()
    {
        var task = RequestFinish();
        if (task is null)
            return null;

        task.Wait(TimeSpan.FromSeconds(3));
        _source.Stop();

        lock (_gate)
        {
            Volatile.Write(ref _state, 0);
            var stream = _captureStream;
            var dataBytes = _dataBytes;
            var elapsedMs = _stopwatch?.ElapsedMilliseconds ?? 0;
            _captureStream = null;
            _stopwatch = null;
            _cts.Dispose();
            _cts = new CancellationTokenSource();

            if (stream is null || dataBytes <= 0)
            {
                stream?.Dispose();
                return null;
            }

            // 回填 WAV 头部长度并seek到末尾。
            FinalizeWav(stream, dataBytes);
            var metadata = new VoiceMetadata(
                Codec: "pcm",
                Container: "wav",
                DurationMs: Math.Max(1, elapsedMs),
                SampleRateHz: Options.SampleRateHz,
                Channels: Options.Channels,
                SizeBytes: stream.Length,
                VoiceWaveformPeaks: ComputeWaveformPeaks(stream, dataBytes));
            return new VoiceRecording(stream, metadata);
        }
    }

    public void Cancel()
    {
        var task = RequestFinish();
        if (task is null)
            return;

        task.Wait(TimeSpan.FromSeconds(3));
        _source.Stop();

        lock (_gate)
        {
            Volatile.Write(ref _state, 0);
            _captureStream?.Dispose();
            _captureStream = null;
            _dataBytes = 0;
            _stopwatch = null;
            _cts.Dispose();
            _cts = new CancellationTokenSource();
        }
    }

    private Task? RequestFinish()
    {
        lock (_gate)
        {
            if (Volatile.Read(ref _state) != 1)
                return null;
            _finishRequested = true;
            return _captureTask;
        }
    }

    private void CaptureLoopAsync()
    {
        var buffer = new byte[64 * 1024];
        var stopwatch = _stopwatch;
        var hitMaxDuration = false;
        try
        {
            while (!_finishRequested)
            {
                if (stopwatch is not null && stopwatch.Elapsed >= _maxDuration)
                {
                    hitMaxDuration = true;
                    break;
                }

                var read = _source.Read(buffer);
                if (read <= 0)
                    break;

                var stream = _captureStream;
                if (stream is null)
                    break;
                stream.Write(buffer, 0, read);
                Interlocked.Add(ref _dataBytes, read);

                // 每读一块触发一次进度（时长）。
                if (stopwatch is not null)
                    Progress?.Invoke(new VoiceRecordingProgress(stopwatch.Elapsed));
            }
        }
        finally
        {
            // 释放被阻塞的采集源（若其 Read 依赖 Stop 解除阻塞）。
            try { _source.Stop(); } catch { /* 忽略 */ }
        }

        // 达到最长时长：自动收尾（而非用户 Stop/Cancel），复位状态并产出合法 WAV。
        if (hitMaxDuration)
            AutoFinalize(stopwatch);
    }

    /// <summary>
    /// 达到最长时长后的自动收尾：复位录音状态、封装 WAV、触发 <see cref="AutoCompleted"/>。
    /// RequestFinish 已由 <see cref="Stop"/> 触发的场景不进入此路径（那时 state 已复位）。
    /// </summary>
    private void AutoFinalize(Stopwatch? stopwatch)
    {
        lock (_gate)
        {
            if (Volatile.Read(ref _state) != 1)
                return;

            Volatile.Write(ref _state, 0);
            var stream = _captureStream;
            var dataBytes = _dataBytes;
            var elapsedMs = stopwatch?.ElapsedMilliseconds ?? 0;
            _captureStream = null;
            _stopwatch = null;
            _cts.Dispose();
            _cts = new CancellationTokenSource();

            if (stream is null || dataBytes <= 0)
            {
                stream?.Dispose();
                return;
            }

            FinalizeWav(stream, dataBytes);
            var metadata = new VoiceMetadata(
                Codec: "pcm",
                Container: "wav",
                DurationMs: Math.Max(1, elapsedMs),
                SampleRateHz: Options.SampleRateHz,
                Channels: Options.Channels,
                SizeBytes: stream.Length,
                VoiceWaveformPeaks: ComputeWaveformPeaks(stream, dataBytes));
            var recording = new VoiceRecording(stream, metadata);
            AutoCompleted?.Invoke(recording);
        }
    }

    private static void FinalizeWav(MemoryStream stream, long dataBytes)
    {
        // 回填 RIFF 大小与 data 长度（WavPcmEncoder 头布局）。
        Span<byte> chunk = stackalloc byte[4];
        stream.Position = 4;
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(chunk, (int)(36 + dataBytes));
        stream.Write(chunk);
        stream.Position = 40;
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(chunk, (int)dataBytes);
        stream.Write(chunk);
        stream.Position = stream.Length;
    }

    /// <summary>
    /// 从已收尾的 WAV 内存流中提取 data 段并计算峰值包络（无有效 PCM 时返回 null，降级为无波形）。
    /// 使用 <see cref="MemoryStream.GetBuffer"/> 避免整段拷贝；桶数固定 48，包络 48 字节。
    /// </summary>
    private static byte[]? ComputeWaveformPeaks(MemoryStream wavStream, long dataBytes)
    {
        if (dataBytes < 2)
            return null;
        var buffer = wavStream.GetBuffer();
        var dataStart = WavPcmEncoder.HeaderLength;
        if (buffer.Length < dataStart + dataBytes)
            return null;
        return WavPcmEncoder.ComputePeakEnvelope(buffer.AsSpan(dataStart, (int)dataBytes));
    }

    public void Dispose()
    {
        Cancel();
        _cts.Dispose();
        _source.Dispose();
    }
}