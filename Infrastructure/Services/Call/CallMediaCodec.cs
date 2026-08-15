using System;
using System.Buffers.Binary;
using SIPSorceryMedia.Abstractions;

namespace Chat_App.Infrastructure.Services.Call;

/// <summary>
/// 通话媒体面的纯编译辅助（CALL-E2E-2）：采样率映射、16-bit PCM short/byte 互转、
/// RTP 时间单位换算。均为无副作用静态函数，便于单测隔离验证。
/// </summary>
public static class CallMediaCodec
{
    /// <summary>
    /// 将设备采样率（Hz）映射为 SIPSorcery 的 <see cref="AudioSamplingRatesEnum"/>。
    /// 仅支持 WebRTC/Opus 常用采样率之一；其余抛 <see cref="NotSupportedException"/>。
    /// </summary>
    public static AudioSamplingRatesEnum ToSamplingRate(int sampleRateHz) => sampleRateHz switch
    {
        8000 => AudioSamplingRatesEnum.Rate8KHz,
        16000 => AudioSamplingRatesEnum.Rate16KHz,
        24000 => AudioSamplingRatesEnum.Rate24kHz,
        44100 => AudioSamplingRatesEnum.Rate44_1kHz,
        48000 => AudioSamplingRatesEnum.Rate48kHz,
        _ => throw new NotSupportedException($"不支持的音频采样率：{sampleRateHz}Hz。"),
    };

    /// <summary>16-bit PCM short[]（小端）转为 byte[]。</summary>
    public static byte[] PcmToBytes(short[] samples)
    {
        var bytes = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++)
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2, 2), samples[i]);
        return bytes;
    }

    /// <summary>16-bit PCM（小端）byte 数据转为 short[]。长度不足的尾部字节被丢弃。</summary>
    public static short[] BytesToPcm(ReadOnlySpan<byte> bytes)
    {
        var count = bytes.Length / 2;
        var samples = new short[count];
        var src = bytes.Slice(0, count * 2);
        for (var i = 0; i < count; i++)
            samples[i] = BinaryPrimitives.ReadInt16LittleEndian(src.Slice(i * 2, 2));
        return samples;
    }

    /// <summary>
    /// 将毫秒时长换算为 RTP 时间单位（用于 <c>RTCPeerConnection.SendAudio</c>
    /// 的 durationRtpUnits 参数）：<paramref name="durationMs"/> × 编码器时钟率 / 1000。
    /// </summary>
    public static uint ToRtpUnits(uint durationMs, int rtpClockRate)
    {
        if (rtpClockRate <= 0) throw new ArgumentOutOfRangeException(nameof(rtpClockRate));
        return (uint)(durationMs * (rtpClockRate / 1000.0));
    }
}