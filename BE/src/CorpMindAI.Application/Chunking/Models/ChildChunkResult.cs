namespace CorpMindAI.Application.Chunking.Models;

public sealed class ChildChunkResult
{
    public ChildChunkResult(
        string id,
        string parentChunkId,
        int documentId,
        int ordinal,
        IEnumerable<string>? sectionPath,
        string rawContent,
        string contextualizedContent,
        int tokenCount,
        int pageFrom,
        int pageTo,
        IEnumerable<string> componentIds,
        string contentHash,
        bool isAtomic = false)
    {
        ChunkingContractGuards.ValidateDocumentId(documentId);
        if (ordinal < 0)
            throw new ArgumentOutOfRangeException(nameof(ordinal));
        if (tokenCount < 0)
            throw new ArgumentOutOfRangeException(nameof(tokenCount));

        ChunkingContractGuards.ValidatePageRange(pageFrom, pageTo);

        Id = ChunkingContractGuards.Required(id, nameof(id));
        ParentChunkId = ChunkingContractGuards.Required(parentChunkId, nameof(parentChunkId));
        DocumentId = documentId;
        Ordinal = ordinal;
        SectionPath = ChunkingContractGuards.OptionalList(sectionPath);
        RawContent = ChunkingContractGuards.Required(rawContent, nameof(rawContent));
        ContextualizedContent = ChunkingContractGuards.Required(
            contextualizedContent,
            nameof(contextualizedContent));
        TokenCount = tokenCount;
        PageFrom = pageFrom;
        PageTo = pageTo;
        ComponentIds = ChunkingContractGuards.RequiredList(componentIds, nameof(componentIds));
        ContentHash = ChunkingContractGuards.Required(contentHash, nameof(contentHash));
        IsAtomic = isAtomic;
    }

    public string Id { get; }
    public string ParentChunkId { get; }
    public int DocumentId { get; }
    public int Ordinal { get; }
    public IReadOnlyList<string> SectionPath { get; }
    public string RawContent { get; }
    public string ContextualizedContent { get; }
    public int TokenCount { get; }
    public int PageFrom { get; }
    public int PageTo { get; }
    public IReadOnlyList<string> ComponentIds { get; }
    public string ContentHash { get; }
    public bool IsAtomic { get; }
}
