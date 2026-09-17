using CorpMindAI.Application.Chunking.Children;
using CorpMindAI.Application.Chunking.Models;
using CorpMindAI.Application.Interfaces;
using Xunit;

namespace CorpMindAI.Tests.Unit.Chunking;

[Trait("TestType", "Unit")]
public sealed class ChildChunkBuilderTests
{
    private readonly ChildChunkBuilder _builder = new();
    private readonly ITokenCounter _tokenCounter = new WordTokenCounter();

    [Fact]
    public void One_parent_creates_children_linked_to_that_parent()
    {
        var parent = Parent("parent-1", 42, "Section 1", "One sentence. Two sentence.");

        var children = Build(parent);

        Assert.NotEmpty(children);
        Assert.All(children, child =>
        {
            Assert.Equal(parent.Id, child.ParentChunkId);
            Assert.Equal(parent.DocumentId, child.DocumentId);
        });
    }

    [Fact]
    public void Multiple_paragraphs_are_split_without_empty_children()
    {
        var parent = Parent(
            "parent-1",
            42,
            "Section 1",
            "First paragraph.\n\nSecond paragraph.");

        var children = Build(parent, new ChunkingOptions { ChildTargetTokens = 9, ChildMaxTokens = 11, ChildOverlapTokens = 0 });

        Assert.Equal(2, children.Count);
        Assert.All(children, child => Assert.False(string.IsNullOrWhiteSpace(child.RawContent)));
    }

    [Fact]
    public void Raw_content_is_preserved_and_context_is_english_first()
    {
        var parent = Parent("parent-1", 42, "Chapter I > Section 1", "Access is required.");

        var child = Assert.Single(Build(parent, documentTitle: "Company Handbook"));

        Assert.Equal("Access is required.", child.RawContent);
        Assert.Equal(
            "Document: Company Handbook\nSection: Chapter I > Section 1\nContent: Access is required.",
            child.ContextualizedContent);
    }

    [Fact]
    public void Missing_title_and_section_use_deterministic_fallbacks()
    {
        var parent = Parent("parent-1", 42, "", "Unsectioned body.");

        var child = Assert.Single(Build(parent, documentTitle: null));

        Assert.Contains("Document: Untitled document", child.ContextualizedContent);
        Assert.Contains("Section: Unsectioned content", child.ContextualizedContent);
    }

    [Fact]
    public void Parent_larger_than_target_is_split_at_sentence_boundaries()
    {
        var parent = Parent(
            "parent-1",
            42,
            "Section 1",
            "One sentence. Two sentence. Three sentence. Four sentence.");

        var children = Build(
            parent,
            new ChunkingOptions { ChildTargetTokens = 11, ChildMaxTokens = 13, ChildOverlapTokens = 2 });

        Assert.True(children.Count >= 2);
        Assert.All(children, child => Assert.True(child.TokenCount <= 13));
        Assert.Contains("One sentence.", children[0].RawContent);
        Assert.Contains("Three sentence.", children[^1].RawContent);
    }

    [Fact]
    public void Overlap_reuses_complete_sentences_within_one_parent()
    {
        var parent = Parent(
            "parent-1",
            42,
            "Section 1",
            "One sentence. Two sentence. Three sentence. Four sentence.");

        var children = Build(
            parent,
            new ChunkingOptions { ChildTargetTokens = 11, ChildMaxTokens = 13, ChildOverlapTokens = 2 });

        Assert.True(children.Count >= 2);
        Assert.Contains("Two sentence.", children[0].RawContent);
        Assert.Contains("Two sentence.", children[1].RawContent);
    }

    [Fact]
    public void Overlap_does_not_cross_parent_boundaries()
    {
        var first = Parent("parent-1", 42, "Section 1", "First sentence. Second sentence.");
        var second = Parent("parent-2", 42, "Section 2", "Third sentence. Fourth sentence.");

        var children = Build(
            new[] { first, second },
            new ChunkingOptions { ChildTargetTokens = 9, ChildMaxTokens = 11, ChildOverlapTokens = 1 });

        Assert.DoesNotContain(children, child =>
            child.ParentChunkId == first.Id && child.RawContent.Contains("Third sentence.", StringComparison.Ordinal));
        Assert.DoesNotContain(children, child =>
            child.ParentChunkId == second.Id && child.RawContent.Contains("Second sentence.", StringComparison.Ordinal));
    }

    [Fact]
    public void Sentence_longer_than_hard_limit_is_split_by_token_counter()
    {
        var parent = Parent("parent-1", 42, "Section 1", "one two three four five six");

        var children = Build(
            parent,
            new ChunkingOptions { ChildTargetTokens = 10, ChildMaxTokens = 10, ChildOverlapTokens = 0 });

        Assert.Equal(2, children.Count);
        Assert.All(children, child => Assert.True(child.TokenCount <= 10));
        Assert.Equal("one two three four five six", string.Join(" ", children.Select(child => child.RawContent)));
    }

    [Fact]
    public void English_abbreviations_do_not_create_false_sentence_boundaries()
    {
        var parent = Parent(
            "parent-1",
            42,
            "Section 1",
            "Dr. Smith reviewed the U.S. policy, e.g. the access rule. The rule applies.");

        var children = Build(parent, new ChunkingOptions { ChildTargetTokens = 100, ChildMaxTokens = 120 });

        var child = Assert.Single(children);
        Assert.Contains("Dr. Smith", child.RawContent);
        Assert.Contains("U.S. policy", child.RawContent);
        Assert.Contains("e.g. the access rule.", child.RawContent);
    }

    [Fact]
    public void Oversized_atomic_parent_is_split_without_losing_content()
    {
        var parent = Parent(
            "parent-atomic",
            42,
            "Section 1",
            "one two three four five six",
            isAtomic: true);

        var children = Build(
            parent,
            new ChunkingOptions { ChildTargetTokens = 8, ChildMaxTokens = 10, ChildOverlapTokens = 0 });

        Assert.True(children.Count > 1);
        Assert.All(children, child =>
        {
            Assert.False(child.IsAtomic);
            Assert.True(child.TokenCount <= 10);
        });
        Assert.Equal(parent.Content, string.Join(" ", children.Select(child => child.RawContent)));
    }

    [Fact]
    public void Atomic_table_children_split_only_between_logical_rows()
    {
        var parent = Parent(
            "parent-table",
            42,
            "Recovery Matrix",
            "Service 1 Owner 1 Critical 8 hours\nService 2 Owner 2 High 12 hours\nService 3 Owner 3 Medium 16 hours",
            isAtomic: true);

        var children = Build(
            parent,
            new ChunkingOptions { ChildTargetTokens = 15, ChildMaxTokens = 20, ChildOverlapTokens = 0 });

        Assert.True(children.Count >= 2);
        Assert.All(children, child =>
        {
            Assert.All(child.RawContent.Split('\n', StringSplitOptions.RemoveEmptyEntries),
                line => Assert.StartsWith("Service ", line, StringComparison.Ordinal));
        });
        Assert.Equal(
            parent.Content.Split('\n'),
            children.SelectMany(child => child.RawContent.Split('\n', StringSplitOptions.RemoveEmptyEntries)));
    }

    [Fact]
    public void Recovery_table_keeps_service_17_wholly_inside_one_child()
    {
        var header = "Service Business Owner Priority RTO RPO Backup Recovery Validation Exception";
        var rows = Enumerable.Range(1, 18)
            .Select(index => $"Service {index} Owner {index} Priority {index} RTO {index} RPO {index} Backup {index} Validation {index} Exception {index}")
            .ToArray();
        var parent = Parent(
            "parent-recovery-table",
            28,
            "Recovery Matrix",
            string.Join('\n', new[] { header }.Concat(rows)),
            pageFrom: 6,
            pageTo: 6,
            isAtomic: true);

        var children = Build(
            parent,
            new ChunkingOptions { ChildTargetTokens = 34, ChildMaxTokens = 40, ChildOverlapTokens = 0 });

        Assert.All(children, child => Assert.True(child.TokenCount <= 40));
        var service17 = rows[16];
        Assert.Contains(children, child => child.RawContent.Contains(service17, StringComparison.Ordinal));
        Assert.DoesNotContain(children, child =>
            child.RawContent.Contains("Service 17", StringComparison.Ordinal) &&
            !child.RawContent.Contains(service17, StringComparison.Ordinal));
        Assert.Equal(
            new[] { header }.Concat(rows),
            children.SelectMany(child => child.RawContent.Split('\n', StringSplitOptions.RemoveEmptyEntries)));
    }

    [Fact]
    public void Oversized_table_row_keeps_header_context_in_contextualized_children()
    {
        const string header = "Service Business Owner Priority RTO RPO Backup Recovery Validation Exception";
        const string oversizedRow =
            "Service 17 Accounting Medium 20 hours 4 hours 01:30 UTC Validate recovery workflow 17 " +
            "with an extended validation procedure that documents every control evidence artifact and approval";
        const string nextRow = "Service 18 HR Low 24 hours 6 hours 02:30 UTC Validate recovery workflow 18 BC-464";
        var content = $"{header}\n{oversizedRow}\n{nextRow}";
        var firstRowStart = header.Length + 1;
        var secondRowStart = firstRowStart + oversizedRow.Length + 1;
        var parent = new ParentChunkResult(
            "parent-table-long-row",
            42,
            0,
            "Recovery Matrix",
            new[] { "Recovery Matrix" },
            content,
            content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length,
            6,
            6,
            new[] { "table" },
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(content))).ToLowerInvariant(),
            isAtomic: true,
            sourceSpans: new[]
            {
                new ChunkSourceSpan(0, header.Length, 6, new[] { "table" }),
                new ChunkSourceSpan(firstRowStart, oversizedRow.Length, 6, new[] { "table" }),
                new ChunkSourceSpan(secondRowStart, nextRow.Length, 6, new[] { "table" }),
            });

        var children = Build(
            parent,
            new ChunkingOptions { ChildTargetTokens = 25, ChildMaxTokens = 25, ChildOverlapTokens = 0 });

        Assert.True(children.Count >= 3);
        Assert.All(children, child => Assert.True(child.TokenCount <= 25));
        Assert.Contains(children, child =>
            child.RawContent.Contains("Service 17", StringComparison.Ordinal) &&
            child.ContextualizedContent.Contains($"Table header: {header}", StringComparison.Ordinal));
    }

    [Fact]
    public void Deep_section_context_is_bounded_but_full_metadata_is_preserved()
    {
        var parent = Parent(
            "parent-context",
            42,
            "Root > Middle > Leaf",
            "Answer content.");

        var child = Assert.Single(Build(
            parent,
            new ChunkingOptions { ChildTargetTokens = 10, ChildMaxTokens = 10, ChildOverlapTokens = 0 }));

        Assert.Contains("Section: Middle > Leaf", child.ContextualizedContent, StringComparison.Ordinal);
        Assert.DoesNotContain("Root > Middle", child.ContextualizedContent, StringComparison.Ordinal);
        Assert.Equal(new[] { "Root", "Middle", "Leaf" }, child.SectionPath);
    }

    [Fact]
    public void Child_metadata_is_inherited_from_parent()
    {
        var parent = Parent(
            "parent-1",
            42,
            "Section 1",
            "Traceable content.",
            pageFrom: 2,
            pageTo: 4,
            componentIds: new[] { "component-1", "component-2" });

        var child = Assert.Single(Build(parent));

        Assert.Equal(2, child.PageFrom);
        Assert.Equal(4, child.PageTo);
        Assert.Equal(new[] { "component-1", "component-2" }, child.ComponentIds);
        Assert.Equal(parent.SectionPath, child.SectionPath);
    }

    [Fact]
    public void Child_citation_is_narrowed_to_the_source_spans_it_contains()
    {
        const string content = "First page.\n\nSecond page.";
        var parent = new ParentChunkResult(
            "parent-spans", 42, 0, "Section 1", new[] { "Section 1" }, content, 4,
            1, 2, new[] { "component-1", "component-2" },
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(content))).ToLowerInvariant(),
            sourceSpans: new[]
            {
                new ChunkSourceSpan(0, "First page.".Length, 1, new[] { "component-1" }),
                new ChunkSourceSpan("First page.\n\n".Length, "Second page.".Length, 2, new[] { "component-2" })
            });

        var children = Build(parent, new ChunkingOptions
        {
            ChildTargetTokens = 9,
            ChildMaxTokens = 11,
            ChildOverlapTokens = 0
        });

        Assert.Equal(2, children.Count);
        Assert.Equal((1, 1), (children[0].PageFrom, children[0].PageTo));
        Assert.Equal(new[] { "component-1" }, children[0].ComponentIds);
        Assert.Equal((2, 2), (children[1].PageFrom, children[1].PageTo));
        Assert.Equal(new[] { "component-2" }, children[1].ComponentIds);
    }

    [Fact]
    public void Multiple_documents_are_not_mixed()
    {
        var first = Parent("parent-1", 42, "Section 1", "First document.");
        var second = Parent("parent-2", 43, "Section 1", "Second document.");

        var children = Build(new[] { second, first });

        Assert.Contains(children, child => child.DocumentId == 42 && child.RawContent == "First document.");
        Assert.Contains(children, child => child.DocumentId == 43 && child.RawContent == "Second document.");
        Assert.DoesNotContain(children, child =>
            child.DocumentId == 42 && child.RawContent.Contains("Second document.", StringComparison.Ordinal));
        Assert.DoesNotContain(children, child =>
            child.DocumentId == 43 && child.RawContent.Contains("First document.", StringComparison.Ordinal));
    }

    [Fact]
    public void Child_ids_and_hashes_are_deterministic()
    {
        var parent = Parent("parent-1", 42, "Section 1", "One sentence. Two sentence.");

        var first = Build(parent);
        var second = Build(parent);

        Assert.Equal(first.Select(child => child.Id), second.Select(child => child.Id));
        Assert.Equal(first.Select(child => child.ContentHash), second.Select(child => child.ContentHash));
        Assert.Equal(first.Select(child => child.RawContent), second.Select(child => child.RawContent));
    }

    [Fact]
    public void Duplicate_parent_ids_are_rejected()
    {
        var parent = Parent("parent-1", 42, "Section 1", "Content.");

        Assert.Throws<ArgumentException>(() => Build(new[] { parent, parent }));
    }

    private IReadOnlyList<ChildChunkResult> Build(
        ParentChunkResult parent,
        ChunkingOptions? options = null,
        string? documentTitle = "Company Handbook") =>
        Build(new[] { parent }, options, documentTitle);

    private IReadOnlyList<ChildChunkResult> Build(
        IReadOnlyList<ParentChunkResult> parents,
        ChunkingOptions? options = null,
        string? documentTitle = "Company Handbook") =>
        _builder.Build(
            parents,
            documentTitle,
            options ?? new ChunkingOptions(),
            _tokenCounter);

    private static ParentChunkResult Parent(
        string id,
        int documentId,
        string section,
        string content,
        int pageFrom = 1,
        int pageTo = 1,
        IEnumerable<string>? componentIds = null,
        bool isAtomic = false) => new(
        id: id,
        documentId: documentId,
        ordinal: 0,
        title: section,
        sectionPath: string.IsNullOrWhiteSpace(section)
            ? Array.Empty<string>()
            : section.Split(" > ", StringSplitOptions.RemoveEmptyEntries),
        content: content,
        tokenCount: content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length,
        pageFrom: pageFrom,
        pageTo: pageTo,
        componentIds: componentIds ?? new[] { id + "-component" },
        contentHash: Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(content))).ToLowerInvariant(),
        isAtomic: isAtomic);

    private sealed class WordTokenCounter : ITokenCounter
    {
        public int Count(string text) =>
            text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
    }
}
