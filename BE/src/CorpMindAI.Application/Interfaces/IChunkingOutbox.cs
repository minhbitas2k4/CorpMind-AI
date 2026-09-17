namespace CorpMindAI.Application.Interfaces;

// Records a durable request to chunk a completed OCR result. The caller
// commits the request through the current unit of work.
public interface IChunkingOutbox
{
    Task AddPendingAsync(
        int documentId,
        string ocrPayloadHash,
        bool redispatchExisting = false,
        CancellationToken cancellationToken = default);

    void DiscardPending(int documentId, string ocrPayloadHash);
}
