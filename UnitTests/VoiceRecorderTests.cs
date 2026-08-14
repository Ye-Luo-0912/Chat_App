using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using Chat_App.Infrastructure.Serialization;
using Core.Interfaces;
using Core.Services.Voice;
using Xunit;
using AttachmentRefDto = ChatApp.Shared.Protocol.Tcp.TcpAttachmentRef;

namespace UnitTests;

public sealed class VoiceRecorderTests
{
    private const int SampleRate = 16_000;
    private const short Channels = 1;

    [Fact]
    public void Stop_ProducesValidPcmWavWithVoiceMetadata()
    {
        using var recorder = new VoiceRecorderService(
            new SineToneSampleSource(SampleRate, Channels, maxDuration: TimeSpan.FromSeconds(5)));

        recorder.Start();
        Thread.Sleep(80);
        var recording = recorder.Stop();

        Assert.NotNull(recording);
        Assert.False(recorder.IsRecording);
        using (recording!)
        {
            // 元数据
            Assert.Equal("pcm", recording.Metadata.Codec);
            Assert.Equal("wav", recording.Metadata.Container);
            Assert.Equal(SampleRate, recording.Metadata.SampleRateHz);
            Assert.Equal(Channels, recording.Metadata.Channels);
            Assert.True(recording.Metadata.DurationMs > 0, "时长应大于 0");
            Assert.True(recording.Metadata.SizeBytes > 44, "WAV 应大于 44 字节头");

            // 头部结构
            var wav = recording.WavStream;
            Assert.True(wav.Length > 44);
            wav.Position = 0;
            var header = new byte[44];
            Assert.Equal(44, wav.Read(header, 0, 44));

            Assert.Equal("RIFF", Encoding.ASCII.GetString(header, 0, 4));
            Assert.Equal("WAVE", Encoding.ASCII.GetString(header, 8, 4));
            Assert.Equal(1, BinaryPrimitives.ReadInt16LittleEndian(header.AsSpan(20))); // PCM
            Assert.Equal(Channels, BinaryPrimitives.ReadInt16LittleEndian(header.AsSpan(22)));
            Assert.Equal(SampleRate, BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(24)));
            var dataLength = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(40));
            Assert.True(dataLength > 0, "data 长度应大于 0");
            Assert.Equal(dataLength, wav.Length - 44);
        }
    }

    [Fact]
    public void Cancel_ReturnsNull_AndClearsState()
    {
        using var recorder = new VoiceRecorderService(
            new SineToneSampleSource(SampleRate, Channels, maxDuration: TimeSpan.FromSeconds(5)));

        recorder.Start();
        Thread.Sleep(40);
        recorder.Cancel();

        Assert.False(recorder.IsRecording);
        // Cancel 后再 Stop 应返回 null（无在录会话）。
        Assert.Null(recorder.Stop());
    }

    [Fact]
    public void Progress_RaisesWithIncreasingElapsed()
    {
        using var recorder = new VoiceRecorderService(
            new SineToneSampleSource(SampleRate, Channels, maxDuration: TimeSpan.FromSeconds(5)));
        TimeSpan? last = null;
        var ticks = 0;
        recorder.Progress += p => { last = p.Elapsed; ticks++; };

        recorder.Start();
        Thread.Sleep(120);
        recorder.Stop();

        Assert.True(ticks > 0, "应至少触发一次进度");
        Assert.True(last > TimeSpan.Zero);
    }

    [Fact]
    public void WavPcmEncoder_WritesDeterministicHeaderForZeroPcm()
    {
        using var pcm = new MemoryStream(new byte[1000]);
        using var wav = new MemoryStream();

        var dataBytes = WavPcmEncoder.WriteWav(wav, pcm, SampleRate, Channels);

        Assert.Equal(1000, dataBytes);
        Assert.Equal(44 + 1000, wav.Length);
        var header = wav.ToArray();
        Assert.Equal("RIFF", Encoding.ASCII.GetString(header, 0, 4));
        Assert.Equal(36 + 1000, BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4)));
        Assert.Equal(1000, BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(40)));
    }

    [Fact]
    public void SineToneSampleSource_IsDeterministicForSameParams()
    {
        static byte[] Capture(int sampleRate, short channels, int frames)
        {
            using var source = new SineToneSampleSource(sampleRate, channels, maxDuration: TimeSpan.FromSeconds(1));
            var buffer = new byte[frames * channels * 2];
            var read = source.Read(buffer);
            return read == buffer.Length ? buffer : buffer[..read];
        }

        var a = Capture(SampleRate, 1, 320); // 320 帧 = 20ms
        var b = Capture(SampleRate, 1, 320);
        Assert.Equal(a, b);
        Assert.NotEqual(0, a.AsSpan().SequenceCompareTo(new byte[a.Length]));
    }

    [Fact]
    public void Start_IsIdempotent_AndStopAfterShortRecordingWorks()
    {
        using var recorder = new VoiceRecorderService(
            new SineToneSampleSource(SampleRate, Channels, maxDuration: TimeSpan.FromSeconds(5)));

        recorder.Start();
        recorder.Start(); // 幂等：不应重置捕获
        Thread.Sleep(30);
        var first = recorder.Stop();
        Assert.NotNull(first);
        first!.Dispose();

        // 可再次 Start（复用实例）。
        recorder.Start();
        Thread.Sleep(30);
        var second = recorder.Stop();
        Assert.NotNull(second);
        second!.Dispose();
    }

    /// <summary>
    /// VOICE-MSG-2 链路桥接：真实录音产物的元数据按 SendVoiceAsync 的映射
    /// 写入 wire AttachmentRefDto，并经 AttachmentJson 往返后语音字段保持一致。
    /// </summary>
    [Fact]
    public void Recording_MetadataMapsToWireVoiceFields_AndRoundTrips()
    {
        using var recorder = new VoiceRecorderService(
            new SineToneSampleSource(SampleRate, Channels, maxDuration: TimeSpan.FromSeconds(5)));
        recorder.Start();
        Thread.Sleep(80);
        using var recording = recorder.Stop();
        Assert.NotNull(recording);

        // 镜像 SendVoiceAsync 的映射：codec=pcm、container=wav。
        var wire = new AttachmentRefDto
        {
            AttachmentId = "voice-abc",
            FileName = "voice.wav",
            ContentType = "audio/wav",
            SizeBytes = recording.Metadata.SizeBytes,
            Status = 1,
            IsVoice = true,
            VoiceCodec = recording.Metadata.Codec,
            VoiceContainer = recording.Metadata.Container,
            VoiceDurationMs = recording.Metadata.DurationMs,
            VoiceSampleRateHz = recording.Metadata.SampleRateHz,
            VoiceChannels = recording.Metadata.Channels
        };

        var json = AttachmentJson.Serialize(new List<AttachmentRefDto> { wire });
        Assert.NotNull(json);
        Assert.Contains("\"isVoice\":true", json);
        Assert.Contains("\"voiceCodec\":\"pcm\"", json);
        Assert.Contains("\"voiceContainer\":\"wav\"", json);

        var roundTrip = AttachmentJson.Deserialize(json);
        var item = Assert.Single(roundTrip!);
        Assert.True(item.IsVoice);
        Assert.Equal(recording.Metadata.Codec, item.VoiceCodec);
        Assert.Equal(recording.Metadata.Container, item.VoiceContainer);
        Assert.Equal(recording.Metadata.DurationMs, item.VoiceDurationMs);
        Assert.Equal(recording.Metadata.SampleRateHz, item.VoiceSampleRateHz);
        Assert.Equal(recording.Metadata.Channels, item.VoiceChannels);
    }
}