using CorpMindAI.Application.Interfaces;
using Hangfire;
using Microsoft.Extensions.Logging;

namespace CorpMindAI.Infrastructure.Jobs;

public sealed class DocumentChunkingJob : IDocumentChunkingJob
{
    private readonly IDocumentChunkingOrchestrator _orchestrator;
    private readonly ILogger<DocumentChunkingJob> _logger;

    public DocumentChunkingJob(IDocumentChunkingOrchestrator orchestrator, ILogger<DocumentChunkingJob> logger)
    {
        _orchestrator = orchestrator;
        _logger = logger;
    }

    [AutomaticRetry(Attempts = 3, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    public async Task Execute(int documentId)
    {
        _logger.LogInformation("Starting chunking job for DocumentId {DocumentId}.", documentId);
        await _orchestrator.ExecuteAsync(documentId);
    }
}
