using System.Collections.Generic;
using Chat_App.Infrastructure.Serialization;
using Xunit;
using AttachmentRefDto = ChatApp.Shared.Protocol.Tcp.TcpAttachmentRef;

namespace Chat_App.UnitTests;

/// <summary>
/// VOICE-MSG-2 Client consumer fixture：验证 Client 通过
/// <see cref="AttachmentJson"/>（源生成 ChatJsonContext）对语音附件元数据的
/// 序列化/反序列化往返，以及旧客户端（无语音字段）载荷的反序列化兼容。
/// </summary>
public sealed class VoiceAttachmentConsumerFixtureTests
{
    [Fact]
    public void Serialize_RoundTripsVoiceMetadata()
    {
        var voice = new AttachmentRefDto
        {
            AttachmentId = "voice-01",
            FileName = "voice.opus",
            ContentType = "audio/opus",
            SizeBytes = 1234,
            Status = 1,
            IsVoice = true,
            VoiceCodec = "opus",
            VoiceContainer = "ogg",
            VoiceDurationMs = 4_500,
            VoiceSampleRateHz = 48_000,
            VoiceChannels = 1
        };

        var json = AttachmentJson.Serialize(new List<AttachmentRefDto> { voice });

        Assert.NotNull(json);
        Assert.Contains("\"isVoice\":true", json);
        Assert.Contains("\"voiceCodec\":\"opus\"", json);
        Assert.Contains("\"voiceContainer\":\"ogg\"", json);
        Assert.Contains("\"voiceDurationMs\":4500", json);
        Assert.Contains("\"voiceSampleRateHz\":48000", json);
        Assert.Contains("\"voiceChannels\":1", json);

        var roundTrip = AttachmentJson.Deserialize(json);
        var item = Assert.Single(roundTrip!);
        Assert.Equal("voice-01", item.AttachmentId);
        Assert.True(item.IsVoice);
        Assert.Equal("opus", item.VoiceCodec);
        Assert.Equal("ogg", item.VoiceContainer);
        Assert.Equal(4_500, item.VoiceDurationMs);
        Assert.Equal(48_000, item.VoiceSampleRateHz);
        Assert.Equal((short)1, item.VoiceChannels);
    }

    [Fact]
    public void Deserialize_LegacyPayloadWithoutVoiceFieldsUsesDefaults()
    {
        const string legacyJson =
            "[{\"refVersion\":1,\"attachmentId\":\"plain-01\",\"fileName\":\"doc.pdf\",\"contentType\":\"application/pdf\",\"sizeBytes\":2048,\"status\":1}]";

        var items = AttachmentJson.Deserialize(legacyJson);

        var item = Assert.Single(items!);
        Assert.Equal("plain-01", item.AttachmentId);
        Assert.False(item.IsVoice);
        Assert.Null(item.VoiceCodec);
        Assert.Null(item.VoiceContainer);
        Assert.Null(item.VoiceDurationMs);
        Assert.Null(item.VoiceSampleRateHz);
        Assert.Null(item.VoiceChannels);
    }

    [Fact]
    public void Deserialize_IgnoresUnknownFieldsAndPreservesVoice()
    {
        const string json =
            "[{\"refVersion\":1,\"attachmentId\":\"voice-02\",\"contentType\":\"audio/mp4\",\"sizeBytes\":5678,\"status\":1,\"isVoice\":true,\"voiceCodec\":\"aac\",\"voiceContainer\":\"m4a\",\"voiceDurationMs\":9800,\"voiceSampleRateHz\":44100,\"voiceChannels\":2,\"futureUnknown\":123}]";

        var items = AttachmentJson.Deserialize(json);

        var item = Assert.Single(items!);
        Assert.True(item.IsVoice);
        Assert.Equal("aac", item.VoiceCodec);
        Assert.Equal("m4a", item.VoiceContainer);
        Assert.Equal(9_800, item.VoiceDurationMs);
        Assert.Equal(44_100, item.VoiceSampleRateHz);
        Assert.Equal((short)2, item.VoiceChannels);
    }
}