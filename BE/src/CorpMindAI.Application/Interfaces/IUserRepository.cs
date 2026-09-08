using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CorpMindAI.Domain.Entities;

namespace CorpMindAI.Application.Interfaces
{
    public sealed record UserAuthorizationState(
        string Status,
        int TokenVersion,
        int? DepartmentId,
        IReadOnlyCollection<string> DepartmentRoles);

    public interface IUserRepository
    {
        Task<User?> GetUserAsync(string email);
        Task<User?> GetUserById(int id);
        Task<UserAuthorizationState?> GetAuthorizationStateAsync(int id);
    }
}
