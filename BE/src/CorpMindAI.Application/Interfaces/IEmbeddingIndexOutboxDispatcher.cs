namespace CorpMindAI.Application.Interfaces;

public interface IEmbeddingIndexOutboxDispatcher
{
    Task DispatchPendingAsync();
}
