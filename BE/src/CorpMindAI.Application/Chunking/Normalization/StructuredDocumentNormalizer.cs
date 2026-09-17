using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CorpMindAI.Application.Chunking.Models;
using CorpMindAI.Application.DTOs.Document;
using CorpMindAI.Application.Interfaces;

namespace CorpMindAI.Application.Chunking.Normalization;

// Converts the structured OCR contract into deterministic, traceable blocks.
// This class deliberately does not detect headings or create parent/child chunks.
public sealed class StructuredDocumentNormalizer : IDocumentNormalizer
{
    private static readonly IReadOnlySet<string> SupportedSchemaVersions =
        new HashSet<string>(StringComparer.Ordinal) { "1.0", "1.1" };
    private const double HeaderBoundary = 0.2;
    private const double FooterBoundary = 0.8;

    private static readonly Regex PageNumberPattern = new(
        @"^(?:page\s+)?\d+(?:\s*(?:of|/)\s*\d+)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex DynamicPageTemplatePattern = new(
        @"\bpage\s+\d+\s+(?<separator>of|/)\s+\d+\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex HorizontalWhitespacePattern = new(
        @"[\t ]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex MarginWhitespacePattern = new(
        @"\s+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ExcessiveBlankLinesPattern = new(
        @"\n{3,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public NormalizedDocument Normalize(StructuredDocumentDto source, string? documentTitle = null)
    {
        ArgumentNullException.ThrowIfNull(source);

        var documentId = ParseDocumentId(source.DocumentId, nameof(source.DocumentId));
        ValidateSchemaVersion(source.SchemaVersion);

        if (source.Pages is null || source.Pages.Count == 0)
            throw new ArgumentException("Structured document must contain at least one page.", nameof(source));

        if (source.TotalPages <= 0)
            throw new ArgumentException("TotalPages must be greater than zero.", nameof(source));

        if (source.TotalPages != source.Pages.Count)
            throw new ArgumentException("TotalPages must match the number of pages.", nameof(source));

        var pages = source.Pages
            .Select(page => page ?? throw new ArgumentException("Page cannot be null.", nameof(source)))
            .OrderBy(page => page.PageNumber)
            .ToArray();

        ValidatePages(pages);

        var candidates = pages
            .SelectMany(page => NormalizePage(page, documentId))
            .OrderBy(candidate => candidate.PageNumber)
            .ThenBy(candidate => candidate.ReadingOrder)
            .ThenBy(candidate => candidate.ComponentId, StringComparer.Ordinal)
            .ToArray();

        var duplicateComponentIds = candidates
            .GroupBy(candidate => candidate.ComponentId, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        if (duplicateComponentIds.Length > 0)
            throw new ArgumentException(
                $"Duplicate component ID(s): {string.Join(", ", duplicateComponentIds)}.");

        var normalizedTitle = string.IsNullOrWhiteSpace(documentTitle)
            ? null
            : NormalizeText(documentTitle);
        var sourceContentHash = ComputeSourceContentHash(
            documentId,
            source.SchemaVersion,
            normalizedTitle,
            candidates);
        var excluded = DetectExcludedCandidates(candidates);
        var notices = excluded
            .Select(item => new NormalizationNotice(
                item.Candidate.ComponentId,
                item.Candidate.PageNumber,
                item.Reason,
                item.Candidate.Text))
            .OrderBy(notice => notice.PageNumber)
            .ThenBy(notice => notice.ComponentId, StringComparer.Ordinal)
            .ToArray();

        var excludedIds = excluded
            .Select(item => item.Candidate.ComponentId)
            .ToHashSet(StringComparer.Ordinal);

        var blocks = candidates
            .Where(candidate => !excludedIds.Contains(candidate.ComponentId))
            .Select(candidate => new NormalizedBlock(
                documentId: documentId,
                blockId: candidate.ComponentId,
                pageNumber: candidate.PageNumber,
                readingOrder: candidate.ReadingOrder,
                blockType: candidate.BlockType,
                text: candidate.Text,
                confidence: candidate.Confidence,
                componentIds: new[] { candidate.ComponentId },
                sourceLayoutType: candidate.SourceLayoutType,
                isAtomic: candidate.IsAtomic,
                tableRows: candidate.TableRows))
            .ToArray();

        return new NormalizedDocument(
            documentId: documentId,
            sourceSchemaVersion: source.SchemaVersion,
            sourceContentHash: sourceContentHash,
            blocks: blocks,
            title: normalizedTitle,
            notices: notices);
    }

    private static IEnumerable<CandidateBlock> NormalizePage(
        StructuredPageDto page,
        int documentId)
    {
        if (page.Components is null)
            throw new ArgumentException($"Page {page.PageNumber} must contain a components collection.");

        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var component in page.Components)
        {
            if (component is null)
                throw new ArgumentException($"Page {page.PageNumber} contains a null component.");

            var componentId = Required(component.ComponentId, "ComponentId");
            if (!seenIds.Add(componentId))
                throw new ArgumentException($"Duplicate component ID '{componentId}' on page {page.PageNumber}.");

            var componentDocumentId = ParseDocumentId(
                component.DocumentId,
                $"Component {componentId} DocumentId");
            if (componentDocumentId != documentId)
                throw new ArgumentException(
                    $"Component '{componentId}' belongs to document {componentDocumentId}, expected {documentId}.");

            if (component.PageNumber != page.PageNumber)
                throw new ArgumentException(
                    $"Component '{componentId}' belongs to page {component.PageNumber}, expected {page.PageNumber}.");

            if (component.ReadingOrder < 0)
                throw new ArgumentOutOfRangeException(
                    $"Component {componentId} ReadingOrder",
                    "ReadingOrder must not be negative.");

            ValidateConfidence(component.Confidence, componentId);

            var blockType = string.IsNullOrWhiteSpace(component.Type)
                ? "unknown"
                : component.Type.Trim().ToLowerInvariant();
            var sourceLayoutType = component.Metadata?.SourceLayoutType;
            var text = BuildComponentText(component);
            var tableRows = ParseTableRows(component, page.PageNumber);

            yield return new CandidateBlock(
                componentId,
                page.PageNumber,
                component.ReadingOrder,
                blockType,
                text,
                component.Confidence,
                string.IsNullOrWhiteSpace(sourceLayoutType) ? null : sourceLayoutType.Trim(),
                IsAtomic(blockType, sourceLayoutType),
                tableRows,
                component.NormalizedBbox ?? new List<double>(),
                component.Bbox ?? new List<double>());
        }
    }

    private static void ValidatePages(IReadOnlyList<StructuredPageDto> pages)
    {
        var seenPageNumbers = new HashSet<int>();
        foreach (var page in pages)
        {
            if (page.PageNumber <= 0)
                throw new ArgumentOutOfRangeException(nameof(page.PageNumber), "PageNumber must be positive.");

            if (!seenPageNumbers.Add(page.PageNumber))
                throw new ArgumentException($"Duplicate page number {page.PageNumber}.");
        }
    }

    private static string BuildComponentText(StructuredComponentDto component)
    {
        var text = NormalizeText(component.Text);
        var caption = component.Caption is null
            ? string.Empty
            : NormalizeText(component.Caption.Text);

        if (string.IsNullOrWhiteSpace(caption))
            return text;

        if (string.IsNullOrWhiteSpace(text))
            return caption;

        if (text.Contains(caption, StringComparison.Ordinal))
            return text;

        return $"{text}\nCaption: {caption}";
    }

    private static IReadOnlyList<NormalizedTableRow> ParseTableRows(
        StructuredComponentDto component,
        int pageNumber)
    {
        if (!component.Rows.HasValue || component.Rows.Value.ValueKind != JsonValueKind.Array)
            return Array.Empty<NormalizedTableRow>();

        var rows = new List<NormalizedTableRow>();
        foreach (var row in component.Rows.Value.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object)
                continue;

            var rowIndex = row.TryGetProperty("row_index", out var rowIndexValue) &&
                           rowIndexValue.TryGetInt32(out var parsedIndex)
                ? parsedIndex
                : rows.Count;
            var rowPage = row.TryGetProperty("page_number", out var pageValue) &&
                          pageValue.TryGetInt32(out var parsedPage)
                ? parsedPage
                : pageNumber;
            if (rowPage <= 0)
                rowPage = pageNumber;

            var text = row.TryGetProperty("text", out var textValue)
                ? NormalizeText(textValue.GetString())
                : string.Empty;
            if (string.IsNullOrWhiteSpace(text))
                continue;

            rows.Add(new NormalizedTableRow(
                rowIndex,
                rowPage,
                text,
                new[] { component.ComponentId }));
        }

        return rows
            .OrderBy(row => row.RowIndex)
            .ToArray();
    }

    private static string NormalizeText(string? value)
    {
        var normalized = (value ?? string.Empty)
            .Normalize(NormalizationForm.FormC)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Replace("\u00AD", string.Empty, StringComparison.Ordinal);

        normalized = HorizontalWhitespacePattern.Replace(normalized, " ");
        normalized = string.Join(
            "\n",
            normalized
                .Split('\n')
                .Select(line => line.Trim()));
        normalized = ExcessiveBlankLinesPattern.Replace(normalized, "\n\n");

        // Keep the hyphen and remove only the physical line break. This is
        // conservative for English compound words and avoids silently changing
        // the meaning of OCR text when de-hyphenation is uncertain.
        normalized = Regex.Replace(
            normalized,
            @"(?<=[A-Za-z])-[ \t]*\n[ \t]*(?=[a-z])",
            "-",
            RegexOptions.CultureInvariant);

        return normalized.Trim();
    }

    private static bool IsAtomic(string blockType, string? sourceLayoutType) =>
        IsAtomicType(blockType) || IsAtomicType(sourceLayoutType);

    private static bool IsAtomicType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var normalized = value.Trim();
        return normalized.Equals("table", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("list", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("table", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("list", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<ExcludedCandidate> DetectExcludedCandidates(
        IReadOnlyList<CandidateBlock> candidates)
    {
        var excluded = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate.Text))
                excluded[candidate.ComponentId] = "Empty component excluded during normalization.";
            else if (IsPageNumberCandidate(candidate))
                excluded[candidate.ComponentId] = "Page number detected in a page margin.";
        }

        var recurringMarginGroups = candidates
            .Where(candidate =>
                !string.IsNullOrWhiteSpace(candidate.Text) &&
                GetMarginZone(candidate) is not null)
            .Select(candidate => new
            {
                Candidate = candidate,
                Zone = GetMarginZone(candidate)!,
                CanonicalText = CanonicalizeMarginText(candidate.Text),
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.CanonicalText))
            .GroupBy(
                item => $"{item.Zone}\u001F{item.CanonicalText}",
                StringComparer.Ordinal)
            .Where(group => group.Select(item => item.Candidate.PageNumber).Distinct().Count() >= 2)
            .ToArray();

        foreach (var group in recurringMarginGroups)
        {
            var isDynamicTemplate = group.Key.Contains("<page-number>", StringComparison.Ordinal);
            foreach (var item in group)
            {
                excluded.TryAdd(
                    item.Candidate.ComponentId,
                    isDynamicTemplate
                        ? "Repeated dynamic page header/footer template detected in the same margin."
                        : "Repeated page header/footer detected in the same margin.");
            }
        }

        return candidates
            .Where(candidate => excluded.ContainsKey(candidate.ComponentId))
            .Select(candidate => new ExcludedCandidate(candidate, excluded[candidate.ComponentId]))
            .ToArray();
    }

    private static bool IsPageNumberCandidate(CandidateBlock candidate)
    {
        if (GetMarginZone(candidate) is null)
            return false;

        return PageNumberPattern.IsMatch(candidate.Text.Trim());
    }

    private static string? GetMarginZone(CandidateBlock candidate)
    {
        var bbox = candidate.NormalizedBbox.Count == 4
            ? candidate.NormalizedBbox
            : candidate.Bbox.Count == 4
                ? candidate.Bbox
                : null;

        if (bbox is null)
            return null;

        var top = Math.Min(bbox[1], bbox[3]);
        var bottom = Math.Max(bbox[1], bbox[3]);
        // A component must be wholly inside the margin band.  Using only its
        // top/bottom edge would classify a body paragraph that crosses 20% or
        // 80% as boilerplate and could discard ordinary numbers/content.
        if (bottom <= HeaderBoundary)
            return "header";
        if (top >= FooterBoundary)
            return "footer";
        return null;
    }

    private static string CanonicalizeMarginText(string text)
    {
        var canonical = MarginWhitespacePattern.Replace(text.Trim(), " ");
        canonical = DynamicPageTemplatePattern.Replace(
            canonical,
            match =>
            {
                var separator = match.Groups["separator"].Value.ToLowerInvariant();
                return $"page <page-number> {separator} <page-number>";
            });
        return canonical.ToLowerInvariant();
    }

    private static string ComputeSourceContentHash(
        int documentId,
        string schemaVersion,
        string? documentTitle,
        IReadOnlyList<CandidateBlock> candidates)
    {
        var canonical = new StringBuilder();
        AppendCanonical(canonical, documentId.ToString(CultureInfo.InvariantCulture));
        AppendCanonical(canonical, schemaVersion);
        AppendCanonical(canonical, documentTitle ?? string.Empty);

        foreach (var candidate in candidates)
        {
            AppendCanonical(canonical, candidate.PageNumber.ToString(CultureInfo.InvariantCulture));
            AppendCanonical(canonical, candidate.ReadingOrder.ToString(CultureInfo.InvariantCulture));
            AppendCanonical(canonical, candidate.ComponentId);
            AppendCanonical(canonical, candidate.BlockType);
            AppendCanonical(canonical, candidate.Text);
            AppendCanonical(canonical, candidate.Confidence.ToString("R", CultureInfo.InvariantCulture));
            AppendCanonical(canonical, candidate.SourceLayoutType ?? string.Empty);
            AppendCanonical(canonical, candidate.IsAtomic ? "1" : "0");
            AppendCanonical(canonical, SerializeCoordinates(candidate.NormalizedBbox));
            AppendCanonical(canonical, SerializeCoordinates(candidate.Bbox));
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string SerializeCoordinates(IReadOnlyList<double> values)
    {
        return string.Join(
            ",",
            values.Select(value => value.ToString("R", CultureInfo.InvariantCulture)));
    }

    private static void AppendCanonical(StringBuilder builder, string value)
    {
        builder.Append(value.Length.ToString(CultureInfo.InvariantCulture));
        builder.Append(':');
        builder.Append(value);
        builder.Append('|');
    }

    private static int ParseDocumentId(string? value, string parameterName)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var documentId) ||
            documentId <= 0)
        {
            throw new ArgumentException("DocumentId must be a positive integer.", parameterName);
        }

        return documentId;
    }

    private static void ValidateSchemaVersion(string? schemaVersion)
    {
        var normalized = schemaVersion?.Trim();
        if (normalized is null || !SupportedSchemaVersions.Contains(normalized))
            throw new ArgumentException(
                "Unsupported structured OCR schema version. Expected '1.0' or '1.1'.",
                nameof(schemaVersion));
    }

    private static void ValidateConfidence(double confidence, string componentId)
    {
        if (double.IsNaN(confidence) || double.IsInfinity(confidence) || confidence is < 0 or > 1)
            throw new ArgumentOutOfRangeException(
                $"Component {componentId} Confidence",
                "Confidence must be finite and between zero and one.");
    }

    private static string Required(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Value is required.", name);
        return value.Trim();
    }

    private sealed record CandidateBlock(
        string ComponentId,
        int PageNumber,
        int ReadingOrder,
        string BlockType,
        string Text,
        double Confidence,
        string? SourceLayoutType,
        bool IsAtomic,
        IReadOnlyList<NormalizedTableRow> TableRows,
        IReadOnlyList<double> NormalizedBbox,
        IReadOnlyList<double> Bbox);

    private sealed record ExcludedCandidate(CandidateBlock Candidate, string Reason);
}
