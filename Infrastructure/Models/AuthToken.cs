namespace Infrastructure.Data
{
    public class AuthToken
    {
        public int Id { get; set; }
        public long UserId { get; set; }
        public string AccessToken { get; set; } = string.Empty;
        public DateTime AccessTokenExpires { get; set; }
        public string RefreshToken { get; set; } = string.Empty;
        public DateTime RefreshTokenExpires { get; set; }

        /// <summary>登录会话唯一标识，对应服务端 Redis SessionRecord。</summary>
        public string? SessionId { get; set; }

        /// <summary>
        /// 设备指纹哈希（存为有符号 long，二进制位模式与 ulong 相同）。
        /// 读取时用 unchecked((ulong)value) 转回 ulong。
        /// </summary>
        public long? DeviceIdHash { get; set; }
    }
}