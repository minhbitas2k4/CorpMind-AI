using CorpMindAI.Application.Interfaces;
using CorpMindAI.Application.Settings;
using CorpMindAI.Domain.Entities;
using CorpMindAI.Infrastructure.Data;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CorpMindAI.Infrastructure.Jobs;

public sealed class EmbeddingIndexOutboxDispatcher : IEmbeddingIndexOutboxDispatcher
{
    private readonly CorpMindDbContext _context;
    private readonly IEmbeddingIndexOutbox _outbox;
    private readonly IJobScheduler _jobScheduler;
    private readonly EmbeddingIndexingOptions _options;
    private readonly ILogger<EmbeddingIndexOutboxDispatcher> _logger;

    public EmbeddingIndexOutboxDispatcher(
        CorpMindDbContext context,
        IEmbeddingIndexOutbox outbox,
        IJobScheduler jobScheduler,
        EmbeddingIndexingOptions options,
        ILogger<EmbeddingIndexOutboxDispatcher> logger)
    {
        _context = context;
        _outbox = outbox;
        _jobScheduler = jobScheduler;
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        _logger = logger;
    }

    [DisableConcurrentExecution(timeoutInSeconds: 300)]
    [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    public async Task DispatchPendingAsync()
    {
        await RecoverMissingMessagesAsync();

        var now = DateTime.UtcNow;
        var pending = await _context.EmbeddingIndexOutboxMessages
            .Where(message =>
                message.CompletedAt == null &&
                message.DispatchedAt == null &&
                message.NextAttemptAt <= now)
            .OrderBy(message => message.CreatedAt)
            .ThenBy(message => message.Id)
            .Take(_options.DispatchBatchSize)
            .ToListAsync();

        foreach (var message in pending)
        {
            try
            {
                var jobId = _jobScheduler.EnqueueFireAndForget<IEmbeddingIndexingJob>(
                    job => job.Execute(message.DocumentId, message.ChunkingRunId));
                message.AttemptCount++;
                message.HangfireJobId = jobId;
                message.DispatchedAt = DateTime.UtcNow;
                message.LastError = null;
                message.UpdatedAt = message.DispatchedAt.Value;
                await _context.SaveChangesAsync();
                _logger.LogInformation(
                    "Dispatched embedding index message {MessageId} as Hangfire job {JobId}.",
                    message.Id,
                    jobId);
            }
            catch (Exception exception)
            {
                message.AttemptCount++;
                message.LastError = exception.Message;
                message.NextAttemptAt = DateTime.UtcNow.AddMinutes(1);
                message.UpdatedAt = message.NextAttemptAt;
                await _context.SaveChangesAsync();
                _logger.LogWarning(exception, "Embedding index dispatch failed for message {MessageId}.", message.Id);
            }
        }
    }

    private async Task RecoverMissingMessagesAsync()
    {
        var activeRuns = await _context.ChunkingRuns
            .AsNoTracking()
            .Where(run => run.Status == ChunkingRunStatuses.Completed && run.IsActive)
            .Select(run => new { run.DocumentId, run.Id })
            .ToListAsync();
        foreach (var run in activeRuns)
            await _outbox.AddPendingAsync(run.DocumentId, run.Id);
        await _context.SaveChangesAsync();
    }
}
