using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CorpMindAI.Application.DTOs.Auth
{
    public class AuthResponseDto
    {
        public string FullName { get; set; } = null!;
        public string Token { get; set; } = null!;
        public List<string> Roles { get; set; } = new List<string>();
        public int AccountId { get; set; }
        public string Email { get; set; } = null!;
    }
}
