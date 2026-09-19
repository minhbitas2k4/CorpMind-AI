namespace CorpMindAI.Application.Interfaces;

public interface IEmbeddingIndexingJob
{
    Task Execute(int documentId, string chunkingRunId);
}
