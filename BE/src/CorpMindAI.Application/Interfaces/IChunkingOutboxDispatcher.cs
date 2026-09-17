namespace CorpMindAI.Application.Interfaces;

public interface IChunkingOutboxDispatcher
{
    Task DispatchPendingAsync();
}
