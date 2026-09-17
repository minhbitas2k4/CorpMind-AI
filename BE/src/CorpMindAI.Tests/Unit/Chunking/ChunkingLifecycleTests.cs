using CorpMindAI.Domain.Entities;
using CorpMindAI.Infrastructure.Services;
using CorpMindAI.Infrastructure.Jobs;
using CorpMindAI.Application.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CorpMindAI.Tests.Unit.Chunking;

[Trait("TestType", "Unit")]
public sealed class ChunkingLifecycleTests
{
    [Fact]
    public void Lifecycle_statuses_are_stable_and_complete()
    {
        Assert.Equal("not_started", ChunkingRunStatuses.NotStarted);
        Assert.Equal("queued", ChunkingRunStatuses.Queued);
        Assert.Equal("processing", ChunkingRunStatuses.Processing);
        Assert.Equal("completed", ChunkingRunStatuses.Completed);
        Assert.Equal("failed", ChunkingRunStatuses.Failed);
    }

    [Fact]
    public void Token_counter_uses_the_configured_chunking_encoding()
    {
        var counter = new ChunkingTokenCounter();
        Assert.Equal("cl100k_base", ChunkingTokenCounter.EncodingName);
        Assert.Equal(4, counter.Count("Hello, world!"));
        Assert.Equal(4, counter.Count("Hello, world!"));
        Assert.Equal(0, counter.Count("  "));
    }

    [Fact]
    public async Task Background_job_forwards_only_the_document_id()
    {
        var orchestrator = new RecordingOrchestrator();
        var job = new DocumentChunkingJob(orchestrator, NullLogger<DocumentChunkingJob>.Instance);

        await job.Execute(42);

        Assert.Equal(42, orchestrator.DocumentId);
    }

    private sealed class RecordingOrchestrator : IDocumentChunkingOrchestrator
    {
        public int DocumentId { get; private set; }
        public Task ExecuteAsync(int documentId, CancellationToken cancellationToken = default)
        {
            DocumentId = documentId;
            return Task.CompletedTask;
        }
    }
}
