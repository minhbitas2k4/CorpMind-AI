namespace CorpMindAI.Application.Chunking.Models;

/// Heading metadata produced by a later section-detection phase.
public sealed class DetectedHeading
{
    public DetectedHeading(
        int documentId,
        string blockId,
        string text,
        int level,
        IEnumerable<string> sectionPath,
        double confidence)
    {
        ChunkingContractGuards.ValidateDocumentId(documentId);
        if (level <= 0)
            throw new ArgumentOutOfRangeException(nameof(level));

        ChunkingContractGuards.ValidateConfidence(confidence);

        DocumentId = documentId;
        BlockId = ChunkingContractGuards.Required(blockId, nameof(blockId));
        Text = ChunkingContractGuards.Required(text, nameof(text));
        Level = level;
        SectionPath = ChunkingContractGuards.RequiredList(sectionPath, nameof(sectionPath));
        Confidence = confidence;
    }

    public int DocumentId { get; }
    public string BlockId { get; }
    public string Text { get; }
    public int Level { get; }
    public IReadOnlyList<string> SectionPath { get; }
    public double Confidence { get; }
}
