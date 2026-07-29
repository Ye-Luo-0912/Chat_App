using Core.Interfaces;
using Core.Models;
using System.Buffers;

namespace Core.Protocol
{
    public class MessagePacketCodec : IMessagePacketCodec
    {
        private const int DefaultBufferSize = 8192;
        private byte[] _buffer = new byte[DefaultBufferSize];
        private int _bufferedLength = 0;


        /// <summary>
        /// 将新的数据块追加到内部缓冲区中，以便后续的读取操作可以从中提取完整的消息包。
        /// </summary>
        /// <param name="chunk"></param>
        public void Append(ReadOnlyMemory<byte> chunk)
        {
            if (chunk.IsEmpty)
                return;

            // 检查缓冲区是否有足够的空间来容纳新的数据块，如果没有，则扩展缓冲区
            if (_bufferedLength +  chunk.Length > _buffer.Length)
            {
                int newSize = Math.Max(_buffer.Length * 2, _bufferedLength + chunk.Length);
                Array.Resize(ref _buffer, newSize);
            }

            // 将新的数据块复制到缓冲区中，并更新读取位置
            chunk.CopyTo(_buffer.AsMemory(_bufferedLength));
            _bufferedLength += chunk.Length;
        }

        public void Reset()
        {
           _bufferedLength = 0;
        }

        public bool TryRead(out MessagePacket packet)
        {
            // 将当前缓冲区里的有效数据包装成 ReadOnlySequence，喂给 Packet 去解析。
            // 注意：MessagePacket.TryDeserialize 返回的 Body 是对该 sequence 的切片，
            // 直接指向 _buffer 内部；后续 Buffer.BlockCopy 前移剩余数据时会覆盖该区域。
            // 因此必须在 BlockCopy 之前把当前帧 Body 复制到独立内存（P0-1 数据完整性修复）。
            var sequence = new ReadOnlySequence<byte>(_buffer, 0, _bufferedLength);
            var originalLength = (int)sequence.Length;

            if (MessagePacket.TryDeserialize(ref sequence, out var parsed, out var result))
            {
                var consumedBytes = originalLength - (int)sequence.Length;
                var remainingBytes = _bufferedLength - consumedBytes;

                // 在移动缓冲区前，把当前帧 body 复制到独立内存，
                // 避免返回给调用方的 packet.Body 引用被后续 BlockCopy 覆盖。
                packet = CopyBodyToOwnedMemory(parsed);

                if (remainingBytes > 0)
                {
                    // 将剩余的数据移动到缓冲区的起始位置
                    Buffer.BlockCopy(_buffer, consumedBytes, _buffer, 0, remainingBytes);
                }

                _bufferedLength = remainingBytes;
                return true;
            }

            packet = default;

            // 如果解析失败但不是因为数据不足（即数据格式错误），则抛出异常
            if (result == PacketParseResult.InvalidPacket)
            {
                throw new InvalidDataException("接收到非法的协议数据包 (魔数错误或长度越界)！");
            }

            // 解析失败但数据不足，等待更多数据到来
            return false;
        }

        /// <summary>
        /// 将 packet.Body 复制到独立内存，返回引用该内存的新 packet。
        /// 避免调用方持有的 Body 切片被内部缓冲区的后续重用/前移覆盖。
        /// </summary>
        private static MessagePacket CopyBodyToOwnedMemory(MessagePacket packet)
        {
            if (packet.Body.IsEmpty)
                return packet;

            var bodyCopy = new byte[(int)packet.Body.Length];
            packet.Body.CopyTo(bodyCopy);
            return new MessagePacket(packet.Command, new ReadOnlySequence<byte>(bodyCopy));
        }


        /// <summary>
        /// 将一个 MessagePacket 对象序列化并写入到提供的 IBufferWriter<byte> 中。
        /// 这个方法会尝试将整个消息包写入缓冲区，如果成功则返回 true，并通过 out 参数 written 返回实际写入的字节数；
        /// 如果缓冲区空间不足以容纳整个消息包，则返回 false，表示需要更多空间来完成写入操作。
        /// </summary>
        /// <param name="packet"></param>
        /// <param name="writer"></param>
        /// <param name="written"></param>
        /// <returns></returns>
        public bool TryWrite(MessagePacket packet, IBufferWriter<byte> writer, out int written)
        {
            // 计算整个消息包的总大小（头部 + 消息体），以便请求足够的缓冲区空间
            var totalSize = MessagePacket.HeaderSize + (int)packet.Body.Length;

            // 获取一个足够大的 span 来写入整个消息包
            var span = writer.GetSpan(totalSize);

            // 尝试将消息包序列化到提供的 span 中，如果成功则 advance 写入的字节数
            if (packet.TrySerialize(ref span, out written))
            {
                writer.Advance(written);
                return true;
            }

            // 如果序列化失败，通常是因为提供的 span 不够大，返回 false 表示需要更多空间
            return false;

        }
    }
}
