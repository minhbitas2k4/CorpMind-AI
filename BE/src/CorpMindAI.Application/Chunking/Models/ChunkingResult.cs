namespace CorpMindAI.Application.Chunking.Models;

public sealed class ChunkingResult
{
    public ChunkingResult(
        int documentId,
        string sourceSchemaVersion,
        string sourceContentHash,
        string chunkerVersion,
        IEnumerable<ParentChunkResult> parents,
        IEnumerable<ChildChunkResult> children,
        IEnumerable<string>? warnings = null)
    {
        ChunkingContractGuards.ValidateDocumentId(documentId);

        DocumentId = documentId;
        SourceSchemaVersion = ChunkingContractGuards.Required(
            sourceSchemaVersion,
            nameof(sourceSchemaVersion));
        SourceContentHash = ChunkingContractGuards.Required(
            sourceContentHash,
            nameof(sourceContentHash));
        ChunkerVersion = ChunkingContractGuards.Required(chunkerVersion, nameof(chunkerVersion));
        Parents = (parents ?? throw new ArgumentNullException(nameof(parents))).ToArray();
        Children = (children ?? throw new ArgumentNullException(nameof(children))).ToArray();
        Warnings = ChunkingContractGuards.OptionalList(warnings);

        if (Parents.Any(parent => parent.DocumentId != documentId))
            throw new ArgumentException("All parents must belong to the document.", nameof(parents));
        if (Children.Any(child => child.DocumentId != documentId))
            throw new ArgumentException("All children must belong to the document.", nameof(children));

        var parentIds = Parents.Select(parent => parent.Id).ToHashSet(StringComparer.Ordinal);
        if (Children.Any(child => !parentIds.Contains(child.ParentChunkId)))
            throw new ArgumentException("Every child must reference a parent in this result.", nameof(children));

        if (Parents.Select(parent => parent.Id).Distinct(StringComparer.Ordinal).Count() != Parents.Count)
            throw new ArgumentException("Parent IDs must be unique.", nameof(parents));
        if (Children.Select(child => child.Id).Distinct(StringComparer.Ordinal).Count() != Children.Count)
            throw new ArgumentException("Child IDs must be unique.", nameof(children));
    }

    public int DocumentId { get; }
    public string SourceSchemaVersion { get; }
    public string SourceContentHash { get; }
    public string ChunkerVersion { get; }
    public IReadOnlyList<ParentChunkResult> Parents { get; }
    public IReadOnlyList<ChildChunkResult> Children { get; }
    public IReadOnlyList<string> Warnings { get; }
}
