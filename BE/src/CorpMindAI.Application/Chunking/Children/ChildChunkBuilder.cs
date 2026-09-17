using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using CorpMindAI.Application.Chunking.Models;
using CorpMindAI.Application.Interfaces;

namespace CorpMindAI.Application.Chunking.Children;

// Builds deterministic, search-sized children independently inside each parent.
public sealed class ChildChunkBuilder : IChildChunkBuilder
{
    private const string ParagraphSeparator = "\n\n";
    private const string SentenceSeparator = " ";

    private static readonly HashSet<string> Abbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        "e.g.", "i.e.", "mr.", "mrs.", "ms.", "dr.", "u.s.", "etc.", "vs.", "fig.", "no."
    };

    public IReadOnlyList<ChildChunkResult> Build(
        IEnumerable<ParentChunkResult> parents,
        string? documentTitle,
        ChunkingOptions options,
        ITokenCounter tokenCounter)
    {
        ArgumentNullException.ThrowIfNull(parents);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(tokenCounter);
        options.Validate();

        var parentList = parents.ToArray();
        var duplicateParentIds = parentList
            .GroupBy(parent => parent.Id, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        if (duplicateParentIds.Length > 0)
        {
            throw new ArgumentException(
                $"Parent IDs must be unique: {string.Join(", ", duplicateParentIds)}.",
                nameof(parents));
        }

        var title = string.IsNullOrWhiteSpace(documentTitle)
            ? "Untitled document"
            : documentTitle.Trim();
        var children = new List<ChildChunkResult>();

        foreach (var parent in parentList
                     .OrderBy(parent => parent.DocumentId)
                     .ThenBy(parent => parent.Ordinal)
                     .ThenBy(parent => parent.Id, StringComparer.Ordinal))
        {
            children.AddRange(BuildForParent(parent, title, options, tokenCounter));
        }

        return children;
    }

    private static IReadOnlyList<ChildChunkResult> BuildForParent(
        ParentChunkResult parent,
        string documentTitle,
        ChunkingOptions options,
        ITokenCounter tokenCounter)
    {
        if (string.IsNullOrWhiteSpace(parent.Content))
            return Array.Empty<ChildChunkResult>();

        var context = ResolveContext(parent, documentTitle, options, tokenCounter);
        if (parent.IsAtomic)
            return BuildAtomicChildren(parent, context, options, tokenCounter);

        var contextOverhead = Count(
            tokenCounter,
            BuildContextualizedContent(context.DocumentTitle, context.SectionPath, string.Empty),
            parent.Id);
        var rawHardLimit = options.ChildMaxTokens - contextOverhead;
        if (rawHardLimit <= 0)
        {
            throw new InvalidOperationException(
                $"CHILD_CONTEXT_BUDGET_EXCEEDED: document and section context leave no token budget for child content in parent '{parent.Id}'.");
        }

        var sentences = ExtractSentences(parent.Content, rawHardLimit, tokenCounter);
        if (sentences.Count == 0)
            return Array.Empty<ChildChunkResult>();

        var drafts = new List<ChildDraft>();
        var index = 0;
        IReadOnlyList<SentenceUnit> overlap = Array.Empty<SentenceUnit>();

        while (index < sentences.Count)
        {
            var current = overlap.ToList();
            var newUnits = new List<SentenceUnit>();

            while (index < sentences.Count)
            {
                var unit = sentences[index];
                var candidate = current.Append(unit).ToArray();
                var candidateContent = Join(candidate);
                var candidateTokens = Count(
                    tokenCounter,
                    BuildContextualizedContent(context.DocumentTitle, context.SectionPath, candidateContent),
                    parent.Id);

                if (newUnits.Count == 0 && current.Count > 0 &&
                    candidateTokens > options.ChildTargetTokens)
                {
                    current.Clear();
                    candidate = new[] { unit };
                    candidateContent = unit.Text;
                    candidateTokens = Count(
                        tokenCounter,
                        BuildContextualizedContent(context.DocumentTitle, context.SectionPath, unit.Text),
                        parent.Id);
                }

                if (current.Count == 0 || candidateTokens <= options.ChildTargetTokens)
                {
                    current.Add(unit);
                    newUnits.Add(unit);
                    index++;
                    continue;
                }

                break;
            }

            if (newUnits.Count == 0)
                throw new InvalidOperationException(
                    $"Unable to advance child chunking for parent '{parent.Id}'.");

            var content = Join(current);
            var tokenCount = Count(
                tokenCounter,
                BuildContextualizedContent(context.DocumentTitle, context.SectionPath, content),
                parent.Id);
            if (tokenCount > options.ChildMaxTokens)
            {
                throw new InvalidOperationException(
                    $"Child content exceeded ChildMaxTokens for parent '{parent.Id}'.");
            }

            drafts.Add(new ChildDraft(content, tokenCount, current.ToArray(), newUnits));
            overlap = TakeOverlap(newUnits, options.ChildOverlapTokens);
        }

        return drafts
            .Select((draft, ordinal) => CreateChild(
                parent,
                context.DocumentTitle,
                context.SectionPath,
                draft.Content,
                ordinal,
                tokenCounter,
                isAtomic: false,
                sourceUnits: draft.Units))
            .ToArray();
    }

    private static ContextSelection ResolveContext(
        ParentChunkResult parent,
        string? documentTitle,
        ChunkingOptions options,
        ITokenCounter tokenCounter)
    {
        var normalizedTitle = string.IsNullOrWhiteSpace(documentTitle)
            ? "Untitled document"
            : documentTitle.Trim();
        var fullPath = parent.SectionPath.ToArray();

        if (FitsContext(normalizedTitle, fullPath, options.ChildMaxTokens, tokenCounter, parent.Id))
            return new ContextSelection(normalizedTitle, fullPath);

        // Keep the leaf and the nearest ancestors first. The complete path is
        // still retained in ChildChunkResult metadata; only the embedding text
        // is bounded so that raw content always has room.
        for (var start = 1; start < fullPath.Length; start++)
        {
            var boundedPath = fullPath[start..];
            if (FitsContext(normalizedTitle, boundedPath, options.ChildMaxTokens, tokenCounter, parent.Id))
                return new ContextSelection(normalizedTitle, boundedPath);
        }

        if (FitsContext(normalizedTitle, Array.Empty<string>(), options.ChildMaxTokens, tokenCounter, parent.Id))
            return new ContextSelection(normalizedTitle, Array.Empty<string>());

        if (FitsContext("Document", Array.Empty<string>(), options.ChildMaxTokens, tokenCounter, parent.Id))
            return new ContextSelection("Document", Array.Empty<string>());

        throw new InvalidOperationException(
            $"CHILD_CONTEXT_BUDGET_EXCEEDED: document and section context leave no token budget for child content in parent '{parent.Id}'.");
    }

    private static bool FitsContext(
        string documentTitle,
        IReadOnlyList<string> sectionPath,
        int childMaxTokens,
        ITokenCounter tokenCounter,
        string parentId) =>
        Count(
            tokenCounter,
            BuildContextualizedContent(documentTitle, sectionPath, string.Empty),
            parentId) < childMaxTokens;

    private static IReadOnlyList<ChildChunkResult> BuildAtomicChildren(
        ParentChunkResult parent,
        ContextSelection context,
        ChunkingOptions options,
        ITokenCounter tokenCounter)
    {
        var contextualizedParent = BuildContextualizedContent(
            context.DocumentTitle,
            context.SectionPath,
            parent.Content);
        var parentTokenCount = Count(tokenCounter, contextualizedParent, parent.Id);

        if (parentTokenCount <= options.ChildMaxTokens)
        {
            return new[]
            {
                CreateChild(
                    parent,
                    context.DocumentTitle,
                    context.SectionPath,
                    parent.Content,
                    ordinal: 0,
                    tokenCounter,
                    isAtomic: true,
                    sourceUnits: null)
            };
        }

        var contextOverhead = Count(
            tokenCounter,
            BuildContextualizedContent(context.DocumentTitle, context.SectionPath, string.Empty),
            parent.Id);
        var rawHardLimit = options.ChildMaxTokens - contextOverhead;
        if (rawHardLimit <= 0)
        {
            throw new InvalidOperationException(
                $"CHILD_CONTEXT_BUDGET_EXCEEDED: document and section context leave no token budget for atomic content in parent '{parent.Id}'.");
        }

        var units = SplitAtomicUnits(
            parent.Content,
            context,
            options.ChildMaxTokens,
            tokenCounter,
            parent.Id,
            ResolveTableHeader(parent));
        if (units.Count == 0)
            return Array.Empty<ChildChunkResult>();

        var drafts = new List<AtomicDraft>();
        var currentStart = units[0].Start;
        var currentEnd = units[0].End;
        var currentUnits = new List<AtomicUnit> { units[0] };

        for (var index = 1; index < units.Count; index++)
        {
            var candidateEnd = units[index].End;
            var candidateContent = parent.Content[currentStart..candidateEnd].Trim();
            var candidateContext = ResolveAtomicContext(currentUnits.Append(units[index]));
            var candidateTokens = Count(
                tokenCounter,
                BuildContextualizedContent(
                    context.DocumentTitle,
                    context.SectionPath,
                    candidateContent,
                    candidateContext),
                parent.Id);

            if (candidateTokens <= options.ChildTargetTokens)
            {
                currentEnd = candidateEnd;
                currentUnits.Add(units[index]);
                continue;
            }

            drafts.Add(new AtomicDraft(
                parent.Content[currentStart..currentEnd].Trim(),
                ResolveAtomicContext(currentUnits)));
            currentStart = units[index].Start;
            currentEnd = candidateEnd;
            currentUnits = new List<AtomicUnit> { units[index] };
        }

        drafts.Add(new AtomicDraft(
            parent.Content[currentStart..currentEnd].Trim(),
            ResolveAtomicContext(currentUnits)));

        return drafts
            .Select((draft, ordinal) => CreateChild(
                parent,
                context.DocumentTitle,
                context.SectionPath,
                draft.Content,
                ordinal,
                tokenCounter,
                isAtomic: false,
                sourceUnits: null,
                additionalContext: draft.AdditionalContext))
            .ToArray();
    }

    private static IReadOnlyList<AtomicUnit> SplitAtomicUnits(
        string content,
        ContextSelection context,
        int childMaxTokens,
        ITokenCounter tokenCounter,
        string parentId,
        string? tableHeader)
    {
        var units = new List<AtomicUnit>();
        foreach (var range in GetLineRanges(content))
        {
            var start = TrimStart(content, range.Start, range.End);
            var end = TrimEnd(content, start, range.End);
            if (start >= end)
                continue;

            var text = content[start..end];
            var tokenCount = Count(
                tokenCounter,
                BuildContextualizedContent(
                    context.DocumentTitle,
                    context.SectionPath,
                    text,
                    tableHeader),
                parentId);
            if (tokenCount <= childMaxTokens)
            {
                units.Add(new AtomicUnit(start, end));
                continue;
            }

            units.AddRange(SplitAtomicRange(
                content,
                start,
                end,
                context,
                childMaxTokens,
                tokenCounter,
                parentId,
                tableHeader));
        }

        return units;
    }

    private static IReadOnlyList<AtomicUnit> SplitAtomicRange(
        string content,
        int start,
        int end,
        ContextSelection context,
        int childMaxTokens,
        ITokenCounter tokenCounter,
        string parentId,
        string? tableHeader)
    {
        var text = content[start..end];
        var words = Regex.Matches(text, @"\S+");
        if (words.Count == 0)
            return Array.Empty<AtomicUnit>();

        var units = new List<AtomicUnit>();
        var relativeStart = words[0].Index;
        var relativeEnd = words[0].Index + words[0].Length;

        for (var index = 1; index < words.Count; index++)
        {
            var nextEnd = words[index].Index + words[index].Length;
            var candidate = text[relativeStart..nextEnd];
            var candidateTokens = Count(
                tokenCounter,
                BuildContextualizedContent(
                    context.DocumentTitle,
                    context.SectionPath,
                    candidate,
                    tableHeader),
                parentId);
            if (candidateTokens <= childMaxTokens)
            {
                relativeEnd = nextEnd;
                continue;
            }

            AddAtomicUnit(
                units,
                content,
                start + relativeStart,
                start + relativeEnd,
                context,
                childMaxTokens,
                tokenCounter,
                parentId,
                tableHeader);
            relativeStart = words[index].Index;
            relativeEnd = nextEnd;
        }

        AddAtomicUnit(
            units,
            content,
            start + relativeStart,
            start + relativeEnd,
            context,
            childMaxTokens,
            tokenCounter,
            parentId,
            tableHeader);
        return units;
    }

    private static void AddAtomicUnit(
        ICollection<AtomicUnit> units,
        string content,
        int start,
        int end,
        ContextSelection context,
        int childMaxTokens,
        ITokenCounter tokenCounter,
        string parentId,
        string? additionalContext = null)
    {
        var text = content[start..end];
        var tokenCount = Count(
            tokenCounter,
            BuildContextualizedContent(
                context.DocumentTitle,
                context.SectionPath,
                text,
                additionalContext),
            parentId);
        if (tokenCount > childMaxTokens)
        {
            throw new InvalidOperationException(
                $"ATOMIC_UNIT_TOO_LARGE: an indivisible atomic unit in parent '{parentId}' exceeds ChildMaxTokens.");
        }

        units.Add(new AtomicUnit(start, end, additionalContext));
    }

    private static string? ResolveTableHeader(ParentChunkResult parent)
    {
        // ParentChunkBuilder emits one source span per logical table row. This
        // lets us distinguish a table from a generic atomic list without
        // adding a document-type-specific flag to the chunk contract.
        if (parent.SourceSpans.Count < 2 || !parent.Content.Contains('\n', StringComparison.Ordinal))
            return null;

        var firstSpan = parent.SourceSpans[0];
        if (firstSpan.Start != 0 || firstSpan.Length <= 0 || firstSpan.End > parent.Content.Length)
            return null;

        var header = parent.Content[firstSpan.Start..firstSpan.End].Trim();
        return string.IsNullOrWhiteSpace(header) ? null : header;
    }

    private static string? ResolveAtomicContext(IEnumerable<AtomicUnit> units) =>
        units.Select(unit => unit.AdditionalContext)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static IReadOnlyList<(int Start, int End)> GetLineRanges(string content)
    {
        var ranges = new List<(int Start, int End)>();
        var start = 0;
        foreach (Match separator in Regex.Matches(content, @"\r?\n"))
        {
            ranges.Add((start, separator.Index));
            start = separator.Index + separator.Length;
        }

        ranges.Add((start, content.Length));
        return ranges;
    }

    private static int TrimStart(string content, int start, int end)
    {
        while (start < end && char.IsWhiteSpace(content[start]))
            start++;
        return start;
    }

    private static int TrimEnd(string content, int start, int end)
    {
        while (end > start && char.IsWhiteSpace(content[end - 1]))
            end--;
        return end;
    }

    private static IReadOnlyList<SentenceUnit> ExtractSentences(
        string content,
        int maxTokens,
        ITokenCounter tokenCounter)
    {
        var result = new List<SentenceUnit>();
        var paragraphRanges = GetParagraphRanges(content);

        foreach (var paragraph in paragraphRanges)
        {
            var searchStart = 0;
            foreach (var sentence in SplitSentences(paragraph.Text))
            {
                var relativeStart = paragraph.Text.IndexOf(sentence, searchStart, StringComparison.Ordinal);
                if (relativeStart < 0)
                    throw new InvalidOperationException("Unable to map a sentence back to parent content.");
                var absoluteStart = paragraph.Start + relativeStart;
                searchStart = relativeStart + sentence.Length;
                var tokenCount = Count(tokenCounter, sentence, paragraph.Index.ToString());
                if (tokenCount <= maxTokens)
                {
                    result.Add(new SentenceUnit(
                        sentence,
                        paragraph.Index,
                        tokenCount,
                        absoluteStart,
                        sentence.Length));
                    continue;
                }

                result.AddRange(SplitLongSentence(
                    sentence,
                    paragraph.Index,
                    absoluteStart,
                    maxTokens,
                    tokenCounter));
            }
        }

        return result;
    }

    private static IReadOnlyList<SentenceUnit> SplitLongSentence(
        string sentence,
        int paragraphIndex,
        int sentenceStart,
        int maxTokens,
        ITokenCounter tokenCounter)
    {
        var words = Regex.Matches(sentence, @"\S+");
        if (words.Count == 0)
            return Array.Empty<SentenceUnit>();

        var result = new List<SentenceUnit>();
        var start = words[0].Index;
        var end = words[0].Index + words[0].Length;

        for (var index = 1; index < words.Count; index++)
        {
            var nextEnd = words[index].Index + words[index].Length;
            var candidate = sentence[start..nextEnd];
            if (Count(tokenCounter, candidate, paragraphIndex.ToString()) <= maxTokens)
            {
                end = nextEnd;
                continue;
            }

            result.Add(CreateSentenceUnit(
                sentence[start..end], paragraphIndex, sentenceStart + start, maxTokens, tokenCounter));
            start = words[index].Index;
            end = nextEnd;
        }

        result.Add(CreateSentenceUnit(
            sentence[start..end], paragraphIndex, sentenceStart + start, maxTokens, tokenCounter));
        return result;
    }

    private static SentenceUnit CreateSentenceUnit(
        string text,
        int paragraphIndex,
        int start,
        int maxTokens,
        ITokenCounter tokenCounter)
    {
        var tokenCount = Count(tokenCounter, text, paragraphIndex.ToString());
        if (tokenCount > maxTokens)
        {
            throw new InvalidOperationException(
                "A non-atomic sentence contains an indivisible token sequence above ChildMaxTokens.");
        }

        var normalized = text.Trim();
        return new SentenceUnit(normalized, paragraphIndex, tokenCount, start, normalized.Length);
    }

    private static IReadOnlyList<SentenceUnit> TakeOverlap(
        IReadOnlyList<SentenceUnit> newUnits,
        int overlapTokens)
    {
        if (overlapTokens <= 0)
            return Array.Empty<SentenceUnit>();

        var result = new List<SentenceUnit>();
        var total = 0;
        for (var index = newUnits.Count - 1; index >= 0; index--)
        {
            var unit = newUnits[index];
            if (total + unit.TokenCount > overlapTokens)
                break;

            result.Insert(0, unit);
            total += unit.TokenCount;
        }

        return result;
    }

    private static ChildChunkResult CreateChild(
        ParentChunkResult parent,
        string documentTitle,
        IReadOnlyList<string> sectionPath,
        string rawContent,
        int ordinal,
        ITokenCounter tokenCounter,
        bool isAtomic,
        IReadOnlyList<SentenceUnit>? sourceUnits,
        string? additionalContext = null)
    {
        var content = rawContent.Trim();
        if (content.Length == 0)
            throw new InvalidOperationException($"Empty child content for parent '{parent.Id}'.");

        var contextualized = BuildContextualizedContent(
            documentTitle,
            sectionPath,
            content,
            additionalContext);
        var tokenCount = Count(tokenCounter, contextualized, parent.Id);
        var contentHash = ComputeHash(content);
        var id = $"child-{parent.Id}-{ordinal:D6}-{contentHash}";
        var citation = ResolveCitation(parent, sourceUnits);

        return new ChildChunkResult(
            id: id,
            parentChunkId: parent.Id,
            documentId: parent.DocumentId,
            ordinal: ordinal,
            sectionPath: parent.SectionPath,
            rawContent: content,
            contextualizedContent: contextualized,
            tokenCount: tokenCount,
            pageFrom: citation.PageFrom,
            pageTo: citation.PageTo,
            componentIds: citation.ComponentIds,
            contentHash: contentHash,
            isAtomic: isAtomic);
    }

    private static string BuildContextualizedContent(
        string documentTitle,
        IReadOnlyList<string> sectionPath,
        string rawContent,
        string? additionalContext = null)
    {
        var section = sectionPath.Count == 0
            ? "Unsectioned content"
            : string.Join(" > ", sectionPath);
        var contextualContent = string.IsNullOrWhiteSpace(additionalContext)
            ? rawContent
            : $"Table header: {additionalContext}\n{rawContent}";

        return $"Document: {documentTitle}\nSection: {section}\nContent: {contextualContent}";
    }

    private static Citation ResolveCitation(
        ParentChunkResult parent,
        IReadOnlyList<SentenceUnit>? sourceUnits)
    {
        if (parent.SourceSpans.Count == 0 || sourceUnits is null || sourceUnits.Count == 0)
            return new Citation(parent.PageFrom, parent.PageTo, parent.ComponentIds.Distinct(StringComparer.Ordinal).ToArray());

        var spans = parent.SourceSpans.Where(span => sourceUnits.Any(unit =>
            span.Start < unit.Start + unit.Length && span.End > unit.Start)).ToArray();
        if (spans.Length == 0)
            throw new InvalidOperationException($"Unable to resolve child citation spans for parent '{parent.Id}'.");

        return new Citation(
            spans.Min(span => span.PageNumber),
            spans.Max(span => span.PageNumber),
            spans.SelectMany(span => span.ComponentIds).Distinct(StringComparer.Ordinal).ToArray());
    }

    private static IReadOnlyList<(int Index, int Start, string Text)> GetParagraphRanges(string content)
    {
        var result = new List<(int Index, int Start, string Text)>();
        var start = 0;
        var index = 0;

        foreach (Match separator in Regex.Matches(content, @"\r?\n\s*\r?\n"))
        {
            AddParagraph(result, index++, start, content[start..separator.Index]);
            start = separator.Index + separator.Length;
        }

        AddParagraph(result, index, start, content[start..]);
        return result;
    }

    private static void AddParagraph(
        ICollection<(int Index, int Start, string Text)> paragraphs,
        int index,
        int sourceStart,
        string text)
    {
        var normalized = text.Trim();
        if (normalized.Length > 0)
        {
            var leadingWhitespace = text.Length - text.TrimStart().Length;
            paragraphs.Add((index, sourceStart + leadingWhitespace, normalized));
        }
    }

    private static IReadOnlyList<string> SplitSentences(string paragraph)
    {
        var result = new List<string>();
        var start = 0;

        for (var index = 0; index < paragraph.Length; index++)
        {
            var character = paragraph[index];
            if (character is not ('.' or '!' or '?') ||
                !IsSentenceBoundary(paragraph, start, index))
            {
                continue;
            }

            var end = index + 1;
            while (end < paragraph.Length && IsClosingPunctuation(paragraph[end]))
                end++;

            result.Add(paragraph[start..end].Trim());
            start = end;
            index = end - 1;
        }

        var remainder = paragraph[start..].Trim();
        if (remainder.Length > 0)
            result.Add(remainder);

        return result.Where(sentence => sentence.Length > 0).ToArray();
    }

    private static bool IsSentenceBoundary(string paragraph, int start, int punctuationIndex)
    {
        var immediateNext = punctuationIndex + 1;
        if (immediateNext < paragraph.Length &&
            !char.IsWhiteSpace(paragraph[immediateNext]) &&
            !IsClosingPunctuation(paragraph[immediateNext]))
        {
            return false;
        }

        var next = immediateNext;
        while (next < paragraph.Length &&
               (char.IsWhiteSpace(paragraph[next]) || IsClosingPunctuation(paragraph[next])))
            next++;

        if (paragraph[punctuationIndex] == '.' &&
            ((punctuationIndex > start && char.IsDigit(paragraph[punctuationIndex - 1]) &&
              punctuationIndex + 1 < paragraph.Length && char.IsDigit(paragraph[punctuationIndex + 1])) ||
             IsAbbreviation(paragraph[start..(punctuationIndex + 1)])))
        {
            return false;
        }

        return next >= paragraph.Length || char.IsLetterOrDigit(paragraph[next]) ||
               char.IsPunctuation(paragraph[next]);
    }

    private static bool IsClosingPunctuation(char character) =>
        character is '\'' or '"' or ')' or ']' or '}';

    private static bool IsAbbreviation(string text)
    {
        var match = Regex.Match(text.Trim(), @"(?<token>[A-Za-z](?:[A-Za-z.]*)\.)$");
        if (!match.Success)
            return false;

        var token = match.Groups["token"].Value;
        if (Abbreviations.Contains(token))
            return true;

        return token.Count(character => character == '.') >= 2;
    }

    private static string Join(IEnumerable<SentenceUnit> units)
    {
        SentenceUnit? previous = null;
        var builder = new StringBuilder();
        foreach (var unit in units)
        {
            if (previous is not null)
            {
                builder.Append(unit.ParagraphIndex == previous.ParagraphIndex
                    ? SentenceSeparator
                    : ParagraphSeparator);
            }

            builder.Append(unit.Text);
            previous = unit;
        }

        return builder.ToString();
    }

    private static int Count(ITokenCounter tokenCounter, string text, string sourceId)
    {
        var count = tokenCounter.Count(text);
        if (count < 0)
            throw new InvalidOperationException($"Token counter returned a negative count for '{sourceId}'.");
        return count;
    }

    private static string ComputeHash(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private sealed record SentenceUnit(
        string Text,
        int ParagraphIndex,
        int TokenCount,
        int Start,
        int Length);

    private sealed record ChildDraft(
        string Content,
        int TokenCount,
        IReadOnlyList<SentenceUnit> Units,
        IReadOnlyList<SentenceUnit> NewUnits);

    private sealed record AtomicDraft(string Content, string? AdditionalContext);

    private sealed record AtomicUnit(int Start, int End, string? AdditionalContext = null);

    private sealed record ContextSelection(
        string DocumentTitle,
        IReadOnlyList<string> SectionPath);

    private sealed record Citation(int PageFrom, int PageTo, IReadOnlyList<string> ComponentIds);
}
