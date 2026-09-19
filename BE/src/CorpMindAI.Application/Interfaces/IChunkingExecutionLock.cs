namespace CorpMindAI.Application.Interfaces;

// Serializes chunking and vector-index activation for the same document across workers.
public interface IChunkingExecutionLock
{
    Task<IAsyncDisposable> AcquireAsync(
        int documentId,
        CancellationToken cancellationToken = default);
}
