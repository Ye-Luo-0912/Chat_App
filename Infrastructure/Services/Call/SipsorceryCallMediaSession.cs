using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Core.Interfaces;
using Core.Services.Voice;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace Chat_App.Infrastructure.Services.Call;

/// <summary>
/// 基于 SIPSorcery/WebRTC（SRTP + ICE/STUN/TURN）的 <see cref="ICallMediaSession"/> 真实实现（CALL-E2E-2）。
/// <para>
/// 控制面只交换 SDP offer/answer 与 ICE candidate；本类持有 <see cref="RTCPeerConnection"/>，
/// 负责媒体面：<see cref="OnAudioFrameReceived"/> 把远端解码后的 16-bit PCM 写入
/// <see cref="ICallAudioSink"/> 播放；<see cref="IWaveSampleSource"/>（麦克风）在 Start 后按帧
/// 编码为 Opus 经 <c>SendAudio</c> 上行。ICE 连接状态经 <see cref="CallMediaStateMapper"/> 上报。
/// </para>
/// <para>
/// 注意：SDP offer/answer 经 <see cref="SetRemoteDescription"/> 传入，方向由 SDP 中的
/// <c>a=setup</c> 属性推断（offer 为 actpass，answer 为 active/passive），真机联调阶段据此校验。
/// </para>
/// </summary>
public sealed class SipsorceryCallMediaSession : ICallMediaSession
{
    private readonly RTCPeerConnection _pc;
    private readonly AudioEncoder _encoder;
    private readonly ICallAudioSink _sink;
    private readonly IWaveSampleSource? _microphone;
    private readonly int _sampleRateHz;
    private readonly short _channels;
    private readonly int _frameBytes; // 一帧（20ms）PCM 字节数

    private readonly object _gate = new();
    private AudioFormat? _negotiatedFormat;
    private CancellationTokenSource? _sendCts;
    private Task? _sendTask;
    private bool _started;
    private bool _disposed;

    /// <summary>所属通话 Id。</summary>
    public string CallId { get; }

    public event EventHandler<CallMediaStateChangedEventArgs>? StateChanged;

    /// <summary>本端生成的 ICE candidate（信令面据此转发对端）。</summary>
    public event EventHandler<string>? LocalIceCandidate;

    /// <summary>
    /// 创建媒体会话。传入 <paramref name="microphone"/> 以启用上行采集；缺省为 null 时仅控制面
    /// 交换 SDP（方向为 SendRecv，但不上行发声），便于信令联调。
    /// </summary>
    public SipsorceryCallMediaSession(
        string callId,
        ICallAudioSink sink,
        IWaveSampleSource? microphone = null,
        RTCConfiguration? config = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callId);
        CallId = callId;
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _microphone = microphone;
        _sampleRateHz = microphone?.SampleRateHz ?? 48000;
        _channels = microphone?.Channels ?? (short)1;
        _frameBytes = _sampleRateHz / 50 * _channels * 2; // 20ms 帧

        config ??= new RTCConfiguration
        {
            iceServers = new List<RTCIceServer>
            {
                // 默认 STUN；生产以 appsettings 注入 TURN 凭据。
                new() { urls = "stun:stun.l.google.com:19302" },
            },
        };

        _pc = new RTCPeerConnection(config);
        _encoder = new AudioEncoder(includeLinearFormats: true, includeOpus: true);

        _pc.addTrack(new MediaStreamTrack(
            new AudioFormat(AudioCodecsEnum.OPUS, 111, 48000, 48000, 1, "minptime=10;useinbandfec=1"),
            MediaStreamStatusEnum.SendRecv));

        _pc.OnAudioFormatsNegotiated += OnAudioFormatsNegotiated;
        _pc.OnAudioFrameReceived += OnAudioFrameReceived;
        _pc.oniceconnectionstatechange += OnIceConnectionStateChange;
        _pc.onicecandidate += OnIceCandidate;
    }

    public string CreateOffer()
    {
        EnsureUsable();
        return _pc.createOffer(new RTCOfferOptions()).sdp;
    }

    public string CreateAnswer()
    {
        EnsureUsable();
        return _pc.createAnswer(new RTCAnswerOptions()).sdp;
    }

    public void SetRemoteDescription(string sdp)
    {
        EnsureUsable();
        ArgumentException.ThrowIfNullOrWhiteSpace(sdp);

        var type = sdp.Contains("a=setup:actpass", StringComparison.Ordinal)
            ? RTCSdpType.offer
            : RTCSdpType.answer;
        var result = _pc.setRemoteDescription(new RTCSessionDescriptionInit { type = type, sdp = sdp });
        if (result != SetDescriptionResultEnum.OK)
            throw new InvalidOperationException($"应用对端 SDP 失败：{result}。");
    }

    public void ApplyIceCandidate(string candidate)
    {
        EnsureUsable();
        ArgumentException.ThrowIfNullOrWhiteSpace(candidate);
        _pc.addIceCandidate(new RTCIceCandidateInit { candidate = candidate });
    }

    public void Start()
    {
        lock (_gate)
        {
            EnsureUsable();
            if (_started) return;
            _started = true;

            if (_microphone is not null)
            {
                _sendCts = new CancellationTokenSource();
                _sendTask = Task.Run(() => SendLoopAsync(_sendCts.Token));
            }
        }
    }

    public void Stop()
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            cts = _sendCts;
            _sendCts = null;
            _started = false;
        }
        cts?.Cancel();
        // 停止上行采集源，避免漏关麦克风。
        _microphone?.Stop();
    }

    public string RestartIce()
    {
        EnsureUsable();
        _pc.restartIce();
        return _pc.createOffer(new RTCOfferOptions()).sdp;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        Stop();
        _encoder.Dispose();
        _pc.Dispose();
        _sink.Dispose();
    }

    private void EnsureUsable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private void OnIceConnectionStateChange(RTCIceConnectionState state)
    {
        var mapped = CallMediaStateMapper.Map(state);
        StateChanged?.Invoke(this, new CallMediaStateChangedEventArgs(mapped));
    }

    private void OnIceCandidate(RTCIceCandidate candidate)
    {
        if (!string.IsNullOrWhiteSpace(candidate.candidate))
            LocalIceCandidate?.Invoke(this, candidate.candidate);
    }

    private void OnAudioFrameReceived(EncodedAudioFrame frame)
    {
        // 远端音频已由 SIPSorcery 解码为 16-bit PCM 小端，直接喂给播放 sink。
        if (frame.EncodedAudio.Length > 0)
            _sink.Write(frame.EncodedAudio);
    }

    private void OnAudioFormatsNegotiated(List<AudioFormat> formats)
    {
        // 记录协商出的上行编码格式（通常为 Opus/48k），供编码时选用。
        lock (_gate)
        {
            _negotiatedFormat = formats.FirstOrDefault(f =>
                f.Codec is AudioCodecsEnum.OPUS or AudioCodecsEnum.L16 or AudioCodecsEnum.PCMU or AudioCodecsEnum.PCMA);
        }
    }

    private async Task SendLoopAsync(CancellationToken ct)
    {
        var pcm = new short[_frameBytes / 2];
        var pcmBytes = new byte[_frameBytes];
        try
        {
            _microphone!.Start();
            while (!ct.IsCancellationRequested)
            {
                AudioFormat? negotiated;
                lock (_gate) negotiated = _negotiatedFormat;
                if (negotiated is null)
                {
                    // 尚未协商出上行格式：空转等待，避免空传。
                    await Task.Delay(20, ct).ConfigureAwait(false);
                    continue;
                }
                AudioFormat format = negotiated.Value;

                var read = _microphone.Read(pcmBytes);
                if (read <= 0) break; // 数据源结束。

                if (read % 2 != 0) read--; // 对齐样本。
                var sampleCount = read / 2;
                Buffer.BlockCopy(pcmBytes, 0, pcm, 0, read);

                var payload = _encoder.EncodeAudio(pcm.AsSpan(0, sampleCount).ToArray(), format);
                if (payload.Length > 0)
                {
                    var units = CallMediaCodec.ToRtpUnits(20, format.ClockRate);
                    _pc.SendAudio(units, payload);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止。
        }
        catch
        {
            // 媒体面上行异常不向上抛（断线由 ICE 状态反馈），保持会话存活。
        }
        finally
        {
            _microphone!.Stop();
        }
    }
}