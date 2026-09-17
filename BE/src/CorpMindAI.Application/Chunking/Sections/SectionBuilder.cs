using System.Text.RegularExpressions;
using CorpMindAI.Application.Chunking.Models;
using CorpMindAI.Application.Interfaces;

namespace CorpMindAI.Application.Chunking.Sections;

// Builds deterministic section paths without changing normalized block content.
public sealed class SectionBuilder : ISectionBuilder
{
    private static readonly Regex NumericHeadingPattern = new(
        @"^(?<number>\d+(?:\.\d+)*)(?:\.)?\s+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex LetterHeadingPattern = new(
        @"^[A-Z]\.\s+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex RomanHeadingPattern = new(
        @"^[IVXLCDM]+\.\s+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex DocumentTitleSuffixPattern = new(
        @"\b(?:handbook|policy|procedure|manual|standard|guideline|playbook)\b\s*[-:]\s*.+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public SectionedDocument Build(
        NormalizedDocument document,
        IReadOnlyList<DetectedHeading> headings)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(headings);

        var blocks = document.Blocks
            .OrderBy(block => block.PageNumber)
            .ThenBy(block => block.ReadingOrder)
            .ThenBy(block => block.BlockId, StringComparer.Ordinal)
            .ToArray();
        var blockIds = blocks.Select(block => block.BlockId).ToHashSet(StringComparer.Ordinal);

        var headingByBlockId = headings.ToDictionary(
            heading => heading.BlockId,
            heading => heading,
            StringComparer.Ordinal);

        if (headings.Any(heading =>
                heading.DocumentId != document.DocumentId ||
                !blockIds.Contains(heading.BlockId)))
        {
            throw new ArgumentException(
                "Every heading must belong to a block in the document.",
                nameof(headings));
        }

        var sectionStack = new List<SectionEntry>();
        var assignments = new List<SectionAssignment>(blocks.Length);
        var resolvedHeadings = new List<DetectedHeading>(headings.Count);
        var warnings = new List<string>();

        foreach (var block in blocks)
        {
            if (headingByBlockId.TryGetValue(block.BlockId, out var heading))
            {
                var hasNamedRoot = HasRootHeading(sectionStack);
                var numericLevel = GetNumericHeadingLevel(heading.Text);
                var effectiveLevel = ResolveEffectiveLevel(heading, sectionStack);
                while (sectionStack.Count >= effectiveLevel)
                    sectionStack.RemoveAt(sectionStack.Count - 1);

                var sectionLabel = CanonicalizeSectionLabel(heading.Text, effectiveLevel);
                sectionStack.Add(new SectionEntry(effectiveLevel, sectionLabel));
                var path = sectionStack.Select(entry => entry.Text).ToArray();

                if (effectiveLevel != heading.Level &&
                    !(numericLevel.HasValue && hasNamedRoot))
                {
                    warnings.Add(
                        $"Heading '{heading.Text}' level {heading.Level} was normalized to {effectiveLevel} " +
                        "because the document skipped an intermediate level.");
                }

                resolvedHeadings.Add(new DetectedHeading(
                    documentId: heading.DocumentId,
                    blockId: heading.BlockId,
                    text: heading.Text,
                    level: effectiveLevel,
                    sectionPath: path,
                    confidence: heading.Confidence));

                assignments.Add(new SectionAssignment(
                    documentId: document.DocumentId,
                    blockId: block.BlockId,
                    sectionPath: path,
                    isHeading: true,
                    headingLevel: effectiveLevel));
            }
            else
            {
                assignments.Add(new SectionAssignment(
                    documentId: document.DocumentId,
                    blockId: block.BlockId,
                    sectionPath: sectionStack.Select(entry => entry.Text).ToArray(),
                    isHeading: false));
            }
        }

        return new SectionedDocument(
            document: document,
            headings: resolvedHeadings,
            assignments: assignments,
            warnings: warnings);
    }

    private static int ResolveEffectiveLevel(
        DetectedHeading heading,
        IReadOnlyList<SectionEntry> sectionStack)
    {
        var text = heading.Text.TrimStart();
        if (StartsWithHeadingKind(text, "chapter") ||
            StartsWithHeadingKind(text, "part") ||
            StartsWithHeadingKind(text, "appendix"))
        {
            return 1;
        }

        if (StartsWithHeadingKind(text, "section"))
            return HasRootHeading(sectionStack) ? 2 : 1;

        if (StartsWithHeadingKind(text, "article"))
        {
            if (sectionStack.Any(entry => StartsWithHeadingKind(entry.Text, "section")))
                return HasRootHeading(sectionStack) ? 3 : 2;
            return 1;
        }

        if (HasRootHeading(sectionStack) &&
            heading.Level == 1 &&
            !RomanHeadingPattern.IsMatch(text))
            return 2;

        if (LetterHeadingPattern.IsMatch(text) && HasRomanRoot(sectionStack))
            return 2;

        var numericLevel = GetNumericHeadingLevel(text);
        if (numericLevel.HasValue && HasRootHeading(sectionStack))
            return Math.Min(numericLevel.Value + 1, 12);

        return Math.Min(heading.Level, sectionStack.Count + 1);
    }

    private static int? GetNumericHeadingLevel(string text)
    {
        var match = NumericHeadingPattern.Match(text);
        if (!match.Success)
            return null;

        var marker = match.Groups["number"].Value;
        var depth = marker.Count(character => character == '.') + 1;
        return Math.Clamp(depth, 1, 12);
    }

    private static bool HasRootHeading(IEnumerable<SectionEntry> sectionStack) =>
        sectionStack.Any(entry =>
            StartsWithHeadingKind(entry.Text, "chapter") ||
            StartsWithHeadingKind(entry.Text, "part") ||
            StartsWithHeadingKind(entry.Text, "appendix"));

    private static bool HasRomanRoot(IEnumerable<SectionEntry> sectionStack) =>
        sectionStack.Any(entry => RomanHeadingPattern.IsMatch(entry.Text.Trim()));

    private static string CanonicalizeSectionLabel(string text, int effectiveLevel)
    {
        if (effectiveLevel != 1 || text.Length < 80)
            return text;

        var match = DocumentTitleSuffixPattern.Match(text);
        return match.Success && match.Index > 0
            ? text[match.Index..].Trim()
            : text;
    }

    private static bool StartsWithHeadingKind(string text, string kind) =>
        text.StartsWith(kind + " ", StringComparison.OrdinalIgnoreCase) ||
        text.StartsWith(kind + ":", StringComparison.OrdinalIgnoreCase);

    private sealed record SectionEntry(int Level, string Text);
}
