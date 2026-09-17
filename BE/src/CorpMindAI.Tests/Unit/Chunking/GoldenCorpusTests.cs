using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CorpMindAI.Application.Chunking.Children;
using CorpMindAI.Application.Chunking.Models;
using CorpMindAI.Application.Chunking.Normalization;
using CorpMindAI.Application.Chunking.Parents;
using CorpMindAI.Application.Chunking.Sections;
using CorpMindAI.Application.Chunking.Validation;
using CorpMindAI.Application.DTOs.Document;
using CorpMindAI.Application.Interfaces;
using CorpMindAI.Infrastructure.Services;
using Xunit;

namespace CorpMindAI.Tests.Unit.Chunking;

[Trait("TestType", "Unit")]
public sealed class GoldenCorpusTests
{
    private static readonly IReadOnlyDictionary<string, string> ExpectedSnapshotHashes =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["policy-access-control"] = "57b67fc55d9e9d82cbc537001be8fdf713b2970b7dc11b3c1ab0548794b4f080",
            ["policy-record-retention"] = "b06c4ce9fe7b6161c4fbc5ed8484644232f6dca8beb22d315f2299a77f7e07f5",
            ["contract-managed-services"] = "0fd99908a6e41a8b3c7dbd1d6865952ec77c4ed19fb97cf67c79c17f1da38a45",
            ["contract-mutual-nda"] = "697a2888650ca23249f4b443595d06d09bcdd06113e92338cc949a58e5f20c12",
            ["tables-financial-summary"] = "dd43450c8a4ccac18c5c2527b8ff6000bd14015c488efc0b884f41b6e1030aa0",
            ["tables-inventory-register"] = "668a783e2e6a998db4167ed4a5e58dd65a57b6d696d0cc16faa9156e6ea09dda",
            ["scan-low-quality-handbook"] = "028caa7956ac6d3519bce4549c2c47b2316111bd24a5f319e1fda096ebd163c8",
            ["scan-low-quality-safety"] = "8465d8c1f46bde1017756508a43c2893888fa4c78badf41f79956e871a96c7cf",
            ["two-column-benefits-handbook"] = "2157f09cb043cbb4c0da50df63e3bfc7d4e0dfbde6e284d09032a0af66cb8dc3",
            ["two-column-risk-report"] = "d226ce9d14984ba298dd944fddce8e20978625a3b15c6024607411d2ea244e9a",
            ["unsectioned-executive-memo"] = "285e71803e05411ea9b1f7a76c6d72266239b30bc7251149401911eec91651ec",
            ["unsectioned-long-notice"] = "d137ae0aa3d9dcfbf4290d0e523c3fbc62178c39708e90329978c472b77e0ced"
        };

    private static readonly Regex CoveragePattern = new(
        @"source_coverage\.value: (?<value>\d+\.\d+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly StructuredDocumentNormalizer _normalizer = new();
    private readonly HeadingDetector _headingDetector = new();
    private readonly SectionBuilder _sectionBuilder = new();
    private readonly ParentChunkBuilder _parentBuilder = new();
    private readonly ChildChunkBuilder _childBuilder = new();
    private readonly ChunkingResultValidator _validator = new();
    private readonly ITokenCounter _tokenCounter = new ChunkingTokenCounter();

    public static IEnumerable<object[]> FixtureNames =>
        LoadFixtures().Select(fixture => new object[] { fixture.Name });

    [Fact]
    public void Corpus_contains_the_required_document_families_and_edge_cases()
    {
        var fixtures = LoadFixtures();

        Assert.Equal(12, fixtures.Count);
        Assert.Equal(2, fixtures.Count(fixture => fixture.Name.StartsWith("policy-", StringComparison.Ordinal)));
        Assert.Equal(2, fixtures.Count(fixture => fixture.Name.StartsWith("contract-", StringComparison.Ordinal)));
        Assert.Equal(2, fixtures.Count(fixture => fixture.Name.StartsWith("tables-", StringComparison.Ordinal)));
        Assert.Equal(2, fixtures.Count(fixture => fixture.Name.StartsWith("scan-low-quality-", StringComparison.Ordinal)));
        Assert.Equal(2, fixtures.Count(fixture => fixture.Name.StartsWith("two-column-", StringComparison.Ordinal)));
        Assert.Equal(2, fixtures.Count(fixture => fixture.Name.StartsWith("unsectioned-", StringComparison.Ordinal)));

        Assert.Contains(fixtures, fixture => fixture.DocumentJson.Contains("1.1.1 Legal Holds", StringComparison.Ordinal));
        Assert.Contains(fixtures, fixture => fixture.DocumentJson.Contains("seven years", StringComparison.Ordinal));
        Assert.Contains(fixtures, fixture => fixture.DocumentJson.Contains("Section 1 Access Control", StringComparison.Ordinal));
        Assert.Contains(fixtures, fixture => fixture.DocumentJson.Contains("synthetic_region", StringComparison.Ordinal));
        Assert.Contains(fixtures, fixture => fixture.DocumentJson.Contains("Customers who submit", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(FixtureNames))]
    public void Json_fixture_matches_its_complete_deterministic_snapshot(string fixtureName)
    {
        var fixture = Assert.Single(LoadFixtures(), item => item.Name == fixtureName);
        var first = Run(fixture.DocumentJson, fixture.Title);
        var reordered = Run(ReorderAsJson(fixture.DocumentJson), fixture.Title);

        Assert.True(first.Validation.IsValid);
        Assert.NotEmpty(first.Result.Parents);
        Assert.NotEmpty(first.Result.Children);
        Assert.Equal(1.0, Coverage(first.Validation));
        var snapshotJson = JsonSerializer.Serialize(CreateSnapshot(first));
        var reorderedSnapshotJson = JsonSerializer.Serialize(CreateSnapshot(reordered));
        Assert.Equal(snapshotJson, reorderedSnapshotJson);

        Assert.All(first.Result.Parents, parent =>
        {
            Assert.False(string.IsNullOrWhiteSpace(parent.Id));
            Assert.False(string.IsNullOrWhiteSpace(parent.ContentHash));
            Assert.False(string.IsNullOrWhiteSpace(parent.Content));
            Assert.NotEmpty(parent.ComponentIds);
            Assert.True(parent.PageFrom <= parent.PageTo);
            Assert.Equal(_tokenCounter.Count(parent.Content), parent.TokenCount);
        });
        Assert.All(first.Result.Children, child =>
        {
            Assert.Contains(first.Result.Parents, parent => parent.Id == child.ParentChunkId);
            Assert.False(string.IsNullOrWhiteSpace(child.Id));
            Assert.False(string.IsNullOrWhiteSpace(child.ContentHash));
            Assert.False(string.IsNullOrWhiteSpace(child.RawContent));
            Assert.NotEmpty(child.ComponentIds);
            Assert.True(child.PageFrom <= child.PageTo);
            Assert.Equal(_tokenCounter.Count(child.ContextualizedContent), child.TokenCount);
        });

        var snapshotHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(snapshotJson)))
            .ToLowerInvariant();
        Assert.True(
            string.Equals(ExpectedSnapshotHashes[fixtureName], snapshotHash, StringComparison.Ordinal),
            $"Snapshot mismatch for '{fixtureName}'. Actual SHA-256: {snapshotHash}");
    }

    [Fact]
    public void Heading_at_the_end_of_a_page_assigns_the_next_page_to_that_section()
    {
        var fixture = Assert.Single(LoadFixtures(), item => item.Name == "policy-access-control");
        var output = Run(fixture.DocumentJson, fixture.Title);
        var nextPageParent = Assert.Single(
            output.Result.Parents,
            parent => parent.ComponentIds.Contains("doc-1001-p2-c0000", StringComparer.Ordinal));

        Assert.Equal(1, nextPageParent.PageFrom);
        Assert.Equal(2, nextPageParent.PageTo);
        Assert.Equal(
            new[] { "Chapter I Corporate Security", "Section 1 Access Control" },
            nextPageParent.SectionPath);
    }

    [Fact]
    public void Long_sentence_uses_the_child_hard_limit_fallback()
    {
        var fixture = Assert.Single(LoadFixtures(), item => item.Name == "unsectioned-long-notice");
        var output = Run(fixture.DocumentJson, fixture.Title);

        Assert.True(output.Result.Children.Count > 1);
        Assert.All(output.Result.Children, child => Assert.True(child.TokenCount <= Options.ChildMaxTokens));
    }

    private PipelineOutput Run(string structuredDocumentJson, string title)
    {
        var source = JsonSerializer.Deserialize<StructuredDocumentDto>(structuredDocumentJson)
            ?? throw new InvalidOperationException("Golden StructuredDocumentJson is invalid.");
        var normalized = _normalizer.Normalize(source, title);
        var headings = _headingDetector.Detect(normalized);
        var sectioned = _sectionBuilder.Build(normalized, headings);
        var parents = _parentBuilder.Build(sectioned, Options, _tokenCounter);
        var children = _childBuilder.Build(parents, normalized.Title, Options, _tokenCounter);
        var result = new ChunkingResult(
            normalized.DocumentId,
            normalized.SourceSchemaVersion,
            normalized.SourceContentHash,
            Options.ChunkerVersion,
            parents,
            children,
            sectioned.Warnings);
        var inventory = new ChunkingSourceInventory(
            normalized.DocumentId,
            source.Pages.SelectMany(page => page.Components).Select(component => component.ComponentId),
            normalized.Blocks.Select(block => new ChunkingSourceBlock(
                block.BlockId,
                block.Text,
                _tokenCounter.Count(block.Text),
                block.ComponentIds)),
            normalized.Notices);

        return new PipelineOutput(
            result,
            _validator.Validate(result, inventory, Options, _tokenCounter));
    }

    private static ChunkingOptions Options { get; } = new()
    {
        ParentTargetTokens = 80,
        ParentMaxTokens = 120,
        ParentMinTokens = 1,
        ChildTargetTokens = 48,
        ChildMaxTokens = 64,
        ChildOverlapTokens = 8
    };

    private static GoldenSnapshot CreateSnapshot(PipelineOutput output) => new(
        output.Result.DocumentId,
        output.Result.SourceSchemaVersion,
        output.Result.SourceContentHash,
        output.Result.ChunkerVersion,
        output.Result.Parents.Count,
        output.Result.Children.Count,
        output.Result.Parents.OrderBy(parent => parent.Ordinal).Select(parent => new ParentSnapshot(
            parent.Id,
            parent.Ordinal,
            parent.Title,
            parent.SectionPath.ToArray(),
            parent.Content,
            parent.TokenCount,
            parent.PageFrom,
            parent.PageTo,
            parent.ComponentIds.ToArray(),
            parent.ContentHash,
            parent.IsAtomic)).ToArray(),
        output.Result.Children.OrderBy(child => child.Ordinal).Select(child => new ChildSnapshot(
            child.Id,
            child.ParentChunkId,
            child.Ordinal,
            child.SectionPath.ToArray(),
            child.RawContent,
            child.ContextualizedContent,
            child.TokenCount,
            child.PageFrom,
            child.PageTo,
            child.ComponentIds.ToArray(),
            child.ContentHash,
            child.IsAtomic)).ToArray(),
        Coverage(output.Validation),
        output.Validation.IsValid,
        output.Validation.Errors.Select(error => $"{error.Code}:{error.Message}").ToArray(),
        output.Validation.Warnings.ToArray());

    private static double Coverage(ChunkingValidationResult validation)
    {
        var warning = Assert.Single(validation.Warnings, item => item.StartsWith("source_coverage.value:", StringComparison.Ordinal));
        var match = CoveragePattern.Match(warning);
        Assert.True(match.Success, $"Coverage warning has an unexpected format: {warning}");
        return double.Parse(match.Groups["value"].Value, CultureInfo.InvariantCulture);
    }

    private static string ReorderAsJson(string structuredDocumentJson)
    {
        var source = JsonSerializer.Deserialize<StructuredDocumentDto>(structuredDocumentJson)
            ?? throw new InvalidOperationException("Golden StructuredDocumentJson is invalid.");
        source.Pages.Reverse();
        foreach (var page in source.Pages)
            page.Components.Reverse();
        return JsonSerializer.Serialize(source);
    }

    private static IReadOnlyList<GoldenFixture> LoadFixtures()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Chunking", "GoldenCorpus", "documents.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.EnumerateArray().Select(element => new GoldenFixture(
            element.GetProperty("name").GetString()
                ?? throw new InvalidOperationException("Golden fixture name is required."),
            element.GetProperty("title").GetString()
                ?? throw new InvalidOperationException("Golden fixture title is required."),
            element.GetProperty("document").GetRawText())).ToArray();
    }

    private sealed record GoldenFixture(string Name, string Title, string DocumentJson);
    private sealed record PipelineOutput(ChunkingResult Result, ChunkingValidationResult Validation);
    private sealed record GoldenSnapshot(
        int DocumentId,
        string SourceSchemaVersion,
        string SourceContentHash,
        string ChunkerVersion,
        int ParentCount,
        int ChildCount,
        IReadOnlyList<ParentSnapshot> Parents,
        IReadOnlyList<ChildSnapshot> Children,
        double Coverage,
        bool ValidationIsValid,
        IReadOnlyList<string> ValidationErrors,
        IReadOnlyList<string> ValidationWarnings);
    private sealed record ParentSnapshot(
        string Id,
        int Ordinal,
        string? Title,
        IReadOnlyList<string> SectionPath,
        string Content,
        int TokenCount,
        int PageFrom,
        int PageTo,
        IReadOnlyList<string> ComponentIds,
        string ContentHash,
        bool IsAtomic);
    private sealed record ChildSnapshot(
        string Id,
        string ParentChunkId,
        int Ordinal,
        IReadOnlyList<string> SectionPath,
        string RawContent,
        string ContextualizedContent,
        int TokenCount,
        int PageFrom,
        int PageTo,
        IReadOnlyList<string> ComponentIds,
        string ContentHash,
        bool IsAtomic);
}
