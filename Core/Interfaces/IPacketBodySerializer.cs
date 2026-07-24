using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Core.Interfaces
{
    public interface IPacketBodySerializer
    {
        ReadOnlyMemory<byte> Serialize<T>(T? value);
        T? Deserialize<T>(ReadOnlySequence<byte> body);
    }
}
