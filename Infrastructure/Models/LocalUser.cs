using Core.Contracts.Auth;
using Infrastructure.Models;

namespace Infrastructure.Data
{
    public class LocalUser
    {
        public long UserId { get; set; }
        public string Username { get; set; } = string.Empty;
        public string? AvatarUrl { get; set; }
        public DateTime LastLoginTime { get; set; }

        // 用户画像快照（登录时由服务端直接下发，无需再请求 /profile）
        public string? Email { get; set; }
        public string? Signature { get; set; }
        public bool Gender { get; set; }
        public string? Region { get; set; }
        public UserStatus Status { get; set; }
        public DateTimeOffset? PreviousLoginDate { get; set; }

        public List<LocalFriend> Friends { get; set; } = [];
    }
}