using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Core.Models
{
    /// <summary>
    /// 网络消息数据包结构
    /// </summary>
    public readonly struct MessagePacket
    {
        #region 字段定义
        public const int CommandOffset = sizeof(uint);
        public const int LengthOffset = sizeof(uint) + sizeof(ushort);

        /// <summary>
        /// 数据包魔数 (0x1A2B3C4D)
        /// </summary>
        public const uint MagicNumber = 0x1A2B3C4D;

        /// <summary>
        /// 帧头魔数字节序列（MagicNumber 的小端字节序表示），用于坏包重新同步时按字节搜索。
        /// </summary>
        public static readonly byte[] MagicBytes = { 0x4D, 0x3C, 0x2B, 0x1A };

        /// <summary>
        /// 包体最大长度 (80KB)，可以根据实际需求调整
        /// </summary>
        public const int MaxBodySize = 81920;

        /// <summary>
        /// 包头总长度 (4字节魔数 + 2字节指令 + 4字节长度 = 10 字节)
        /// </summary>
        public const int HeaderSize = sizeof(uint) + sizeof(ushort) + sizeof(int);

        /// <summary>
        /// 业务指令类型
        /// </summary>
        public PacketCommand Command { get; }

        /// <summary>
        /// 消息体载荷
        /// </summary>
        public ReadOnlySequence<byte> Body { get; }
        #endregion

       
        /// <summary>
        /// 构造函数，初始化数据包
        /// </summary>
        /// <param name="command"></param>
        /// <param name="body"></param>
        public MessagePacket(PacketCommand command, ReadOnlySequence<byte> body)
        {
            Command = command;
            Body = body;
        }


        #region 序列化和反序列化
        /// <summary>
        /// 序列化数据包到提供的缓冲区，成功后推进 buffer 并返回写入字节数；失败时抛出异常。
        /// </summary>
        public readonly int Serialize(ref Span<byte> buffer)
        {
            if (Body.Length > MaxBodySize)
                throw new InvalidOperationException($"Body too large: {Body.Length}");

            var bodyLength = checked((int)Body.Length);
            var totalSize = checked(HeaderSize + bodyLength);

            if (buffer.Length < totalSize)
                throw new ArgumentException("Buffer is too small.", nameof(buffer));

            // 获取包头写入位置
            var headerSpan = buffer[..HeaderSize];

            BinaryPrimitives.WriteUInt32LittleEndian(headerSpan, MagicNumber);
            BinaryPrimitives.WriteUInt16LittleEndian(headerSpan[CommandOffset..], (ushort)Command);
            BinaryPrimitives.WriteInt32LittleEndian(headerSpan[LengthOffset..], bodyLength);
            // 提交包头
            if (!Body.IsEmpty)
            {
                Body.CopyTo(buffer.Slice(HeaderSize, bodyLength));
            }

            buffer = buffer[totalSize..];
            return totalSize;
        }


        /// <summary>
        /// 尝试序列化数据包到提供的缓冲区，如果成功则返回 true 和写入的字节数；如果缓冲区不足或数据异常则返回 false。
        /// </summary>
        /// <param name="buffer"></param>
        /// <param name="written"></param>
        /// <returns></returns>
        public readonly bool TrySerialize(ref Span<byte> buffer, out int written)
        {
            // 初始化输出参数
            written = 0;

            // 包体长度异常，可能是恶意攻击或者数据损坏
            if (Body.Length > MaxBodySize)
                return false;

            // 计算整个包的总长度
            var bodyLength = checked((int)Body.Length);
            var totalSize = checked(HeaderSize + bodyLength);

            // 判断提供的缓冲区是否足够写入整个包
            if (buffer.Length < totalSize)
                return false;

            // 获取包头写入位置
            var headerSpan = buffer[..HeaderSize];

            // 写入包头
            BinaryPrimitives.WriteUInt32LittleEndian(headerSpan, MagicNumber);
            BinaryPrimitives.WriteUInt16LittleEndian(headerSpan[CommandOffset..], (ushort)Command);
            BinaryPrimitives.WriteInt32LittleEndian(headerSpan[LengthOffset..], bodyLength);

            // 提交包头和包体
            if (!Body.IsEmpty)
            {
                Body.CopyTo(buffer.Slice(HeaderSize, bodyLength));
            }

            // 成功写入整个包
            buffer = buffer[totalSize..];
            written = totalSize;
            return true;
        }

        /// <summary>
        /// 反序列化
        /// </summary>
        public static bool TryDeserialize(ref ReadOnlySequence<byte> buffer, out MessagePacket packet, out PacketParseResult result)
        {
            // 初始化输出参数
            packet = default;
            result = PacketParseResult.InvalidPacket;

            // 首先检查包头长度是否足够
            if (buffer.Length < HeaderSize)
            {
                result = PacketParseResult.NeedMoreData;
                return false;
            }


            // 提取 10 字节包头
            Span<byte> headerSpan = stackalloc byte[HeaderSize];
            buffer.Slice(0, HeaderSize).CopyTo(headerSpan);

            var magic = BinaryPrimitives.ReadUInt32LittleEndian(headerSpan);
            if (magic != MagicNumber)
                return false;

            // 读出实际应该有的包体长度
            var bodyLength = BinaryPrimitives.ReadInt32LittleEndian(headerSpan[6..]);

            // 包体长度异常，可能是恶意攻击或者数据损坏
            if (bodyLength < 0 || bodyLength > MaxBodySize)
                return false;

            // 计算整个包的总长度
            var totalSize = HeaderSize + bodyLength;
            // 判断收到的数据够不够一整个包
            if (buffer.Length < totalSize)
            {
                result = PacketParseResult.NeedMoreData;
                return false;
            }

            // 组装 Packet
            var command = (PacketCommand)BinaryPrimitives.ReadUInt16LittleEndian(headerSpan[CommandOffset..]);
            packet = new MessagePacket(command, buffer.Slice(HeaderSize, bodyLength));

            // 切掉已经解析的包头和包体，剩下的留给下一轮解析
            buffer = buffer.Slice(totalSize);
            result = PacketParseResult.Success;
            return true;
        }

        #endregion
    }
}
