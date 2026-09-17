using CorpMindAI.Application.Interfaces;
using CorpMindAI.Domain.Entities;
using CorpMindAI.Infrastructure.Data;
using CorpMindAI.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CorpMindAI.Tests.Component.Chunking;

[Trait("TestType", "Component")]
public sealed class ChunkingPersistenceTests
{
    [Fact]
    public async Task Chunking_run_graph_round_trips_versioning_and_traceability()
    {
        await using var context = CreateContext();
        var document = AddDocument(context);
        var run = CreateRun(document, isActive: true);
        var parent = CreateParent(run);
        var child = CreateChild(run, parent);
        parent.ChildChunks.Add(child);
        run.ParentChunks.Add(parent);
        run.Document = document;
        document.ChunkingRuns.Add(run);

        var repository = new ChunkRepository(context);
        await repository.AddAsync(run);
        await context.SaveChangesAsync();

        var loaded = await repository.GetActiveRunAsync(document.Id);

        Assert.NotNull(loaded);
        Assert.Equal("source-hash", loaded!.SourceContentHash);
        Assert.Equal("1.0", loaded.SourceSchemaVersion);
        Assert.Equal("chunker-1", loaded.ChunkerVersion);
        Assert.Equal("{\"ParentTargetTokens\":1200}", loaded.ConfigurationJson);
        var loadedParent = Assert.Single(loaded.ParentChunks);
        var loadedChild = Assert.Single(loadedParent.ChildChunks);
        Assert.Equal(new[] { "Chapter I", "Section 1" }, loadedParent.SectionPath);
        Assert.Equal(loadedParent.SectionPath, loadedChild.SectionPath);
        Assert.Equal("raw child", loadedChild.RawContent);
        Assert.Equal("Document: Handbook\nContent: raw child", loadedChild.ContextualizedContent);
        Assert.Equal(new[] { "component-1" }, loadedChild.ComponentIds);
        Assert.True(loadedParent.IsAtomic);
        Assert.True(loadedChild.IsAtomic);
    }

    [Fact]
    public async Task Repository_rejects_mismatched_parent_document_or_run()
    {
        await using var context = CreateContext();
        var document = AddDocument(context);
        var run = CreateRun(document, isActive: false);
        run.ParentChunks.Add(new ParentChunk
        {
            Id = "parent-invalid",
            ChunkingRunId = run.Id,
            DocumentId = 999,
            DepartmentId = document.DepartmentId,
            Content = "invalid",
            ContentHash = "hash",
            ComponentIds = new List<string> { "component" },
            SectionPath = new List<string>()
        });

        var repository = new ChunkRepository(context);

        await Assert.ThrowsAsync<ArgumentException>(() => repository.AddAsync(run));
    }

    [Fact]
    public async Task Repository_rejects_two_active_runs_for_one_document()
    {
        await using var context = CreateContext();
        var document = AddDocument(context);
        var repository = new ChunkRepository(context);

        await repository.AddAsync(CreateRun(document, "run-active-1", isActive: true));
        await context.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.AddAsync(CreateRun(document, "run-active-2", isActive: true)));
    }

    [Fact]
    public async Task Failed_run_does_not_replace_existing_active_run()
    {
        await using var context = CreateContext();
        var document = AddDocument(context);
        var repository = new ChunkRepository(context);
        var active = CreateRun(document, "run-active", isActive: true);
        await repository.AddAsync(active);
        await context.SaveChangesAsync();

        var failed = CreateRun(document, "run-failed", isActive: false);
        failed.Status = ChunkingRunStatuses.Failed;
        failed.ErrorMessage = "chunking failed";
        await repository.AddAsync(failed);
        await context.SaveChangesAsync();

        var loaded = await repository.GetActiveRunAsync(document.Id);

        Assert.Equal("run-active", loaded!.Id);
        Assert.Equal(ChunkingRunStatuses.Failed,
            await context.ChunkingRuns.Where(run => run.Id == "run-failed").Select(run => run.Status).SingleAsync());
    }

    [Fact]
    public async Task Repository_finds_same_source_version_without_creating_duplicate()
    {
        await using var context = CreateContext();
        var document = AddDocument(context);
        var repository = new ChunkRepository(context);
        var run = CreateRun(document, isActive: false);
        await repository.AddAsync(run);
        await context.SaveChangesAsync();

        var found = await repository.GetByVersionAsync(
            document.Id,
            "source-hash",
            "1.0",
            "chunker-1",
            "{\"ParentTargetTokens\":1200}");

        Assert.NotNull(found);
        Assert.Equal(run.Id, found!.Id);
    }

    [Fact]
    public void Model_contains_required_indexes_and_foreign_keys()
    {
        using var context = CreateContext();
        var run = context.Model.FindEntityType(typeof(ChunkingRun))!;
        var parent = context.Model.FindEntityType(typeof(ParentChunk))!;
        var child = context.Model.FindEntityType(typeof(ChildChunk))!;

        Assert.Contains(run.GetIndexes(), index =>
            index.IsUnique && index.GetFilter()!.Contains("is_active", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(parent.GetForeignKeys(), foreignKey =>
            foreignKey.PrincipalEntityType.ClrType == typeof(ChunkingRun));
        Assert.Contains(child.GetForeignKeys(), foreignKey =>
            foreignKey.PrincipalEntityType.ClrType == typeof(ParentChunk));
        Assert.Contains(child.GetForeignKeys(), foreignKey =>
            foreignKey.PrincipalEntityType.ClrType == typeof(ChunkingRun));
    }

    [Fact]
    public async Task Deleting_document_cascades_chunking_graph()
    {
        await using var context = CreateContext();
        var document = AddDocument(context);
        var run = CreateRun(document, isActive: true);
        var parent = CreateParent(run);
        parent.ChildChunks.Add(CreateChild(run, parent));
        run.ParentChunks.Add(parent);
        run.Document = document;
        document.ChunkingRuns.Add(run);
        var repository = new ChunkRepository(context);
        await repository.AddAsync(run);
        await context.SaveChangesAsync();

        context.Documents.Remove(document);
        await context.SaveChangesAsync();

        Assert.Empty(await context.ChunkingRuns.ToListAsync());
        Assert.Empty(await context.ParentChunks.ToListAsync());
        Assert.Empty(await context.ChildChunks.ToListAsync());
    }

    private static CorpMindDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<CorpMindDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new CorpMindDbContext(options);
    }

    private static Document AddDocument(CorpMindDbContext context, int id = 1, int departmentId = 7)
    {
        var department = new Department { Id = departmentId, Name = $"Department {departmentId}" };
        var document = new Document
        {
            Id = id,
            OriginalFileName = "handbook.pdf",
            Title = "Handbook",
            StorageKey = "documents/handbook.pdf",
            DepartmentId = departmentId,
            Department = department
        };
        context.Departments.Add(department);
        context.Documents.Add(document);
        context.SaveChanges();
        return document;
    }

    private static ChunkingRun CreateRun(
        Document document,
        string id = "run-1",
        bool isActive = false) => new()
    {
        Id = id,
        DocumentId = document.Id,
        DepartmentId = document.DepartmentId,
        SourceContentHash = "source-hash",
        SourceSchemaVersion = "1.0",
        ChunkerVersion = "chunker-1",
        ConfigurationJson = "{\"ParentTargetTokens\":1200}",
        Status = isActive ? ChunkingRunStatuses.Completed : ChunkingRunStatuses.NotStarted,
        IsActive = isActive
    };

    private static ParentChunk CreateParent(ChunkingRun run) => new()
    {
        Id = "parent-1",
        ChunkingRunId = run.Id,
        DocumentId = run.DocumentId,
        DepartmentId = run.DepartmentId,
        Ordinal = 0,
        Title = "Section 1",
        SectionPath = new List<string> { "Chapter I", "Section 1" },
        Content = "parent content",
        TokenCount = 2,
        PageFrom = 1,
        PageTo = 2,
        ComponentIds = new List<string> { "component-1" },
        ContentHash = "parent-hash",
        IsAtomic = true
    };

    private static ChildChunk CreateChild(ChunkingRun run, ParentChunk parent) => new()
    {
        Id = "child-1",
        ParentChunkId = parent.Id,
        ChunkingRunId = run.Id,
        DocumentId = run.DocumentId,
        DepartmentId = run.DepartmentId,
        Ordinal = 0,
        SectionPath = new List<string> { "Chapter I", "Section 1" },
        RawContent = "raw child",
        ContextualizedContent = "Document: Handbook\nContent: raw child",
        TokenCount = 2,
        PageFrom = 1,
        PageTo = 2,
        ComponentIds = new List<string> { "component-1" },
        ContentHash = "child-hash",
        IsAtomic = true
    };
}
