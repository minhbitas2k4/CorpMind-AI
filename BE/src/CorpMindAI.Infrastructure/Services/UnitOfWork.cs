using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CorpMindAI.Application.Interfaces;
using CorpMindAI.Domain.Entities;
using CorpMindAI.Infrastructure.Data;
using CorpMindAI.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore.Storage;

namespace CorpMindAI.Infrastructure.Services
{
    public class UnitOfWork : IUnitOfWork
    {
        private readonly CorpMindDbContext _context;
        private IDbContextTransaction? _transaction;
        public IUserRepository UserRepo { get; }
        public IDocumentRepository DocumentRepo { get; }
        public IChunkRepository ChunkRepo { get; }

        public UnitOfWork(
            IUserRepository users,
            IDocumentRepository documents,
            IChunkRepository chunks,
            CorpMindDbContext context)
        {
            _context = context;
            UserRepo = users;
            DocumentRepo = documents;
            ChunkRepo = chunks;
        }

        public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            return await _context.SaveChangesAsync(cancellationToken);
        }

        public async Task BeginTransactionAsync()
        {
            if (_transaction is not null)
                throw new InvalidOperationException("A unit-of-work transaction is already active.");

            _transaction = await _context.Database.BeginTransactionAsync();
        }

        public async Task CommitTransactionAsync()
        {
            var transaction = _transaction
                ?? throw new InvalidOperationException("No unit-of-work transaction is active.");
            try
            {
                await transaction.CommitAsync();
            }
            finally
            {
                await transaction.DisposeAsync();
                _transaction = null;
            }
        }

        public async Task RollbackTransactionAsync()
        {
            var transaction = _transaction;
            if (transaction is null)
                return;

            try
            {
                await transaction.RollbackAsync();
            }
            finally
            {
                await transaction.DisposeAsync();
                _transaction = null;
            }
        }

        public void ClearTrackedChanges() => _context.ChangeTracker.Clear();

        public void Dispose()
        {
            _transaction?.Dispose();
            _context.Dispose();
        }
    }
}
