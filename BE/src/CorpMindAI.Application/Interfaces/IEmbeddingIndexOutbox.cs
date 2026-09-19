namespace CorpMindAI.Application.Interfaces;

public interface IEmbeddingIndexOutbox
{
    Task AddPendingAsync(
        int documentId,
        string chunkingRunId,
        bool reindexCompleted = false,
        CancellationToken cancellationToken = default);

    Task MarkCompletedAsync(
        int documentId,
        string chunkingRunId,
        CancellationToken cancellationToken = default);
}
