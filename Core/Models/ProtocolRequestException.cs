using System;
using Core.Models.DTO;

namespace Core.Models
{
    /// <summary>
    /// 协议错误异常：服务端返回 Error 命令且关联了 RequestId 时，
    /// 由 ChatSessionClient 以该异常完成对应请求的 TCS，调用方可捕获并读取 Error。
    /// </summary>
    public sealed class ProtocolRequestException : Exception
    {
        public ProtocolErrorDto Error { get; }

        public ProtocolRequestException(ProtocolErrorDto error)
            : base(error.ErrorMessage ?? "协议错误")
        {
            Error = error;
        }
    }
}
