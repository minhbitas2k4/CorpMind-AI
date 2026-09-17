using System.Security.Cryptography;
using System.Text;
using CorpMindAI.Application.Interfaces;
using CorpMindAI.Infrastructure.Data;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CorpMindAI.Infrastructure.Jobs;

public sealed class ChunkingOutboxDispatcher : IChunkingOutboxDispatcher
{
    private const int BatchSize = 100;
    private readonly CorpMindDbContext _context;
    private readonly IChunkingOutbox _outbox;
    private readonly IJobScheduler _jobScheduler;
    private readonly ILogger<ChunkingOutboxDispatcher> _logger;

    public ChunkingOutboxDispatcher(
        CorpMindDbContext context,
        IChunkingOutbox outbox,
        IJobScheduler jobScheduler,
        ILogger<ChunkingOutboxDispatcher> logger)
    {
        _context = context;
        _outbox = outbox;
        _jobScheduler = jobScheduler;
        _logger = logger;
    }

    [DisableConcurrentExecution(timeoutInSeconds: 300)]
    [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    public async Task DispatchPendingAsync()
    {
        await RecoverMissingMessagesAsync();

        var now = DateTime.UtcNow;
        var pending = await _context.ChunkingOutboxMessages
            .Where(message => message.DispatchedAt == null && message.NextAttemptAt <= now)
            .OrderBy(message => message.CreatedAt)
            .ThenBy(message => message.Id)
            .Take(BatchSize)
            .ToListAsync();

        foreach (var message in pending)
        {
            if (!await HasMatchingCompletedSourceAsync(
                    message.DocumentId,
                    message.OcrPayloadHash))
            {
                message.DispatchedAt = DateTime.UtcNow;
                message.NextAttemptAt = message.DispatchedAt.Value;
                message.LastError = "Skipped because the completed OCR source no longer matches this request.";
                message.UpdatedAt = message.DispatchedAt.Value;
                await _context.SaveChangesAsync();
                continue;
            }

            try
            {
                var jobId = _jobScheduler.EnqueueFireAndForget<IDocumentChunkingJob>(
                    job => job.Execute(message.DocumentId));
                message.AttemptCount++;
                message.HangfireJobId = jobId;
                message.DispatchedAt = DateTime.UtcNow;
                message.NextAttemptAt = message.DispatchedAt.Value;
                message.LastError = null;
                message.UpdatedAt = message.DispatchedAt.Value;
                await _context.SaveChangesAsync();

                _logger.LogInformation(
                    "Dispatched chunking outbox message {OutboxMessageId} as Hangfire job {JobId} for DocumentId {DocumentId}.",
                    message.Id, jobId, message.DocumentId);
            }
            catch (Exception exception)
            {
                message.AttemptCount++;
                message.LastError = exception.Message;
                message.NextAttemptAt = DateTime.UtcNow;
                message.UpdatedAt = message.NextAttemptAt;
                await _context.SaveChangesAsync();

                _logger.LogWarning(
                    exception,
                    "Chunking outbox dispatch failed for message {OutboxMessageId}, DocumentId {DocumentId}; it remains pending.",
                    message.Id, message.DocumentId);
            }
        }
    }

    private async Task RecoverMissingMessagesAsync()
    {
        var completedSources = await _context.OcrResults
            .Where(result => result.Status == "completed" && result.StructuredDocumentJson != null)
            .Select(result => new { result.DocumentId, result.StructuredDocumentJson })
            .ToListAsync();

        foreach (var source in completedSources.Where(source =>
                     !string.IsNullOrWhiteSpace(source.StructuredDocumentJson)))
        {
            var hash = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(source.StructuredDocumentJson!))).ToLowerInvariant();
            await _outbox.AddPendingAsync(source.DocumentId, hash);
        }

        await _context.SaveChangesAsync();
    }

    private async Task<bool> HasMatchingCompletedSourceAsync(
        int documentId,
        string ocrPayloadHash)
    {
        var source = await _context.OcrResults
            .AsNoTracking()
            .Where(result => result.DocumentId == documentId && result.Status == "completed")
            .Select(result => result.StructuredDocumentJson)
            .SingleOrDefaultAsync();
        if (string.IsNullOrWhiteSpace(source))
            return false;

        var currentHash = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(source))).ToLowerInvariant();
        return string.Equals(currentHash, ocrPayloadHash, StringComparison.Ordinal);
    }
}
