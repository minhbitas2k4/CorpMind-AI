using System.Text.RegularExpressions;
using CorpMindAI.Application.Chunking.Models;
using CorpMindAI.Application.Interfaces;

namespace CorpMindAI.Application.Chunking.Sections;

// Detects English-first heading candidates without changing source block text.
public sealed class HeadingDetector : IHeadingDetector
{
    // A heading is persisted as ParentChunk.Title (varchar(255)). Keeping the
    // candidate bound here prevents a long OCR body/list from becoming a
    // section label while preserving realistic enterprise headings.
    private const int MaxHeadingCharacters = 255;
    private const int MaxHeadingWords = 32;
    private const int MaxHeadingLines = 1;

    private static readonly Regex ChapterPattern = new(
        @"^(?<kind>chapter|part|appendix)\s+(?:[IVXLCDM]+|\d+|[A-Z])(?:\s*[:.-]?\s+)(?<title>.+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex SectionPattern = new(
        @"^(?<kind>section|article)\s+(?:\d+(?:\.\d+)*|[IVXLCDM]+|[A-Z])(?:\s*[:.-]?\s+)(?<title>.+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex NumericPattern = new(
        @"^(?<number>\d+(?:\.\d+)*)(?:\.)?\s+(?<title>.+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex CalloutPattern = new(
        @"^(?:warning|caution|note|important|tip)\s*:\s*(?<body>.+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex CalloutSentenceCuePattern = new(
        @"^(?:do|don't|use|record|ensure|keep|contact|escalate|review|submit|follow|refer|verify|confirm|stop|avoid|retain|apply|check|complete|include|exclude|notify|report|allow|never|always)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex RomanPattern = new(
        @"^(?<number>[IVXLCDM]+)\.\s+(?<title>.+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex LetterPattern = new(
        @"^(?<number>[A-Z])\.\s+(?<title>.+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ListItemMarkerPattern = new(
        @"(?<!\w)(?:\d+|[A-Z]|[IVXLCDM]+)[.)]\s+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public IReadOnlyList<DetectedHeading> Detect(NormalizedDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var blocks = document.Blocks
            .OrderBy(block => block.PageNumber)
            .ThenBy(block => block.ReadingOrder)
            .ThenBy(block => block.BlockId, StringComparer.Ordinal)
            .ToArray();

        var duplicateBlockIds = blocks
            .GroupBy(block => block.BlockId, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        if (duplicateBlockIds.Length > 0)
            throw new ArgumentException(
                $"Normalized document contains duplicate block ID(s): {string.Join(", ", duplicateBlockIds)}.",
                nameof(document));

        return blocks
            .Select(block => TryCreateHeading(document.DocumentId, block))
            .Where(heading => heading is not null)
            .Cast<DetectedHeading>()
            .ToArray();
    }

    private static DetectedHeading? TryCreateHeading(int documentId, NormalizedBlock block)
    {
        var text = block.Text.Trim();
        if (text.Length == 0)
            return null;

        if (IsListOrTableBlock(block))
            return null;

        if (IsSentenceShapedCallout(text))
            return null;

        var explicitMatch = MatchExplicitHeading(text);
        var isTitleType = string.Equals(block.BlockType, "title", StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(block.SourceLayoutType, "title", StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(block.SourceLayoutType, "heading", StringComparison.OrdinalIgnoreCase);

        if (IsSentenceShapedNumericFootnote(text, isTitleType))
            return null;

        var isUppercaseCandidate = IsConservativeUppercaseCandidate(text);
        var isStandaloneLabel = IsStandaloneLabelCandidate(block, text);
        if (!explicitMatch.HasValue && !isTitleType && !isUppercaseCandidate && !isStandaloneLabel)
            return null;

        if (!PassesHeadingShape(text))
            return null;

        var level = explicitMatch?.Level ?? 1;
        return new DetectedHeading(
            documentId: documentId,
            blockId: block.BlockId,
            text: text,
            level: level,
            sectionPath: new[] { text },
            confidence: block.Confidence);
    }

    private static HeadingMatch? MatchExplicitHeading(string text)
    {
        var chapterMatch = ChapterPattern.Match(text);
        if (chapterMatch.Success)
        {
            var kind = chapterMatch.Groups["kind"].Value;
            return new HeadingMatch(
                string.Equals(kind, "appendix", StringComparison.OrdinalIgnoreCase) ? 1 : 1);
        }

        var sectionMatch = SectionPattern.Match(text);
        if (sectionMatch.Success)
        {
            var kind = sectionMatch.Groups["kind"].Value;
            return new HeadingMatch(
                string.Equals(kind, "article", StringComparison.OrdinalIgnoreCase) ? 3 : 2);
        }

        var numericMatch = NumericPattern.Match(text);
        if (numericMatch.Success)
            return new HeadingMatch(GetNumericLevel(numericMatch.Groups["number"].Value));

        if (RomanPattern.IsMatch(text))
            return new HeadingMatch(1);

        if (LetterPattern.IsMatch(text))
            return new HeadingMatch(2);

        return null;
    }

    private static int GetNumericLevel(string number)
    {
        var level = number.Count(character => character == '.') + 1;
        return Math.Clamp(level, 1, 12);
    }

    private static bool IsListOrTableBlock(NormalizedBlock block) =>
        IsListOrTableType(block.BlockType) || IsListOrTableType(block.SourceLayoutType);

    private static bool IsListOrTableType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var normalized = value.Trim();
        return normalized.Contains("list", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("table", StringComparison.OrdinalIgnoreCase);
    }

    private static bool PassesHeadingShape(string text)
    {
        if (text.Length > MaxHeadingCharacters ||
            text.Count(character => character == '\n') + 1 > MaxHeadingLines)
        {
            return false;
        }

        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > MaxHeadingWords)
            return false;

        if (ListItemMarkerPattern.Matches(text).Count > 1)
            return false;

        if (ListItemMarkerPattern.IsMatch(text) &&
            words.Length > 3 &&
            (text.EndsWith(".", StringComparison.Ordinal) ||
             text.EndsWith("?", StringComparison.Ordinal) ||
             text.EndsWith("!", StringComparison.Ordinal) ||
             text.EndsWith(";", StringComparison.Ordinal)))
        {
            return false;
        }

        return true;
    }

    private static bool IsSentenceShapedCallout(string text)
    {
        var match = CalloutPattern.Match(text);
        if (!match.Success)
            return false;

        var body = match.Groups["body"].Value.Trim();
        var words = body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return words.Length >= 4 ||
               CalloutSentenceCuePattern.IsMatch(body) ||
               body.EndsWith(".", StringComparison.Ordinal) ||
               body.EndsWith("?", StringComparison.Ordinal) ||
               body.EndsWith("!", StringComparison.Ordinal) ||
               body.EndsWith(";", StringComparison.Ordinal);
    }

    private static bool IsSentenceShapedNumericFootnote(
        string text,
        bool isTitleType)
    {
        var match = NumericPattern.Match(text);
        if (!match.Success)
            return false;

        var title = match.Groups["title"].Value.Trim();
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var hasSentencePunctuation = title.EndsWith(".", StringComparison.Ordinal) ||
                                      title.EndsWith("?", StringComparison.Ordinal) ||
                                      title.EndsWith("!", StringComparison.Ordinal) ||
                                      title.EndsWith(";", StringComparison.Ordinal);

        if (!isTitleType && words.Length >= 5)
            return true;

        return words.Length >= 4 && hasSentencePunctuation;
    }

    private static bool IsConservativeUppercaseCandidate(string text)
    {
        if (text.Length > 120 || text.EndsWith(".", StringComparison.Ordinal) ||
            text.EndsWith("?", StringComparison.Ordinal) ||
            text.EndsWith("!", StringComparison.Ordinal) ||
            text.EndsWith(";", StringComparison.Ordinal))
        {
            return false;
        }

        if (!text.Any(char.IsLetter) || text != text.ToUpperInvariant())
            return false;

        if (!text.Contains(' ', StringComparison.Ordinal) && text.Any(char.IsDigit))
            return false;

        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length <= 12;
    }

    private static bool IsStandaloneLabelCandidate(NormalizedBlock block, string text)
    {
        if (!string.Equals(block.SourceLayoutType, "text", StringComparison.OrdinalIgnoreCase) ||
            text.Contains('\n', StringComparison.Ordinal) ||
            text.Length > 80 ||
            text.Any(character => character is '.' or '?' or '!' or ';' or ':' || char.IsDigit(character)))
        {
            return false;
        }

        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length is not (>= 2 and <= 6) ||
            !char.IsLetter(text[0]) ||
            !char.IsUpper(text[0]))
        {
            return false;
        }

        var connectors = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "a", "an", "and", "for", "in", "of", "on", "the", "to", "with"
        };
        return words
            .Where(word => !connectors.Contains(word))
            .All(word => char.IsLetter(word[0]) && char.IsUpper(word[0]));
    }

    private readonly record struct HeadingMatch(int Level);
}
