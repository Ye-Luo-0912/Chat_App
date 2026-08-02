using System;
using System.Buffers;

namespace Core.Buffers
{
    /// <summary>
    /// 基于 ArrayPool 的出站帧缓冲写入器。
    /// 同时实现 IBufferWriter&lt;byte&gt;（供 JSON 序列化器直写）与 IMemoryOwner&lt;byte&gt;
    /// （供传输层接管所有权，发送完成后归还池），消除出站路径的多次完整帧 byte[] 分配。
    /// 扩容时通过 ArrayPool 重新租借并复制，不依赖 Array.Resize。
    /// </summary>
    internal sealed class PooledBufferWriter : IBufferWriter<byte>, IMemoryOwner<byte>
    {
        private byte[]? _buffer;
        private int _written;

        public PooledBufferWriter(int initialCapacity = 256)
        {
            _buffer = ArrayPool<byte>.Shared.Rent(Math.Max(initialCapacity, 64));
            _written = 0;
        }

        /// <summary>已写入的有效内存（传输层发送此切片）。</summary>
        public Memory<byte> Memory
        {
            get
            {
                if (_buffer is null) throw new ObjectDisposedException(nameof(PooledBufferWriter));
                return _buffer.AsMemory(0, _written);
            }
        }

        public ReadOnlySpan<byte> WrittenSpan
        {
            get
            {
                if (_buffer is null) throw new ObjectDisposedException(nameof(PooledBufferWriter));
                return _buffer.AsSpan(0, _written);
            }
        }

        public int WrittenCount => _written;

        /// <summary>
        /// 返回已写入区域内 [start, start+length) 的可写切片，用于帧头长度字段回填。
        /// 调用方必须保证 start+length 不超过 WrittenCount。
        /// </summary>
        public Span<byte> GetWritableSlice(int start, int length)
        {
            if (_buffer is null) throw new ObjectDisposedException(nameof(PooledBufferWriter));
            if (start < 0 || length < 0 || start + length > _written)
                throw new ArgumentOutOfRangeException();
            return _buffer.AsSpan(start, length);
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            if (_buffer is null) throw new ObjectDisposedException(nameof(PooledBufferWriter));
            EnsureCapacity(sizeHint);
            return _buffer.AsMemory(_written);
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            if (_buffer is null) throw new ObjectDisposedException(nameof(PooledBufferWriter));
            EnsureCapacity(sizeHint);
            return _buffer.AsSpan(_written);
        }

        public void Advance(int count)
        {
            if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
            if (_buffer is null) throw new ObjectDisposedException(nameof(PooledBufferWriter));
            if (_written + count > _buffer.Length) throw new InvalidOperationException("Advance 超出缓冲容量");
            _written += count;
        }

        private void EnsureCapacity(int hint)
        {
            if (_buffer is null) throw new ObjectDisposedException(nameof(PooledBufferWriter));
            var required = _written + Math.Max(hint, 1);
            if (_buffer.Length >= required) return;

            var newSize = Math.Max(_buffer.Length * 2, required);
            var newBuffer = ArrayPool<byte>.Shared.Rent(newSize);
            _buffer.AsSpan(0, _written).CopyTo(newBuffer);
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = newBuffer;
        }

        public void Dispose()
        {
            if (_buffer is not null)
            {
                ArrayPool<byte>.Shared.Return(_buffer);
                _buffer = null;
                _written = 0;
            }
        }
    }
}
