using Core.Models;
using System;
using System.Buffers;

namespace Core.Interfaces
{
    public interface IMessagePacketCodec
    {
        /// <summary>
        /// 尝试将消息包序列化写入缓冲区。
        /// </summary>
        /// <param name="packet">待发送的消息包。</param>
        /// <param name="writer">写入目标缓冲区。</param>
        /// <param name="written">实际写入的字节数。</param>
        /// <returns>是否写入成功。</returns>
        bool TryWrite(MessagePacket packet, IBufferWriter<byte> writer, out int written);

        /// <summary>
        /// 将接收到的字节数据追加到内部缓冲区，供后续 <see cref="TryRead"/> 解析。
        /// </summary>
        /// <param name="chunk">接收到的数据块。</param>
        void Append(ReadOnlyMemory<byte> chunk);

        /// <summary>
        /// 尝试从内部缓冲区读取一个完整的消息包。
        /// 成功时返回 true 并通过 <paramref name="packet"/> 返回零拷贝切片；
        /// 缓冲区不足时返回 false。
        /// </summary>
        /// <param name="packet">解析出的消息包（零拷贝切片，在下次 Append 前有效）。</param>
        /// <returns>是否成功读取完整包。</returns>
        bool TryRead(out MessagePacket packet);

        /// <summary>
        /// 重置内部状态，清空缓冲区和任何未完成的消息包数据，以准备处理新的消息流。
        /// </summary>
        void Reset();
    }
}
