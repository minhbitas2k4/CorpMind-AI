namespace CorpMindAI.Application.Chunking.Models;

// Normalized document contract consumed by the chunking pipeline.
public sealed class NormalizedDocument
{
    public NormalizedDocument(
        int documentId,
        string sourceSchemaVersion,
        string sourceContentHash,
        IEnumerable<NormalizedBlock> blocks,
        string? title = null,
        IEnumerable<NormalizationNotice>? notices = null)
    {
        ChunkingContractGuards.ValidateDocumentId(documentId);

        DocumentId = documentId;
        SourceSchemaVersion = ChunkingContractGuards.Required(
            sourceSchemaVersion,
            nameof(sourceSchemaVersion));
        SourceContentHash = ChunkingContractGuards.Required(
            sourceContentHash,
            nameof(sourceContentHash));
        Blocks = (blocks ?? throw new ArgumentNullException(nameof(blocks))).ToArray();

        if (Blocks.Any(block => block.DocumentId != documentId))
            throw new ArgumentException("All blocks must belong to the document.", nameof(blocks));

        Title = string.IsNullOrWhiteSpace(title) ? null : title.Trim();
        Notices = (notices ?? Enumerable.Empty<NormalizationNotice>()).ToArray();

        if (Notices.Any(notice => notice.PageNumber <= 0))
            throw new ArgumentException("Normalization notice page numbers must be positive.", nameof(notices));
    }

    public int DocumentId { get; }
    public string? Title { get; }
    public string SourceSchemaVersion { get; }
    public string SourceContentHash { get; }
    public IReadOnlyList<NormalizedBlock> Blocks { get; }
    public IReadOnlyList<NormalizationNotice> Notices { get; }
}
