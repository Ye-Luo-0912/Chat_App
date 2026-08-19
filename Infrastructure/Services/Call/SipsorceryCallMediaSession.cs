using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    private volatile CallMediaState _state = CallMediaState.Idle;

    /// <summary>所属通话 Id。</summary>
    public string CallId { get; }

    /// <summary>当前媒体面状态（由 ICE 连接状态映射，供上层/测试断言）。</summary>
    public CallMediaState State => _state;

    /// <summary>是否已建立媒体连接（ICE connected）。</summary>
    public bool IsConnected => _state == CallMediaState.Connected;

    /// <summary>诊断计数器：上行发送次数 / 发送异常次数 / 协商事件次数 / 收到音频帧数。</summary>
    public long SendCalls { get; private set; }
    public long SendFailures { get; private set; }
    public long NegotiateCalls { get; private set; }
    public long ReceiveCalls { get; private set; }

    /// <summary>诊断观测（跨端联调用）：RTP 包级接收计数与最近 RTP 头，用于区分「包未到达
    /// （源地址过滤/SRTP 丢弃）」与「到达但解码失败」；<see cref="LastDecodedRms"/> 为最近一帧
    /// 解码后 PCM 的归一化 RMS（0~1），区分「到达但静音/解码异常」与「真实音频」。</summary>
    public long RtpPacketsReceived { get; private set; }
    public int LastRtpPayloadType { get; private set; }
    public uint LastRtpSsrc { get; private set; }
    public double LastDecodedRms { get; private set; }

    /// <summary>当前协商出的本端上行编码格式（null 表示尚未协商）。跨端联调用：
    /// preferPcmu 模式应协商为 <see cref="AudioCodecsEnum.PCMU"/>/<see cref="AudioCodecsEnum.PCMA"/>，
    /// 据此断言 G.711 路径确实避开 Concentus opus 编码器。</summary>
    public AudioCodecsEnum? NegotiatedCodec
    {
        get { lock (_gate) return _negotiatedFormat?.Codec; }
    }

    public event EventHandler<CallMediaStateChangedEventArgs>? StateChanged;

    /// <summary>
    /// 对等连接（ICE+DTLS 总体）状态变化，仅用于可观测性：<see cref="RTCPeerConnectionState.connected"/>
    /// 表示 DTLS-SRTP 握手已完成、媒体可双向加解密（仅 ICE connected 不代表 SRTP 可用）。
    /// </summary>
    public event EventHandler<RTCPeerConnectionState>? ConnectionStateChanged;

    /// <summary>本端生成的 ICE candidate（信令面据此转发对端）。</summary>
    public event EventHandler<string>? LocalIceCandidate;

    /// <summary>
    /// 创建媒体会话。传入 <paramref name="microphone"/> 以启用上行采集；缺省为 null 时仅控制面
    /// 交换 SDP（方向为 SendRecv，但不上行发声），便于信令联调。
    /// <para>
    /// <paramref name="preferPcmu"/> 为 true 时本端音频 track 仅声明 PCMU/PCMA（G.711 μ-law/A-law，
    /// 8kHz/单声道），用于与真实浏览器互通时让浏览器可听本端出站音频——规避 SIPSorcery 内置
    /// Concentus opus 编码器输出与 Chrome 解码器不兼容的互操作缺口（见 NEXT-STAGE.md）。此模式下
    /// <paramref name="microphone"/> 须提供 8kHz/单声道样本（如 <c>SineToneSampleSource(8000, 1)</c>
    /// 或 8k 采集的麦克风），以便与 G.711 的 RTP 时钟一致。默认 false 保持 Opus/48k，客户端↔客户端
    /// （双端 SIPSorcery，Concentus 自洽链路）不受影响。
    /// </para>
    /// </summary>
    public SipsorceryCallMediaSession(
        string callId,
        ICallAudioSink sink,
        IWaveSampleSource? microphone = null,
        RTCConfiguration? config = null,
        bool preferPcmu = false)
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

        if (preferPcmu)
        {
            // G.711 μ-law/A-law，8kHz/单声道：Chrome 原生解码，用于跨端互操作用户可听。
            // 协商后 OnAudioFormatsNegotiated 上报本地声明格式，首个即 PCMU，编码走 MuLawEncoder。
            _pc.addTrack(new MediaStreamTrack(new List<AudioFormat>
            {
                new AudioFormat(SDPWellKnownMediaFormatsEnum.PCMU),
                new AudioFormat(SDPWellKnownMediaFormatsEnum.PCMA),
            }, MediaStreamStatusEnum.SendRecv));
        }
        else
        {
            // 默认 Opus。声道数声明为 2：浏览器（Chrome/Edge）对 WebRTC 音频一律发出 opus/48000/2 的 rtpmap，
            // SIPSorcery 的 AreMatch 按 rtpmap 字符串全等匹配，声明 1 声道会与浏览器不兼容（AudioIncompatible）。
            // 编码/解码侧不受影响：AudioEncoder 始终以 mono（OPUS_STREAM_CHANNELS=1）编码，解码对双声道流自动降混。
            _pc.addTrack(new MediaStreamTrack(
                new AudioFormat(AudioCodecsEnum.OPUS, 111, 48000, 48000, 2, "minptime=10;useinbandfec=1"),
                MediaStreamStatusEnum.SendRecv));
        }

        _pc.OnAudioFormatsNegotiated += OnAudioFormatsNegotiated;
        _pc.OnAudioFrameReceived += OnAudioFrameReceived;
        _pc.oniceconnectionstatechange += OnIceConnectionStateChange;
        _pc.onconnectionstatechange += s => ConnectionStateChanged?.Invoke(this, s);
        _pc.onicecandidate += OnIceCandidate;
    }

    public string CreateOffer()
    {
        EnsureUsable();
        // wire 信令面仅透传 SDP，不转发 ICE candidate：必须等 gathering 完成把候选内嵌进 SDP。
        return _pc.createOffer(new RTCOfferOptions { X_WaitForIceGatheringToComplete = true }).sdp;
    }

    public string CreateAnswer()
    {
        EnsureUsable();
        return _pc.createAnswer(new RTCAnswerOptions { X_WaitForIceGatheringToComplete = true }).sdp;
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
        return _pc.createOffer(new RTCOfferOptions { X_WaitForIceGatheringToComplete = true }).sdp;
    }

    /// <summary>
    /// 仅重启本端 ICE 会话（旋转 ufrag/pwd 并重新采集 candidate），不生成新 offer。
    /// 用于 ICE restart 协商中 answer 一侧：接收对端 restart offer 后先旋转本端凭据，
    /// 使 <see cref="CreateAnswer"/> 产出的 answer 携带新凭据，否则凭据不匹配无法重连。
    /// </summary>
    public void RestartLocalIce()
    {
        EnsureUsable();
        _pc.restartIce();
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
        _state = mapped;
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
        {
            ReceiveCalls++;
            _sink.Write(frame.EncodedAudio);
        }
    }

    private void OnAudioFormatsNegotiated(List<AudioFormat> formats)
    {
        NegotiateCalls++;
        // 记录协商出的上行编码格式（通常为 Opus/48k），供编码时选用。
        lock (_gate)
        {
            _negotiatedFormat = formats.FirstOrDefault(f =>
                f.Codec is AudioCodecsEnum.OPUS or AudioCodecsEnum.L16 or AudioCodecsEnum.PCMU or AudioCodecsEnum.PCMA);
        }
        Console.WriteLine($"[media {CallId}] OnAudioFormatsNegotiated #{NegotiateCalls}: [{string.Join(",", formats.Select(f => f.Codec))}] chosen={_negotiatedFormat?.Codec}");
    }

    private async Task SendLoopAsync(CancellationToken ct)
    {
        var pcm = new short[_frameBytes / 2];
        var pcmBytes = new byte[_frameBytes];
        try
        {
            _microphone!.Start();
            // 实时节流基准：按「累计已产出采样 vs 墙上时钟」对齐真实时间节奏。
            var paceSw = Stopwatch.StartNew();
            long producedSamples = 0;
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
                    SendCalls++;
                    try
                    {
                        var units = CallMediaCodec.ToRtpUnits(20, format.ClockRate);
                        _pc.SendAudio(units, payload);
                    }
                    catch
                    {
                        SendFailures++;
                        // ICE/DTLS 尚未完全就绪时 SendAudio 可能瞬时抛错：跳过本帧继续重试，
                        // 避免上行循环被一次瞬时失败静默终止（否则该方向音频不再发送）。
                        await Task.Delay(10, ct);
                    }
                }

                // 实时节流：无节流源（如测试用 SineTone）若不节流会在数秒内突发耗尽全部
                // 音频，使媒体流验证窗口捕捉到「已耗尽方向」而误判断流；硬件麦克风源本身
                // 实时产出，expected≈elapsed，几乎不产生额外等待。
                producedSamples += sampleCount;
                var expectedMs = producedSamples / (double)_sampleRateHz * 1000;
                var elapsedMs = paceSw.Elapsed.TotalMilliseconds;
                if (expectedMs > elapsedMs)
                    await Task.Delay(TimeSpan.FromMilliseconds(expectedMs - elapsedMs), ct).ConfigureAwait(false);
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