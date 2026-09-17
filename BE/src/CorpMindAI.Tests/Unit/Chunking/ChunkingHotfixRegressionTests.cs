using System.Text.Json;
using CorpMindAI.Application.Chunking.Children;
using CorpMindAI.Application.Chunking.Models;
using CorpMindAI.Application.Chunking.Normalization;
using CorpMindAI.Application.Chunking.Parents;
using CorpMindAI.Application.Chunking.Sections;
using CorpMindAI.Application.Chunking.Validation;
using CorpMindAI.Application.DTOs.Document;
using CorpMindAI.Infrastructure.Services;
using Xunit;

namespace CorpMindAI.Tests.Unit.Chunking;

[Trait("TestType", "Unit")]
public sealed class ChunkingHotfixRegressionTests
{
    [Fact]
    public void Numbered_list_fixture_remains_atomic_and_inherits_the_valid_section()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "Chunking",
            "Fixtures",
            "numbered-list-heading-regression.json");
        var json = File.ReadAllText(path);
        var source = JsonSerializer.Deserialize<StructuredDocumentDto>(json)
            ?? throw new InvalidOperationException("Regression fixture is invalid.");

        var normalizer = new StructuredDocumentNormalizer();
        var detector = new HeadingDetector();
        var sections = new SectionBuilder();
        var parents = new ParentChunkBuilder();
        var children = new ChildChunkBuilder();
        var validator = new ChunkingResultValidator();
        var counter = new ChunkingTokenCounter();
        var options = new ChunkingOptions();
        var normalized = normalizer.Normalize(source, "Incident Response Test Document");
        var headings = detector.Detect(normalized);
        var sectioned = sections.Build(normalized, headings);
        var parentResults = parents.Build(sectioned, options, counter);
        var childResults = children.Build(parentResults, normalized.Title, options, counter);
        var result = new ChunkingResult(
            normalized.DocumentId,
            normalized.SourceSchemaVersion,
            normalized.SourceContentHash,
            options.ChunkerVersion,
            parentResults,
            childResults,
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

        var listHeading = headings.SingleOrDefault(heading => heading.BlockId == "doc-27-p4-c0004");
        Assert.Null(listHeading);
        var listParent = Assert.Single(
            result.Parents,
            parent => parent.ComponentIds.Contains("doc-27-p4-c0004", StringComparer.Ordinal));
        Assert.True(listParent.IsAtomic);
        Assert.Equal(new[] { "5. Incident Response Checklist" }, listParent.SectionPath);
        Assert.True(listParent.Title is null || listParent.Title.Length <= 255);
        var listChildren = result.Children
            .Where(child => child.ParentChunkId == listParent.Id)
            .OrderBy(child => child.Ordinal)
            .ToArray();
        Assert.NotEmpty(listChildren);
        Assert.All(listChildren, listChild =>
        {
            Assert.Equal(listParent.SectionPath, listChild.SectionPath);
            Assert.True(listChild.TokenCount <= options.ChildMaxTokens);
        });
        if (listChildren.Length == 1)
            Assert.True(listChildren[0].IsAtomic);
        else
            Assert.All(listChildren, listChild => Assert.False(listChild.IsAtomic));
        Assert.DoesNotContain("doc-27-p4-c0004", headings.Select(heading => heading.BlockId));

        var validation = validator.Validate(result, inventory, options, counter);
        Assert.True(validation.IsValid);
        Assert.Equal(1.0, validation.SourceCoverage);
        Assert.Equal(1.0, validation.ParentCoverage);
        Assert.Equal(1.0, validation.ChildCoverage);
        Assert.Contains(validation.Warnings, warning =>
            warning.StartsWith("source_to_parent_coverage.value:", StringComparison.Ordinal));
        Assert.Contains(validation.Warnings, warning =>
            warning.StartsWith("parent_to_child_coverage.value:", StringComparison.Ordinal));
    }

    [Fact]
    public void Document28_dynamic_margin_boilerplate_does_not_reach_parent_or_child_content()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "Chunking",
            "Fixtures",
            "Document28",
            "structured-ocr-v1.json");
        var source = JsonSerializer.Deserialize<StructuredDocumentDto>(File.ReadAllText(path))
            ?? throw new InvalidOperationException("Document 28 fixture is invalid.");

        var normalizer = new StructuredDocumentNormalizer();
        var detector = new HeadingDetector();
        var sections = new SectionBuilder();
        var parents = new ParentChunkBuilder();
        var children = new ChildChunkBuilder();
        var counter = new ChunkingTokenCounter();
        var options = new ChunkingOptions();
        var normalized = normalizer.Normalize(source, "Document 28 test fixture");
        var headings = detector.Detect(normalized);
        var sectioned = sections.Build(normalized, headings);
        var parentResults = parents.Build(sectioned, options, counter);
        var childResults = children.Build(parentResults, normalized.Title, options, counter);

        Assert.Equal(18, normalized.Notices.Count);
        Assert.DoesNotContain(normalized.Blocks, block =>
            block.Text.Contains("Internal Use Only", StringComparison.Ordinal));
        Assert.DoesNotContain(normalized.Blocks, block =>
            block.Text.Contains("Controlled Copy - Page", StringComparison.Ordinal));
        Assert.DoesNotContain(parentResults, parent =>
            parent.Content.Contains("Internal Use Only", StringComparison.Ordinal) ||
            parent.Content.Contains("Controlled Copy - Page", StringComparison.Ordinal));
        Assert.DoesNotContain(childResults, child =>
            child.RawContent.Contains("Internal Use Only", StringComparison.Ordinal) ||
            child.RawContent.Contains("Controlled Copy - Page", StringComparison.Ordinal) ||
            child.ContextualizedContent.Contains("Internal Use Only", StringComparison.Ordinal) ||
            child.ContextualizedContent.Contains("Controlled Copy - Page", StringComparison.Ordinal));
        var excludedComponentIds = normalized.Notices
            .Select(notice => notice.ComponentId)
            .ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain(parentResults, parent =>
            parent.ComponentIds.All(componentId => excludedComponentIds.Contains(componentId)));
    }

    [Fact]
    public void Document28_phase5_heading_semantics_preserve_callouts_footnotes_and_roots()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "Chunking",
            "Fixtures",
            "Document28",
            "structured-ocr-v1.json");
        var source = JsonSerializer.Deserialize<StructuredDocumentDto>(File.ReadAllText(path))
            ?? throw new InvalidOperationException("Document 28 fixture is invalid.");

        var normalizer = new StructuredDocumentNormalizer();
        var detector = new HeadingDetector();
        var sections = new SectionBuilder();
        var normalized = normalizer.Normalize(source, "Document 28 test fixture");
        var headings = detector.Detect(normalized);
        var sectioned = sections.Build(normalized, headings);

        Assert.DoesNotContain(headings, heading => heading.BlockId == "doc-28-p7-c0007");
        Assert.DoesNotContain(headings, heading => heading.BlockId == "doc-28-p8-c0007");
        Assert.Equal(
            new[] { "IV. Recovery Exercise Procedure", "B. Execution" },
            sectioned.GetSectionPath("doc-28-p7-c0007"));
        Assert.Equal(
            new[] { "IV. Recovery Exercise Procedure", "C. Closure" },
            sectioned.GetSectionPath("doc-28-p7-c0011"));
        Assert.Equal(
            new[] { "Chapter II - Incident Response", "2. Incident Response Checklist" },
            sectioned.GetSectionPath("doc-28-p4-c0002"));
        Assert.Equal(
            new[] { "Chapter V - Vendor Risk Management", "Temporary Exception" },
            sectioned.GetSectionPath("doc-28-p8-c0007"));
    }
}
