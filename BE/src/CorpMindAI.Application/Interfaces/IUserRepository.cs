using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CorpMindAI.Domain.Entities;

namespace CorpMindAI.Application.Interfaces
{
    public interface IUserRepository
    {
        Task<User?> GetUserAsync(string email);
        Task<User?> GetUserById(int id);
    }
}
