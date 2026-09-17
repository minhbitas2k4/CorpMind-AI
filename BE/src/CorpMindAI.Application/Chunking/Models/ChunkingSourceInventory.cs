namespace CorpMindAI.Application.Chunking.Models;

// Immutable source-side inventory used only to validate traceability and
// coverage before a chunk graph is persisted.
public sealed class ChunkingSourceInventory
{
    public ChunkingSourceInventory(
        int documentId,
        IEnumerable<string> sourceComponentIds,
        IEnumerable<ChunkingSourceBlock> retainedBlocks,
        IEnumerable<NormalizationNotice> normalizationNotices)
    {
        ChunkingContractGuards.ValidateDocumentId(documentId);
        DocumentId = documentId;
        SourceComponentIds = (sourceComponentIds ??
            throw new ArgumentNullException(nameof(sourceComponentIds))).ToArray();
        RetainedBlocks = (retainedBlocks ??
            throw new ArgumentNullException(nameof(retainedBlocks))).ToArray();
        NormalizationNotices = (normalizationNotices ??
            throw new ArgumentNullException(nameof(normalizationNotices))).ToArray();
    }

    public int DocumentId { get; }
    public IReadOnlyList<string> SourceComponentIds { get; }
    public IReadOnlyList<ChunkingSourceBlock> RetainedBlocks { get; }
    public IReadOnlyList<NormalizationNotice> NormalizationNotices { get; }
    public long TotalRetainedTokens => RetainedBlocks.Sum(block => (long)block.TokenCount);
}

public sealed class ChunkingSourceBlock
{
    public ChunkingSourceBlock(
        string blockId,
        string content,
        int tokenCount,
        IEnumerable<string> componentIds)
    {
        if (tokenCount < 0)
            throw new ArgumentOutOfRangeException(nameof(tokenCount));

        BlockId = ChunkingContractGuards.Required(blockId, nameof(blockId));
        Content = content ?? string.Empty;
        TokenCount = tokenCount;
        ComponentIds = ChunkingContractGuards.RequiredList(componentIds, nameof(componentIds));
    }

    public string BlockId { get; }
    public string Content { get; }
    public int TokenCount { get; }
    public IReadOnlyList<string> ComponentIds { get; }
}
