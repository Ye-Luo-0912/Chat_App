using Core.Interfaces;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Infrastructure.Serialization
{
    public class JsonPacketBodySerializer : IPacketBodySerializer
    {
        private static readonly JsonSerializerOptions options = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };
        public T? Deserialize<T>(ReadOnlySequence<byte> body)
        {
            if (body.IsEmpty)
                return default;

            if (body.IsSingleSegment)
                return JsonSerializer.Deserialize<T>(body.First.Span, options);

            return JsonSerializer.Deserialize<T>(body.ToArray(), options);
        }

        public ReadOnlyMemory<byte> Serialize<T>(T? value)
        {
            if(value is null)
                return ReadOnlyMemory<byte>.Empty;

            return JsonSerializer.SerializeToUtf8Bytes(value, options);
        }
    }
}
