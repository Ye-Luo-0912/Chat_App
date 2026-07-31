using Core.Interfaces;
using Core.Models;
using System.Buffers;

namespace Core.Protocol
{
    /// <summary>
    /// 基于 growable buffer 的消息包编解码器（P0-十 热路径优化）。
    ///
    /// 入站路径：
    /// - byte[] + offset/count，仅在容量不足时 compact，非每帧前移，避免 O(n²) 复制。
    /// - body 零拷贝：TryRead 返回内部 buffer 的切片（ReadOnlySequence），不分配新 byte[]。
    ///   契约：Body 仅在下一次 Append/TryRead 调用前有效。生产代码 RoutePacket 在 TryRead
    ///   循环内同步反序列化，消费完 Body 后才进入下一轮，满足此契约。
    /// </summary>
    public class MessagePacketCodec : IMessagePacketCodec
    {
        private byte[] _buffer = Array.Empty<byte>();
        private int _offset;  // 未消费数据起始位置
        private int _count;   // 未消费数据长度

        /// <summary>
        /// 将新数据块追加到缓冲区末尾。容量不足时先 compact（前移未消费数据），再按需扩容。
        /// compact 仅在容量不足时触发，而非每帧，避免 O(n²) 复制。
        /// </summary>
        public void Append(ReadOnlyMemory<byte> chunk)
        {
            if (chunk.IsEmpty) return;
            EnsureCapacity(chunk.Length);
            chunk.Span.CopyTo(_buffer.AsSpan(_offset + _count));
            _count += chunk.Length;
        }

        private void EnsureCapacity(int additional)
        {
            var required = _offset + _count + additional;
            if (required <= _buffer.Length) return;

            // Compact: 将未消费数据前移到 buffer 起点，回收已消费空间
            if (_offset > 0)
            {
                if (_count > 0)
                    Array.Copy(_buffer, _offset, _buffer, 0, _count);
                _offset = 0;
                required = _count + additional;
            }

            // 扩容（翻倍策略，保证摊还 O(1)）
            if (required > _buffer.Length)
            {
                var newSize = _buffer.Length == 0 ? 256 : _buffer.Length;
                while (newSize < required)
                    newSize *= 2;
                Array.Resize(ref _buffer, newSize);
            }
        }

        public void Reset()
        {
            _offset = 0;
            _count = 0;
        }

        public bool TryRead(out MessagePacket packet)
        {
            packet = default;

            while (_count >= MessagePacket.HeaderSize)
            {
                var seq = new ReadOnlySequence<byte>(_buffer, _offset, _count);
                var originalLen = seq.Length;

                if (MessagePacket.TryDeserialize(ref seq, out var parsed, out var result))
                {
                    // 成功解析一帧：推进 offset，回收已消费空间
                    var consumed = originalLen - seq.Length;
                    _offset += (int)consumed;
                    _count -= (int)consumed;
                    // 零拷贝：返回内部 buffer 的切片。契约：Body 仅在下一次 Append/TryRead 前有效。
                    packet = parsed;
                    return true;
                }

                if (result == PacketParseResult.NeedMoreData)
                    return false; // 数据不足，等待下一次 Append

                // InvalidPacket：坏帧重同步到下一个魔数
                seq = new ReadOnlySequence<byte>(_buffer, _offset, _count);
                var beforeResync = seq.Length;
                Resync(ref seq);
                var skipped = beforeResync - seq.Length;
                if (skipped == 0)
                    return false;
                _offset += (int)skipped;
                _count -= (int)skipped;
                // 继续循环，从重同步位置重新解析
            }

            return false;
        }

        /// <summary>
        /// 坏帧重同步：跳过当前位置 1 字节，用 SequenceReader 向后搜索下一个魔数；
        /// 找到则定位到魔数起点；找不到则保留末尾 magic.Length-1 字节防止跨 chunk 边界漏匹配。
        /// </summary>
        private static void Resync(ref ReadOnlySequence<byte> buffer)
        {
            var magic = MessagePacket.MagicBytes;
            if (buffer.Length < magic.Length)
                return;

            // 跳过位置 0（坏帧开头），从位置 1 起搜索下一个魔数
            var reader = new SequenceReader<byte>(buffer.Slice(1));
            if (reader.TryReadTo(out ReadOnlySequence<byte> _, (ReadOnlySpan<byte>)magic, advancePastDelimiter: false))
            {
                // reader 现定位在魔数起点；UnreadSequence 从魔数开始
                buffer = reader.UnreadSequence;
                return;
            }

            // 保留末尾 magic.Length-1 字节，等待后续 chunk 拼出完整魔数
            var keep = magic.Length - 1;
            if (buffer.Length > keep)
                buffer = buffer.Slice(buffer.Length - keep);
        }

        /// <summary>
        /// 将一个 MessagePacket 序列化写入 IBufferWriter（出站编码，供测试/旧路径使用）。
        /// 生产出站路径在 ChatSessionClient.SendPacketAsync 中直接构建帧，不经此方法。
        /// </summary>
        public bool TryWrite(MessagePacket packet, IBufferWriter<byte> writer, out int written)
        {
            var totalSize = MessagePacket.HeaderSize + (int)packet.Body.Length;
            var span = writer.GetSpan(totalSize);

            if (packet.TrySerialize(ref span, out written))
            {
                writer.Advance(written);
                return true;
            }

            return false;
        }
    }
}