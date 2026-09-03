using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Chat_App.Infrastructure.Models;
using Chat_App.Infrastructure.Serialization;
using Chat_App.Presentation.Converters;
using Chat_App.Presentation.ViewModels.Chat;
using Core.Interfaces;
using Core.Services.Voice;
using Xunit;
using AttachmentRefDto = ChatApp.Shared.Protocol.Tcp.TcpAttachmentRef;

namespace UnitTests;

/// <summary>
/// 语音波形峰值包络（VOICE-MSG-2 波形）三段链路测试：
/// 生成（WavPcmEncoder/录音机）→ 上行映射（PendingAttachment/AttachmentRefDto/JSON）→ 渲染（VoiceWaveformConverter）。
/// </summary>
public sealed class VoiceWaveformTests
{
    private const int SampleRate = 16_000;
    private const short Channels = 1;

    // ── 生成：WavPcmEncoder.ComputePeakEnvelope ────────────────────────

    [Fact]
    public void ComputePeakEnvelope_IsDeterministicForSameInput()
    {
        var pcm = BuildSinePcm(sampleCount: 4_800, amplitude: 12_345);

        var a = WavPcmEncoder.ComputePeakEnvelope(pcm);
        var b = WavPcmEncoder.ComputePeakEnvelope(pcm);

        Assert.Equal(a, b);
    }

    [Fact]
    public void ComputePeakEnvelope_ProducesFixedBucketCount()
    {
        // 长输入与短输入（样本数 < 桶数）都恒定输出 48 桶。
        Assert.Equal(48, WavPcmEncoder.ComputePeakEnvelope(BuildSinePcm(48_000, 20_000)).Length);
        Assert.Equal(48, WavPcmEncoder.ComputePeakEnvelope(BuildSinePcm(10, 20_000)).Length);
        Assert.Equal(WavPcmEncoder.WaveformPeakBucketCount, WavPcmEncoder.ComputePeakEnvelope(BuildSinePcm(1_000, 1)).Length);
    }

    [Fact]
    public void ComputePeakEnvelope_SilenceNormalizesToAllZero()
    {
        var peaks = WavPcmEncoder.ComputePeakEnvelope(new byte[9_600]);

        Assert.Equal(48, peaks.Length);
        Assert.All(peaks, p => Assert.Equal(0, p));
    }

    [Fact]
    public void ComputePeakEnvelope_FullScaleNormalizesTo255()
    {
        // 正负满幅样本混排：+32767 与 -32768 都应归一化为 255。
        var pcm = new byte[96 * 2];
        for (var i = 0; i < 96; i++)
        {
            var sample = i % 2 == 0 ? short.MaxValue : short.MinValue;
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), sample);
        }

        var peaks = WavPcmEncoder.ComputePeakEnvelope(pcm);

        Assert.Equal(48, peaks.Length);
        Assert.All(peaks, p => Assert.Equal(255, p));
    }

    [Fact]
    public void ComputePeakEnvelope_AssignsPeakToItsOwnBucket()
    {
        // 48 桶 × 每桶 2 样本：仅第 3 桶注入满幅样本，其余静音。
        var pcm = new byte[96 * 2];
        BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(6 * 2), short.MaxValue);
        BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(7 * 2), short.MaxValue);

        var peaks = WavPcmEncoder.ComputePeakEnvelope(pcm);

        Assert.Equal(48, peaks.Length);
        Assert.Equal(255, peaks[3]);
        Assert.All(peaks.Select((p, i) => (p, i)).Where(x => x.i != 3), x => Assert.Equal(0, x.p));
    }

    [Fact]
    public void ComputePeakEnvelope_TracksBucketWiseMaxAmplitude()
    {
        // 幅度线性递增的音频 → 包络单调非降；且桶内取最大值（段内峰值保留）。
        const int samplesPerBucket = 100;
        var pcm = new byte[48 * samplesPerBucket * 2];
        for (var bucket = 0; bucket < 48; bucket++)
        {
            for (var j = 0; j < samplesPerBucket; j++)
            {
                // 桶内先高后低：最大值出现在桶中段，验证取的是桶内最大而非首/末样本。
                var amplitude = j == samplesPerBucket / 2 ? (bucket + 1) * 680 : (bucket + 1) * 100;
                BinaryPrimitives.WriteInt16LittleEndian(
                    pcm.AsSpan((bucket * samplesPerBucket + j) * 2), (short)Math.Min(amplitude, short.MaxValue));
            }
        }

        var peaks = WavPcmEncoder.ComputePeakEnvelope(pcm);

        for (var i = 1; i < peaks.Length; i++)
            Assert.True(peaks[i] >= peaks[i - 1], $"包络应单调非降：peaks[{i}]={peaks[i]} < peaks[{i - 1}]={peaks[i - 1]}");
        // 末桶峰值 32640 → round(32640×255/32768) = 254（近满幅，非 255）。
        Assert.Equal(254, peaks[^1]);
    }

    [Fact]
    public void ComputePeakEnvelope_EmptyOrTruncatedInput_ReturnsEmpty()
    {
        Assert.Empty(WavPcmEncoder.ComputePeakEnvelope(ReadOnlySpan<byte>.Empty));
        Assert.Empty(WavPcmEncoder.ComputePeakEnvelope(new byte[1])); // 不足一个完整样本
    }

    [Fact]
    public void ComputePeakEnvelope_IgnoresTrailingOddByte()
    {
        // 1000 字节有效样本 + 1 字节尾部截断：按 500 样本计算，结果与截断后一致。
        var pcm = BuildSinePcm(500, 8_000);
        var withTail = new byte[pcm.Length + 1];
        pcm.CopyTo(withTail, 0);

        Assert.Equal(WavPcmEncoder.ComputePeakEnvelope(pcm), WavPcmEncoder.ComputePeakEnvelope(withTail));
    }

    [Fact]
    public void ComputePeakEnvelope_CustomBucketCount()
    {
        Assert.Equal(24, WavPcmEncoder.ComputePeakEnvelope(BuildSinePcm(4_800, 10_000), bucketCount: 24).Length);
        Assert.Throws<ArgumentOutOfRangeException>(() => WavPcmEncoder.ComputePeakEnvelope(BuildSinePcm(100, 1), bucketCount: 0));
    }

    // ── 生成：录音机元数据携带包络 ─────────────────────────────────────

    [Fact]
    public void Recorder_Stop_MetadataCarriesWaveformPeaks()
    {
        using var recorder = new VoiceRecorderService(
            new SineToneSampleSource(SampleRate, Channels, maxDuration: TimeSpan.FromSeconds(5)));
        recorder.Start();
        Thread.Sleep(80);
        using var recording = recorder.Stop();

        Assert.NotNull(recording);
        var peaks = recording!.Metadata.VoiceWaveformPeaks;
        Assert.NotNull(peaks);
        Assert.Equal(WavPcmEncoder.WaveformPeakBucketCount, peaks!.Length);
        // 正弦音源应有实际幅度（非静音包络）。
        Assert.Contains(peaks, p => p > 0);
    }

    // ── 上行：PendingAttachment → AttachmentRefDto 透传 + JSON 往返 ────

    [Fact]
    public void PendingAttachment_MapsWaveformToWireRef_AndRoundTripsJson()
    {
        byte[] peaks = [3, 40, 120, 255, 200, 90, 12];
        var pending = new PendingAttachment
        {
            AttachmentId = "voice-1",
            FileName = "voice.wav",
            ContentType = "audio/wav",
            SizeBytes = 1024,
            IsVoice = true,
            VoiceCodec = "pcm",
            VoiceContainer = "wav",
            VoiceDurationMs = 1500,
            VoiceSampleRateHz = SampleRate,
            VoiceChannels = Channels,
            VoiceWaveformPeaks = peaks
        };

        // 镜像 MessageViewModel.SendMessage 的映射（语音才透传波形）。
        var wire = new AttachmentRefDto
        {
            AttachmentId = pending.AttachmentId,
            FileName = pending.FileName,
            ContentType = pending.ContentType,
            SizeBytes = pending.SizeBytes,
            Status = 1,
            DownloadApiHint = pending.AttachmentId,
            IsVoice = pending.IsVoice,
            VoiceCodec = pending.IsVoice ? pending.VoiceCodec : null,
            VoiceContainer = pending.IsVoice ? pending.VoiceContainer : null,
            VoiceDurationMs = pending.IsVoice ? pending.VoiceDurationMs : null,
            VoiceSampleRateHz = pending.IsVoice ? pending.VoiceSampleRateHz : null,
            VoiceChannels = pending.IsVoice ? pending.VoiceChannels : null,
            VoiceWaveformPeaks = pending.IsVoice ? pending.VoiceWaveformPeaks : null
        };

        Assert.Equal(peaks, wire.VoiceWaveformPeaks);

        // JSON（outbox AttachmentsJson/refs 上行）往返：byte[] 走 STJ base64。
        var json = AttachmentJson.Serialize(new List<AttachmentRefDto> { wire });
        Assert.NotNull(json);
        Assert.Contains("\"voiceWaveformPeaks\":", json);

        var roundTrip = AttachmentJson.Deserialize(json);
        var item = Assert.Single(roundTrip!);
        Assert.True(item.IsVoice);
        Assert.Equal(peaks, item.VoiceWaveformPeaks);
    }

    [Fact]
    public void NonVoiceAttachment_MapsWaveformToNull()
    {
        var pending = new PendingAttachment
        {
            AttachmentId = "file-1",
            ContentType = "application/pdf",
            SizeBytes = 2048,
            VoiceWaveformPeaks = [1, 2, 3]
        };

        var wire = new AttachmentRefDto
        {
            AttachmentId = pending.AttachmentId,
            ContentType = pending.ContentType,
            SizeBytes = pending.SizeBytes,
            Status = 1,
            IsVoice = pending.IsVoice,
            VoiceWaveformPeaks = pending.IsVoice ? pending.VoiceWaveformPeaks : null
        };

        Assert.False(wire.IsVoice);
        Assert.Null(wire.VoiceWaveformPeaks);
    }

    [Fact]
    public void NullOrEmptyPeaks_SerializeOmitsWaveform_AndRoundTripsAsNull()
    {
        var wire = new AttachmentRefDto
        {
            AttachmentId = "voice-2",
            IsVoice = true,
            VoiceCodec = "pcm",
            VoiceWaveformPeaks = null
        };

        var json = AttachmentJson.Serialize(new List<AttachmentRefDto> { wire });
        Assert.DoesNotContain("voiceWaveformPeaks", json); // WhenWritingNull：缺省即缺字段

        var item = Assert.Single(AttachmentJson.Deserialize(json)!);
        Assert.Null(item.VoiceWaveformPeaks); // 接收端：null = 无波形，降级渲染
    }

    // ── 草稿持久化：DraftAttachment 波形 base64 往返 ───────────────────

    [Fact]
    public void DraftAttachment_WaveformRoundTripsThroughDraftState()
    {
        byte[] peaks = [5, 60, 200, 255, 100];
        var state = new DraftState
        {
            Text = string.Empty,
            Attachments =
            [
                new DraftAttachment
                {
                    AttachmentId = "voice-3",
                    IsVoice = true,
                    VoiceCodec = "pcm",
                    VoiceWaveformPeaks = peaks
                }
            ],
            UpdatedAtMs = 42,
            Revision = 1
        };

        var restored = JsonSerializer.Deserialize<DraftState>(JsonSerializer.Serialize(state));

        Assert.NotNull(restored);
        var attachment = Assert.Single(restored!.Attachments!);
        Assert.Equal(peaks, attachment.VoiceWaveformPeaks);
    }

    // ── 渲染：VoiceWaveformConverter ───────────────────────────────────

    [Fact]
    public void BuildBarHeights_NullOrEmptyPeaks_ReturnsEmpty_Degrades()
    {
        Assert.Empty(VoiceWaveformConverter.BuildBarHeights(null));
        Assert.Empty(VoiceWaveformConverter.BuildBarHeights([]));
    }

    [Fact]
    public void BuildBarHeights_ShortPeaks_KeepsAllBars()
    {
        byte[] peaks = [10, 128, 255];

        var bars = VoiceWaveformConverter.BuildBarHeights(peaks);

        Assert.Equal(3, bars.Count);
        // 纯函数：输入数组不被修改。
        Assert.Equal(new byte[] { 10, 128, 255 }, peaks);
    }

    [Fact]
    public void BuildBarHeights_LongPeaks_DownsamplesToRenderedBarCount()
    {
        var peaks = new byte[48];
        for (var i = 0; i < peaks.Length; i++)
            peaks[i] = (byte)(i * 5); // 0..235

        var bars = VoiceWaveformConverter.BuildBarHeights(peaks);

        Assert.Equal(VoiceWaveformConverter.RenderedBarCount, bars.Count);
        Assert.All(bars, h => Assert.InRange(h, VoiceWaveformConverter.MinBarHeight, VoiceWaveformConverter.MaxBarHeight));
    }

    [Fact]
    public void BuildBarHeights_DownsamplingKeepsBlockMax()
    {
        // 48 桶 → 24 柱：每柱覆盖 2 桶，柱高由块内最大值决定。
        var peaks = new byte[48];
        for (var i = 0; i < peaks.Length; i++)
            peaks[i] = i % 2 == 0 ? (byte)10 : (byte)200;

        var bars = VoiceWaveformConverter.BuildBarHeights(peaks);

        Assert.Equal(24, bars.Count);
        // 每块都含一个 200 → 所有柱等高，且高于仅由 10 计算的高度。
        var expected = VoiceWaveformConverter.MinBarHeight
                       + (VoiceWaveformConverter.MaxBarHeight - VoiceWaveformConverter.MinBarHeight) * 200 / 255d;
        Assert.All(bars, h => Assert.Equal(expected, h, precision: 6));
    }

    [Fact]
    public void BuildBarHeights_FullScalePeaks_ReachesMaxBarHeight()
    {
        var peaks = Enumerable.Repeat((byte)255, 24).ToArray();

        var bars = VoiceWaveformConverter.BuildBarHeights(peaks);

        Assert.All(bars, h => Assert.Equal(VoiceWaveformConverter.MaxBarHeight, h, precision: 6));
    }

    [Fact]
    public void BuildBarHeights_SilentPeaks_ClampToMinBarHeight()
    {
        var peaks = new byte[24]; // 全 0

        var bars = VoiceWaveformConverter.BuildBarHeights(peaks);

        Assert.Equal(24, bars.Count);
        Assert.All(bars, h => Assert.Equal(VoiceWaveformConverter.MinBarHeight, h, precision: 6));
    }

    [Fact]
    public void BuildBarHeights_IsDeterministic()
    {
        var peaks = Enumerable.Range(0, 48).Select(i => (byte)(i * 5 + 1)).ToArray();

        Assert.Equal(VoiceWaveformConverter.BuildBarHeights(peaks), VoiceWaveformConverter.BuildBarHeights(peaks));
    }

    [Fact]
    public void Converter_Parameters_SelectCorrectBranches()
    {
        var converter = new VoiceWaveformConverter();
        byte[] peaks = [0, 128, 255];

        Assert.True(Assert.IsType<bool>(converter.Convert(peaks, typeof(bool), "HasPeaks", null!)));
        Assert.False(Assert.IsType<bool>(converter.Convert(null, typeof(bool), "HasPeaks", null!)));
        Assert.False(Assert.IsType<bool>(converter.Convert(Array.Empty<byte>(), typeof(bool), "HasPeaks", null!)));

        Assert.False(Assert.IsType<bool>(converter.Convert(peaks, typeof(bool), "LacksPeaks", null!)));
        Assert.True(Assert.IsType<bool>(converter.Convert(null, typeof(bool), "LacksPeaks", null!)));

        // 默认（Bars）：空输入 → 空集合；非空 → 柱高集合。
        Assert.Empty(Assert.IsType<double[]>(converter.Convert(null, typeof(object), null, null!)));
        var bars = Assert.IsAssignableFrom<IReadOnlyList<double>>(converter.Convert(peaks, typeof(object), null, null!));
        Assert.Equal(3, bars.Count);

        Assert.Throws<NotSupportedException>(() => converter.ConvertBack(null, typeof(object), null, null!));
    }

    // ── 工具 ───────────────────────────────────────────────────────────

    /// <summary>生成确定性正弦 PCM（16-bit LE），供包络测试使用。</summary>
    private static byte[] BuildSinePcm(int sampleCount, short amplitude)
    {
        var pcm = new byte[sampleCount * 2];
        for (var i = 0; i < sampleCount; i++)
        {
            var value = (short)Math.Round(amplitude * Math.Sin(2 * Math.PI * i / 32.0));
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), value);
        }

        return pcm;
    }
}
