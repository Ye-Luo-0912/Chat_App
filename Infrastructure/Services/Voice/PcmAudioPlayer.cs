using System;
using System.Collections.Generic;
using System.Globalization;
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
/// 输出设备路由（VOICE-MSG-3）：<see cref="SelectOutputDevice"/> 只影响下一次 Play ——
/// 正在播放不热切换（重建渲染会打断进度/停止事件流，且收益低）；设备枚举经
/// <see cref="IAudioOutputDeviceEnumerator"/> 抽象隔离，测试不依赖真实音频设备。
/// </summary>
public sealed class PcmAudioPlayer : IAudioPlayer
{
    /// <summary>WaveOut 的"系统默认设备"序号（NAudio 约定 -1 = 默认）。</summary>
    private const int DefaultDeviceNumber = -1;

    private readonly object _gate = new();
    private readonly IAudioOutputDeviceEnumerator _devices;
    private WaveOutEvent? _waveOut;
    private WaveFileReader? _reader;
    private Timer? _progressTimer;
    private string? _currentKey;
    private bool _paused;
    private bool _disposed;
    private int _selectedDeviceNumber = DefaultDeviceNumber;
    private string? _selectedDeviceId;

    /// <summary>deviceEnumerator 传 null 时使用真实 WaveOut 枚举（生产默认路径）。</summary>
    public PcmAudioPlayer(IAudioOutputDeviceEnumerator? deviceEnumerator = null)
    {
        _devices = deviceEnumerator ?? new WaveOutDeviceEnumerator();
    }

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

    public string? SelectedOutputDeviceId
    {
        get
        {
            lock (_gate) return _selectedDeviceId;
        }
    }

    public event Action<AudioPlaybackProgress>? Progress;
    public event Action? Stopped;

    public IReadOnlyList<AudioOutputDevice> GetOutputDevices() => _devices.EnumerateOutputDevices();

    public void SelectOutputDevice(string? deviceId)
    {
        lock (_gate)
        {
            // null/空白 = 系统默认。
            if (string.IsNullOrWhiteSpace(deviceId))
            {
                ResetToDefaultDevice();
                return;
            }
            if (!int.TryParse(deviceId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
                || number < 0)
            {
                // 非法持久化值（损坏/旧版本数据）：优雅回退系统默认，不抛异常。
                ResetToDefaultDevice();
                return;
            }

            var count = _devices.GetDeviceCount();
            if (count is not null && number >= count)
            {
                // 设备越界（已拔出/枚举序漂移）：回退系统默认。
                ResetToDefaultDevice();
                return;
            }

            _selectedDeviceNumber = number;
            _selectedDeviceId = number.ToString(CultureInfo.InvariantCulture);
        }
    }

    public void Play(string key, string wavPath)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentNullException(nameof(key));
        if (string.IsNullOrWhiteSpace(wavPath)) throw new ArgumentNullException(nameof(wavPath));
        if (!File.Exists(wavPath))
            throw new FileNotFoundException("语音文件不存在", wavPath);

        lock (_gate)
        {
            StopInternal();

            // 快照当前选择的设备：此后到 Init 前的切换顺延到下一次 Play（无热切）。
            var deviceNumber = _selectedDeviceNumber;

            var reader = new WaveFileReader(wavPath);
            var waveOut = new WaveOutEvent { DesiredLatency = 200, DeviceNumber = deviceNumber };
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

    /// <summary>回退系统默认设备（调用方须持有 _gate）。</summary>
    private void ResetToDefaultDevice()
    {
        _selectedDeviceNumber = DefaultDeviceNumber;
        _selectedDeviceId = null;
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