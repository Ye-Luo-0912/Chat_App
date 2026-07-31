using System;
using System.Buffers;

namespace Core.Interfaces
{
    /// <summary>
    /// 协议帧 body 的 JSON 序列化器（P0-十 热路径优化）。
    /// 出站：直接写入 IBufferWriter&lt;byte&gt;，避免中间 byte[] 分配。
    /// 入站：实现内部使用 source-generated JsonTypeInfo 直接反序列化，避免运行时反射。
    /// （JsonTypeInfo&lt;T&gt; 的显式类型化重载由 Infrastructure 侧的实现暴露，
    ///  Core 接口保持与 System.Text.Json 类型解耦，便于 P0-十一 分层。）
    /// </summary>
    public interface IPacketBodySerializer
    {
        /// <summary>
        /// 将 value 直接序列化写入 writer，不产生中间 byte[] 分配。
        /// 实现内部通过 source-generated JsonTypeInfo&lt;T&gt; 走无反射路径。
        /// </summary>
        void Serialize<T>(IBufferWriter<byte> writer, T? value);

        /// <summary>
        /// 从 body 直接反序列化为 T。单段 sequence 走 span 重载零分配；
        /// 多段回退 ToArray。实现内部使用 source-generated JsonTypeInfo&lt;T&gt;。
        /// </summary>
        T? Deserialize<T>(in ReadOnlySequence<byte> body);
    }
}
