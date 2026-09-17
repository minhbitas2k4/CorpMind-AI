using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;


namespace CorpMindAI.Application.Interfaces
{
    public interface IUnitOfWork : IDisposable
    {
        // Quản lý các Repository
        IUserRepository UserRepo { get; }
        IDocumentRepository DocumentRepo { get; }
        IChunkRepository ChunkRepo { get; }

        // Hàm quyết định việc lưu dữ liệu xuống DB
        Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
        Task BeginTransactionAsync();
        Task CommitTransactionAsync();
        Task RollbackTransactionAsync();
        void ClearTrackedChanges();
    }
}
