using System.Globalization;
using System.Text.Json;
using CorpMindAI.Application.Chunking.Models;
using CorpMindAI.Application.DTOs.Document;

namespace CorpMindAI.Application.Chunking.Validation;

// Validates the source-side contract that must be true before a v6 graph is
// accepted.  This is deliberately independent from parent/child validation:
// source fidelity and table structure are properties of OCR output, not of
// the chunks produced from it.
public static class StructuredDocumentContractValidator
{
    private const string SchemaV1 = "1.0";
    private const string SchemaV11 = "1.1";
    private const string V6 = "6.0";

    public static ChunkingValidationResult Validate(
        StructuredDocumentDto source,
        ChunkingOptions options)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        var errors = new List<ChunkingValidationError>();
        var warnings = new List<string>();
        var documentId = ParseDocumentId(source.DocumentId);

        if (!string.Equals(source.SchemaVersion, SchemaV1, StringComparison.Ordinal) &&
            !string.Equals(source.SchemaVersion, SchemaV11, StringComparison.Ordinal))
        {
            Add(
                errors,
                "structured_schema.unsupported",
                $"Structured OCR schema '{source.SchemaVersion}' is not supported for replay.",
                documentId);
        }

        if (string.Equals(source.SchemaVersion, SchemaV1, StringComparison.Ordinal) &&
            string.Equals(options.ChunkerVersion, V6, StringComparison.Ordinal))
        {
            warnings.Add(
                "structured_schema.legacy_replay: Schema 1.0 is readable for audit/replay; " +
                "new OCR processing emits schema 1.1.");
        }

        var sourceFidelity = ValidateSourceFidelity(source, documentId, errors, warnings);

        if (string.Equals(options.ChunkerVersion, V6, StringComparison.Ordinal))
        {
            foreach (var page in source.Pages ?? new List<StructuredPageDto>())
            {
                foreach (var component in page.Components ?? new List<StructuredComponentDto>())
                {
                    if (!IsTable(component) || HasUsableTableRows(component))
                        continue;

                    Add(
                        errors,
                        "TABLE_ROW_BOUNDARIES_UNUSABLE",
                        $"Table component '{component.ComponentId}' does not contain usable row/cell boundaries.",
                        documentId);
                }
            }
        }

        return new ChunkingValidationResult(
            errors,
            warnings,
            sourceFidelity: sourceFidelity);
    }

    private static double? ValidateSourceFidelity(
        StructuredDocumentDto source,
        int? documentId,
        ICollection<ChunkingValidationError> errors,
        ICollection<string> warnings)
    {
        if (!source.SourceFidelity.HasValue ||
            source.SourceFidelity.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            var pageValues = (source.Pages ?? new List<StructuredPageDto>())
                .Where(IsNativeEvaluablePage)
                .Select(page => page.SourceFidelity)
                .Where(value => value.HasValue)
                .Select(value => value!.Value)
                .ToArray();
            if (pageValues.Length > 0)
            {
                var pageFidelity = pageValues.Min();
                warnings.Add(
                    $"source_fidelity.value: {pageFidelity.ToString("F6", CultureInfo.InvariantCulture)}; " +
                    $"threshold: 1.000000; evaluable_pages: {pageValues.Length}; " +
                    $"total_pages: {source.TotalPages}; codes: .");
                if (pageFidelity + 1e-12 < 1.0)
                {
                    Add(
                        errors,
                        "SOURCE_TEXT_FIDELITY_BELOW_THRESHOLD",
                        $"Source fidelity {pageFidelity:F6} is below threshold 1.000000.",
                        documentId);
                }
                return pageFidelity;
            }

            warnings.Add(
                "source_fidelity.not_reported: No native-source fidelity record was supplied.");
            return null;
        }

        var root = source.SourceFidelity.Value;
        if (root.ValueKind != JsonValueKind.Object)
        {
            Add(
                errors,
                "SOURCE_FIDELITY_INVALID",
                "Structured OCR source_fidelity must be a JSON object.",
                documentId);
            return null;
        }

        var fidelity = ReadNullableDouble(root, "fidelity");
        var threshold = ReadNullableDouble(root, "threshold") ?? 1.0;
        if (threshold <= 0 || threshold > 1 || double.IsNaN(threshold) || double.IsInfinity(threshold))
        {
            Add(
                errors,
                "SOURCE_FIDELITY_THRESHOLD_INVALID",
                $"Source fidelity threshold {threshold.ToString(CultureInfo.InvariantCulture)} must be greater than zero and no greater than one.",
                documentId);
            threshold = 1.0;
        }
        var evaluablePages = ReadInt(root, "evaluable_pages") ?? 0;
        var totalPages = ReadInt(root, "total_pages") ?? source.TotalPages;
        var codes = ReadCodes(root, "codes");

        foreach (var page in source.Pages ?? new List<StructuredPageDto>())
        {
            foreach (var code in page.FidelityCodes ?? new List<string>())
            {
                if (!string.IsNullOrWhiteSpace(code))
                    codes.Add(code.Trim());
            }
        }

        codes = codes
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(code => code, StringComparer.Ordinal)
            .ToList();

        if (evaluablePages > 0 && !fidelity.HasValue)
        {
            Add(
                errors,
                "SOURCE_FIDELITY_NOT_REPORTED",
                "Native-source fidelity is missing for evaluable pages.",
                documentId);
        }

        foreach (var code in codes)
        {
            Add(
                errors,
                code,
                $"Structured OCR source fidelity reported failure code '{code}'.",
                documentId);
        }

        if (fidelity.HasValue && fidelity.Value + 1e-12 < threshold)
        {
            if (!codes.Contains("SOURCE_TEXT_FIDELITY_BELOW_THRESHOLD", StringComparer.Ordinal))
            {
                Add(
                    errors,
                    "SOURCE_TEXT_FIDELITY_BELOW_THRESHOLD",
                    $"Source fidelity {fidelity.Value:F6} is below threshold {threshold:F6}.",
                    documentId);
            }
        }

        warnings.Add(
            $"source_fidelity.value: {(fidelity.HasValue ? fidelity.Value.ToString("F6", CultureInfo.InvariantCulture) : "not_evaluable")}; " +
            $"threshold: {threshold.ToString("F6", CultureInfo.InvariantCulture)}; " +
            $"evaluable_pages: {evaluablePages}; total_pages: {totalPages}; " +
            $"codes: {string.Join(',', codes)}.");

        return fidelity;
    }

    private static bool IsNativeEvaluablePage(StructuredPageDto page) =>
        page.NativeLineCount > 0 ||
        string.Equals(page.ExtractionMode, "born_digital", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(page.ExtractionMode, "mixed", StringComparison.OrdinalIgnoreCase);

    private static bool IsTable(StructuredComponentDto component) =>
        string.Equals(component.Type?.Trim(), "table", StringComparison.OrdinalIgnoreCase) ||
        (component.Metadata?.SourceLayoutType?.Contains("table", StringComparison.OrdinalIgnoreCase) ?? false);

    private static bool HasUsableTableRows(StructuredComponentDto component)
    {
        if (!component.Rows.HasValue || component.Rows.Value.ValueKind != JsonValueKind.Array ||
            component.Rows.Value.GetArrayLength() == 0 ||
            !component.Cells.HasValue || component.Cells.Value.ValueKind != JsonValueKind.Array ||
            component.Cells.Value.GetArrayLength() == 0)
        {
            return false;
        }

        var indexes = new List<int>();
        foreach (var row in component.Rows.Value.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object ||
                !row.TryGetProperty("row_index", out var rowIndexValue) ||
                !rowIndexValue.TryGetInt32(out var rowIndex) ||
                rowIndex < 0 ||
                !row.TryGetProperty("text", out var textValue) ||
                textValue.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(textValue.GetString()))
            {
                return false;
            }

            indexes.Add(rowIndex);
        }

        var expected = Enumerable.Range(0, indexes.Count).ToArray();
        if (!indexes
            .OrderBy(index => index)
            .SequenceEqual(expected))
        {
            return false;
        }

        var cellRows = new HashSet<int>();
        foreach (var cell in component.Cells.Value.EnumerateArray())
        {
            if (cell.ValueKind != JsonValueKind.Object ||
                !cell.TryGetProperty("row_index", out var rowIndexValue) ||
                !rowIndexValue.TryGetInt32(out var rowIndex) ||
                rowIndex < 0 ||
                !cell.TryGetProperty("column_index", out var columnIndexValue) ||
                !columnIndexValue.TryGetInt32(out var columnIndex) ||
                columnIndex < 0)
            {
                return false;
            }

            cellRows.Add(rowIndex);
        }

        return expected.All(cellRows.Contains);
    }

    private static List<string> ReadCodes(JsonElement root, string propertyName)
    {
        var result = new List<string>();
        if (!root.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                result.Add(item.GetString()!.Trim());
        }

        return result;
    }

    private static double? ReadNullableDouble(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value) ||
            value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ||
            !value.TryGetDouble(out var parsed))
        {
            return null;
        }

        return parsed;
    }

    private static int? ReadInt(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value) || !value.TryGetInt32(out var parsed))
            return null;
        return parsed;
    }

    private static int? ParseDocumentId(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    private static void Add(
        ICollection<ChunkingValidationError> errors,
        string code,
        string message,
        int? documentId) =>
        errors.Add(new ChunkingValidationError(code, message, documentId));
}
