using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using CorpMindAI.Domain.Entities;
using CorpMindAI.Application.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;

namespace CorpMindAI.Infrastructure.Services
{
    public class JwtService : IJwtService
    {
        private readonly IConfiguration _configuration;
        private readonly string _secretKey;
        private readonly string _issuer;
        private readonly string _audience;
        private readonly int _accessTokenExpirationMinutes;

        public JwtService(IConfiguration configuration)
        {
            _configuration = configuration;
            _secretKey = configuration["JwtSettings:SecretKey"] ?? throw new InvalidOperationException("JWT Secret not configured");
            _issuer = configuration["JwtSettings:Issuer"] ?? "CorpMindAI";
            _audience = configuration["JwtSettings:Audience"] ?? "CorpMindAI.Client";
            _accessTokenExpirationMinutes = int.Parse(configuration["JwtSettings:AccessTokenExpirationMinutes"] ?? "60"); 
        }

        public string GenerateToken(User user) 
        {
            var tokenHandler = new JwtSecurityTokenHandler();
            var key = Encoding.UTF8.GetBytes(_secretKey);

            // Tạo claims danh tính
            var claims = new List<Claim>
            {
                new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
                new Claim(JwtRegisteredClaimNames.Email, user.Email),
                new Claim(JwtRegisteredClaimNames.Name, user.FullName),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
                new Claim("token_version", user.TokenVersion.ToString()),
                new Claim("department_id", user.DepartmentId?.ToString() ?? string.Empty)
            };

            foreach (var userRole in user.UserRoles)
            {
                // Claim DepartmentRole lưu dưới định dạng standard Role:DepartmentId
                claims.Add(new Claim("DepartmentRole", $"{userRole.Role.RoleName}:{userRole.DepartmentId}"));
                claims.Add(new Claim(ClaimTypes.Role, userRole.Role.RoleName));
            }

            var tokenDescriptor = new SecurityTokenDescriptor
            {
                Subject = new ClaimsIdentity(claims),
                Expires = DateTime.UtcNow.AddMinutes(_accessTokenExpirationMinutes),
                Issuer = _issuer,
                Audience = _audience,
                SigningCredentials = new SigningCredentials(
                    new SymmetricSecurityKey(key),
                    SecurityAlgorithms.HmacSha256Signature
                )
            };

            var token = tokenHandler.CreateToken(tokenDescriptor);
            return tokenHandler.WriteToken(token);
        }
    }
}
