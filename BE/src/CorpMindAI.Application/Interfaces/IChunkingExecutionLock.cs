namespace CorpMindAI.Application.Interfaces;

// Serializes chunking executions for the same document across workers.
public interface IChunkingExecutionLock
{
    Task<IAsyncDisposable> AcquireAsync(
        int documentId,
        CancellationToken cancellationToken = default);
}
