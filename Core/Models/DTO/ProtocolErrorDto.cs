namespace Core.Models.DTO
{
    /// <summary>
    /// 服务端协议错误描述（PacketCommand.Error 的包体）。
    /// RequestId 非空时表示对应某个在途请求，应由请求方处理而非全局弹错。
    /// IsFatal 为 true 时表示连接级致命错误（如鉴权被拒），需要走鉴权失败流程。
    /// </summary>
    public sealed class ProtocolErrorDto
    {
        /// <summary>出错请求的 RequestId；服务器推送类错误（非请求响应）时为空。</summary>
        public string? RequestId { get; set; }

        /// <summary>触发错误的命令。</summary>
        public PacketCommand? Command { get; set; }

        /// <summary>错误码（服务端定义）。</summary>
        public string? ErrorCode { get; set; }

        /// <summary>人类可读的错误描述。</summary>
        public string? ErrorMessage { get; set; }

        /// <summary>是否连接级致命错误（鉴权失效等）。</summary>
        public bool IsFatal { get; set; }
    }
}
