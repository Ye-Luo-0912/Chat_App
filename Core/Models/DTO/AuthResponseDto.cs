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
    }
}
