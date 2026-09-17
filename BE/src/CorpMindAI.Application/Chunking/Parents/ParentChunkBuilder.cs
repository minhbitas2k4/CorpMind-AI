using System.Security.Cryptography;
using System.Text;
using CorpMindAI.Application.Chunking.Models;
using CorpMindAI.Application.Interfaces;

namespace CorpMindAI.Application.Chunking.Parents;

// Builds deterministic parent context chunks from the section assignments produced by Phase 3.
public sealed class ParentChunkBuilder : IParentChunkBuilder
{
    private const string Separator = "\n\n";

    public IReadOnlyList<ParentChunkResult> Build(
        SectionedDocument document,
        ChunkingOptions options,
        ITokenCounter tokenCounter)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(tokenCounter);
        options.Validate();

        var normalizedDocument = document.Document;
        var blocks = normalizedDocument.Blocks
            .OrderBy(block => block.PageNumber)
            .ThenBy(block => block.ReadingOrder)
            .ThenBy(block => block.BlockId, StringComparer.Ordinal)
            .ToArray();
        var assignments = document.Assignments.ToDictionary(
            assignment => assignment.BlockId,
            assignment => assignment,
            StringComparer.Ordinal);

        var results = new List<ParentChunkResult>();
        var activePath = (IReadOnlyList<string>?)null;
        var activeUnits = new List<BlockUnit>();

        foreach (var block in blocks)
        {
            if (string.IsNullOrWhiteSpace(block.Text))
                continue;

            if (!assignments.TryGetValue(block.BlockId, out var assignment))
            {
                throw new ArgumentException(
                    $"No section assignment exists for normalized block '{block.BlockId}'.",
                    nameof(document));
            }

            if (activePath is not null && !activePath.SequenceEqual(assignment.SectionPath))
            {
                FlushSection(activeUnits, activePath, normalizedDocument, options, tokenCounter, results);
                activeUnits.Clear();
                activePath = assignment.SectionPath.ToArray();
            }

            activePath ??= assignment.SectionPath.ToArray();
            activeUnits.AddRange(CreateUnits(block, assignment.SectionPath, options, tokenCounter));
        }

        if (activePath is not null)
            FlushSection(activeUnits, activePath, normalizedDocument, options, tokenCounter, results);

        return results
            .Select((parent, index) => Reordinal(parent, index))
            .ToArray();
    }

    private static IReadOnlyList<BlockUnit> CreateUnits(
        NormalizedBlock block,
        IReadOnlyList<string> sectionPath,
        ChunkingOptions options,
        ITokenCounter tokenCounter)
    {
        var tokenCount = Count(tokenCounter, block.Text, block.BlockId);
        if (tokenCount <= options.ParentMaxTokens || block.IsAtomic)
        {
            return new[] { new BlockUnit(block, block.Text, sectionPath, tokenCount) };
        }

        return SplitNonAtomicBlock(block, sectionPath, options.ParentMaxTokens, tokenCounter);
    }

    private static IReadOnlyList<BlockUnit> SplitNonAtomicBlock(
        NormalizedBlock block,
        IReadOnlyList<string> sectionPath,
        int maxTokens,
        ITokenCounter tokenCounter)
    {
        var words = System.Text.RegularExpressions.Regex.Matches(block.Text, @"\S+");
        if (words.Count == 0)
            return Array.Empty<BlockUnit>();

        var units = new List<BlockUnit>();
        var start = words[0].Index;
        var lastEnd = words[0].Index + words[0].Length;

        for (var index = 1; index < words.Count; index++)
        {
            var nextEnd = words[index].Index + words[index].Length;
            var candidate = block.Text[start..nextEnd];
            if (Count(tokenCounter, candidate, block.BlockId) <= maxTokens)
            {
                lastEnd = nextEnd;
                continue;
            }

            units.Add(CreateSlice(block, sectionPath, start, lastEnd, maxTokens, tokenCounter));
            start = words[index].Index;
            lastEnd = nextEnd;
        }

        units.Add(CreateSlice(block, sectionPath, start, lastEnd, maxTokens, tokenCounter));
        return units;
    }

    private static BlockUnit CreateSlice(
        NormalizedBlock block,
        IReadOnlyList<string> sectionPath,
        int start,
        int end,
        int maxTokens,
        ITokenCounter tokenCounter)
    {
        var text = block.Text[start..end];
        var tokenCount = Count(tokenCounter, text, block.BlockId);
        if (tokenCount > maxTokens)
        {
            throw new InvalidOperationException(
                $"Non-atomic block '{block.BlockId}' contains an indivisible token sequence above ParentMaxTokens.");
        }

        return new BlockUnit(block, text, sectionPath, tokenCount);
    }

    private static void FlushSection(
        IReadOnlyList<BlockUnit> units,
        IReadOnlyList<string> sectionPath,
        NormalizedDocument document,
        ChunkingOptions options,
        ITokenCounter tokenCounter,
        ICollection<ParentChunkResult> results)
    {
        if (units.Count == 0)
            return;

        var current = new List<BlockUnit>();
        foreach (var unit in units)
        {
            if (unit.Block.IsAtomic)
            {
                if (current.Count > 0)
                {
                    var pendingContent = Join(current);
                    var pendingTokens = Count(tokenCounter, pendingContent, current[^1].Block.BlockId);
                    results.Add(CreateParent(
                        document,
                        options,
                        current,
                        sectionPath,
                        pendingContent,
                        pendingTokens,
                        results.Count));
                    current.Clear();
                }

                results.Add(CreateParent(
                    document,
                    options,
                    new[] { unit },
                    sectionPath,
                    unit.Text,
                    unit.TokenCount,
                    results.Count));
                continue;
            }

            if (current.Count == 0)
            {
                current.Add(unit);
                continue;
            }

            var candidateText = Join(current.Append(unit));
            var candidateTokens = Count(tokenCounter, candidateText, unit.Block.BlockId);
            var currentText = Join(current);
            var currentTokens = Count(tokenCounter, currentText, current[^1].Block.BlockId);

            if (candidateTokens <= options.ParentTargetTokens ||
                (currentTokens < options.ParentMinTokens && candidateTokens <= options.ParentMaxTokens))
            {
                current.Add(unit);
                continue;
            }

            results.Add(CreateParent(document, options, current, sectionPath, currentText, currentTokens, results.Count));
            current.Clear();
            current.Add(unit);
        }

        if (current.Count > 0)
        {
            var content = Join(current);
            var tokenCount = Count(tokenCounter, content, current[^1].Block.BlockId);
            results.Add(CreateParent(document, options, current, sectionPath, content, tokenCount, results.Count));
        }
    }

    private static ParentChunkResult CreateParent(
        NormalizedDocument document,
        ChunkingOptions options,
        IReadOnlyList<BlockUnit> units,
        IReadOnlyList<string> sectionPath,
        string content,
        int tokenCount,
        int ordinal)
    {
        var first = units[0].Block;
        var last = units[^1].Block;
        var componentIds = units
            .SelectMany(unit => unit.Block.ComponentIds)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var contentHash = ComputeHash(content);
        var title = sectionPath.LastOrDefault() ?? document.Title;
        var configurationJson = ChunkingRunIdentity.SerializeConfiguration(options);
        var runId = ChunkingRunIdentity.CreateId(
            document.DocumentId,
            document.SourceContentHash,
            document.SourceSchemaVersion,
            options.ChunkerVersion,
            configurationJson);
        var id = $"parent-{runId}-{ordinal:D6}-{contentHash}";
        var sourceSpans = CreateSourceSpans(units);

        return new ParentChunkResult(
            id: id,
            documentId: document.DocumentId,
            ordinal: ordinal,
            title: title,
            sectionPath: sectionPath,
            content: content,
            tokenCount: tokenCount,
            pageFrom: first.PageNumber,
            pageTo: last.PageNumber,
            componentIds: componentIds,
            contentHash: contentHash,
            isAtomic: units.Count == 1 && units[0].Block.IsAtomic,
            sourceSpans: sourceSpans);
    }

    private static ParentChunkResult Reordinal(ParentChunkResult parent, int ordinal) => new(
        id: parent.Id,
        documentId: parent.DocumentId,
        ordinal: ordinal,
        title: parent.Title,
        sectionPath: parent.SectionPath,
        content: parent.Content,
        tokenCount: parent.TokenCount,
        pageFrom: parent.PageFrom,
        pageTo: parent.PageTo,
        componentIds: parent.ComponentIds,
        contentHash: parent.ContentHash,
        isAtomic: parent.IsAtomic,
        sourceSpans: parent.SourceSpans);

    private static IReadOnlyList<ChunkSourceSpan> CreateSourceSpans(IReadOnlyList<BlockUnit> units)
    {
        var spans = new List<ChunkSourceSpan>(units.Count);
        var offset = 0;
        foreach (var unit in units)
        {
            if (unit.Block.TableRows.Count > 0 &&
                string.Equals(unit.Text, unit.Block.Text, StringComparison.Ordinal))
            {
                var rowOffset = 0;
                foreach (var row in unit.Block.TableRows)
                {
                    var rowStart = unit.Text.IndexOf(
                        row.Text,
                        rowOffset,
                        StringComparison.Ordinal);
                    if (rowStart < 0)
                        break;

                    spans.Add(new ChunkSourceSpan(
                        offset + rowStart,
                        row.Text.Length,
                        row.PageNumber,
                        row.ComponentIds));
                    rowOffset = rowStart + row.Text.Length;
                }

                if (rowOffset == 0)
                {
                    spans.Add(new ChunkSourceSpan(
                        offset,
                        unit.Text.Length,
                        unit.Block.PageNumber,
                        unit.Block.ComponentIds));
                }
            }
            else
            {
                spans.Add(new ChunkSourceSpan(
                    offset,
                    unit.Text.Length,
                    unit.Block.PageNumber,
                    unit.Block.ComponentIds));
            }
            offset += unit.Text.Length + Separator.Length;
        }

        return spans;
    }

    private static string Join(IEnumerable<BlockUnit> units) =>
        string.Join(Separator, units.Select(unit => unit.Text));

    private static int Count(ITokenCounter tokenCounter, string text, string blockId)
    {
        var count = tokenCounter.Count(text);
        if (count < 0)
        {
            throw new InvalidOperationException(
                $"Token counter returned a negative count for block '{blockId}'.");
        }

        return count;
    }

    private static string ComputeHash(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private sealed record BlockUnit(
        NormalizedBlock Block,
        string Text,
        IReadOnlyList<string> SectionPath,
        int TokenCount);
}
