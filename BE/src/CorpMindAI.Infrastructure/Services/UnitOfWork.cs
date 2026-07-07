using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CorpMindAI.Application.Interfaces;
using CorpMindAI.Domain.Entities;
using CorpMindAI.Infrastructure.Data;
using CorpMindAI.Infrastructure.Repositories;

namespace CorpMindAI.Infrastructure.Services
{
    public class UnitOfWork : IUnitOfWork
    {
        private readonly CorpMindDbContext _context;
        public IUserRepository UserRepo { get; }

        public UnitOfWork(IUserRepository users, CorpMindDbContext context)
        {
            _context = context;
            UserRepo = users;
        }

        public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            return await _context.SaveChangesAsync(cancellationToken);
        }

        public async Task BeginTransactionAsync() => await _context.Database.BeginTransactionAsync();
        public async Task CommitTransactionAsync() => await _context.Database.CommitTransactionAsync();
        public async Task RollbackTransactionAsync() => await _context.Database.RollbackTransactionAsync();

        public void Dispose() => _context.Dispose();
    }
}
