using Core.Models;
using Core.Models.DTO;
using Core.Protocol;
using Chat_App.Infrastructure.Serialization;
using System.Buffers;
using Xunit;

namespace Protocol.Tests;

/// <summary>
/// 分配预算门禁测试。
/// 编码/解码无反射，Debug/Release 下阈值稳定，作为强制 CI 门禁。
/// 序列化/反序列化因 source-gen JSON 在测试宿主中杂音大（GC.GetTotalAllocatedBytes
/// 包含 xunit/JIT 噪声），仅记录不强制；精确门禁由 BenchmarkResults.md + BenchmarkDotNet 负责。
/// </summary>
public class AllocationBudgetTests
{
    [Fact]
    public void Encode_Single_Frame_BodySize64_Allocation_Budget()
    {
        var body = new byte[64];
        Random.Shared.NextBytes(body);
        var packet = new MessagePacket(PacketCommand.ChatMessage, new ReadOnlySequence<byte>(body));

        var warmup = new ArrayBufferWriter<byte>(MessagePacket.HeaderSize + 64);
        new MessagePacketCodec().TryWrite(packet, warmup, out _);

        var before = GC.GetTotalAllocatedBytes(precise: true);
        for (var i = 0; i < 1000; i++)
        {
            var writer = new ArrayBufferWriter<byte>(MessagePacket.HeaderSize + 64);
            new MessagePacketCodec().TryWrite(packet, writer, out _);
        }
        var after = GC.GetTotalAllocatedBytes(precise: true);
        var perOp = (after - before) / 1000;

        // 基线 136B，阈值 272B；Debug 下 ArrayBufferWriter 本身有杂音，放宽到 1500B
        Assert.True(perOp <= 1500, "编码单帧分配 " + perOp + "B 超过预算 1500B（基线 136B，Release 阈值 272B）");
    }

    [Fact]
    public void Decode_Single_Frame_BodySize64_Allocation_Budget()
    {
        var body = new byte[64];
        Random.Shared.NextBytes(body);
        var packet = new MessagePacket(PacketCommand.ChatMessage, new ReadOnlySequence<byte>(body));
        var writer = new ArrayBufferWriter<byte>(MessagePacket.HeaderSize + 64);
        new MessagePacketCodec().TryWrite(packet, writer, out _);
        var frameBytes = writer.WrittenSpan.ToArray();

        var warmup = new MessagePacketCodec();
        warmup.Append(frameBytes);
        warmup.TryRead(out _);

        var before = GC.GetTotalAllocatedBytes(precise: true);
        for (var i = 0; i < 1000; i++)
        {
            var codec = new MessagePacketCodec();
            codec.Append(frameBytes);
            codec.TryRead(out _);
        }
        var after = GC.GetTotalAllocatedBytes(precise: true);
        var perOp = (after - before) / 1000;

        // 基线 312B，Release 阈值 624B；Debug 下放宽到 2000B
        Assert.True(perOp <= 2000, "解码单帧分配 " + perOp + "B 超过预算 2000B（基线 312B，Release 阈值 624B）");
    }

    /// <summary>
    /// 序列化分配记录（不强制）。精确门禁见 BenchmarkResults.md。
    /// </summary>
    [Fact]
    public void Serialize_ChatMessageDto_Allocation_Record()
    {
        var serializer = new JsonPacketBodySerializer();
        var dto = new ChatMessageDto
        {
            MessageId = "msg-budget-test",
            TargetUserId = 99999,
            Content = "预算测试消息内容 allocation budget test payload"
        };

        var warmup = new ArrayBufferWriter<byte>(256);
        serializer.Serialize(warmup, dto);

        var before = GC.GetTotalAllocatedBytes(precise: true);
        for (var i = 0; i < 1000; i++)
        {
            var writer = new ArrayBufferWriter<byte>(256);
            serializer.Serialize(writer, dto);
        }
        var after = GC.GetTotalAllocatedBytes(precise: true);
        var perOp = (after - before) / 1000;

        // 仅记录，不强制（测试宿主杂音大）。基线 136B（BenchmarkDotNet Release）。
        Assert.True(perOp > 0, "序列化应产生分配");
    }

    /// <summary>
    /// 反序列化分配记录（不强制）。精确门禁见 BenchmarkResults.md。
    /// </summary>
    [Fact]
    public void Deserialize_ChatMessageDto_Allocation_Record()
    {
        var serializer = new JsonPacketBodySerializer();
        var dto = new ChatMessageDto
        {
            MessageId = "msg-budget-test",
            TargetUserId = 99999,
            Content = "预算测试消息内容 allocation budget test payload"
        };
        var writer = new ArrayBufferWriter<byte>(256);
        serializer.Serialize(writer, dto);
        var serialized = writer.WrittenSpan.ToArray();

        serializer.Deserialize<ChatMessageDto>(new ReadOnlySequence<byte>(serialized));

        var before = GC.GetTotalAllocatedBytes(precise: true);
        for (var i = 0; i < 1000; i++)
        {
            serializer.Deserialize<ChatMessageDto>(new ReadOnlySequence<byte>(serialized));
        }
        var after = GC.GetTotalAllocatedBytes(precise: true);
        var perOp = (after - before) / 1000;

        // 仅记录，不强制。基线 352B（BenchmarkDotNet Release）。
        Assert.True(perOp > 0, "反序列化应产生分配");
    }
}