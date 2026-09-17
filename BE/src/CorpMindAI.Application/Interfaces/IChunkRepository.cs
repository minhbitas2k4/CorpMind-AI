using CorpMindAI.Domain.Entities;

namespace CorpMindAI.Application.Interfaces;

public interface IChunkRepository
{
    Task AddAsync(ChunkingRun run, CancellationToken cancellationToken = default);

    Task<ChunkingRun?> GetActiveRunAsync(
        int documentId,
        CancellationToken cancellationToken = default);

    Task<ChunkingRun?> GetByVersionAsync(
        int documentId,
        string sourceContentHash,
        string sourceSchemaVersion,
        string chunkerVersion,
        string configurationJson,
        CancellationToken cancellationToken = default);

    Task UpdateAsync(ChunkingRun run, CancellationToken cancellationToken = default);
}
