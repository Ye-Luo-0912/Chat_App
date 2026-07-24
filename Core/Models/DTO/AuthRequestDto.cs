using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Core.Models.DTO
{
    public sealed class AuthRequestDto
    {
        public string AccessToken { get; set; } = string.Empty;

        /// <summary>客户端用户 ID，服务端无需重复查询 Redis。</summary>
        public long UserId { get; set; }

        /// <summary>登录会话 ID，用于 TCP 与 HTTP 会话关联。</summary>
        public string? SessionId { get; set; }

        /// <summary>设备指纹哈希，TCP 侧整数比对，防止令牌被盗用。</summary>
        public ulong? DeviceIdHash { get; set; }
    }
}
