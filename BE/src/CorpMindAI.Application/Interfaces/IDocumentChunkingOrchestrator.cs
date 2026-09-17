namespace CorpMindAI.Application.Interfaces;

public interface IDocumentChunkingOrchestrator
{
    Task ExecuteAsync(int documentId, CancellationToken cancellationToken = default);
}
