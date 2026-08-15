using System;
using System.Threading;
using Core.Interfaces;
using NAudio.Wave;

namespace Chat_App.Infrastructure.Services.Call;

/// <summary>
/// 基于 NAudio/WaveOut 的实时通话音频播放 sink（CALL-E2E-2）。
/// <para>
/// 与 <see cref="PcmAudioPlayer"/>（播放整段本地 WAV）不同，本类面向连续 PCM 拉流：
/// <see cref="Open"/> 建立波形设备与环形缓冲，<see cref="Write"/> 由媒体面（RTP/SRTP 解码）
/// 线程持续写入 16-bit PCM 小端样本，WaveOut 异步消费边收边播，<see cref="Close"/> 关闭设备。
/// 所有对外方法均线程安全（内部以锁串行化，避免解码线程与 UI/媒体线程竞争）。
/// </para>
/// </summary>
public sealed class WaveOutCallAudioSink : ICallAudioSink
{
    private readonly object _gate = new();
    private WaveFormat? _format;
    private BufferedWaveProvider? _buffer;
    private WaveOutEvent? _waveOut;
    private bool _disposed;

    public bool IsOpen
    {
        get
        {
            lock (_gate) return _buffer is not null && !_disposed;
        }
    }

    public void Open(int sampleRateHz, short channels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRateHz);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_buffer is not null)
                throw new InvalidOperationException("输出设备已打开，需先 Close 再重新 Open。");

            var format = new WaveFormat(sampleRateHz, 16, channels);
            var buffer = new BufferedWaveProvider(format)
            {
                // 通话缓冲：约 300ms，兼顾抗抖与端到端时延。
                BufferDuration = TimeSpan.FromMilliseconds(300),
                DiscardOnBufferOverflow = true,
            };
            var waveOut = new WaveOutEvent { DesiredLatency = 100 };
            waveOut.Init(buffer);

            _format = format;
            _buffer = buffer;
            _waveOut = waveOut;
            waveOut.Play();
        }
    }

    public void Write(ReadOnlySpan<byte> pcm16)
    {
        if (pcm16.IsEmpty) return;

        lock (_gate)
        {
            if (_buffer is null || _disposed)
                return; // 未打开或已关闭：静默丢弃（媒体面可能仍有在途包）。

            if (pcm16.Length % 2 != 0)
                pcm16 = pcm16[..^1]; // 丢弃奇数尾部字节，保证样本对齐。

            var block = pcm16.ToArray();
            _buffer.AddSamples(block, 0, block.Length);
        }
    }

    public void Close()
    {
        lock (_gate)
        {
            if (_disposed) return;

            var waveOut = _waveOut;
            var buffer = _buffer;
            _waveOut = null;
            _buffer = null;
            _format = null;

            if (waveOut is not null)
            {
                try { waveOut.Stop(); } catch { /* 忽略 */ }
                waveOut.Dispose();
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        Close();
    }
}