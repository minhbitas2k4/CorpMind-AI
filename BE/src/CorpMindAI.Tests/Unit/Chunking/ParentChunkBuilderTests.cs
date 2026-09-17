using CorpMindAI.Application.Chunking.Models;
using CorpMindAI.Application.Chunking.Parents;
using CorpMindAI.Application.Chunking.Sections;
using CorpMindAI.Application.Interfaces;
using Xunit;

namespace CorpMindAI.Tests.Unit.Chunking;

[Trait("TestType", "Unit")]
public sealed class ParentChunkBuilderTests
{
    private readonly HeadingDetector _headingDetector = new();
    private readonly SectionBuilder _sectionBuilder = new();
    private readonly ParentChunkBuilder _builder = new();
    private readonly ITokenCounter _tokenCounter = new WordTokenCounter();

    [Fact]
    public void One_section_keeps_heading_and_body_in_parent()
    {
        var result = Build(
            Block("heading", 1, 0, "Section 1 Access Control"),
            Block("body", 1, 1, "Access requirements"));

        var parent = Assert.Single(result);
        Assert.Contains("Section 1 Access Control", parent.Content);
        Assert.Contains("Access requirements", parent.Content);
        Assert.Equal(new[] { "Section 1 Access Control" }, parent.SectionPath);
        Assert.Equal("Section 1 Access Control", parent.Title);
    }

    [Fact]
    public void Multiple_sections_are_not_combined()
    {
        var result = Build(
            Block("one", 1, 0, "Section 1 Access"),
            Block("one-body", 1, 1, "Access body"),
            Block("two", 1, 2, "Section 2 Auditing"),
            Block("two-body", 1, 3, "Audit body"));

        Assert.Equal(2, result.Count);
        Assert.Equal(new[] { "Section 1 Access" }, result[0].SectionPath);
        Assert.Equal(new[] { "Section 2 Auditing" }, result[1].SectionPath);
        Assert.DoesNotContain("Audit body", result[0].Content);
    }

    [Fact]
    public void Section_can_span_multiple_pages_and_records_range()
    {
        var result = Build(
            Block("heading", 1, 0, "Chapter I Policy"),
            Block("first", 1, 1, "First page body"),
            Block("second", 3, 0, "Third page body"));

        var parent = Assert.Single(result);
        Assert.Equal(1, parent.PageFrom);
        Assert.Equal(3, parent.PageTo);
        Assert.Contains("Third page body", parent.Content);
    }

    [Fact]
    public void Document_without_heading_has_empty_section_path()
    {
        var result = Build(
            new[]
            {
                Block("one", 1, 0, "Plain body"),
                Block("two", 1, 1, "More plain body")
            },
            "Company Handbook");

        var parent = Assert.Single(result);
        Assert.Empty(parent.SectionPath);
        Assert.Equal("Company Handbook", parent.Title);
    }

    [Fact]
    public void Small_parent_is_merged_until_target_or_section_boundary()
    {
        var result = Build(
            new ChunkingOptions { ParentTargetTokens = 5, ParentMaxTokens = 8, ParentMinTokens = 5 },
            Block("heading", 1, 0, "Section 1"),
            Block("one", 1, 1, "one two"),
            Block("two", 1, 2, "three four"));

        Assert.Single(result);
        Assert.Contains("three four", result[0].Content);
        Assert.True(result[0].TokenCount <= 8);
    }

    [Fact]
    public void Large_section_is_split_at_block_boundaries()
    {
        var result = Build(
            new ChunkingOptions { ParentTargetTokens = 4, ParentMaxTokens = 5, ParentMinTokens = 1 },
            Block("heading", 1, 0, "Section 1"),
            Block("one", 1, 1, "one two"),
            Block("two", 1, 2, "three four"),
            Block("three", 1, 3, "five six"));

        Assert.True(result.Count >= 2);
        Assert.All(result, parent => Assert.True(parent.TokenCount <= 5));
        Assert.Equal(
            "Section 1\n\none two\n\nthree four\n\nfive six",
            string.Join("\n\n", result.SelectMany(parent => parent.Content.Split("\n\n"))));
    }

    [Fact]
    public void Oversized_atomic_block_is_kept_as_one_parent()
    {
        var result = Build(
            new ChunkingOptions { ParentTargetTokens = 3, ParentMaxTokens = 3, ParentMinTokens = 1 },
            Block("table", 1, 0, "one two three four five", type: "table", isAtomic: true));

        var parent = Assert.Single(result);
        Assert.Equal(5, parent.TokenCount);
        Assert.Equal("one two three four five", parent.Content);
        Assert.True(parent.IsAtomic);
    }

    [Fact]
    public void Atomic_block_is_not_merged_with_adjacent_heading_or_body()
    {
        var result = Build(
            Block("heading", 1, 0, "Section 1"),
            Block("table", 1, 1, "Cell A | Cell B", type: "table", isAtomic: true),
            Block("body", 1, 2, "Following body"));

        var atomic = Assert.Single(result, parent => parent.IsAtomic);
        Assert.Equal("Cell A | Cell B", atomic.Content);
        Assert.Equal(new[] { "table" }, atomic.ComponentIds);
        Assert.DoesNotContain(result.Where(parent => !parent.IsAtomic), parent =>
            parent.ComponentIds.Contains("table", StringComparer.Ordinal));
    }

    [Fact]
    public void Atomic_table_parent_keeps_row_level_source_spans()
    {
        var rows = new[]
        {
            new NormalizedTableRow(0, 1, "Header Value", new[] { "table" }),
            new NormalizedTableRow(1, 1, "Row 1 Value", new[] { "table" }),
            new NormalizedTableRow(2, 1, "Row 2 Value", new[] { "table" }),
        };
        var table = new NormalizedBlock(
            42,
            "table",
            1,
            0,
            "table",
            "Header Value\nRow 1 Value\nRow 2 Value",
            0.96,
            new[] { "table" },
            sourceLayoutType: "table",
            isAtomic: true,
            tableRows: rows);

        var parent = Assert.Single(Build(table));

        Assert.Equal(3, parent.SourceSpans.Count);
        Assert.Equal(
            rows.Select(row => row.Text),
            parent.SourceSpans.Select(span => parent.Content.Substring(span.Start, span.Length)));
        Assert.All(parent.SourceSpans, span => Assert.Equal(new[] { "table" }, span.ComponentIds));
    }

    [Fact]
    public void Oversized_non_atomic_block_is_split_without_losing_text()
    {
        var result = Build(
            new ChunkingOptions { ParentTargetTokens = 3, ParentMaxTokens = 3, ParentMinTokens = 1 },
            Block("long", 1, 0, "one two three four five"));

        Assert.True(result.Count >= 2);
        Assert.All(result, parent => Assert.True(parent.TokenCount <= 3));
        Assert.Equal("one two three four five", string.Join(" ", result.Select(parent => parent.Content)));
    }

    [Fact]
    public void Empty_blocks_do_not_create_empty_parents()
    {
        var result = Build(
            Block("empty", 1, 0, "   "),
            Block("body", 1, 1, "Actual content"));

        var parent = Assert.Single(result);
        Assert.False(string.IsNullOrWhiteSpace(parent.Content));
        Assert.DoesNotContain("empty", parent.ComponentIds);
    }

    [Fact]
    public void Reading_order_is_preserved_even_when_input_is_reordered()
    {
        var first = Build(
            Block("second", 1, 1, "Second"),
            Block("first", 1, 0, "First"));
        var second = Build(
            Block("first", 1, 0, "First"),
            Block("second", 1, 1, "Second"));

        Assert.Equal(first.Select(parent => parent.Content), second.Select(parent => parent.Content));
        Assert.Contains("First\n\nSecond", first[0].Content);
    }

    [Fact]
    public void Component_ids_are_complete_and_unique()
    {
        var result = Build(
            Block("heading", 1, 0, "Section 1", componentIds: new[] { "c1", "c2" }),
            Block("body", 1, 1, "Body", componentIds: new[] { "c2", "c3" }));

        Assert.Equal(new[] { "c1", "c2", "c3" }, result[0].ComponentIds);
    }

    [Fact]
    public void Parent_ids_and_hashes_are_deterministic_and_unique()
    {
        var blocks = new[]
        {
            Block("one", 1, 0, "Section 1"),
            Block("body", 1, 1, "Same content")
        };

        var first = Build(blocks);
        var second = Build(blocks);

        Assert.Equal(first.Select(parent => parent.Id), second.Select(parent => parent.Id));
        Assert.Equal(first.Select(parent => parent.ContentHash), second.Select(parent => parent.ContentHash));
        Assert.Equal(first.Count, first.Select(parent => parent.Id).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Parent_ids_are_namespaced_by_the_chunking_run()
    {
        var first = Build(Document(new[] { Block("body", 1, 0, "Stable content") }));
        var secondDocument = new NormalizedDocument(
            42,
            "1.0",
            "different-source-hash",
            new[] { Block("body", 1, 0, "Stable content") });
        var second = Build(secondDocument);

        Assert.NotEqual(Assert.Single(first).Id, Assert.Single(second).Id);
        Assert.Equal(first[0].ContentHash, second[0].ContentHash);
    }

    [Fact]
    public void Different_sections_keep_distinct_paths_and_documents_are_not_mixed()
    {
        var firstDocument = Document(
            new[]
            {
                Block("first", 1, 0, "Chapter I First"),
                Block("first-body", 1, 1, "First body")
            },
            documentId: 42);
        var secondDocument = Document(
            new[]
            {
                Block("second", 1, 0, "Chapter I Second", documentId: 43),
                Block("second-body", 1, 1, "Second body", documentId: 43)
            },
            documentId: 43);

        var first = Build(firstDocument);
        var second = Build(secondDocument);

        Assert.All(first, parent => Assert.Equal(42, parent.DocumentId));
        Assert.All(second, parent => Assert.Equal(43, parent.DocumentId));
        Assert.DoesNotContain(first, parent => parent.Content.Contains("Second", StringComparison.Ordinal));
    }

    [Fact]
    public void No_child_chunks_are_created_in_phase_four()
    {
        var parents = Build(Block("body", 1, 0, "Body only"));

        Assert.NotEmpty(parents);
        Assert.All(parents, parent => Assert.DoesNotContain("child", parent.Id, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Negative_token_counter_result_is_rejected()
    {
        var document = Sectioned(
            Document(new[] { Block("body", 1, 0, "Body") }));

        Assert.Throws<InvalidOperationException>(() => _builder.Build(
            document,
            new ChunkingOptions(),
            new FixedTokenCounter(-1)));
    }

    private IReadOnlyList<ParentChunkResult> Build(
        params NormalizedBlock[] blocks) => Build(new ChunkingOptions(), blocks);

    private IReadOnlyList<ParentChunkResult> Build(
        NormalizedBlock[] blocks,
        string? title) => Build(Document(blocks, title: title), new ChunkingOptions());

    private IReadOnlyList<ParentChunkResult> Build(
        ChunkingOptions options,
        params NormalizedBlock[] blocks) => Build(Document(blocks), options);

    private IReadOnlyList<ParentChunkResult> Build(
        NormalizedDocument document,
        ChunkingOptions? options = null) =>
        _builder.Build(Sectioned(document), options ?? new ChunkingOptions(), _tokenCounter);

    private SectionedDocument Sectioned(NormalizedDocument document)
    {
        var headings = _headingDetector.Detect(document);
        return _sectionBuilder.Build(document, headings);
    }

    private static NormalizedDocument Document(
        NormalizedBlock[] blocks,
        int documentId = 42,
        string? title = null) => new(
        documentId: documentId,
        sourceSchemaVersion: "1.0",
        sourceContentHash: $"source-{documentId}",
        blocks: blocks,
        title: title);

    private static NormalizedBlock Block(
        string id,
        int page,
        int order,
        string text,
        string type = "text",
        bool isAtomic = false,
        IEnumerable<string>? componentIds = null,
        int documentId = 42) => new(
        documentId: documentId,
        blockId: id,
        pageNumber: page,
        readingOrder: order,
        blockType: type,
        text: text,
        confidence: 0.8,
        componentIds: componentIds ?? new[] { id },
        sourceLayoutType: type,
        isAtomic: isAtomic);

    private sealed class WordTokenCounter : ITokenCounter
    {
        public int Count(string text) =>
            text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
    }

    private sealed class FixedTokenCounter : ITokenCounter
    {
        private readonly int _count;

        public FixedTokenCounter(int count) => _count = count;

        public int Count(string text) => _count;
    }
}
