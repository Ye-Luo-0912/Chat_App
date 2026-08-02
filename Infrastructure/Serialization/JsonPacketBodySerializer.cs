using Core.Interfaces;
using System;
using System.Buffers;
using System.Text.Json;

namespace Chat_App.Infrastructure.Serialization
{
    /// <summary>
    /// 基于 source-generated JSON 上下文的协议 body 序列化器。
    /// 出站：Utf8JsonWriter(IBufferWriter&lt;byte&gt;) + JsonSerializer.Serialize(Utf8JsonWriter, ...)
    /// 直接写入池化缓冲，无 SerializeToUtf8Bytes 的中间 byte[] 分配。
    /// 入站：单段走 span 重载零分配；多段用 ArrayPool 租借避免 ToArray 堆分配。
    /// </summary>
    public class JsonPacketBodySerializer : IPacketBodySerializer
    {
        private static readonly JsonSerializerOptions Options = ChatJsonContext.Default.Options;

        public void Serialize<T>(IBufferWriter<byte> writer, T? value)
        {
            if (value is null) return;
            using (var jsonWriter = new Utf8JsonWriter(writer))
            {
                JsonSerializer.Serialize(jsonWriter, value, typeof(T), Options);
            }
        }

        public T? Deserialize<T>(in ReadOnlySequence<byte> body)
        {
            if (body.IsEmpty) return default;
            if (body.IsSingleSegment)
                return JsonSerializer.Deserialize<T>(body.First.Span, Options);
            var len = (int)body.Length;
            var pooled = ArrayPool<byte>.Shared.Rent(len);
            try
            {
                body.CopyTo(pooled);
                return JsonSerializer.Deserialize<T>(pooled.AsSpan(0, len), Options);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(pooled);
            }
        }
    }
}