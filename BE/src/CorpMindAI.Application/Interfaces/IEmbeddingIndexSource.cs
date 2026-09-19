namespace CorpMindAI.Application.Interfaces;

public interface IEmbeddingIndexSource
{
    Task<IReadOnlyList<EmbeddingSourceChunk>> GetChunksForRunAsync(
        int documentId,
        string chunkingRunId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<string, SemanticSearchChunk>> GetChunksByIdsAsync(
        IReadOnlyCollection<string> childChunkIds,
        int departmentId,
        CancellationToken cancellationToken = default);
}

public sealed record EmbeddingSourceChunk(
    string Id,
    int DocumentId,
    int DepartmentId,
    string ChunkingRunId,
    string SourceContentHash,
    string SourceSchemaVersion,
    string ChunkerVersion,
    string ContextualizedContent);

public sealed record SemanticSearchChunk(
    string Id,
    int DocumentId,
    int DepartmentId,
    string ParentChunkId,
    IReadOnlyList<string> SectionPath,
    string RawContent,
    int? PageFrom,
    int? PageTo,
    IReadOnlyList<string> ComponentIds);
