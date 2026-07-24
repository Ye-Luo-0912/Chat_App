using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Core.Models.DTO
{
    public class ErrorResponseDto
    {
        public byte StatusCode { get; set; }
        public string ErrorMessage { get; set; } = string.Empty;
    }
}
