using System;
using System.Buffers.Binary;
using Chat_App.Infrastructure.Services.Call;
using Core.Interfaces;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using Xunit;

namespace UnitTests;

/// <summary>
/// CALL-E2E-2 媒体面纯辅助（<see cref="CallMediaCodec"/>）单测：采样率映射、
/// 16-bit PCM short/byte 互转、RTP 时间单位换算。
/// </summary>
public class CallMediaCodecTests
{
    [Theory]
    [InlineData(8000, AudioSamplingRatesEnum.Rate8KHz)]
    [InlineData(16000, AudioSamplingRatesEnum.Rate16KHz)]
    [InlineData(24000, AudioSamplingRatesEnum.Rate24kHz)]
    [InlineData(44100, AudioSamplingRatesEnum.Rate44_1kHz)]
    [InlineData(48000, AudioSamplingRatesEnum.Rate48kHz)]
    public void ToSamplingRate_MapsKnownRates(int hz, AudioSamplingRatesEnum expected)
        => Assert.Equal(expected, CallMediaCodec.ToSamplingRate(hz));

    [Theory]
    [InlineData(0)]
    [InlineData(11025)]
    [InlineData(96000)]
    public void ToSamplingRate_Unsupported_Throws(int hz)
        => Assert.Throws<NotSupportedException>(() => CallMediaCodec.ToSamplingRate(hz));

    [Fact]
    public void PcmToBytes_WritesLittleEndianShorts()
    {
        var samples = new short[] { 0x1234, -0x1234, 1, -1, 0 };
        var bytes = CallMediaCodec.PcmToBytes(samples);

        Assert.Equal(samples.Length * 2, bytes.Length);
        for (var i = 0; i < samples.Length; i++)
            Assert.Equal(samples[i], BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(i * 2, 2)));
    }

    [Fact]
    public void BytesToPcm_ReadsLittleEndianShorts()
    {
        var samples = new short[] { 1234, -4321, short.MaxValue, short.MinValue };
        var bytes = CallMediaCodec.PcmToBytes(samples);

        var back = CallMediaCodec.BytesToPcm(bytes);
        Assert.Equal(samples, back);
    }

    [Fact]
    public void BytesToPcm_DropsOddTrailingByte()
    {
        var samples = new short[] { 100, 200 };
        var bytes = CallMediaCodec.PcmToBytes(samples);
        Array.Resize(ref bytes, bytes.Length + 1); // 追加孤字节

        var back = CallMediaCodec.BytesToPcm(bytes);
        Assert.Equal(samples, back);
    }

    [Fact]
    public void PcmRoundTrip_IsStable()
    {
        var samples = new short[100_000];
        var rng = new Random(42);
        for (var i = 0; i < samples.Length; i++)
            samples[i] = (short)rng.Next(short.MinValue, short.MaxValue);

        Assert.Equal(samples, CallMediaCodec.BytesToPcm(CallMediaCodec.PcmToBytes(samples)));
    }

    [Theory]
    [InlineData(20u, 48000, 960u)]   // Opus 20ms @ 48k
    [InlineData(10u, 8000, 80u)]     // 10ms @ 8k
    [InlineData(40u, 48000, 1920u)]  // 40ms @ 48k
    public void ToRtpUnits_ComputesClockUnits(uint ms, int clockRate, uint expected)
        => Assert.Equal(expected, CallMediaCodec.ToRtpUnits(ms, clockRate));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ToRtpUnits_NonPositiveClockRate_Throws(int clockRate)
        => Assert.Throws<ArgumentOutOfRangeException>(() => CallMediaCodec.ToRtpUnits(20, clockRate));
}

/// <summary>
/// CALL-E2E-2 ICE 状态 → 控制面 <see cref="CallMediaState"/> 映射单测。
/// </summary>
public class CallMediaStateMapperTests
{
    [Theory]
    [InlineData(RTCIceConnectionState.@new, CallMediaState.Connecting)]
    [InlineData(RTCIceConnectionState.checking, CallMediaState.Connecting)]
    [InlineData(RTCIceConnectionState.connected, CallMediaState.Connected)]
    [InlineData(RTCIceConnectionState.disconnected, CallMediaState.Reconnecting)]
    [InlineData(RTCIceConnectionState.failed, CallMediaState.Failed)]
    [InlineData(RTCIceConnectionState.closed, CallMediaState.Closed)]
    public void Map_MapsKnownStates(RTCIceConnectionState ice, CallMediaState expected)
        => Assert.Equal(expected, CallMediaStateMapper.Map(ice));
}

/// <summary>
/// CALL-E2E-2 <see cref="WaveOutCallAudioSink"/> 守卫逻辑单测（不触碰真实音频设备）：
/// Open 参数校验、Write 前置 isOpen 守卫、Dispose 幂等。
/// </summary>
public class WaveOutCallAudioSinkTests
{
    [Fact]
    public void Open_NonPositiveSampleRate_Throws()
    {
        using var sink = new WaveOutCallAudioSink();
        Assert.Throws<ArgumentOutOfRangeException>(() => sink.Open(0, 1));
    }

    [Fact]
    public void Open_NonPositiveChannels_Throws()
    {
        using var sink = new WaveOutCallAudioSink();
        Assert.Throws<ArgumentOutOfRangeException>(() => sink.Open(48_000, 0));
    }

    [Fact]
    public void Write_BeforeOpen_IsNoOp()
    {
        using var sink = new WaveOutCallAudioSink();
        // 未打开时写入不应抛异常（媒体面在途包静默丢弃）。
        sink.Write(new byte[] { 1, 2, 3, 4 });
        Assert.False(sink.IsOpen);
    }

    [Fact]
    public void Write_EmptySpan_IsNoOp()
    {
        using var sink = new WaveOutCallAudioSink();
        // 空包在任何状态下都不触碰设备，直接返回。
        sink.Write(ReadOnlySpan<byte>.Empty);
        Assert.False(sink.IsOpen);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var sink = new WaveOutCallAudioSink();
        sink.Dispose();
        // 二次释放不抛。
        sink.Dispose();
    }
}