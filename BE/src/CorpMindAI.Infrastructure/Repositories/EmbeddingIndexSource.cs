using CorpMindAI.Application.Interfaces;
using CorpMindAI.Application.Services;
using CorpMindAI.Domain.Entities;
using CorpMindAI.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace CorpMindAI.Infrastructure.Repositories;

public sealed class EmbeddingIndexSource : IEmbeddingIndexSource
{
    private readonly CorpMindDbContext _context;

    public EmbeddingIndexSource(CorpMindDbContext context) => _context = context;

    public async Task<IReadOnlyList<EmbeddingSourceChunk>> GetChunksForRunAsync(
        int documentId,
        string chunkingRunId,
        CancellationToken cancellationToken = default)
    {
        var candidates = await _context.ChildChunks
            .AsNoTracking()
            .Where(chunk =>
                chunk.DocumentId == documentId &&
                chunk.ChunkingRunId == chunkingRunId &&
                chunk.ChunkingRun.Status == ChunkingRunStatuses.Completed &&
                chunk.ChunkingRun.IsActive)
            .OrderBy(chunk => chunk.ParentChunkId)
            .ThenBy(chunk => chunk.Ordinal)
            .Select(chunk => new
            {
                chunk.Id,
                chunk.DocumentId,
                chunk.DepartmentId,
                chunk.ChunkingRunId,
                SourceContentHash = chunk.ChunkingRun.SourceContentHash,
                SourceSchemaVersion = chunk.ChunkingRun.SourceSchemaVersion,
                ChunkerVersion = chunk.ChunkingRun.ChunkerVersion,
                chunk.ContextualizedContent,
                chunk.RawContent,
                chunk.SectionPath
            })
            .ToListAsync(cancellationToken);

        return candidates
            .Where(chunk => EmbeddingChunkSearchability.IsSearchable(chunk.RawContent, chunk.SectionPath))
            .Select(chunk => new EmbeddingSourceChunk(
                chunk.Id,
                chunk.DocumentId,
                chunk.DepartmentId,
                chunk.ChunkingRunId,
                chunk.SourceContentHash,
                chunk.SourceSchemaVersion,
                chunk.ChunkerVersion,
                chunk.ContextualizedContent))
            .ToArray();
    }

    public async Task<IReadOnlyDictionary<string, SemanticSearchChunk>> GetChunksByIdsAsync(
        IReadOnlyCollection<string> childChunkIds,
        int departmentId,
        CancellationToken cancellationToken = default)
    {
        if (childChunkIds.Count == 0)
            return new Dictionary<string, SemanticSearchChunk>(StringComparer.Ordinal);

        var chunks = await _context.ChildChunks
            .AsNoTracking()
            .Where(chunk => childChunkIds.Contains(chunk.Id) && chunk.DepartmentId == departmentId)
            .ToListAsync(cancellationToken);
        return chunks
            .Select(chunk => new SemanticSearchChunk(
                chunk.Id,
                chunk.DocumentId,
                chunk.DepartmentId,
                chunk.ParentChunkId,
                chunk.SectionPath,
                chunk.RawContent,
                chunk.PageFrom,
                chunk.PageTo,
                chunk.ComponentIds))
            .ToDictionary(chunk => chunk.Id, StringComparer.Ordinal);
    }
}
