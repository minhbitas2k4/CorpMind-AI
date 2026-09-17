namespace CorpMindAI.Application.Chunking.Models;

// Normalized document plus deterministic heading and section assignments.
public sealed class SectionedDocument
{
    public SectionedDocument(
        NormalizedDocument document,
        IEnumerable<DetectedHeading> headings,
        IEnumerable<SectionAssignment> assignments,
        IEnumerable<string>? warnings = null)
    {
        Document = document ?? throw new ArgumentNullException(nameof(document));
        Headings = (headings ?? throw new ArgumentNullException(nameof(headings))).ToArray();
        Assignments = (assignments ?? throw new ArgumentNullException(nameof(assignments))).ToArray();
        Warnings = ChunkingContractGuards.OptionalList(warnings);

        var blockIds = document.Blocks.Select(block => block.BlockId).ToHashSet(StringComparer.Ordinal);
        if (Assignments.Count != blockIds.Count ||
            Assignments.Select(assignment => assignment.BlockId).Distinct(StringComparer.Ordinal).Count() !=
            Assignments.Count ||
            Assignments.Any(assignment =>
                assignment.DocumentId != document.DocumentId ||
                !blockIds.Contains(assignment.BlockId)))
        {
            throw new ArgumentException(
                "Assignments must contain exactly one entry for every normalized block.",
                nameof(assignments));
        }

        if (Headings.Any(heading =>
                heading.DocumentId != document.DocumentId ||
                !blockIds.Contains(heading.BlockId)))
        {
            throw new ArgumentException(
                "Every heading must belong to a normalized block in the document.",
                nameof(headings));
        }

        if (Headings.Select(heading => heading.BlockId).Distinct(StringComparer.Ordinal).Count() !=
            Headings.Count)
        {
            throw new ArgumentException("Heading block IDs must be unique.", nameof(headings));
        }
    }

    public NormalizedDocument Document { get; }
    public IReadOnlyList<DetectedHeading> Headings { get; }
    public IReadOnlyList<SectionAssignment> Assignments { get; }
    public IReadOnlyList<string> Warnings { get; }

    public IReadOnlyList<string> GetSectionPath(string blockId)
    {
        var assignment = Assignments.SingleOrDefault(item =>
            string.Equals(item.BlockId, blockId, StringComparison.Ordinal));
        if (assignment is null)
            throw new KeyNotFoundException($"No section assignment exists for block '{blockId}'.");

        return assignment.SectionPath;
    }
}
