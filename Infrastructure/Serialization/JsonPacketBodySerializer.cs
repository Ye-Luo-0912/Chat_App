using Core.Interfaces;
using System;
using System.Buffers;
using System.Text.Json;

namespace Infrastructure.Serialization
{
    public class JsonPacketBodySerializer : IPacketBodySerializer
    {
        private static readonly JsonSerializerOptions Options = ChatJsonContext.Default.Options;

        public T? Deserialize<T>(ReadOnlySequence<byte> body)
        {
            if (body.IsEmpty) return default;
            if (body.IsSingleSegment)
                return JsonSerializer.Deserialize<T>(body.First.Span, Options);
            return JsonSerializer.Deserialize<T>(body.ToArray(), Options);
        }

        public ReadOnlyMemory<byte> Serialize<T>(T? value)
        {
            if (value is null) return ReadOnlyMemory<byte>.Empty;
            return JsonSerializer.SerializeToUtf8Bytes(value, Options);
        }
    }
}