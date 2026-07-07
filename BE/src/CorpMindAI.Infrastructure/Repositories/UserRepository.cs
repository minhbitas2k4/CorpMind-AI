using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CorpMindAI.Domain.Entities;
using CorpMindAI.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using CorpMindAI.Application.Interfaces;

namespace CorpMindAI.Infrastructure.Repositories
{
    public class UserRepository : IUserRepository
    {
        private readonly CorpMindDbContext _context;

        public UserRepository(CorpMindDbContext context)
        {
            _context = context;
        }

        public async Task<User?> GetUserAsync(string email)
        {
            return await _context.Users
                .AsNoTracking() // Tối ưu hiệu năng bộ nhớ
                .Include(u => u.UserRoles)
                    .ThenInclude(ur => ur.Role)
                .FirstOrDefaultAsync(p => EF.Functions.ILike(p.Email, email)); 
        }

        public async Task<User?> GetUserById(int id)
        {
            return await _context.Users
                .AsNoTracking() 
                .Include(u => u.UserRoles)
                    .ThenInclude(ur => ur.Role)
                .FirstOrDefaultAsync(p => p.Id == id);
        }
    }
}
