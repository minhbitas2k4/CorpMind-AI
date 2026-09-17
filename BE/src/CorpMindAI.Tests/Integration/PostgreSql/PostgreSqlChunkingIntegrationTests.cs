using CorpMindAI.Application.Chunking;
using CorpMindAI.Application.Chunking.Children;
using CorpMindAI.Application.Chunking.Models;
using CorpMindAI.Application.Chunking.Normalization;
using CorpMindAI.Application.Chunking.Parents;
using CorpMindAI.Application.Chunking.Sections;
using CorpMindAI.Application.Chunking.Validation;
using CorpMindAI.Application.DTOs.Document;
using CorpMindAI.Application.Interfaces;
using CorpMindAI.Domain.Entities;
using CorpMindAI.Infrastructure.Repositories;
using CorpMindAI.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using CorpMindAI.Tests.Support.Database;
using Xunit;

namespace CorpMindAI.Tests.Integration.PostgreSql;

[Trait("Category", "PostgreSqlIntegration")]
[Trait("TestType", "Integration")]
public sealed class PostgreSqlChunkingIntegrationTests
{
    [SkippableFact]
    public async Task Migrations_create_jsonb_columns_and_filtered_active_index()
    {
        await using var database = await PostgreSqlIntegrationDatabase.CreateAsync();
        await using var context = database.CreateContext();

        var migrations = await context.Database.SqlQueryRaw<string>("""
            SELECT "MigrationId" AS "Value"
            FROM "__EFMigrationsHistory"
            ORDER BY "MigrationId"
            """).ToListAsync();
        var jsonbColumns = await context.Database.SqlQueryRaw<string>("""
            SELECT table_name || '.' || column_name AS "Value"
            FROM information_schema.columns
            WHERE table_schema = 'public'
              AND data_type = 'jsonb'
              AND table_name IN ('chunking_runs', 'parent_chunks', 'child_chunks')
            ORDER BY table_name, column_name
            """).ToListAsync();
        var activeIndex = await context.Database.SqlQueryRaw<string>("""
            SELECT indexdef AS "Value"
            FROM pg_indexes
            WHERE schemaname = 'public'
              AND tablename = 'chunking_runs'
              AND indexdef ILIKE '%is_active%'
              AND indexdef ILIKE '%WHERE%'
            """).SingleAsync();

        Assert.Contains("20260909032453_AddParentChildChunking", migrations);
        Assert.Equal(
            new[]
            {
                "child_chunks.component_ids",
                "child_chunks.section_path",
                "chunking_runs.configuration_json",
                "chunking_runs.normalization_notices_json",
                "chunking_runs.validation_warnings_json",
                "parent_chunks.component_ids",
                "parent_chunks.section_path"
            },
            jsonbColumns);
        Assert.Contains("UNIQUE INDEX", activeIndex, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("is_active", activeIndex, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task PostgreSql_round_trips_jsonb_and_enforces_composite_foreign_keys()
    {
        await using var database = await PostgreSqlIntegrationDatabase.CreateAsync();
        await using (var context = database.CreateContext())
        {
            var first = AddDocument(context, 101, 11);
            AddDocument(context, 102, 11);
            context.ChunkingRuns.Add(CreateRun(first, "run-jsonb", true, "source-jsonb"));
            var parent = CreateParent("run-jsonb", 101, 11, "parent-jsonb");
            parent.ChildChunks.Add(CreateChild("run-jsonb", 101, 11, parent.Id, "child-jsonb"));
            context.ParentChunks.Add(parent);
            await context.SaveChangesAsync();

            var firstSection = await context.Database.SqlQueryRaw<string>("""
                SELECT section_path ->> 0 AS "Value"
                FROM parent_chunks
                WHERE id = 'parent-jsonb'
                """).SingleAsync();
            Assert.Equal("Chapter I", firstSection);
        }

        await using (var invalidContext = database.CreateContext())
        {
            var invalidParent = CreateParent("run-jsonb", 102, 11, "parent-invalid");
            invalidParent.Ordinal = 1;
            invalidContext.ParentChunks.Add(invalidParent);
            var exception = await Assert.ThrowsAsync<DbUpdateException>(
                () => invalidContext.SaveChangesAsync());
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, FindPostgresException(exception).SqlState);
        }

        await using (var invalidChildContext = database.CreateContext())
        {
            var invalidChild = CreateChild("run-jsonb", 102, 11, "parent-jsonb", "child-invalid");
            invalidChild.Ordinal = 1;
            invalidChildContext.ChildChunks.Add(invalidChild);
            var exception = await Assert.ThrowsAsync<DbUpdateException>(
                () => invalidChildContext.SaveChangesAsync());
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, FindPostgresException(exception).SqlState);
        }

        await using var verificationContext = database.CreateContext();
        var loaded = await new ChunkRepository(verificationContext).GetActiveRunAsync(101);
        var loadedParent = Assert.Single(loaded!.ParentChunks);
        var loadedChild = Assert.Single(loadedParent.ChildChunks);
        Assert.Equal(new[] { "Chapter I", "Section 1" }, loadedParent.SectionPath);
        Assert.Equal(new[] { "component-1" }, loadedChild.ComponentIds);
    }

    [SkippableFact]
    public async Task Transaction_rollback_removes_the_entire_chunk_graph()
    {
        await using var database = await PostgreSqlIntegrationDatabase.CreateAsync();
        await using (var context = database.CreateContext())
        {
            var document = AddDocument(context, 201, 21);
            await context.SaveChangesAsync();
            await using var transaction = await context.Database.BeginTransactionAsync();
            var run = CreateRun(document, "run-rollback", true, "source-rollback");
            var parent = CreateParent(run.Id, document.Id, document.DepartmentId, "parent-rollback");
            parent.ChildChunks.Add(CreateChild(run.Id, document.Id, document.DepartmentId, parent.Id, "child-rollback"));
            run.ParentChunks.Add(parent);
            context.ChunkingRuns.Add(run);
            await context.SaveChangesAsync();
            await transaction.RollbackAsync();
        }

        await using var verificationContext = database.CreateContext();
        Assert.False(await verificationContext.ChunkingRuns.AnyAsync());
        Assert.False(await verificationContext.ParentChunks.AnyAsync());
        Assert.False(await verificationContext.ChildChunks.AnyAsync());
    }

    [SkippableFact]
    public async Task Concurrent_active_runs_are_rejected_by_the_filtered_unique_index()
    {
        await using var database = await PostgreSqlIntegrationDatabase.CreateAsync();
        await using (var seedContext = database.CreateContext())
        {
            AddDocument(seedContext, 301, 31);
            await seedContext.SaveChangesAsync();
        }

        var attempts = await Task.WhenAll(
            InsertActiveRunAsync(database, 301, 31, "run-concurrent-a"),
            InsertActiveRunAsync(database, 301, 31, "run-concurrent-b"));

        Assert.Single(attempts, exception => exception is null);
        var failure = Assert.Single(attempts, exception => exception is not null);
        var updateException = Assert.IsType<DbUpdateException>(failure);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, FindPostgresException(updateException).SqlState);

        await using var verificationContext = database.CreateContext();
        Assert.Equal(1, await verificationContext.ChunkingRuns.CountAsync(run => run.IsActive));
    }

    [SkippableFact]
    public async Task Structured_ocr_runs_through_orchestrator_and_replaces_the_active_graph()
    {
        await using var database = await PostgreSqlIntegrationDatabase.CreateAsync();
        await using (var context = database.CreateContext())
        {
            var document = AddDocument(context, 401, 41);
            document.OcrResult = CreateOcrResult(document.Id, "Employees must use approved credentials.");
            await context.SaveChangesAsync();

            var documents = new DocumentRepository(context);
            var chunks = new ChunkRepository(context);
            using var unitOfWork = new UnitOfWork(new UserRepository(context), documents, chunks, context);
            var orchestrator = new DocumentChunkingOrchestrator(
                documents,
                unitOfWork,
                new StructuredDocumentNormalizer(),
                new HeadingDetector(),
                new SectionBuilder(),
                new ParentChunkBuilder(),
                new ChildChunkBuilder(),
                new ChunkingTokenCounter(),
                NullLogger<DocumentChunkingOrchestrator>.Instance,
                new ChunkingResultValidator(),
                new PostgreSqlChunkingExecutionLock(database.ConnectionString),
                new ChunkingOptions());

            await orchestrator.ExecuteAsync(document.Id);

            var persistedOcr = await context.OcrResults.SingleAsync(result => result.DocumentId == document.Id);
            persistedOcr.StructuredDocumentJson = BuildStructuredOcrJson(
                document.Id,
                "Employees must use approved credentials and rotate them annually.");
            persistedOcr.UpdatedAt = DateTime.UtcNow;
            await context.SaveChangesAsync();
            await orchestrator.ExecuteAsync(document.Id);
        }

        await using var verificationContext = database.CreateContext();
        var runs = await verificationContext.ChunkingRuns
            .AsNoTracking()
            .Include(run => run.ParentChunks)
            .ThenInclude(parent => parent.ChildChunks)
            .OrderBy(run => run.CreatedAt)
            .ToListAsync();

        Assert.Equal(2, runs.Count);
        Assert.Single(runs, run => run.IsActive);
        Assert.All(runs, run => Assert.Equal(ChunkingRunStatuses.Completed, run.Status));
        var active = Assert.Single(runs, run => run.IsActive);
        var inactive = Assert.Single(runs, run => !run.IsActive);
        Assert.NotEqual(inactive.SourceContentHash, active.SourceContentHash);
        Assert.NotEmpty(active.ParentChunks);
        Assert.All(active.ParentChunks, parent => Assert.NotEmpty(parent.ChildChunks));
        Assert.All(active.ParentChunks.SelectMany(parent => parent.ChildChunks), child =>
        {
            Assert.Equal(active.DocumentId, child.DocumentId);
            Assert.Equal(active.DepartmentId, child.DepartmentId);
            Assert.False(string.IsNullOrWhiteSpace(child.ContextualizedContent));
            Assert.NotEmpty(child.ComponentIds);
        });
    }

    [SkippableFact]
    public async Task Title_only_change_creates_a_new_run_without_chunk_id_collisions()
    {
        await using var database = await PostgreSqlIntegrationDatabase.CreateAsync();
        await using (var context = database.CreateContext())
        {
            var document = AddDocument(context, 403, 43);
            document.Title = "Original handbook title";
            document.OcrResult = CreateOcrResult(document.Id, "The body remains unchanged.");
            await context.SaveChangesAsync();

            using var unitOfWork = CreateUnitOfWork(context);
            var orchestrator = CreateOrchestrator(unitOfWork, database.ConnectionString);
            await orchestrator.ExecuteAsync(document.Id);
            var persistedDocument = await context.Documents.SingleAsync(item => item.Id == document.Id);
            persistedDocument.Title = "Revised handbook title";
            await context.SaveChangesAsync();
            await orchestrator.ExecuteAsync(document.Id);
        }

        await using var verificationContext = database.CreateContext();
        var runs = await verificationContext.ChunkingRuns
            .AsNoTracking()
            .Include(run => run.ParentChunks)
            .ThenInclude(parent => parent.ChildChunks)
            .Where(run => run.DocumentId == 403)
            .ToListAsync();

        Assert.Equal(2, runs.Count);
        Assert.Single(runs, run => run.IsActive);
        Assert.Equal(2, runs.SelectMany(run => run.ParentChunks).Select(parent => parent.Id).Distinct().Count());
        Assert.All(runs, run =>
        {
            Assert.Equal(ChunkingRunStatuses.Completed, run.Status);
            Assert.NotNull(run.SourceCoverage);
            Assert.StartsWith("[", run.ValidationWarningsJson, StringComparison.Ordinal);
            Assert.StartsWith("[", run.NormalizationNoticesJson, StringComparison.Ordinal);
        });
    }

    [SkippableFact]
    public async Task Returning_to_an_older_ocr_source_reactivates_its_completed_run()
    {
        await using var database = await PostgreSqlIntegrationDatabase.CreateAsync();
        string firstRunId;
        await using (var context = database.CreateContext())
        {
            var document = AddDocument(context, 404, 44);
            document.OcrResult = CreateOcrResult(document.Id, "Source version A.");
            var sourceA = document.OcrResult.StructuredDocumentJson;
            await context.SaveChangesAsync();

            using var unitOfWork = CreateUnitOfWork(context);
            var orchestrator = CreateOrchestrator(unitOfWork, database.ConnectionString);
            await orchestrator.ExecuteAsync(document.Id);
            firstRunId = (await context.ChunkingRuns.SingleAsync(run => run.IsActive)).Id;

            var persistedOcr = await context.OcrResults.SingleAsync(result => result.DocumentId == document.Id);
            persistedOcr.StructuredDocumentJson = BuildStructuredOcrJson(document.Id, "Source version B.");
            await context.SaveChangesAsync();
            await orchestrator.ExecuteAsync(document.Id);

            persistedOcr = await context.OcrResults.SingleAsync(result => result.DocumentId == document.Id);
            persistedOcr.StructuredDocumentJson = sourceA;
            await context.SaveChangesAsync();
            await orchestrator.ExecuteAsync(document.Id);
        }

        await using var verificationContext = database.CreateContext();
        var runs = await verificationContext.ChunkingRuns
            .AsNoTracking()
            .Where(run => run.DocumentId == 404)
            .ToListAsync();
        Assert.Equal(2, runs.Count);
        Assert.Equal(firstRunId, Assert.Single(runs, run => run.IsActive).Id);
        Assert.All(runs, run => Assert.Equal(ChunkingRunStatuses.Completed, run.Status));
    }

    [SkippableFact]
    public async Task Unsectioned_document_uses_database_title_in_parent_and_child_metadata()
    {
        await using var database = await PostgreSqlIntegrationDatabase.CreateAsync();
        await using (var context = database.CreateContext())
        {
            var document = AddDocument(context, 402, 42);
            document.Title = "Employee Benefits Handbook";
            document.OcrResult = CreateOcrResult(document.Id, "Benefits become effective after enrollment.");
            document.OcrResult.StructuredDocumentJson = BuildUnsectionedOcrJson(
                document.Id,
                "Benefits become effective after enrollment.");
            await context.SaveChangesAsync();

            using var unitOfWork = CreateUnitOfWork(context);
            var orchestrator = CreateOrchestrator(unitOfWork, database.ConnectionString);
            await orchestrator.ExecuteAsync(document.Id);
        }

        await using var verificationContext = database.CreateContext();
        var run = await verificationContext.ChunkingRuns
            .AsNoTracking()
            .Include(item => item.ParentChunks)
            .ThenInclude(parent => parent.ChildChunks)
            .SingleAsync(item => item.DocumentId == 402 && item.IsActive);
        var parent = Assert.Single(run.ParentChunks);
        var child = Assert.Single(parent.ChildChunks);

        Assert.Empty(parent.SectionPath);
        Assert.Equal("Employee Benefits Handbook", parent.Title);
        Assert.StartsWith(
            "Document: Employee Benefits Handbook\nSection: Unsectioned content\nContent:",
            child.ContextualizedContent,
            StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Numbered_list_heading_regression_fixture_persists_without_title_overflow()
    {
        await using var database = await PostgreSqlIntegrationDatabase.CreateAsync();
        await using (var context = database.CreateContext())
        {
            var document = AddDocument(context, 27, 27);
            document.Title = "Incident Response Test Document";
            var fixturePath = Path.Combine(
                AppContext.BaseDirectory,
                "Chunking",
                "Fixtures",
                "numbered-list-heading-regression.json");
            var structuredJson = await File.ReadAllTextAsync(fixturePath);
            document.OcrResult = new OcrResult
            {
                DocumentId = document.Id,
                TotalPages = 4,
                PageAverageConfidence = 0.93,
                OverallLevel = "high",
                SchemaVersion = "1.0",
                StructuredDocumentJson = structuredJson,
                Status = "completed"
            };
            await context.SaveChangesAsync();

            using var unitOfWork = CreateUnitOfWork(context);
            var orchestrator = CreateOrchestrator(unitOfWork, database.ConnectionString);

            await orchestrator.ExecuteAsync(document.Id);
        }

        await using var verificationContext = database.CreateContext();
        var run = await verificationContext.ChunkingRuns
            .AsNoTracking()
            .Include(item => item.ParentChunks)
            .ThenInclude(parent => parent.ChildChunks)
            .SingleAsync(item => item.DocumentId == 27 && item.IsActive);

        Assert.Equal("6.0", run.ChunkerVersion);
        Assert.Equal(ChunkingRunStatuses.Completed, run.Status);
        Assert.Equal(1.0, run.SourceCoverage);
        Assert.All(run.ParentChunks, parent => Assert.True(parent.Title is null || parent.Title.Length <= 255));

        var listParent = Assert.Single(
            run.ParentChunks,
            parent => parent.ComponentIds.Contains("doc-27-p4-c0004", StringComparer.Ordinal));
        Assert.True(listParent.IsAtomic);
        Assert.Equal(new[] { "5. Incident Response Checklist" }, listParent.SectionPath);
        Assert.True(listParent.PageFrom <= 4 && listParent.PageTo >= 4);
        var listChild = Assert.Single(listParent.ChildChunks);
        Assert.True(listChild.IsAtomic);
        Assert.Equal(listParent.Id, listChild.ParentChunkId);
        Assert.Equal(new[] { "5. Incident Response Checklist" }, listChild.SectionPath);
        Assert.Contains("1. Record the detection time", listChild.RawContent, StringComparison.Ordinal);
    }

    [SkippableTheory]
    [InlineData(LifecycleFault.BeforeTransaction)]
    [InlineData(LifecycleFault.GraphSave)]
    [InlineData(LifecycleFault.ActivationSave)]
    [InlineData(LifecycleFault.Commit)]
    public async Task Failure_rolls_back_keeps_old_run_active_and_retry_succeeds(
        LifecycleFault fault)
    {
        await using var database = await PostgreSqlIntegrationDatabase.CreateAsync();
        await using (var context = database.CreateContext())
        {
            var document = AddDocument(context, 501, 51);
            document.OcrResult = CreateOcrResult(document.Id, "A controlled lifecycle must be retryable.");
            context.ChunkingRuns.Add(CreateRun(document, "run-old-active", true, "old-source"));
            await context.SaveChangesAsync();

            var inner = CreateUnitOfWork(context);
            var faulting = new FaultingUnitOfWork(inner, fault);
            IChunkingResultValidator validator = fault == LifecycleFault.BeforeTransaction
                ? new RejectingValidator()
                : new ChunkingResultValidator();
            var orchestrator = CreateOrchestrator(
                faulting,
                database.ConnectionString,
                validator: validator);

            await Assert.ThrowsAnyAsync<Exception>(() => orchestrator.ExecuteAsync(document.Id));

            await using (var failureContext = database.CreateContext())
            {
                var failedRuns = await failureContext.ChunkingRuns.AsNoTracking().ToListAsync();
                Assert.Equal(2, failedRuns.Count);
                Assert.True(Assert.Single(failedRuns, run => run.Id == "run-old-active").IsActive);
                var failed = Assert.Single(failedRuns, run => run.Id != "run-old-active");
                Assert.Equal(ChunkingRunStatuses.Failed, failed.Status);
                Assert.False(failed.IsActive);
                Assert.False(await failureContext.ParentChunks.AnyAsync(parent => parent.ChunkingRunId == failed.Id));
                Assert.False(await failureContext.ChildChunks.AnyAsync(child => child.ChunkingRunId == failed.Id));
            }

            // Retry through the same DbContext/unit of work to prove rollback
            // cleanup removed stale graph entities from the ChangeTracker.
            var retry = CreateOrchestrator(faulting, database.ConnectionString);
            await retry.ExecuteAsync(501);
        }

        await using var verificationContext = database.CreateContext();
        var runs = await verificationContext.ChunkingRuns
            .AsNoTracking()
            .Include(run => run.ParentChunks)
            .ThenInclude(parent => parent.ChildChunks)
            .ToListAsync();
        var oldRun = Assert.Single(runs, run => run.Id == "run-old-active");
        var completed = Assert.Single(runs, run => run.Id != "run-old-active");
        Assert.False(oldRun.IsActive);
        Assert.True(completed.IsActive);
        Assert.Equal(ChunkingRunStatuses.Completed, completed.Status);
        Assert.NotEmpty(completed.ParentChunks);
        Assert.All(completed.ParentChunks, parent => Assert.NotEmpty(parent.ChildChunks));
    }

    [SkippableFact]
    public async Task Cancellation_after_processing_marks_run_failed_and_preserves_old_active_run()
    {
        await using var database = await PostgreSqlIntegrationDatabase.CreateAsync();
        await using (var context = database.CreateContext())
        {
            var document = AddDocument(context, 601, 61);
            document.OcrResult = CreateOcrResult(document.Id, "Cancellation must have a terminal state.");
            context.ChunkingRuns.Add(CreateRun(document, "run-cancel-old", true, "old-source"));
            await context.SaveChangesAsync();

            var unitOfWork = CreateUnitOfWork(context);
            var orchestrator = CreateOrchestrator(
                unitOfWork,
                database.ConnectionString,
                validator: new CancellingValidator());

            await Assert.ThrowsAsync<OperationCanceledException>(() => orchestrator.ExecuteAsync(document.Id));
        }

        await using var verificationContext = database.CreateContext();
        var runs = await verificationContext.ChunkingRuns.AsNoTracking().ToListAsync();
        Assert.True(Assert.Single(runs, run => run.Id == "run-cancel-old").IsActive);
        var cancelled = Assert.Single(runs, run => run.Id != "run-cancel-old");
        Assert.Equal(ChunkingRunStatuses.Failed, cancelled.Status);
        Assert.False(cancelled.IsActive);
        Assert.Equal("Chunking execution was cancelled.", cancelled.ErrorMessage);
    }

    [SkippableFact]
    public async Task Commit_acknowledgement_failure_confirms_the_durable_active_run()
    {
        await using var database = await PostgreSqlIntegrationDatabase.CreateAsync();
        await using (var context = database.CreateContext())
        {
            var document = AddDocument(context, 651, 65);
            document.OcrResult = CreateOcrResult(document.Id, "A committed run must survive an ambiguous acknowledgement.");
            context.ChunkingRuns.Add(CreateRun(document, "run-commit-old", true, "old-source"));
            await context.SaveChangesAsync();

            var faulting = new FaultingUnitOfWork(
                CreateUnitOfWork(context),
                LifecycleFault.CommitAfterSuccess);
            var orchestrator = CreateOrchestrator(faulting, database.ConnectionString);

            await orchestrator.ExecuteAsync(document.Id);
        }

        await using var verificationContext = database.CreateContext();
        var runs = await verificationContext.ChunkingRuns.AsNoTracking().ToListAsync();
        Assert.False(Assert.Single(runs, run => run.Id == "run-commit-old").IsActive);
        var completed = Assert.Single(runs, run => run.Id != "run-commit-old");
        Assert.True(completed.IsActive);
        Assert.Equal(ChunkingRunStatuses.Completed, completed.Status);
    }

    [SkippableFact]
    public async Task Two_workers_for_the_same_document_do_not_execute_the_pipeline_concurrently()
    {
        await using var database = await PostgreSqlIntegrationDatabase.CreateAsync();
        await using (var seedContext = database.CreateContext())
        {
            var document = AddDocument(seedContext, 701, 71);
            document.OcrResult = CreateOcrResult(document.Id, "Concurrent workers must be serialized.");
            await seedContext.SaveChangesAsync();
        }

        var probe = new ConcurrencyProbeNormalizer();
        async Task ExecuteWorkerAsync()
        {
            await using var context = database.CreateContext();
            var unitOfWork = CreateUnitOfWork(context);
            var orchestrator = CreateOrchestrator(
                unitOfWork,
                database.ConnectionString,
                normalizer: probe);
            await orchestrator.ExecuteAsync(701);
        }

        await Task.WhenAll(
            Task.Run(ExecuteWorkerAsync),
            Task.Run(ExecuteWorkerAsync));

        Assert.Equal(1, probe.MaximumConcurrency);
        await using var verificationContext = database.CreateContext();
        var run = await new ChunkRepository(verificationContext).GetActiveRunAsync(701);
        Assert.NotNull(run);
        Assert.Equal(ChunkingRunStatuses.Completed, run!.Status);
        Assert.NotEmpty(run.ParentChunks);
    }

    private static async Task<Exception?> InsertActiveRunAsync(
        PostgreSqlIntegrationDatabase database,
        int documentId,
        int departmentId,
        string runId)
    {
        try
        {
            await using var context = database.CreateContext();
            context.ChunkingRuns.Add(new ChunkingRun
            {
                Id = runId,
                DocumentId = documentId,
                DepartmentId = departmentId,
                SourceContentHash = runId,
                SourceSchemaVersion = "1.0",
                ChunkerVersion = "1.0",
                ConfigurationJson = "{}",
                Status = ChunkingRunStatuses.Completed,
                IsActive = true
            });
            await context.SaveChangesAsync();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static UnitOfWork CreateUnitOfWork(CorpMindAI.Infrastructure.Data.CorpMindDbContext context)
    {
        var documents = new DocumentRepository(context);
        return new UnitOfWork(
            new UserRepository(context),
            documents,
            new ChunkRepository(context),
            context);
    }

    private static DocumentChunkingOrchestrator CreateOrchestrator(
        IUnitOfWork unitOfWork,
        string connectionString,
        IChunkingResultValidator? validator = null,
        IDocumentNormalizer? normalizer = null) =>
        new(
            unitOfWork.DocumentRepo,
            unitOfWork,
            normalizer ?? new StructuredDocumentNormalizer(),
            new HeadingDetector(),
            new SectionBuilder(),
            new ParentChunkBuilder(),
            new ChildChunkBuilder(),
            new ChunkingTokenCounter(),
            NullLogger<DocumentChunkingOrchestrator>.Instance,
            validator ?? new ChunkingResultValidator(),
            new PostgreSqlChunkingExecutionLock(connectionString),
            new ChunkingOptions());

    private static Document AddDocument(
        DbContext context,
        int documentId,
        int departmentId)
    {
        var department = context.Set<Department>().Local.SingleOrDefault(item => item.Id == departmentId);
        if (department is null)
        {
            department = new Department { Id = departmentId, Name = $"Department {departmentId}" };
            context.Add(department);
        }

        var document = new Document
        {
            Id = documentId,
            DepartmentId = departmentId,
            Department = department,
            OriginalFileName = $"document-{documentId}.pdf",
            Title = $"Document {documentId}",
            StorageKey = $"documents/{documentId}.pdf",
            OcrStatus = "completed"
        };
        context.Add(document);
        return document;
    }

    private static OcrResult CreateOcrResult(int documentId, string body) => new()
    {
        DocumentId = documentId,
        TotalPages = 1,
        PageAverageConfidence = 0.96,
        OverallLevel = "high",
        SchemaVersion = "1.0",
        StructuredDocumentJson = BuildStructuredOcrJson(documentId, body),
        Status = "completed"
    };

    private static string BuildStructuredOcrJson(int documentId, string body) =>
        System.Text.Json.JsonSerializer.Serialize(new StructuredDocumentDto
        {
            SchemaVersion = "1.0",
            DocumentId = documentId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            TotalPages = 1,
            Pages = new List<StructuredPageDto>
            {
                new()
                {
                    PageNumber = 1,
                    Components = new List<StructuredComponentDto>
                    {
                        new()
                        {
                            ComponentId = $"{documentId}-title",
                            DocumentId = documentId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            PageNumber = 1,
                            Type = "title",
                            ReadingOrder = 0,
                            Confidence = 0.98,
                            Text = "Chapter I Corporate Policy",
                            Metadata = new ComponentMetadataDto { SourceLayoutType = "title" }
                        },
                        new()
                        {
                            ComponentId = $"{documentId}-body",
                            DocumentId = documentId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            PageNumber = 1,
                            Type = "text",
                            ReadingOrder = 1,
                            Confidence = 0.95,
                            Text = body,
                            Metadata = new ComponentMetadataDto { SourceLayoutType = "text" }
                        }
                    }
                }
            }
        });

    private static string BuildUnsectionedOcrJson(int documentId, string body) =>
        System.Text.Json.JsonSerializer.Serialize(new StructuredDocumentDto
        {
            SchemaVersion = "1.0",
            DocumentId = documentId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            TotalPages = 1,
            Pages = new List<StructuredPageDto>
            {
                new()
                {
                    PageNumber = 1,
                    Components = new List<StructuredComponentDto>
                    {
                        new()
                        {
                            ComponentId = $"{documentId}-body",
                            DocumentId = documentId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            PageNumber = 1,
                            Type = "text",
                            ReadingOrder = 0,
                            Confidence = 0.95,
                            Text = body,
                            Metadata = new ComponentMetadataDto { SourceLayoutType = "text" }
                        }
                    }
                }
            }
        });

    private static ChunkingRun CreateRun(
        Document document,
        string runId,
        bool active,
        string sourceHash) => new()
    {
        Id = runId,
        DocumentId = document.Id,
        DepartmentId = document.DepartmentId,
        SourceContentHash = sourceHash,
        SourceSchemaVersion = "1.0",
        ChunkerVersion = "1.0",
        ConfigurationJson = "{\"ParentTargetTokens\":1200}",
        Status = ChunkingRunStatuses.Completed,
        IsActive = active
    };

    private static ParentChunk CreateParent(
        string runId,
        int documentId,
        int departmentId,
        string parentId) => new()
    {
        Id = parentId,
        ChunkingRunId = runId,
        DocumentId = documentId,
        DepartmentId = departmentId,
        Ordinal = 0,
        Title = "Section 1",
        SectionPath = new List<string> { "Chapter I", "Section 1" },
        Content = "Parent content",
        TokenCount = 2,
        PageFrom = 1,
        PageTo = 1,
        ComponentIds = new List<string> { "component-1" },
        ContentHash = $"hash-{parentId}"
    };

    private static ChildChunk CreateChild(
        string runId,
        int documentId,
        int departmentId,
        string parentId,
        string childId) => new()
    {
        Id = childId,
        ParentChunkId = parentId,
        ChunkingRunId = runId,
        DocumentId = documentId,
        DepartmentId = departmentId,
        Ordinal = 0,
        SectionPath = new List<string> { "Chapter I", "Section 1" },
        RawContent = "Child content",
        ContextualizedContent = "Document: Test\nContent: Child content",
        TokenCount = 2,
        PageFrom = 1,
        PageTo = 1,
        ComponentIds = new List<string> { "component-1" },
        ContentHash = $"hash-{childId}"
    };

    private static PostgresException FindPostgresException(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgres)
                return postgres;
        }

        throw new Xunit.Sdk.XunitException("Expected a PostgreSQL server exception.");
    }

    public enum LifecycleFault
    {
        BeforeTransaction,
        GraphSave,
        ActivationSave,
        Commit,
        CommitAfterSuccess
    }

    private sealed class FaultingUnitOfWork : IUnitOfWork
    {
        private readonly IUnitOfWork _inner;
        private readonly LifecycleFault _fault;
        private int _saveCount;
        private bool _raised;

        public FaultingUnitOfWork(IUnitOfWork inner, LifecycleFault fault)
        {
            _inner = inner;
            _fault = fault;
        }

        public IUserRepository UserRepo => _inner.UserRepo;
        public IDocumentRepository DocumentRepo => _inner.DocumentRepo;
        public IChunkRepository ChunkRepo => _inner.ChunkRepo;

        public Task BeginTransactionAsync() => _inner.BeginTransactionAsync();

        public async Task CommitTransactionAsync()
        {
            if (!_raised && _fault == LifecycleFault.Commit)
            {
                _raised = true;
                throw new InvalidOperationException("Injected commit failure.");
            }

            await _inner.CommitTransactionAsync();
            if (!_raised && _fault == LifecycleFault.CommitAfterSuccess)
            {
                _raised = true;
                throw new InvalidOperationException("Injected commit acknowledgement failure.");
            }
        }

        public Task RollbackTransactionAsync() => _inner.RollbackTransactionAsync();

        public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            _saveCount++;
            var target = _fault switch
            {
                LifecycleFault.GraphSave => 4,
                LifecycleFault.ActivationSave => 6,
                _ => -1
            };
            if (!_raised && _saveCount == target)
            {
                _raised = true;
                throw new InvalidOperationException($"Injected save failure at call {_saveCount}.");
            }

            return await _inner.SaveChangesAsync(cancellationToken);
        }

        public void ClearTrackedChanges() => _inner.ClearTrackedChanges();
        public void Dispose() { }
    }

    private sealed class RejectingValidator : IChunkingResultValidator
    {
        public ChunkingValidationResult Validate(
            ChunkingResult? result,
            ChunkingSourceInventory sourceInventory,
            double minimumSourceCoverage) => new(
            new[] { new ChunkingValidationError("injected_validation_failure", "Injected validation failure.") });
    }

    private sealed class CancellingValidator : IChunkingResultValidator
    {
        public ChunkingValidationResult Validate(
            ChunkingResult? result,
            ChunkingSourceInventory sourceInventory,
            double minimumSourceCoverage) =>
            throw new OperationCanceledException("Injected cancellation.");
    }

    private sealed class ConcurrencyProbeNormalizer : IDocumentNormalizer
    {
        private readonly StructuredDocumentNormalizer _inner = new();
        private int _active;
        private int _invocationCount;
        private int _maximumConcurrency;

        public int MaximumConcurrency => Volatile.Read(ref _maximumConcurrency);

        public NormalizedDocument Normalize(StructuredDocumentDto source, string? documentTitle = null)
        {
            Interlocked.Increment(ref _invocationCount);
            var active = Interlocked.Increment(ref _active);
            UpdateMaximum(active);
            try
            {
                Thread.Sleep(200);
                return _inner.Normalize(source, documentTitle);
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }

        private void UpdateMaximum(int value)
        {
            var current = Volatile.Read(ref _maximumConcurrency);
            while (value > current)
            {
                var observed = Interlocked.CompareExchange(ref _maximumConcurrency, value, current);
                if (observed == current)
                    return;
                current = observed;
            }
        }
    }
}
