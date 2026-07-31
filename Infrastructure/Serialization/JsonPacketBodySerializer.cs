using Core.Interfaces;
using System;
using System.Buffers;
using System.Text.Json;

namespace Infrastructure.Serialization
{
    /// <summary>
    /// 基于 source-generated JSON 上下文的协议 body 序列化器（P0-十）。
    /// 出站：Utf8JsonWriter(IBufferWriter&lt;byte&gt;) + JsonSerializer.Serialize(Utf8JsonWriter, ...)
    ///       直接写入池化缓冲，无 SerializeToUtf8Bytes 的中间 byte[] 分配。
    /// 入站：JsonSerializer.Deserialize&lt;T&gt;(span, Options) 走 source-gen 路径
    ///       （Options = ChatJsonContext.Default.Options，携带 source-gen TypeInfoResolver）。
    /// </summary>
    public class JsonPacketBodySerializer : IPacketBodySerializer
    {
        // source-generated 上下文的 Options：内含 TypeInfoResolver，走无反射序列化路径。
        private static readonly JsonSerializerOptions Options = ChatJsonContext.Default.Options;

        public void Serialize<T>(IBufferWriter<byte> writer, T? value)
        {
            if (value is null) return;
            // 通过 Utf8JsonWriter 直接写入 IBufferWriter，避免 SerializeToUtf8Bytes
            // 产生中间 byte[] 分配。Utf8JsonWriter 增量写入池化缓冲，Dispose 时 Flush。
            using (var jsonWriter = new Utf8JsonWriter(writer))
            {
                JsonSerializer.Serialize(jsonWriter, value, typeof(T), Options);
            }
        }

        public T? Deserialize<T>(in ReadOnlySequence<byte> body)
        {
            if (body.IsEmpty) return default;
            // 单段直接用 span 重载零分配；codec 产出的 body 已是单段（独立内存）。
            if (body.IsSingleSegment)
                return JsonSerializer.Deserialize<T>(body.First.Span, Options);
            // 多段回退：先合并再反序列化（罕见路径）
            return JsonSerializer.Deserialize<T>(body.ToArray(), Options);
        }
    }
}
