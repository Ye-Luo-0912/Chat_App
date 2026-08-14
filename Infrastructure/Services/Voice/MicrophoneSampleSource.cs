using System;
using System.Collections.Concurrent;
using System.Threading;
using Core.Services.Voice;
using NAudio.Wave;

namespace Chat_App.Infrastructure.Services.Voice;

/// <summary>
/// 真实麦克风采集源（VOICE-MSG-2，Windows/NAudio WinMM）。
/// 将 NAudio 的推式（DataAvailable 回调）采集桥接为 <see cref="IWaveSampleSource"/>
/// 所需的拉式（Read 阻塞）模型：回调把 16-bit PCM 追加到有界队列，Read 从中取块，
/// 队列空时阻塞等待新的采样或停止信号。
/// 仅支持 Windows；在非 Windows 平台构造时抛出 <see cref="PlatformNotSupportedException"/>，
/// 由 DI 层回退到 <see cref="SineToneSampleSource"/>。
/// </summary>
public sealed class MicrophoneSampleSource : IWaveSampleSource
{
    /// <summary>采集缓冲时长（毫秒）：NAudio 每块回调的数据量。</summary>
    private const int BufferMilliseconds = 100;

    /// <summary>有界块队列上限：约 100ms×8 = 0.8s 缓冲，越界丢弃最旧块，避免阻塞音频线程。</summary>
    private const int MaxQueuedChunks = 8;

    private readonly ConcurrentQueue<byte[]> _queue = new();
    private readonly ManualResetEventSlim _dataAvailable = new(false);
    private readonly int _maxQueuedChunks;
    private WaveInEvent? _waveIn;
    private volatile bool _recording;
    private volatile bool _stopping;
    private volatile bool _ended;

    public MicrophoneSampleSource(int sampleRateHz = 16_000, short channels = 1)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "MicrophoneSampleSource 依赖 NAudio/WinMM，仅支持 Windows。");
        if (sampleRateHz <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRateHz));
        if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));

        SampleRateHz = sampleRateHz;
        Channels = channels;
        _maxQueuedChunks = MaxQueuedChunks;
    }

    public int SampleRateHz { get; }
    public short Channels { get; }

    public void Start()
    {
        if (_recording)
            return;

        DrainQueue();
        _stopping = false;
        _ended = false;

        _waveIn = new WaveInEvent
        {
            DeviceNumber = 0,
            WaveFormat = new WaveFormat(SampleRateHz, 16, Channels),
            BufferMilliseconds = BufferMilliseconds
        };
        _waveIn.DataAvailable += OnDataAvailable;
        _waveIn.RecordingStopped += OnRecordingStopped;
        _waveIn.StartRecording();
        _recording = true;
    }

    public int Read(Span<byte> pcm16)
    {
        while (true)
        {
            if (_queue.TryDequeue(out var chunk))
            {
                var n = Math.Min(chunk.Length, pcm16.Length);
                chunk.AsSpan(0, n).CopyTo(pcm16);
                // 块远小于 Read 缓冲（100ms≈3200B vs 64KB），不会出现剩余。
                if (_queue.IsEmpty)
                    _dataAvailable.Reset();
                return n;
            }

            if (_ended || _stopping)
                return 0;

            _dataAvailable.Wait(200);
        }
    }

    public void Stop()
    {
        _stopping = true;
        var waveIn = _waveIn;
        _waveIn = null;
        if (waveIn is not null)
        {
            try { waveIn.StopRecording(); } catch { /* 忽略停止异常 */ }
            try { waveIn.Dispose(); } catch { /* 忽略释放异常 */ }
        }
        _ended = true;
        _recording = false;
        _dataAvailable.Set();
    }

    public void Dispose() => Stop();

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0)
            return;

        var chunk = GC.AllocateUninitializedArray<byte>(e.BytesRecorded);
        Buffer.BlockCopy(e.Buffer, 0, chunk, 0, e.BytesRecorded);

        // 有界队列：满则丢弃最旧块，保证实时性且不阻塞采集线程。
        if (_queue.Count >= _maxQueuedChunks)
        {
            while (_queue.Count >= _maxQueuedChunks && _queue.TryDequeue(out _)) { }
        }
        _queue.Enqueue(chunk);
        _dataAvailable.Set();
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        _recording = false;
        _ended = true;
        _dataAvailable.Set();
    }

    private void DrainQueue()
    {
        while (_queue.TryDequeue(out _)) { }
        _dataAvailable.Reset();
    }
}