namespace CorpMindAI.Application.Chunking.Models;

// Parent context produced by the chunking pipeline before persistence.
public sealed class ParentChunkResult
{
    public ParentChunkResult(
        string id,
        int documentId,
        int ordinal,
        string? title,
        IEnumerable<string>? sectionPath,
        string content,
        int tokenCount,
        int pageFrom,
        int pageTo,
        IEnumerable<string> componentIds,
        string contentHash,
        bool isAtomic = false,
        IEnumerable<ChunkSourceSpan>? sourceSpans = null)
    {
        ChunkingContractGuards.ValidateDocumentId(documentId);
        if (ordinal < 0)
            throw new ArgumentOutOfRangeException(nameof(ordinal));
        if (tokenCount < 0)
            throw new ArgumentOutOfRangeException(nameof(tokenCount));

        ChunkingContractGuards.ValidatePageRange(pageFrom, pageTo);

        Id = ChunkingContractGuards.Required(id, nameof(id));
        DocumentId = documentId;
        Ordinal = ordinal;
        Title = string.IsNullOrWhiteSpace(title) ? null : title.Trim();
        SectionPath = ChunkingContractGuards.OptionalList(sectionPath);
        Content = ChunkingContractGuards.Required(content, nameof(content));
        TokenCount = tokenCount;
        PageFrom = pageFrom;
        PageTo = pageTo;
        ComponentIds = ChunkingContractGuards.RequiredList(componentIds, nameof(componentIds));
        ContentHash = ChunkingContractGuards.Required(contentHash, nameof(contentHash));
        IsAtomic = isAtomic;
        SourceSpans = (sourceSpans ?? Array.Empty<ChunkSourceSpan>()).ToArray();
        if (SourceSpans.Any(span => span.End > Content.Length))
            throw new ArgumentException("Source spans must stay within parent content.", nameof(sourceSpans));
    }

    public string Id { get; }
    public int DocumentId { get; }
    public int Ordinal { get; }
    public string? Title { get; }
    public IReadOnlyList<string> SectionPath { get; }
    public string Content { get; }
    public int TokenCount { get; }
    public int PageFrom { get; }
    public int PageTo { get; }
    public IReadOnlyList<string> ComponentIds { get; }
    public string ContentHash { get; }
    public bool IsAtomic { get; }
    public IReadOnlyList<ChunkSourceSpan> SourceSpans { get; }
}
