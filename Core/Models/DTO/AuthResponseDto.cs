using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Core.Models.DTO
{
    public sealed class AuthResponseDto
    {
        public bool Success { get; set; }

        public long? UserId { get; set; }

        public string? ErrorMessage { get; set; }

        public string? SessionId { get; set; }

        public ulong? DeviceIdHash { get; set; }

        public string? DeviceId { get; set; }

        public string? ResumeToken { get; set; }
    }
}
