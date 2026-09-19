using CorpMindAI.Application.Interfaces;
using CorpMindAI.Infrastructure.Data;
using CorpMindAI.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CorpMindAI.Infrastructure.Services;

public sealed class EmbeddingIndexOutbox : IEmbeddingIndexOutbox
{
    private readonly CorpMindDbContext _context;

    public EmbeddingIndexOutbox(CorpMindDbContext context) => _context = context;

    public async Task AddPendingAsync(
        int documentId,
        string chunkingRunId,
        bool reindexCompleted = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(documentId, 0);
        ArgumentException.ThrowIfNullOrWhiteSpace(chunkingRunId);
        var id = CreateId(documentId, chunkingRunId);
        var message = _context.EmbeddingIndexOutboxMessages.Local.FirstOrDefault(item => item.Id == id)
            ?? await _context.EmbeddingIndexOutboxMessages.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (message is not null)
        {
            if (reindexCompleted && message.CompletedAt is not null)
            {
                var requeueAt = DateTime.UtcNow;
                message.CompletedAt = null;
                message.DispatchedAt = null;
                message.HangfireJobId = null;
                message.NextAttemptAt = requeueAt;
                message.LastError = null;
                message.UpdatedAt = requeueAt;
            }
            return;
        }

        var now = DateTime.UtcNow;
        _context.EmbeddingIndexOutboxMessages.Add(new EmbeddingIndexOutboxMessage
        {
            Id = id,
            DocumentId = documentId,
            ChunkingRunId = chunkingRunId,
            NextAttemptAt = now,
            CreatedAt = now,
            UpdatedAt = now
        });
    }

    public async Task MarkCompletedAsync(
        int documentId,
        string chunkingRunId,
        CancellationToken cancellationToken = default)
    {
        var id = CreateId(documentId, chunkingRunId);
        var message = _context.EmbeddingIndexOutboxMessages.Local.FirstOrDefault(item => item.Id == id)
            ?? await _context.EmbeddingIndexOutboxMessages.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (message is null)
            return;

        message.CompletedAt = DateTime.UtcNow;
        message.UpdatedAt = message.CompletedAt.Value;
        message.LastError = null;
        await _context.SaveChangesAsync(cancellationToken);
    }

    internal static string CreateId(int documentId, string chunkingRunId) =>
        $"embedding-index-{documentId}-{chunkingRunId}";
}
