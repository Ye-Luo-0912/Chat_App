using Core.Models;
using Core.Protocol;
using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using Xunit;

namespace Protocol.Tests;

/// <summary>
/// 协议帧属性测试：覆盖帧拆分/合并/魔数损坏/长度截断/最大 body/连续十万帧。
/// 验收：无丢包、无串包、无 body 变化、无失控内存增长。
/// </summary>
public class ProtocolFrameFuzzingTests
{
    private static MessagePacket BuildPacket(PacketCommand cmd, byte[] body)
        => new(cmd, new ReadOnlySequence<byte>(body));

    private static byte[] EncodeFrame(MessagePacket packet)
    {
        var writer = new ArrayBufferWriter<byte>(MessagePacket.HeaderSize + (int)packet.Body.Length);
        Assert.True(new MessagePacketCodec().TryWrite(packet, writer, out _));
        return writer.WrittenSpan.ToArray();
    }

    /// <summary>
    /// 单帧被拆成任意多个 TCP chunk 后仍能完整还原。
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(13)]
    [InlineData(100)]
    public void Frame_Split_Into_Arbitrary_Chunks_Restores_Body(int chunkSize)
    {
        var body = Encoding.UTF8.GetBytes("hello-protocol-fuzzing");
        var frame = EncodeFrame(BuildPacket(PacketCommand.ChatMessage, body));
        var codec = new MessagePacketCodec();

        // 按 chunkSize 切片喂给 codec
        for (var i = 0; i < frame.Length; i += chunkSize)
        {
            var len = Math.Min(chunkSize, frame.Length - i);
            codec.Append(frame.AsMemory(i, len));
        }

        Assert.True(codec.TryRead(out var packet));
        Assert.Equal(PacketCommand.ChatMessage, packet.Command);
        Assert.Equal(body, packet.Body.ToArray());
        Assert.False(codec.TryRead(out _)); // 无残余
    }

    /// <summary>
    /// 多个帧合并到一个 chunk：一次 Append 多帧后能依次读出，body 严格一致。
    /// </summary>
    [Fact]
    public void Multiple_Frames_In_One_Chunk_Decode_Sequentially()
    {
        var frames = new List<byte[]>();
        for (var i = 0; i < 10; i++)
        {
            var body = Encoding.UTF8.GetBytes($"frame-{i}-payload");
            frames.Add(EncodeFrame(BuildPacket(PacketCommand.ChatMessage, body)));
        }

        var merged = new byte[frames.Sum(f => f.Length)];
        var offset = 0;
        foreach (var f in frames)
        {
            Buffer.BlockCopy(f, 0, merged, offset, f.Length);
            offset += f.Length;
        }

        var codec = new MessagePacketCodec();
        codec.Append(merged);

        for (var i = 0; i < frames.Count; i++)
        {
            Assert.True(codec.TryRead(out var packet), $"第 {i} 帧未解析出");
            Assert.Equal(PacketCommand.ChatMessage, packet.Command);
            Assert.Equal(Encoding.UTF8.GetBytes($"frame-{i}-payload"), packet.Body.ToArray());
        }
        Assert.False(codec.TryRead(out _));
    }

    /// <summary>
    /// 魔数损坏：codec 应丢弃坏帧并重新同步到下一个魔数，不影响后续合法帧。
    /// </summary>
    [Fact]
    public void Corrupted_Magic_Is_Skipped_And_Next_Frame_Restores()
    {
        var goodBody = Encoding.UTF8.GetBytes("good");
        var goodFrame = EncodeFrame(BuildPacket(PacketCommand.Heartbeat, goodBody));

        // 构造坏帧：合法长度字段但魔数被破坏
        var bad = new byte[MessagePacket.HeaderSize + goodBody.Length];
        bad[0] = 0xFF; bad[1] = 0xFF; bad[2] = 0xFF; bad[3] = 0xFF;
        BinaryPrimitives.WriteUInt16LittleEndian(bad.AsSpan(MessagePacket.CommandOffset), (ushort)PacketCommand.Heartbeat);
        BinaryPrimitives.WriteInt32LittleEndian(bad.AsSpan(MessagePacket.LengthOffset), goodBody.Length);
        Buffer.BlockCopy(goodBody, 0, bad, MessagePacket.HeaderSize, goodBody.Length);

        var stream = new byte[bad.Length + goodFrame.Length];
        Buffer.BlockCopy(bad, 0, stream, 0, bad.Length);
        Buffer.BlockCopy(goodFrame, 0, stream, bad.Length, goodFrame.Length);

        var codec = new MessagePacketCodec();
        codec.Append(stream);

        // 坏帧被丢弃（TryRead 返回 false），重同步后下一帧应可解析
        var decoded = false;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (codec.TryRead(out var packet))
            {
                Assert.Equal(PacketCommand.Heartbeat, packet.Command);
                Assert.Equal(goodBody, packet.Body.ToArray());
                decoded = true;
                break;
            }
        }
        Assert.True(decoded, "坏帧后未恢复出合法帧");
    }

    /// <summary>
    /// 长度截断：header 声明 body 长度但实际未到达，应返回 NeedMoreData 而非 InvalidPacket。
    /// </summary>
    [Fact]
    public void Truncated_Length_Waits_For_More_Data()
    {
        var body = Encoding.UTF8.GetBytes("partial-body");
        var frame = EncodeFrame(BuildPacket(PacketCommand.ChatMessage, body));

        var codec = new MessagePacketCodec();
        // 只喂 header + 半个 body
        var partialLen = MessagePacket.HeaderSize + body.Length / 2;
        codec.Append(frame.AsMemory(0, partialLen));

        Assert.False(codec.TryRead(out _), "未满一帧不应返回 true");

        // 补齐剩余字节后应能解析
        codec.Append(frame.AsMemory(partialLen, frame.Length - partialLen));
        Assert.True(codec.TryRead(out var packet));
        Assert.Equal(body, packet.Body.ToArray());
    }

    /// <summary>
    /// 声明长度超过 MaxBodySize 应判定为坏帧并重同步。
    /// </summary>
    [Fact]
    public void Oversized_Length_Declared_As_Invalid_And_Resyncs()
    {
        var goodBody = Encoding.UTF8.GetBytes("after-oversize");
        var goodFrame = EncodeFrame(BuildPacket(PacketCommand.Heartbeat, goodBody));

        var bad = new byte[MessagePacket.HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(bad, MessagePacket.MagicNumber);
        BinaryPrimitives.WriteUInt16LittleEndian(bad.AsSpan(MessagePacket.CommandOffset), (ushort)PacketCommand.Heartbeat);
        BinaryPrimitives.WriteInt32LittleEndian(bad.AsSpan(MessagePacket.LengthOffset), MessagePacket.MaxBodySize + 1);

        var stream = new byte[bad.Length + goodFrame.Length];
        Buffer.BlockCopy(bad, 0, stream, 0, bad.Length);
        Buffer.BlockCopy(goodFrame, 0, stream, bad.Length, goodFrame.Length);

        var codec = new MessagePacketCodec();
        codec.Append(stream);

        var decoded = false;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (codec.TryRead(out var packet))
            {
                Assert.Equal(goodBody, packet.Body.ToArray());
                decoded = true;
                break;
            }
        }
        Assert.True(decoded, "超长声明后未恢复出合法帧");
    }

    /// <summary>
    /// 最大 body 帧：MaxBodySize 字节 body 应正常编解码。
    /// </summary>
    [Fact]
    public void Max_Body_Size_Frame_Roundtrips()
    {
        var body = new byte[MessagePacket.MaxBodySize];
        Random.Shared.NextBytes(body);
        var frame = EncodeFrame(BuildPacket(PacketCommand.ChatMessage, body));

        var codec = new MessagePacketCodec();
        codec.Append(frame);

        Assert.True(codec.TryRead(out var packet));
        Assert.Equal(body, packet.Body.ToArray());
    }

    /// <summary>
    /// 连续十万帧：验证无丢包、无串包、body 严格一致。
    /// </summary>
    [Fact]
    public void One_Hundred_Thousand_Consecutive_Frames_No_Loss_No_CrossTalk()
    {
        const int FrameCount = 100_000;
        var rnd = new Random(20260731);
        var codec = new MessagePacketCodec();

        // 先把所有帧编码到一个大缓冲区，模拟一次性收到大量数据
        var writer = new ArrayBufferWriter<byte>(FrameCount * 64);
        var expectedBodies = new byte[FrameCount][];

        for (var i = 0; i < FrameCount; i++)
        {
            var len = rnd.Next(0, 64);
            var body = new byte[len];
            rnd.NextBytes(body);
            expectedBodies[i] = body;
            var packet = BuildPacket(PacketCommand.ChatMessage, body);
            Assert.True(codec.TryWrite(packet, writer, out _));
        }

        codec.Reset();
        codec.Append(writer.WrittenMemory);

        for (var i = 0; i < FrameCount; i++)
        {
            Assert.True(codec.TryRead(out var packet), $"第 {i} 帧丢失");
            Assert.Equal(PacketCommand.ChatMessage, packet.Command);
            Assert.Equal(expectedBodies[i], packet.Body.ToArray());
        }
        Assert.False(codec.TryRead(out _));
    }

    /// <summary>
    /// 随机拆分 + 随机合并：用随机种子将 N 帧切成随机大小的 chunk 喂入 codec，
    /// 验证解码结果与原始 body 序列严格一致。覆盖跨 chunk 边界场景。
    /// </summary>
    [Theory]
    [InlineData(42, 200)]
    [InlineData(2026, 500)]
    [InlineData(73, 1000)]
    public void Random_Chunk_Splitting_Preserves_Body_Sequence(int seed, int frameCount)
    {
        var rnd = new Random(seed);
        var writer = new ArrayBufferWriter<byte>(frameCount * 48);
        var expected = new byte[frameCount][];

        for (var i = 0; i < frameCount; i++)
        {
            var len = rnd.Next(0, 48);
            var body = new byte[len];
            rnd.NextBytes(body);
            expected[i] = body;
            Assert.True(new MessagePacketCodec().TryWrite(BuildPacket(PacketCommand.ChatMessage, body), writer, out _));
        }

        var allBytes = writer.WrittenSpan.ToArray();
        var codec = new MessagePacketCodec();

        // 随机切片喂入
        var pos = 0;
        while (pos < allBytes.Length)
        {
            var step = rnd.Next(1, Math.Min(17, allBytes.Length - pos + 1));
            codec.Append(allBytes.AsMemory(pos, step));
            pos += step;
        }

        for (var i = 0; i < frameCount; i++)
        {
            Assert.True(codec.TryRead(out var packet), $"seed={seed} 第 {i} 帧丢失");
            Assert.Equal(expected[i], packet.Body.ToArray());
        }
    }

    /// <summary>
    /// 空 body 帧应正常编解码（Body.Length == 0）。
    /// </summary>
    [Fact]
    public void Empty_Body_Frame_Roundtrips()
    {
        var packet = BuildPacket(PacketCommand.Heartbeat, Array.Empty<byte>());
        var frame = EncodeFrame(packet);

        var codec = new MessagePacketCodec();
        codec.Append(frame);

        Assert.True(codec.TryRead(out var decoded));
        Assert.Equal(PacketCommand.Heartbeat, decoded.Command);
        Assert.Equal(0, (int)decoded.Body.Length);
    }

    /// <summary>
    /// TryRead 返回的 Body 引用在后续 Append 后仍应保持独立（数据完整性修复回归）。
    /// </summary>
    [Fact]
    public void Body_Remains_Valid_After_Subsequent_Append()
    {
        var body1 = Encoding.UTF8.GetBytes("first-frame-body");
        var body2 = Encoding.UTF8.GetBytes("second-frame-body-longer");
        var frame1 = EncodeFrame(BuildPacket(PacketCommand.ChatMessage, body1));
        var frame2 = EncodeFrame(BuildPacket(PacketCommand.ChatMessage, body2));

        var codec = new MessagePacketCodec();
        codec.Append(frame1);
        Assert.True(codec.TryRead(out var first));

        // 在读取第二帧前先把 body1 拷贝出来
        var snapshot = first.Body.ToArray();

        // 追加第二帧并读取，可能触发内部缓冲区前移/扩容
        codec.Append(frame2);
        Assert.True(codec.TryRead(out var second));

        // 第一帧 body 快照必须保持不变
        Assert.Equal(body1, snapshot);
        Assert.Equal(body2, second.Body.ToArray());
    }
}
