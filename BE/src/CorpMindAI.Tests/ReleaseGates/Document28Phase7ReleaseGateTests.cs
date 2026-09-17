using System.Text.Json;
using System.Text.RegularExpressions;
using CorpMindAI.Application.Chunking;
using CorpMindAI.Application.Chunking.Children;
using CorpMindAI.Application.Chunking.Models;
using CorpMindAI.Application.Chunking.Normalization;
using CorpMindAI.Application.Chunking.Parents;
using CorpMindAI.Application.Chunking.Sections;
using CorpMindAI.Application.Chunking.Validation;
using CorpMindAI.Application.DTOs.Document;
using CorpMindAI.Domain.Entities;
using CorpMindAI.Infrastructure.Data;
using CorpMindAI.Infrastructure.Repositories;
using CorpMindAI.Infrastructure.Services;
using CorpMindAI.Tests.Support.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CorpMindAI.Tests.ReleaseGates;

public sealed class Document28Phase7ReleaseGateTests
{
    private static readonly Regex TokenPattern = new(
        @"[\w@.+-]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    [SkippableFact]
    [Trait("Category", "Phase7ReleaseGate")]
    [Trait("TestType", "ReleaseGate")]
    public void Fresh_ocr_document_passes_v6_chunking_and_answerability_gate()
    {
        var structuredPath = Environment.GetEnvironmentVariable("PHASE7_STRUCTURED_JSON");
        var manifestPath = Environment.GetEnvironmentVariable("PHASE7_MANIFEST");
        Skip.If(
            string.IsNullOrWhiteSpace(structuredPath) || string.IsNullOrWhiteSpace(manifestPath),
            "Set PHASE7_STRUCTURED_JSON and PHASE7_MANIFEST to run the fresh Document 28 release gate.");

        var source = JsonSerializer.Deserialize<StructuredDocumentDto>(
            File.ReadAllText(structuredPath!))
            ?? throw new InvalidOperationException("Fresh structured OCR output is invalid.");
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath!));

        Assert.Equal("1.1", source.SchemaVersion);
        Assert.Equal(10, source.TotalPages);

        var options = new ChunkingOptions();
        var sourceContract = StructuredDocumentContractValidator.Validate(source, options);
        Assert.True(sourceContract.IsValid, string.Join("; ", sourceContract.Errors.Select(error => error.Code)));
        Assert.Equal(1.0, sourceContract.SourceFidelity);

        var counter = new ChunkingTokenCounter();
        var normalized = new StructuredDocumentNormalizer().Normalize(
            source,
            "Northstar Dynamics Corporate Governance Handbook");
        var headings = new HeadingDetector().Detect(normalized);
        var sectioned = new SectionBuilder().Build(normalized, headings);
        var parents = new ParentChunkBuilder().Build(sectioned, options, counter);
        var children = new ChildChunkBuilder().Build(
            parents,
            normalized.Title,
            options,
            counter);
        var result = new ChunkingResult(
            normalized.DocumentId,
            normalized.SourceSchemaVersion,
            normalized.SourceContentHash,
            options.ChunkerVersion,
            parents,
            children,
            sectioned.Warnings);
        var inventory = new ChunkingSourceInventory(
            normalized.DocumentId,
            source.Pages.SelectMany(page => page.Components).Select(component => component.ComponentId),
            normalized.Blocks.Select(block => new ChunkingSourceBlock(
                block.BlockId,
                block.Text,
                counter.Count(block.Text),
                block.ComponentIds)),
            normalized.Notices);

        var validation = new ChunkingResultValidator().Validate(
            result,
            inventory,
            options,
            counter);

        Assert.True(validation.IsValid, string.Join("; ", validation.Errors.Select(error => error.Code)));
        Assert.Equal("6.0", result.ChunkerVersion);
        Assert.Equal(1.0, validation.SourceToParentCoverage);
        Assert.Equal(1.0, validation.ParentToChildCoverage);
        Assert.All(children, child => Assert.True(child.TokenCount <= options.ChildMaxTokens));
        Assert.All(parents, parent => Assert.True(parent.Title is null || parent.Title.Length <= 255));

        var facts = manifest.RootElement.GetProperty("facts");
        foreach (var fact in facts.EnumerateArray())
        {
            var page = fact.GetProperty("page").GetInt32();
            var text = fact.GetProperty("text").GetString()!;
            Assert.True(
                ContainsSemantic(PageChildText(children, page), text),
                $"{fact.GetProperty("id").GetString()} is not answerable.");
        }

        var incidentList = manifest.RootElement.GetProperty("incident_list");
        foreach (var item in incidentList.EnumerateArray())
        {
            var page = item.GetProperty("page").GetInt32();
            var expected = $"{item.GetProperty("number").GetInt32()}. {item.GetProperty("text").GetString()}";
            Assert.True(
                ContainsSemantic(PageChildText(children, page), expected),
                $"Incident item {item.GetProperty("number").GetInt32()} is not answerable.");
        }

        var qaCases = manifest.RootElement.GetProperty("qa_cases");
        foreach (var qa in qaCases.EnumerateArray())
        {
            var pages = qa.GetProperty("pages").EnumerateArray().Select(page => page.GetInt32()).ToArray();
            var answerText = PageChildText(children, pages);
            foreach (var expected in qa.GetProperty("answer_contains").EnumerateArray())
            {
                var expectedText = expected.GetString()!;
                Assert.True(
                    ContainsSemantic(answerText, expectedText),
                    $"{qa.GetProperty("id").GetString()} is missing answer text '{expectedText}' on the declared page(s).");
            }
        }

        var expectedPaths = manifest.RootElement.GetProperty("expected_headings");
        var actualPaths = parents.Select(parent => parent.SectionPath.ToArray()).ToArray();
        foreach (var expected in expectedPaths.EnumerateArray())
        {
            var path = expected.GetProperty("path").EnumerateArray()
                .Select(part => Normalize(part.GetString()!))
                .ToArray();
            Assert.True(
                actualPaths.Any(actual =>
                    actual.Select(Normalize).SequenceEqual(path, StringComparer.Ordinal)),
                $"Missing expected path: {string.Join(" > ", path)}");
        }

        var expectedPathKeys = expectedPaths.EnumerateArray()
            .Select(expected => string.Join(
                " > ",
                expected.GetProperty("path").EnumerateArray()
                    .Select(part => Normalize(part.GetString()!))))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        var actualPathKeys = actualPaths
            .Select(path => string.Join(" > ", path.Select(Normalize)))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(expectedPathKeys, actualPathKeys);

        Assert.DoesNotContain(parents, parent =>
            parent.Title is not null &&
            manifest.RootElement.GetProperty("negative_headings").EnumerateArray()
                .Any(item => string.Equals(
                    Normalize(item.GetProperty("text").GetString()!),
                    Normalize(parent.Title),
                    StringComparison.Ordinal)));
        Assert.DoesNotContain(parents, parent =>
            parent.Content.Contains("Internal Use Only", StringComparison.Ordinal) ||
            parent.Content.Contains("Controlled Copy - Page", StringComparison.Ordinal));
        Assert.DoesNotContain(children, child =>
            child.RawContent.Contains("Internal Use Only", StringComparison.Ordinal) ||
            child.RawContent.Contains("Controlled Copy - Page", StringComparison.Ordinal));

        var tableRows = manifest.RootElement.GetProperty("recovery_table").GetProperty("rows");
        var pageSixText = PageChildText(children, 6);
        foreach (var row in tableRows.EnumerateArray())
        {
            var expectedRow = string.Join(
                " ",
                row.EnumerateArray().Select(cell => cell.GetString()!));
            Assert.True(ContainsSemantic(pageSixText, expectedRow), $"Missing recovery row: {expectedRow}");
        }

        var service17 = "Service 17 Accounting Medium 20 hours 4 hours 01:30 UTC Validate recovery workflow 17 BC-463";
        Assert.Single(children, child => ContainsSemantic(child.RawContent, "Service 17"));
        Assert.True(ContainsSemantic(pageSixText, service17));
    }

    [SkippableFact]
    [Trait("Category", "Phase7ReleaseGate")]
    [Trait("TestType", "ReleaseGate")]
    [Trait("Dependency", "PostgreSql")]
    public async Task Fresh_document28_graph_persists_and_reads_back_from_postgresql()
    {
        var connection = Environment.GetEnvironmentVariable("CORPMIND_CHUNKING_TEST_CONNECTION");
        var structuredPath = Environment.GetEnvironmentVariable("PHASE7_STRUCTURED_JSON");
        var manifestPath = Environment.GetEnvironmentVariable("PHASE7_MANIFEST");
        Skip.If(
            string.IsNullOrWhiteSpace(connection) ||
            string.IsNullOrWhiteSpace(structuredPath) ||
            string.IsNullOrWhiteSpace(manifestPath),
            "Set the Phase 7 fixture paths and CORPMIND_CHUNKING_TEST_CONNECTION to run the PostgreSQL release gate.");

        await using var database = await PostgreSqlIntegrationDatabase.CreateAsync();
        var structuredJson = File.ReadAllText(structuredPath!);
        await using (var context = database.CreateContext())
        {
            var department = new Department { Id = 428, Name = "Phase 7 Document 28" };
            var document = new Document
            {
                Id = 28,
                OriginalFileName = "corp_chunking_stress_test_v1.pdf",
                Title = "Northstar Dynamics Corporate Governance Handbook",
                StorageKey = "phase7/document-28.pdf",
                DepartmentId = department.Id,
                Department = department,
                Status = "approved",
                OcrStatus = "completed"
            };
            var structured = JsonSerializer.Deserialize<StructuredDocumentDto>(structuredJson)
                ?? throw new InvalidOperationException("Fresh structured OCR output is invalid.");
            context.Departments.Add(department);
            context.Documents.Add(document);
            context.OcrResults.Add(new OcrResult
            {
                DocumentId = document.Id,
                TotalPages = structured.TotalPages,
                PageAverageConfidence = 0.99,
                OverallLevel = "high",
                SchemaVersion = structured.SchemaVersion,
                StructuredDocumentJson = structuredJson,
                Status = "completed"
            });
            await context.SaveChangesAsync();

            var documents = new DocumentRepository(context);
            var chunks = new ChunkRepository(context);
            using var unitOfWork = new UnitOfWork(
                new UserRepository(context),
                documents,
                chunks,
                context);
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
        }

        await using var verification = database.CreateContext();
        var active = await verification.ChunkingRuns
            .AsNoTracking()
            .Include(run => run.ParentChunks)
            .ThenInclude(parent => parent.ChildChunks)
            .SingleAsync(run => run.DocumentId == 28 && run.IsActive);

        Assert.Equal(ChunkingRunStatuses.Completed, active.Status);
        Assert.Equal("1.1", active.SourceSchemaVersion);
        Assert.Equal("6.0", active.ChunkerVersion);
        Assert.Equal(1.0, active.SourceCoverage);
        Assert.NotEmpty(active.ParentChunks);
        Assert.NotEmpty(active.ParentChunks.SelectMany(parent => parent.ChildChunks));
        Assert.All(active.ParentChunks, parent =>
            Assert.All(parent.ChildChunks, child => Assert.True(child.TokenCount <= 500)));

        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath!));
        var childText = string.Join(
            " ",
            active.ParentChunks
                .SelectMany(parent => parent.ChildChunks)
                .Select(child => child.RawContent + " " + child.ContextualizedContent));
        foreach (var fact in manifest.RootElement.GetProperty("facts").EnumerateArray())
        {
            Assert.True(
                ContainsSemantic(childText, fact.GetProperty("text").GetString()!),
                $"Persisted Document 28 graph is missing {fact.GetProperty("id").GetString()}.");
        }
    }

    private static string PageChildText(
        IReadOnlyList<ChildChunkResult> children,
        params int[] pages) =>
        string.Join(
            " ",
            children
                .Where(child => pages.Any(page => child.PageFrom <= page && child.PageTo >= page))
                .OrderBy(child => child.Ordinal)
                .Select(child => child.RawContent + " " + child.ContextualizedContent));

    private static bool ContainsSemantic(string haystack, string needle) =>
        Normalize(haystack).Contains(Normalize(needle), StringComparison.Ordinal);

    private static string Normalize(string value) =>
        string.Join(
            " ",
            TokenPattern.Matches(value.ToLowerInvariant()).Select(match => match.Value));
}
