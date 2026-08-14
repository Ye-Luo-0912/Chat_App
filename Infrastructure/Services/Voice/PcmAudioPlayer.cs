using System;
using System.IO;
using System.Threading;
using Core.Interfaces;
using NAudio.Wave;

namespace Chat_App.Infrastructure.Services.Voice;

/// <summary>
/// 基于 NAudio/WaveOut 的 WAV 播放器实现（VOICE-MSG-2）。
/// 通过 <see cref="WaveFileReader"/> 直接喂给 <see cref="WaveOutEvent"/>；用轻量定时器按
/// 其 Position 上报进度，PlaybackStopped 触发 <see cref="IAudioPlayer.Stopped"/>。
/// 仅支持标准 PCM WAV（本链路统一 codec=pcm、container=wav）。
/// </summary>
public sealed class PcmAudioPlayer : IAudioPlayer
{
    private readonly object _gate = new();
    private WaveOutEvent? _waveOut;
    private WaveFileReader? _reader;
    private Timer? _progressTimer;
    private string? _currentKey;
    private bool _paused;
    private bool _disposed;

    public bool IsPlaying
    {
        get
        {
            lock (_gate) return _waveOut?.PlaybackState == PlaybackState.Playing;
        }
    }

    public string? CurrentKey
    {
        get
        {
            lock (_gate) return _currentKey;
        }
    }

    public event Action<AudioPlaybackProgress>? Progress;
    public event Action? Stopped;

    public void Play(string key, string wavPath)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentNullException(nameof(key));
        if (string.IsNullOrWhiteSpace(wavPath)) throw new ArgumentNullException(nameof(wavPath));
        if (!File.Exists(wavPath))
            throw new FileNotFoundException("语音文件不存在", wavPath);

        lock (_gate)
        {
            StopInternal();

            var reader = new WaveFileReader(wavPath);
            var waveOut = new WaveOutEvent { DesiredLatency = 200 };
            waveOut.PlaybackStopped += OnPlaybackStopped;
            waveOut.Init(reader);

            _reader = reader;
            _waveOut = waveOut;
            _currentKey = key;
            _paused = false;
            waveOut.Play();

            _progressTimer = new Timer(OnProgressTick, null,
                TimeSpan.FromMilliseconds(120), TimeSpan.FromMilliseconds(120));
        }
    }

    public void Pause()
    {
        lock (_gate)
        {
            if (_waveOut is null || _disposed) return;
            _waveOut.Pause();
            _paused = true;
        }
    }

    public void Resume()
    {
        lock (_gate)
        {
            if (_waveOut is null || _disposed) return;
            _waveOut.Play();
            _paused = false;
        }
    }

    public void Stop()
    {
        lock (_gate) StopInternal();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            StopInternal();
        }
    }

    private void StopInternal()
    {
        _progressTimer?.Dispose();
        _progressTimer = null;

        var waveOut = _waveOut;
        var reader = _reader;
        _waveOut = null;
        _reader = null;
        _currentKey = null;
        _paused = false;

        if (waveOut is not null)
        {
            waveOut.PlaybackStopped -= OnPlaybackStopped;
            try { waveOut.Stop(); } catch { /* 忽略 */ }
            waveOut.Dispose();
        }
        reader?.Dispose();
    }

    private void OnProgressTick(object? state)
    {
        WaveFileReader? reader;
        string? key;
        lock (_gate)
        {
            reader = _reader;
            key = _currentKey;
            if (reader is null || key is null || _paused)
                return;
        }

        try
        {
            var position = reader.CurrentTime;
            var duration = reader.TotalTime;
            Progress?.Invoke(new AudioPlaybackProgress(key, position, duration));
        }
        catch
        {
            // 文件已结束/被释放，忽略。
        }
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        // 自然结束或手动停止：清理定时器并广播停止。
        lock (_gate)
        {
            _progressTimer?.Dispose();
            _progressTimer = null;
            _currentKey = null;
            _paused = false;
        }
        Stopped?.Invoke();
    }
}