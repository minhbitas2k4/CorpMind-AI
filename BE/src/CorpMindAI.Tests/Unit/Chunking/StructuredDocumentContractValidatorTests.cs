using System.Text.Json;
using CorpMindAI.Application.Chunking.Models;
using CorpMindAI.Application.Chunking.Validation;
using CorpMindAI.Application.DTOs.Document;
using Xunit;

namespace CorpMindAI.Tests.Unit.Chunking;

[Trait("TestType", "Unit")]
public sealed class StructuredDocumentContractValidatorTests
{
    [Fact]
    public void Incomplete_born_digital_extraction_fails_with_stable_fidelity_codes()
    {
        var source = Source(
            fidelity: 0.5,
            threshold: 1.0,
            evaluablePages: 1,
            totalPages: 1,
            codes: new[] { "SOURCE_LINE_UNACCOUNTED" },
            page: new StructuredPageDto
            {
                PageNumber = 1,
                ExtractionMode = "born_digital",
                NativeLineCount = 2,
                AccountedLineCount = 1
            });

        var result = StructuredDocumentContractValidator.Validate(source, Options());

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Code == "SOURCE_LINE_UNACCOUNTED");
        Assert.Contains(result.Errors, error => error.Code == "SOURCE_TEXT_FIDELITY_BELOW_THRESHOLD");
    }

    [Fact]
    public void Scanned_only_page_is_not_failed_for_missing_native_text()
    {
        var source = Source(
            fidelity: null,
            threshold: 1.0,
            evaluablePages: 0,
            totalPages: 1,
            codes: Array.Empty<string>(),
            page: new StructuredPageDto
            {
                PageNumber = 1,
                ExtractionMode = "scanned",
                NativeLineCount = 0,
                AccountedLineCount = 0
            });

        var result = StructuredDocumentContractValidator.Validate(source, Options());

        Assert.True(result.IsValid);
        Assert.Null(result.SourceFidelity);
    }

    [Fact]
    public void V6_rejects_table_without_usable_row_boundaries()
    {
        var table = new StructuredComponentDto
        {
            ComponentId = "doc-42-p1-c0000",
            DocumentId = "42",
            PageNumber = 1,
            Type = "table",
            Text = "Header Value",
            Confidence = 1.0
        };
        var source = Source(
            fidelity: 1.0,
            threshold: 1.0,
            evaluablePages: 1,
            totalPages: 1,
            codes: Array.Empty<string>(),
            page: new StructuredPageDto
            {
                PageNumber = 1,
                ExtractionMode = "born_digital",
                NativeLineCount = 1,
                AccountedLineCount = 1,
                Components = new() { table }
            });

        var result = StructuredDocumentContractValidator.Validate(source, Options());

        Assert.Contains(result.Errors, error => error.Code == "TABLE_ROW_BOUNDARIES_UNUSABLE");
    }

    [Fact]
    public void V6_accepts_table_with_contiguous_nonempty_rows_and_cells()
    {
        var table = new StructuredComponentDto
        {
            ComponentId = "doc-42-p1-c0000",
            DocumentId = "42",
            PageNumber = 1,
            Type = "table",
            Text = "Header Value\nRow Value",
            Confidence = 1.0,
            Rows = JsonSerializer.SerializeToElement(new[]
            {
                new { row_index = 0, text = "Header Value" },
                new { row_index = 1, text = "Row Value" }
            }),
            Cells = JsonSerializer.SerializeToElement(new[]
            {
                new { row_index = 0, column_index = 0, text = "Header" },
                new { row_index = 0, column_index = 1, text = "Value" },
                new { row_index = 1, column_index = 0, text = "Row" },
                new { row_index = 1, column_index = 1, text = "Value" }
            })
        };
        var source = Source(
            fidelity: 1.0,
            threshold: 1.0,
            evaluablePages: 1,
            totalPages: 1,
            codes: Array.Empty<string>(),
            page: new StructuredPageDto
            {
                PageNumber = 1,
                ExtractionMode = "born_digital",
                NativeLineCount = 2,
                AccountedLineCount = 2,
                Components = new() { table }
            });

        var result = StructuredDocumentContractValidator.Validate(source, Options());

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Schema_1_1_is_readable_by_the_normalizer()
    {
        var source = Source(
            fidelity: 1.0,
            threshold: 1.0,
            evaluablePages: 0,
            totalPages: 1,
            codes: Array.Empty<string>(),
            page: new StructuredPageDto
            {
                PageNumber = 1,
                ExtractionMode = "scanned"
            });

        var normalizer = new CorpMindAI.Application.Chunking.Normalization.StructuredDocumentNormalizer();

        var result = normalizer.Normalize(source, "Schema 1.1 replay");

        Assert.Equal("1.1", result.SourceSchemaVersion);
    }

    private static ChunkingOptions Options() => new() { ChunkerVersion = "6.0" };

    private static StructuredDocumentDto Source(
        double? fidelity,
        double threshold,
        int evaluablePages,
        int totalPages,
        IReadOnlyList<string> codes,
        StructuredPageDto page) =>
        new()
        {
            SchemaVersion = "1.1",
            DocumentId = "42",
            TotalPages = totalPages,
            Pages = new() { page },
            SourceFidelity = JsonSerializer.SerializeToElement(new
            {
                fidelity,
                threshold,
                evaluable_pages = evaluablePages,
                total_pages = totalPages,
                codes
            })
        };
}
