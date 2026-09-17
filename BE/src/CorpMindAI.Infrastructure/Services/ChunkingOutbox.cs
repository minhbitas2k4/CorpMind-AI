using CorpMindAI.Application.Interfaces;
using CorpMindAI.Infrastructure.Data;
using CorpMindAI.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CorpMindAI.Infrastructure.Services;

public sealed class ChunkingOutbox : IChunkingOutbox
{
    private readonly CorpMindDbContext _context;

    public ChunkingOutbox(CorpMindDbContext context) => _context = context;

    public async Task AddPendingAsync(
        int documentId,
        string ocrPayloadHash,
        bool redispatchExisting = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ocrPayloadHash);
        var id = CreateId(documentId, ocrPayloadHash);

        var message = _context.ChunkingOutboxMessages.Local.FirstOrDefault(item => item.Id == id)
            ?? await _context.ChunkingOutboxMessages.SingleOrDefaultAsync(
                item => item.Id == id,
                cancellationToken);
        if (message is not null)
        {
            if (redispatchExisting && message.DispatchedAt is not null)
            {
                var redispatchAt = DateTime.UtcNow;
                message.DispatchedAt = null;
                message.HangfireJobId = null;
                message.LastError = null;
                message.NextAttemptAt = redispatchAt;
                message.UpdatedAt = redispatchAt;
            }

            return;
        }

        var now = DateTime.UtcNow;
        _context.ChunkingOutboxMessages.Add(new ChunkingOutboxMessage
        {
            Id = id,
            DocumentId = documentId,
            OcrPayloadHash = ocrPayloadHash,
            NextAttemptAt = now,
            CreatedAt = now,
            UpdatedAt = now
        });
    }

    public void DiscardPending(int documentId, string ocrPayloadHash)
    {
        var id = CreateId(documentId, ocrPayloadHash);
        var tracked = _context.ChunkingOutboxMessages.Local.FirstOrDefault(message => message.Id == id);
        if (tracked is null)
            return;

        var entry = _context.Entry(tracked);
        if (entry.State == EntityState.Added)
            _context.ChunkingOutboxMessages.Remove(tracked);
        else
        {
            entry.CurrentValues.SetValues(entry.OriginalValues);
            entry.State = EntityState.Unchanged;
        }
    }

    internal static string CreateId(int documentId, string ocrPayloadHash) =>
        $"chunking-{documentId}-{ocrPayloadHash}";
}
