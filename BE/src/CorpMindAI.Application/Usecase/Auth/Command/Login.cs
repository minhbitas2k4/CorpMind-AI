using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CorpMindAI.Application.DTOs.Auth;
using CorpMindAI.Application.DTOs.Common;
using CorpMindAI.Application.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;

namespace CorpMindAI.Application.Usecase.Auth.Command
{
    public record LoginCommand (AuthRequestDto dto) : IRequest<ServiceResult<AuthResponseDto>>;
    
    public class LoginCommandHandler : IRequestHandler<LoginCommand, ServiceResult<AuthResponseDto>>
    {
        private readonly IUserRepository _userRepo;
        private readonly IJwtService _jwtService;
        private readonly ILogger<LoginCommandHandler> _logger;

        public LoginCommandHandler(IUserRepository userRepo, IJwtService jwtService, ILogger<LoginCommandHandler> logger)
        {
            _userRepo = userRepo;
            _jwtService = jwtService;
            _logger = logger;
        }

        public async Task<ServiceResult<AuthResponseDto>> Handle(LoginCommand command, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(command.dto.Email) || string.IsNullOrWhiteSpace(command.dto.Password))
            {
                return ServiceResult<AuthResponseDto>.Fail("Email and password are required.");
            }

            var user = await _userRepo.GetUserAsync(command.dto.Email);

            // Xác thực mật khẩu thông qua BCrypt an toàn
            if (user == null || !BCrypt.Net.BCrypt.Verify(command.dto.Password, user.PasswordHash))
            {
                return ServiceResult<AuthResponseDto>.Fail("Invalid email or password.");
            }

            if (user.Status != "active")
            {
                return ServiceResult<AuthResponseDto>.Fail("Your account has been locked!");
            }

            var token = _jwtService.GenerateToken(user);
            _logger.LogInformation("User logged in successfully: {Email}", user.Email);

            var returnDto = new AuthResponseDto
            {
                AccountId = user.Id,
                FullName = user.FullName,
                Email = user.Email,
                Roles = user.UserRoles
                            .Select(ur => $"{ur.Role.RoleName}:{ur.DepartmentId}")
                            .ToList(),
                Token = token,
            };

            return ServiceResult<AuthResponseDto>.Ok(returnDto, "Login successful.");
        }
    }
}
