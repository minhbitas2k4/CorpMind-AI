using CorpMindAI.Application.Interfaces;
using CorpMindAI.Domain.Entities;
using CorpMindAI.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace CorpMindAI.Infrastructure.Repositories
{
    public class DocumentRepository : IDocumentRepository
    {
        private readonly CorpMindDbContext _context;

        public DocumentRepository(CorpMindDbContext context)
        {
            _context = context;
        }

        public async Task AddAsync(Document document, CancellationToken cancellationToken = default)
        {
            await _context.Documents.AddAsync(document, cancellationToken);
        }

        public async Task AddRangeAsync(IEnumerable<Document> documents, CancellationToken cancellationToken = default)
        {
            await _context.Documents.AddRangeAsync(documents, cancellationToken);
        }

        public async Task<Document?> GetByIdAsync(int id, CancellationToken cancellationToken = default)
        {
            return await _context.Documents
                .AsNoTracking()
                .Include(d => d.UploadedBy)
                .Include(d => d.Department)
                .FirstOrDefaultAsync(d => d.Id == id, cancellationToken);
        }

        public async Task<Document?> GetByIdWithOcrResultAsync(int id, CancellationToken cancellationToken = default)
        {
            return await _context.Documents
                .AsNoTracking()
                .Include(d => d.OcrResult)
                .FirstOrDefaultAsync(d => d.Id == id, cancellationToken);
        }

        public async Task<IReadOnlyList<Document>> GetByDepartmentAsync(
            int departmentId,
            int page = 1,
            int pageSize = 20,
            CancellationToken cancellationToken = default)
        {
            return await _context.Documents
                .AsNoTracking()
                .Where(d => d.DepartmentId == departmentId)
                .OrderByDescending(d => d.CreatedAt)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync(cancellationToken);
        }

        public async Task AddOcrResultAsync(OcrResult ocrResult, CancellationToken cancellationToken = default)
        {
            await _context.OcrResults.AddAsync(ocrResult, cancellationToken);
        }

        public async Task<OcrResult?> GetOcrResultByDocumentIdAsync(int documentId, CancellationToken cancellationToken = default)
        {
            return await _context.OcrResults
                .AsNoTracking()
                .FirstOrDefaultAsync(o => o.DocumentId == documentId, cancellationToken);
        }
    }
}
