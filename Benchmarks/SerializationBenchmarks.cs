using BenchmarkDotNet.Attributes;
using Core.Models.DTO;
using Chat_App.Infrastructure.Serialization;
using System.Buffers;

namespace Benchmarks;

/// <summary>
/// 序列化基准：source-generated JSON 直写 IBufferWriter 的吞吐与分配对比。
/// 验收：搜索和输入 p95 响应低于 50ms 的预算内。
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 5)]
public class SerializationBenchmarks
{
    private JsonPacketBodySerializer _serializer = null!;
    private ChatMessageDto _dto = null!;
    private ArrayBufferWriter<byte> _writer = null!;
    private byte[] _serialized = null!;

    [GlobalSetup]
    public void Setup()
    {
        _serializer = new JsonPacketBodySerializer();
        _dto = new ChatMessageDto
        {
            MessageId = "msg-0192f8a7-benchmark",
            TargetUserId = 99999,
            Content = "基准测试消息内容 benchmark payload for serialization measurement",
            SentUtc = DateTime.UtcNow,
            ReplyToMessageId = null,
            ReplyToSenderUserId = null,
            ReplyToPreview = null,
            ForwardedFromMessageId = null,
            ForwardedFromSenderUserId = null,
            ForwardedFromPreview = null,
            AttachmentIds = null
        };
        _writer = new ArrayBufferWriter<byte>(256);
        _serializer.Serialize(_writer, _dto);
        _serialized = _writer.WrittenSpan.ToArray();
    }

    [Benchmark(Description = "序列化 ChatMessageDto (直写 IBufferWriter)")]
    public int Serialize()
    {
        _writer.Clear();
        _serializer.Serialize(_writer, _dto);
        return _writer.WrittenCount;
    }

    [Benchmark(Description = "反序列化 ChatMessageDto (单段 span)")]
    public ChatMessageDto? DeserializeSingleSegment()
    {
        var seq = new ReadOnlySequence<byte>(_serialized);
        return _serializer.Deserialize<ChatMessageDto>(seq);
    }

    [Benchmark(Description = "序列化+反序列化往返")]
    public ChatMessageDto? RoundTrip()
    {
        _writer.Clear();
        _serializer.Serialize(_writer, _dto);
        return _serializer.Deserialize<ChatMessageDto>(new ReadOnlySequence<byte>(_writer.WrittenSpan.ToArray()));
    }
}
