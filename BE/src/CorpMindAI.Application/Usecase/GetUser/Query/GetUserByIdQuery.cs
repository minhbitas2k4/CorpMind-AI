using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CorpMindAI.Application.Interfaces;
using CorpMindAI.Domain.Entities;
using MediatR;
using CorpMindAI.Application.DTOs.Common;
using CorpMindAI.Application.DTOs.UserDto;

namespace CorpMindAI.Application.Usecase.GetUser.Query
{
    public record GetUserByIdQuery(int Id, int RequestorUserId, int DepartmentId) : IRequest<ServiceResult<UserDTO>>;

    public class GetUserByIdQueryHandler : IRequestHandler<GetUserByIdQuery, ServiceResult<UserDTO>>
    {
        private readonly IUserRepository _userRepo;

        public GetUserByIdQueryHandler(IUserRepository userRepo)
        {
            _userRepo = userRepo;
        }

        public async Task<ServiceResult<UserDTO>> Handle(GetUserByIdQuery query, CancellationToken cancellationToken)
        {
            var targetUser = await _userRepo.GetUserById(query.Id);
            if (targetUser == null)
            {
                return ServiceResult<UserDTO>.Fail("User not found.");
            }

            var requestor = await _userRepo.GetUserById(query.RequestorUserId);
            if (requestor == null)
            {
                return ServiceResult<UserDTO>.Fail("Access denied: Invalid requestor.");
            }

            bool hasRequiredRoleInDepartment = requestor.UserRoles.Any(ur =>
                ur.DepartmentId == query.DepartmentId &&
                (ur.Role.RoleName == "knowledge_contributor" ||
                 ur.Role.RoleName == "knowledge_manager" ||
                 ur.Role.RoleName == "system_admin"));

            bool targetBelongsToDepartment = targetUser.DepartmentId == query.DepartmentId;

            if (!hasRequiredRoleInDepartment || !targetBelongsToDepartment)
            {
                return ServiceResult<UserDTO>.Fail("Access denied: You do not have permission to view this user's details.");
            }

            var result = new UserDTO
            {
                Id = targetUser.Id,
                Email = targetUser.Email,
                FullName = targetUser.FullName
            };

            return ServiceResult<UserDTO>.Ok(result, "User retrieved successfully.");
        }
    }
}
