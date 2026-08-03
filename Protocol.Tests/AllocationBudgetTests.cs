using Core.Models;
using Core.Models.DTO;
using Core.Protocol;
using Chat_App.Infrastructure.Serialization;
using System.Buffers;
using Xunit;

namespace Protocol.Tests;

/// <summary>
/// 分配预算测试（CI allocation-budget 工作流入口）：
/// 守护网络热路径的分配上限，防止出现每操作级回归（LINQ、装箱、字符串拼接、ToArray 复制等）。
/// 测量方式：预热（含 JIT/静态初始化/缓冲扩容）→ GC.Collect → 批量执行 → 取当前线程分配增量。
/// 预算按实测基线上浮约 2-3 倍：只拦"每操作新增分配"，容忍偶发 GC 背景噪声。
/// </summary>
public class AllocationBudgetTests
{
    private static long MeasureLoop(Action action, int warmup, int iterations)
    {
        for (var i = 0; i < warmup; i++)
            action();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < iterations; i++)
            action();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    /// <summary>
    /// 帧编解码热路径：10,000 次 TryWrite+Append+TryRead 往返必须零分配。
    /// （TryRead 返回内部 buffer 切片零拷贝；TryWrite 直写 ArrayBufferWriter 池化缓冲。
    /// 断言在测量循环之外：xUnit 的 Assert.Equal 对枚举有 ~500B/op 的自身开销，
    /// 混入测量会污染结果。）
    /// </summary>
    [Fact]
    public void Codec_RoundTrip_10k_Operations_Zero_Allocation()
    {
        var codec = new MessagePacketCodec();
        var serializer = new JsonPacketBodySerializer();
        var writer = new ArrayBufferWriter<byte>(512);
        var dto = new ChatMessageDto
        {
            MessageId = "m-1",
            TargetUserId = 42,
            Content = "零分配往返测试"
        };
        serializer.Serialize(writer, dto);
        var body = new ReadOnlySequence<byte>(writer.WrittenSpan.ToArray());
        var packet = new MessagePacket(PacketCommand.ChatMessage, body);

        var frameWriter = new ArrayBufferWriter<byte>(MessagePacket.HeaderSize + (int)body.Length);
        codec.TryWrite(packet, frameWriter, out _);
        var frame = frameWriter.WrittenMemory;

        // 预热：扩容完成、静态初始化完成（TryRead 的 Resync 分支等懒初始化）
        MeasureLoop(() =>
        {
            codec.Append(frame);
            while (codec.TryRead(out var pkt)) { }
        }, warmup: 2_000, iterations: 100);

        // 正确性抽样（测量循环外，避免 xUnit 断言开销污染）
        codec.Append(frame);
        var got = codec.TryRead(out var sample);
        Assert.True(got);
        Assert.Equal(PacketCommand.ChatMessage, sample.Command);

        var allocated = MeasureLoop(() =>
        {
            codec.Append(frame);
            while (codec.TryRead(out var pkt)) { }
        }, warmup: 2_000, iterations: 10_000);

        Assert.True(allocated <= 0, $"编解码往返 10,000 次应零分配，实际分配 {allocated} 字节");
    }

    /// <summary>
    /// 出站序列化热路径：10,000 次 Serialize 直写池化缓冲（复用单一 writer，与生产路径一致），
    /// 每操作分配 ≤ 500 字节（容忍 Utf8JsonWriter 实例本身，拦截任何 per-op 回归）。
    /// </summary>
    [Fact]
    public void Serialize_10k_Operations_Budget()
    {
        var serializer = new JsonPacketBodySerializer();
        var dto = new ChatMessageDto
        {
            MessageId = "m-1",
            TargetUserId = 42,
            Content = "序列化预算测试"
        };
        var writer = new ArrayBufferWriter<byte>(256);

        var allocated = MeasureLoop(() =>
        {
            writer.Clear();
            serializer.Serialize(writer, dto);
        }, warmup: 2_000, iterations: 10_000);

        var budget = 500 * 10_000L;
        Assert.True(allocated <= budget,
            $"Serialize 10,000 次分配 {allocated} 字节，超出预算 {budget}（每操作 {(double)allocated / 10_000:F1} 字节）");
    }

    /// <summary>
    /// 入站反序列化热路径：10,000 次 Deserialize 单段 body，
    /// 每操作分配 ≤ 2,000 字节（DTO 对象本身 + 反序列化内部，拦截解析路径回归）。
    /// </summary>
    [Fact]
    public void Deserialize_10k_Operations_Budget()
    {
        var serializer = new JsonPacketBodySerializer();
        var dto = new ChatMessageDto
        {
            MessageId = "m-1",
            TargetUserId = 42,
            Content = "反序列化预算测试",

        };
        var writer = new ArrayBufferWriter<byte>(256);
        serializer.Serialize(writer, dto);
        var body = new ReadOnlySequence<byte>(writer.WrittenSpan.ToArray());

        ChatMessageDto? last = null;
        var allocated = MeasureLoop(() =>
        {
            last = serializer.Deserialize<ChatMessageDto>(body);
        }, warmup: 2_000, iterations: 10_000);

        var budget = 2_000 * 10_000L;
        Assert.True(allocated <= budget,
            $"Deserialize 10,000 次分配 {allocated} 字节，超出预算 {budget}（每操作 {(double)allocated / 10_000:F1} 字节）");
        Assert.NotNull(last);
    }
}


