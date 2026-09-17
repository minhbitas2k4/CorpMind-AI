using CorpMindAI.Application.Interfaces;
using CorpMindAI.Domain.Entities;
using CorpMindAI.Infrastructure.Data;
using CorpMindAI.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CorpMindAI.Tests.Component.Chunking;

[Trait("TestType", "Component")]
public sealed class ChunkingLifecycleIntegrationTests
{
    [Fact]
    public async Task Failed_run_keeps_the_previous_active_run_and_retry_lookup_is_stable()
    {
        await using var context = new CorpMindDbContext(new DbContextOptionsBuilder<CorpMindDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var department = new Department { Id = 9, Name = "Knowledge" };
        var document = new Document { Id = 77, DepartmentId = 9, Department = department, Title = "Policy", OriginalFileName = "policy.pdf", StorageKey = "policy.pdf" };
        context.Departments.Add(department);
        context.Documents.Add(document);
        await context.SaveChangesAsync();
        var repository = new ChunkRepository(context);
        var active = Run("active", document, ChunkingRunStatuses.Completed, true);
        await repository.AddAsync(active);
        await context.SaveChangesAsync();

        var failed = Run("failed", document, ChunkingRunStatuses.Failed, false, "failed-source");
        failed.ErrorMessage = "validation failed";
        await repository.AddAsync(failed);
        await context.SaveChangesAsync();

        var current = await repository.GetActiveRunAsync(document.Id);
        var sameVersion = await repository.GetByVersionAsync(document.Id, "failed-source", "1.0", "1.0", "{}");

        Assert.Equal("active", current!.Id);
        Assert.Equal("failed", sameVersion!.Id);
        Assert.Equal(2, await context.ChunkingRuns.CountAsync());
    }

    private static ChunkingRun Run(string id, Document document, string status, bool active, string source = "source") => new()
    {
        Id = id, DocumentId = document.Id, DepartmentId = document.DepartmentId,
        SourceContentHash = source, SourceSchemaVersion = "1.0", ChunkerVersion = "1.0",
        ConfigurationJson = "{}", Status = status, IsActive = active
    };
}
