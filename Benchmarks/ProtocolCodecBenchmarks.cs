using BenchmarkDotNet.Attributes;
using Core.Models;
using Core.Protocol;
using System.Buffers;

namespace Benchmarks;

/// <summary>
/// 协议编解码基准：测量帧编码/解码热路径耗时与分配。
/// 目标：p95 滚动帧耗时低于 16.7ms（60fps）的预算内。
/// 回归阈值（见 BenchmarkResults.md）：
/// - 编码单帧 0B: ≤ 30 ns, ≤ 80 B
/// - 解码单帧 0B: ≤ 150 ns, ≤ 320 B
/// - 解码1000帧 0B: ≤ 35 us, ≤ 17 KB
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 5)]
public class ProtocolCodecBenchmarks
{
    private byte[] _frameBytes = null!;
    private byte[] _multiFrameBytes = null!;
    private MessagePacket _packet;

    [Params(0, 64, 512, 4096)]
    public int BodySize { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var body = new byte[BodySize];
        Random.Shared.NextBytes(body);
        _packet = new MessagePacket(PacketCommand.ChatMessage, new ReadOnlySequence<byte>(body));

        var writer = new ArrayBufferWriter<byte>(MessagePacket.HeaderSize + BodySize);
        new MessagePacketCodec().TryWrite(_packet, writer, out _);
        _frameBytes = writer.WrittenSpan.ToArray();

        // 构造 1000 帧的连续字节流
        var multi = new ArrayBufferWriter<byte>(1000 * (MessagePacket.HeaderSize + BodySize));
        var codec = new MessagePacketCodec();
        for (var i = 0; i < 1000; i++)
            codec.TryWrite(_packet, multi, out _);
        _multiFrameBytes = multi.WrittenSpan.ToArray();
    }

    [Benchmark(Description = "编码单帧")]
    public int EncodeSingleFrame()
    {
        var writer = new ArrayBufferWriter<byte>(MessagePacket.HeaderSize + BodySize);
        new MessagePacketCodec().TryWrite(_packet, writer, out _);
        return writer.WrittenCount;
    }

    [Benchmark(Description = "解码单帧")]
    public bool DecodeSingleFrame()
    {
        var codec = new MessagePacketCodec();
        codec.Append(_frameBytes);
        return codec.TryRead(out _);
    }

    [Benchmark(Description = "解码1000帧")]
    public int DecodeThousandFrames()
    {
        var codec = new MessagePacketCodec();
        codec.Append(_multiFrameBytes);
        var count = 0;
        while (codec.TryRead(out _)) count++;
        return count;
    }
}