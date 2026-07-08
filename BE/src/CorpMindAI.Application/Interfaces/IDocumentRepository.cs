using CorpMindAI.Domain.Entities;

namespace CorpMindAI.Application.Interfaces
{
    public interface IDocumentRepository
    {
        Task AddAsync(Document document, CancellationToken cancellationToken = default);

        Task AddRangeAsync(IEnumerable<Document> documents, CancellationToken cancellationToken = default);

        Task<Document?> GetByIdAsync(int id, CancellationToken cancellationToken = default);

        Task<IReadOnlyList<Document>> GetByDepartmentAsync(
            int departmentId,
            int page = 1,
            int pageSize = 20,
            CancellationToken cancellationToken = default);
    }
}
