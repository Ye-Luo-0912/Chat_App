using Core.Models;
using System;
using System.Buffers;

namespace Core.Interfaces
{
    public interface IMessagePacketCodec
    {
        /// <summary>
        /// 尝试将消息包写入缓冲区。
        /// </summary>
        /// <param name="packet"></param>
        /// <param name="writer"></param>
        /// <param name="written"></param>
        /// <returns></returns>
        bool TryWrite(MessagePacket packet, IBufferWriter<byte> writer, out int written);

        /// <summary>
        /// 将接收到的字节数据追加到内部缓冲区，并尝试解析出完整的消息包。
        /// </summary>
        /// <param name="chunk"></param>
        void Append(ReadOnlyMemory<byte> chunk);

        /// <summary>
        /// 尝试从内部缓冲区读取一个完整的消息包。如果成功，返回true并将解析出的消息包赋值给packet；否则返回false。
        /// </summary>
        /// <param name="packet"></param>
        /// <returns></returns>
        bool TryRead(out MessagePacket packet);

        /// <summary>
        /// 重置内部状态，清空缓冲区和任何未完成的消息包数据，以准备处理新的消息流。
        /// </summary>
        void Reset();
    }
}
