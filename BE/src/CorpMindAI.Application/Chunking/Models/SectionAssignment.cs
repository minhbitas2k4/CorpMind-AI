namespace CorpMindAI.Application.Chunking.Models;

// The section context associated with one normalized source block.
public sealed class SectionAssignment
{
    public SectionAssignment(
        int documentId,
        string blockId,
        IEnumerable<string>? sectionPath,
        bool isHeading,
        int? headingLevel = null)
    {
        ChunkingContractGuards.ValidateDocumentId(documentId);
        if (isHeading && (!headingLevel.HasValue || headingLevel.Value <= 0))
            throw new ArgumentOutOfRangeException(nameof(headingLevel));
        if (!isHeading && headingLevel.HasValue)
            throw new ArgumentException("A non-heading block cannot have a heading level.", nameof(headingLevel));

        DocumentId = documentId;
        BlockId = ChunkingContractGuards.Required(blockId, nameof(blockId));
        SectionPath = ChunkingContractGuards.OptionalList(sectionPath);
        IsHeading = isHeading;
        HeadingLevel = headingLevel;
    }

    public int DocumentId { get; }
    public string BlockId { get; }
    public IReadOnlyList<string> SectionPath { get; }
    public bool IsHeading { get; }
    public int? HeadingLevel { get; }
}
