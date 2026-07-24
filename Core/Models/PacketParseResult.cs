using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Core.Models
{
    /// <summary>
    /// 表示数据包解析结果的枚举类型，用于指示在解析数据包时可能出现的不同结果状态。
    /// </summary>
    public enum PacketParseResult : byte
    {
        /// <summary>
        /// “无结果”
        /// </summary>
        None = 0,
        /// <summary>
        /// “需要更多数据”，表示当前缓冲区中的数据不足以构成一个完整的数据包，需要继续等待更多数据的到来才能完成解析。
        /// </summary>
        NeedMoreData,
        /// <summary>
        /// “成功”，表示数据包已经成功解析，可以继续进行后续的处理。
        /// </summary>
        Success,
        /// <summary>
        /// “无效数据包”，表示当前缓冲区中的数据无法解析成一个合法的数据包，可能是因为数据格式错误、缺少必要的字段或者数据损坏等原因导致的。
        /// </summary>
        InvalidPacket
    }
}
