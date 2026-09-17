using System.Linq.Expressions;
using CorpMindAI.Application.Interfaces;
using CorpMindAI.Domain.Entities;
using CorpMindAI.Infrastructure.Jobs;
using CorpMindAI.Infrastructure.Services;
using CorpMindAI.Tests.Support.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CorpMindAI.Tests.Component.Chunking;

public sealed class ChunkingOutboxTests
{
    [Fact]
    [Trait("TestType", "Component")]
    public async Task Explicit_ocr_request_reopens_a_dispatched_message_but_recovery_does_not()
    {
        var options = new DbContextOptionsBuilder<CorpMindAI.Infrastructure.Data.CorpMindDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var context = new CorpMindAI.Infrastructure.Data.CorpMindDbContext(options);
        context.ChunkingOutboxMessages.Add(new CorpMindAI.Infrastructure.Data.Entities.ChunkingOutboxMessage
        {
            Id = "chunking-91-payload-a",
            DocumentId = 91,
            OcrPayloadHash = "payload-a",
            DispatchedAt = DateTime.UtcNow,
            HangfireJobId = "old-job",
            NextAttemptAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();
        var outbox = new ChunkingOutbox(context);

        await outbox.AddPendingAsync(91, "payload-a");
        Assert.NotNull((await context.ChunkingOutboxMessages.SingleAsync()).DispatchedAt);

        await outbox.AddPendingAsync(91, "payload-a", redispatchExisting: true);
        var reopened = await context.ChunkingOutboxMessages.SingleAsync();
        Assert.Null(reopened.DispatchedAt);
        Assert.Null(reopened.HangfireJobId);
        Assert.Null(reopened.LastError);
    }

    [Fact]
    [Trait("TestType", "Component")]
    public async Task Dispatcher_retries_failed_enqueue_and_does_not_dispatch_twice()
    {
        var options = new DbContextOptionsBuilder<CorpMindAI.Infrastructure.Data.CorpMindDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var context = new CorpMindAI.Infrastructure.Data.CorpMindDbContext(options);
        context.Departments.Add(new Department { Id = 72, Name = "Outbox unit" });
        context.Documents.Add(new Document
        {
            Id = 7201, OriginalFileName = "outbox.pdf", Title = "Outbox",
            StorageKey = "documents/outbox.pdf", DepartmentId = 72,
            Status = "completed", OcrStatus = "completed"
        });
        context.OcrResults.Add(new OcrResult
        {
            DocumentId = 7201, OverallLevel = "high", ComponentsJson = "[]",
            ValidationErrorsJson = "[]", Status = "completed",
            StructuredDocumentJson =
                "{\"schema_version\":\"1.0\",\"document_id\":\"7201\",\"total_pages\":0,\"pages\":[]}"
        });
        await context.SaveChangesAsync();

        var scheduler = new FailOnceJobScheduler();
        var dispatcher = new ChunkingOutboxDispatcher(
            context, new ChunkingOutbox(context), scheduler,
            NullLogger<ChunkingOutboxDispatcher>.Instance);

        await dispatcher.DispatchPendingAsync();
        await dispatcher.DispatchPendingAsync();
        Assert.NotNull((await context.ChunkingOutboxMessages.SingleAsync()).DispatchedAt);
        Assert.Equal(2, scheduler.Attempts);
        await dispatcher.DispatchPendingAsync();

        var message = await context.ChunkingOutboxMessages.SingleAsync();
        Assert.Equal(2, message.AttemptCount);
        Assert.NotNull(message.DispatchedAt);
        Assert.Equal(new[] { 7201 }, scheduler.SuccessfulDocumentIds);
        Assert.Equal(2, scheduler.Attempts);
    }

    [SkippableFact]
    [Trait("Category", "PostgreSqlIntegration")]
    [Trait("TestType", "Integration")]
    public async Task Failed_enqueue_is_recovered_without_creating_a_duplicate_message_or_job()
    {
        await using var database = await PostgreSqlIntegrationDatabase.CreateAsync();
        await using var context = database.CreateContext();
        context.Departments.Add(new Department { Id = 71, Name = "Outbox" });
        context.Documents.Add(new Document
        {
            Id = 7101,
            OriginalFileName = "outbox.pdf",
            Title = "Outbox recovery",
            StorageKey = "documents/outbox.pdf",
            FileType = "application/pdf",
            DepartmentId = 71,
            Status = "completed",
            OcrStatus = "completed"
        });
        context.OcrResults.Add(new OcrResult
        {
            DocumentId = 7101,
            TotalPages = 1,
            PageAverageConfidence = 1,
            OverallLevel = "high",
            ComponentsJson = "[]",
            ValidationErrorsJson = "[]",
            SchemaVersion = "1.0",
            StructuredDocumentJson =
                "{\"schema_version\":\"1.0\",\"document_id\":\"7101\",\"total_pages\":1,\"pages\":[]}",
            Status = "completed"
        });
        await context.SaveChangesAsync();

        var scheduler = new FailOnceJobScheduler();
        var dispatcher = new ChunkingOutboxDispatcher(
            context,
            new ChunkingOutbox(context),
            scheduler,
            NullLogger<ChunkingOutboxDispatcher>.Instance);

        await dispatcher.DispatchPendingAsync();

        var failed = await context.ChunkingOutboxMessages.SingleAsync();
        Assert.Null(failed.DispatchedAt);
        Assert.Equal(1, failed.AttemptCount);
        Assert.NotNull(failed.LastError);

        await dispatcher.DispatchPendingAsync();
        await dispatcher.DispatchPendingAsync();

        context.ChangeTracker.Clear();
        var dispatched = await context.ChunkingOutboxMessages.SingleAsync();
        Assert.NotNull(dispatched.DispatchedAt);
        Assert.Equal(2, dispatched.AttemptCount);
        Assert.Null(dispatched.LastError);
        Assert.Equal("job-1", dispatched.HangfireJobId);
        Assert.Equal(2, scheduler.Attempts);
        Assert.Equal(new[] { 7101 }, scheduler.SuccessfulDocumentIds);
    }

    private sealed class FailOnceJobScheduler : IJobScheduler
    {
        public int Attempts { get; private set; }
        public List<int> SuccessfulDocumentIds { get; } = new();

        public string EnqueueFireAndForget<TJob>(Expression<Func<TJob, Task>> methodCall)
            where TJob : class
        {
            Attempts++;
            if (Attempts == 1)
                throw new InvalidOperationException("forced enqueue failure");

            var call = Assert.IsAssignableFrom<MethodCallExpression>(methodCall.Body);
            var documentId = Expression.Lambda<Func<int>>(call.Arguments[0]).Compile()();
            SuccessfulDocumentIds.Add(documentId);
            return $"job-{SuccessfulDocumentIds.Count}";
        }
    }
}
