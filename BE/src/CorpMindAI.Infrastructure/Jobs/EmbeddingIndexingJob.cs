using CorpMindAI.Application.Interfaces;
using CorpMindAI.Application.Services;
using Hangfire;
using Microsoft.Extensions.Logging;

namespace CorpMindAI.Infrastructure.Jobs;

public sealed class EmbeddingIndexingJob : IEmbeddingIndexingJob
{
    private readonly IEmbeddingIndexingService _indexing;
    private readonly ILogger<EmbeddingIndexingJob> _logger;

    public EmbeddingIndexingJob(
        IEmbeddingIndexingService indexing,
        ILogger<EmbeddingIndexingJob> logger)
    {
        _indexing = indexing;
        _logger = logger;
    }

    [Queue("embedding")]
    [DisableConcurrentExecution(timeoutInSeconds: 3600)]
    [AutomaticRetry(Attempts = 3, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    public async Task Execute(int documentId, string chunkingRunId)
    {
        _logger.LogInformation(
            "Starting embedding indexing for DocumentId {DocumentId}, ChunkingRunId {ChunkingRunId}.",
            documentId,
            chunkingRunId);
        await _indexing.ExecuteAsync(documentId, chunkingRunId);
    }
}
