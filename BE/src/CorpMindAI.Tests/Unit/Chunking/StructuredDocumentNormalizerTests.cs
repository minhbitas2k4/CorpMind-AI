using System.Text;
using System.Text.Json;
using CorpMindAI.Application.Chunking.Models;
using CorpMindAI.Application.Chunking.Normalization;
using CorpMindAI.Application.DTOs.Document;
using Xunit;

namespace CorpMindAI.Tests.Unit.Chunking;

[Trait("TestType", "Unit")]
public sealed class StructuredDocumentNormalizerTests
{
    private readonly StructuredDocumentNormalizer _normalizer = new();

    [Fact]
    public void Null_source_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => _normalizer.Normalize(null!));
    }

    [Fact]
    public void Invalid_document_id_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => _normalizer.Normalize(Document("invalid")));
    }

    [Fact]
    public void Unsupported_schema_version_is_rejected()
    {
        var source = Document("42");
        source.SchemaVersion = "2.0";

        Assert.Throws<ArgumentException>(() => _normalizer.Normalize(source));
    }

    [Fact]
    public void Pages_and_components_are_sorted_deterministically()
    {
        var source = Document(
            "42",
            Page(2, Component("doc-42-p2-c0001", 2, 1, "second")),
            Page(1,
                Component("doc-42-p1-c0002", 1, 2, "second in order"),
                Component("doc-42-p1-c0001", 1, 1, "first in order")));

        var result = _normalizer.Normalize(source);

        Assert.Equal(
            new[] { "doc-42-p1-c0001", "doc-42-p1-c0002", "doc-42-p2-c0001" },
            result.Blocks.Select(block => block.BlockId));
    }

    [Fact]
    public void Duplicate_component_ids_are_rejected_across_pages()
    {
        var source = Document(
            "42",
            Page(1, Component("duplicate", 1, 0, "one")),
            Page(2, Component("duplicate", 2, 0, "two")));

        Assert.Throws<ArgumentException>(() => _normalizer.Normalize(source));
    }

    [Fact]
    public void Component_document_and_page_mismatch_are_rejected()
    {
        var wrongDocument = Document("42", Page(1, Component("component", 1, 0, "text")));
        wrongDocument.Pages[0].Components[0].DocumentId = "99";

        Assert.Throws<ArgumentException>(() => _normalizer.Normalize(wrongDocument));

        var wrongPage = Document("42", Page(1, Component("component", 2, 0, "text")));
        Assert.Throws<ArgumentException>(() => _normalizer.Normalize(wrongPage));
    }

    [Fact]
    public void Unicode_whitespace_and_newlines_are_normalized_without_ascii_folding()
    {
        var source = Document(
            "42",
            Page(1, Component("component", 1, 0, "  Cafe\u0301\t©  $100  \r\n\r\n\r\n  policy  ")));

        var result = _normalizer.Normalize(source);

        Assert.Equal("Café © $100\n\npolicy", result.Blocks[0].Text);
    }

    [Fact]
    public void English_line_wrap_hyphenation_is_handled_conservatively()
    {
        var source = Document("42", Page(1, Component("component", 1, 0, "well-\r\nknown")));

        var result = _normalizer.Normalize(source);

        Assert.Equal("well-known", result.Blocks[0].Text);
    }

    [Fact]
    public void English_abbreviations_and_numeric_references_are_preserved()
    {
        const string text = "Dr. Smith reviewed Section 1.2, e.g. the U.S. policy and value 3.14.";
        var source = Document("42", Page(1, Component("component", 1, 0, text)));

        var result = _normalizer.Normalize(source);

        Assert.Equal(text, result.Blocks[0].Text);
    }

    [Fact]
    public void Low_confidence_is_retained_and_empty_component_has_an_exclusion_notice()
    {
        var lowConfidence = Component("low", 1, 0, "low confidence", confidence: 0.01);
        var unknown = Component("unknown", 1, 1, "", type: "equation");
        var source = Document("42", Page(1, lowConfidence, unknown));

        var result = _normalizer.Normalize(source);

        Assert.Contains(result.Blocks, block => block.BlockId == "low");
        Assert.DoesNotContain(result.Blocks, block => block.BlockId == "unknown");
        var notice = Assert.Single(result.Notices, item => item.ComponentId == "unknown");
        Assert.Contains("Empty component", notice.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Lists_and_tables_are_marked_atomic()
    {
        var source = Document(
            "42",
            Page(1,
                Component("list", 1, 0, "- one\n- two", type: "list"),
                Component("table", 1, 1, "Header\nValue", type: "table")));

        var result = _normalizer.Normalize(source);

        Assert.True(result.Blocks.Single(block => block.BlockId == "list").IsAtomic);
        Assert.True(result.Blocks.Single(block => block.BlockId == "table").IsAtomic);
    }

    [Fact]
    public void Structured_table_rows_are_retained_for_row_level_source_spans()
    {
        var table = Component("table", 1, 0, "Header Value\nRow 1 Value\nRow 2 Value", type: "table");
        table.Rows = JsonDocument.Parse("""
            [
              {"row_index":0,"page_number":1,"text":"Header Value"},
              {"row_index":1,"page_number":1,"text":"Row 1 Value"},
              {"row_index":2,"page_number":1,"text":"Row 2 Value"}
            ]
            """).RootElement.Clone();

        var result = _normalizer.Normalize(Document("42", Page(1, table)));
        var block = Assert.Single(result.Blocks);

        Assert.Equal(new[] { "Header Value", "Row 1 Value", "Row 2 Value" },
            block.TableRows.Select(row => row.Text));
        Assert.All(block.TableRows, row => Assert.Equal(1, row.PageNumber));
    }

    [Fact]
    public void Table_source_layout_type_is_atomic_even_when_component_type_is_text()
    {
        var table = Component("table-layout", 1, 0, "Header\nValue", type: "text");
        table.Metadata!.SourceLayoutType = "table";
        var source = Document("42", Page(1, table));

        var result = _normalizer.Normalize(source);

        Assert.True(result.Blocks.Single().IsAtomic);
    }

    [Fact]
    public void Figure_caption_is_included_once_without_asset_path()
    {
        var figure = Component("figure", 1, 0, "Revenue", type: "figure");
        figure.Asset = new AssetMetadataDto { Path = "secret/image.png" };
        figure.Caption = new FigureCaptionDto { Text = "Figure 1" };
        var source = Document("42", Page(1, figure));

        var result = _normalizer.Normalize(source);

        Assert.Equal("Revenue\nCaption: Figure 1", result.Blocks[0].Text);
        Assert.DoesNotContain("secret/image.png", result.Blocks[0].Text);
    }

    [Fact]
    public void Repeated_margin_headers_and_page_numbers_are_excluded_with_notices()
    {
        var source = Document(
            "42",
            Page(1,
                Component("h1", 1, 0, "Acme Corporation", bbox: new[] { 0.1, 0.05, 0.9, 0.1 }),
                Component("body1", 1, 1, "Policy body", bbox: new[] { 0.1, 0.3, 0.9, 0.4 }),
                Component("p1", 1, 2, "Page 1", bbox: new[] { 0.1, 0.9, 0.9, 0.95 })),
            Page(2,
                Component("h2", 2, 0, "Acme Corporation", bbox: new[] { 0.1, 0.05, 0.9, 0.1 }),
                Component("body2", 2, 1, "More policy body", bbox: new[] { 0.1, 0.3, 0.9, 0.4 }),
                Component("p2", 2, 2, "Page 2", bbox: new[] { 0.1, 0.9, 0.9, 0.95 })));

        var result = _normalizer.Normalize(source);

        Assert.Equal(new[] { "body1", "body2" }, result.Blocks.Select(block => block.BlockId));
        Assert.Equal(4, result.Notices.Count);
        Assert.Contains(result.Notices, notice => notice.ComponentId == "h1");
        Assert.Contains(result.Notices, notice => notice.ComponentId == "p2");
    }

    [Fact]
    public void Dynamic_page_template_is_excluded_only_when_repeated_in_the_same_margin()
    {
        var pages = Enumerable.Range(1, 3)
            .Select(pageNumber => Page(
                pageNumber,
                Component($"header-{pageNumber}", pageNumber, 0,
                    "Northstar Dynamics Ltd. - Internal Use Only",
                    bbox: new[] { 0.1, 0.02, 0.9, 0.04 }),
                Component($"body-{pageNumber}", pageNumber, 1,
                    $"The ordinary value {pageNumber} of 3 remains searchable.",
                    bbox: new[] { 0.1, 0.3, 0.9, 0.4 }),
                Component($"body-template-{pageNumber}", pageNumber, 2,
                    "Controlled Copy - Page 99 of 99",
                    bbox: new[] { 0.1, 0.4, 0.9, 0.5 }),
                Component($"footer-{pageNumber}", pageNumber, 3,
                    $"Controlled Copy - Page {pageNumber} of 3",
                    bbox: new[] { 0.1, 0.96, 0.9, 0.98 })))
            .ToArray();

        var result = _normalizer.Normalize(Document("42", pages));

        Assert.Equal(
            new[] { "body-1", "body-template-1", "body-2", "body-template-2", "body-3", "body-template-3" },
            result.Blocks.Select(block => block.BlockId));
        Assert.Equal(6, result.Notices.Count);
        Assert.Equal(
            new[] { "Controlled Copy - Page 1 of 3", "Controlled Copy - Page 2 of 3", "Controlled Copy - Page 3 of 3" },
            result.Notices
                .Where(notice => notice.ComponentId.StartsWith("footer-", StringComparison.Ordinal))
                .Select(notice => notice.Text));
        Assert.All(
            result.Notices.Where(notice => notice.ComponentId.StartsWith("footer-", StringComparison.Ordinal)),
            notice => Assert.Contains("dynamic page", notice.Reason, StringComparison.OrdinalIgnoreCase));
        Assert.All(
            result.Blocks.Where(block =>
                block.BlockId.StartsWith("body-", StringComparison.Ordinal) &&
                !block.BlockId.StartsWith("body-template-", StringComparison.Ordinal)),
            block => Assert.Contains("remains searchable", block.Text, StringComparison.Ordinal));
        Assert.All(
            result.Blocks.Where(block => block.BlockId.StartsWith("body-template-", StringComparison.Ordinal)),
            block => Assert.Equal("Controlled Copy - Page 99 of 99", block.Text));
    }

    [Fact]
    public void Margin_template_seen_on_one_page_is_retained()
    {
        var source = Document(
            "42",
            Page(1, Component(
                "footer-1", 1, 0, "Controlled Copy - Page 1 of 1",
                bbox: new[] { 0.1, 0.96, 0.9, 0.98 })));

        var result = _normalizer.Normalize(source);

        Assert.Single(result.Blocks);
        Assert.Empty(result.Notices);
    }

    [Fact]
    public void Document28_frozen_fixture_excludes_all_repeated_headers_and_dynamic_footers()
    {
        var fixturePath = Path.Combine(
            AppContext.BaseDirectory,
            "Chunking",
            "Fixtures",
            "Document28",
            "structured-ocr-v1.json");
        var source = JsonSerializer.Deserialize<StructuredDocumentDto>(
            File.ReadAllText(fixturePath));

        Assert.NotNull(source);
        var result = _normalizer.Normalize(source!);

        Assert.Equal(18, result.Notices.Count);
        Assert.Equal(9, result.Notices.Count(notice =>
            notice.Reason.Contains("dynamic page", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(9, result.Notices.Count(notice =>
            notice.Reason.Contains("Repeated page header/footer", StringComparison.Ordinal) &&
            !notice.Reason.Contains("dynamic page", StringComparison.OrdinalIgnoreCase)));
        Assert.DoesNotContain(result.Blocks, block =>
            block.Text.Contains("Internal Use Only", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Blocks, block =>
            block.Text.Contains("Controlled Copy - Page", StringComparison.Ordinal));
        Assert.Contains(result.Blocks, block =>
            block.Text.Contains("15 minutes", StringComparison.Ordinal));
    }

    [Fact]
    public void Source_hash_is_stable_when_input_collections_are_reordered()
    {
        var first = Document(
            "42",
            Page(1,
                Component("b", 1, 1, "B"),
                Component("a", 1, 0, "A")));
        var second = Document(
            "42",
            Page(1,
                Component("a", 1, 0, "A"),
                Component("b", 1, 1, "B")));

        Assert.Equal(
            _normalizer.Normalize(first).SourceContentHash,
            _normalizer.Normalize(second).SourceContentHash);
    }

    [Fact]
    public void Source_hash_changes_when_source_content_changes()
    {
        var first = _normalizer.Normalize(Document("42", Page(1, Component("a", 1, 0, "A"))));
        var second = _normalizer.Normalize(Document("42", Page(1, Component("a", 1, 0, "B"))));

        Assert.NotEqual(first.SourceContentHash, second.SourceContentHash);
        Assert.Equal(64, first.SourceContentHash.Length);
        Assert.Matches("^[0-9a-f]+$", first.SourceContentHash);
    }

    [Fact]
    public void Database_title_is_normalized_preserved_and_part_of_source_identity()
    {
        var source = Document("42", Page(1, Component("a", 1, 0, "Body")));

        var first = _normalizer.Normalize(source, "  Employee   Handbook  ");
        var second = _normalizer.Normalize(source, "Benefits Handbook");

        Assert.Equal("Employee Handbook", first.Title);
        Assert.NotEqual(first.SourceContentHash, second.SourceContentHash);
    }

    [Fact]
    public void Normalizer_is_repeatable_and_does_not_create_chunks_or_headings()
    {
        var source = Document("42", Page(1, Component("a", 1, 0, "Policy")));

        var first = _normalizer.Normalize(source);
        var second = _normalizer.Normalize(source);

        Assert.Equal(first.SourceContentHash, second.SourceContentHash);
        Assert.Equal(first.Blocks.Select(block => block.BlockId), second.Blocks.Select(block => block.BlockId));
        Assert.DoesNotContain(first.Blocks, block => block.BlockType is "parent" or "child" or "heading");
    }

    private static StructuredDocumentDto Document(
        string documentId,
        params StructuredPageDto[] pages) => new()
        {
            SchemaVersion = "1.0",
            DocumentId = documentId,
            TotalPages = pages.Length,
            Pages = pages.ToList(),
        };

    private static StructuredPageDto Page(int pageNumber, params StructuredComponentDto[] components) => new()
    {
        PageNumber = pageNumber,
        Width = 1000,
        Height = 1400,
        RenderDpi = 200,
        Components = components.ToList(),
    };

    private static StructuredComponentDto Component(
        string componentId,
        int pageNumber,
        int readingOrder,
        string text,
        string type = "text",
        double confidence = 0.95,
        double[]? bbox = null) => new()
        {
            ComponentId = componentId,
            DocumentId = "42",
            PageNumber = pageNumber,
            Type = type,
            ReadingOrder = readingOrder,
            Confidence = confidence,
            Text = text,
            Bbox = (bbox ?? new[] { 0.1, 0.3, 0.9, 0.4 }).ToList(),
            NormalizedBbox = (bbox ?? new[] { 0.1, 0.3, 0.9, 0.4 }).ToList(),
            Metadata = new ComponentMetadataDto { SourceLayoutType = type },
        };
}
