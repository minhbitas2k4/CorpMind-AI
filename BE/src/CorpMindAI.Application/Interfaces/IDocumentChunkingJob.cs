namespace CorpMindAI.Application.Interfaces;

public interface IDocumentChunkingJob
{
    Task Execute(int documentId);
}
