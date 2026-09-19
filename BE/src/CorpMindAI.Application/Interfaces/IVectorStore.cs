namespace CorpMindAI.Application.Interfaces;

public interface IVectorStore
{
    Task EnsureCollectionAsync(CancellationToken cancellationToken = default);

    Task UpsertAsync(
        IReadOnlyList<VectorPoint> points,
        CancellationToken cancellationToken = default);

    Task ActivateRunAsync(
        string chunkingRunId,
        CancellationToken cancellationToken = default);

    Task DeleteOtherRunsAsync(
        int documentId,
        string activeChunkingRunId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<VectorSearchHit>> SearchAsync(
        IReadOnlyList<float> queryVector,
        IReadOnlyCollection<int> departmentIds,
        int limit,
        double? minimumScore = null,
        CancellationToken cancellationToken = default);
}

public sealed record VectorPoint(
    string ChildChunkId,
    int DocumentId,
    int DepartmentId,
    string ChunkingRunId,
    string SourceContentHash,
    string SourceSchemaVersion,
    string ChunkerVersion,
    string EmbeddingVersion,
    IReadOnlyList<float> Vector);

public sealed record VectorSearchHit(
    string ChildChunkId,
    int DocumentId,
    int DepartmentId,
    string ChunkingRunId,
    double Score);
