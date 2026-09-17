namespace CorpMindAI.Application.Chunking.Models;

// A source block that has been normalized from structured OCR output.
public sealed class NormalizedBlock
{
    public NormalizedBlock(
        int documentId,
        string blockId,
        int pageNumber,
        int readingOrder,
        string blockType,
        string text,
        double confidence,
        IEnumerable<string> componentIds,
        string? sourceLayoutType = null,
        bool isAtomic = false,
        IEnumerable<NormalizedTableRow>? tableRows = null)
    {
        ChunkingContractGuards.ValidateDocumentId(documentId);
        if (pageNumber <= 0)
            throw new ArgumentOutOfRangeException(nameof(pageNumber));
        if (readingOrder < 0)
            throw new ArgumentOutOfRangeException(nameof(readingOrder));

        ChunkingContractGuards.ValidateConfidence(confidence);

        DocumentId = documentId;
        BlockId = ChunkingContractGuards.Required(blockId, nameof(blockId));
        PageNumber = pageNumber;
        ReadingOrder = readingOrder;
        BlockType = ChunkingContractGuards.Required(blockType, nameof(blockType));
        Text = text ?? string.Empty;
        Confidence = confidence;
        ComponentIds = ChunkingContractGuards.RequiredList(componentIds, nameof(componentIds));
        SourceLayoutType = string.IsNullOrWhiteSpace(sourceLayoutType) ? null : sourceLayoutType.Trim();
        IsAtomic = isAtomic;
        TableRows = (tableRows ?? Enumerable.Empty<NormalizedTableRow>()).ToArray();
        if (TableRows.Any(row => row.PageNumber != pageNumber))
            throw new ArgumentException("Normalized table rows must belong to the block page.", nameof(tableRows));
    }

    public int DocumentId { get; }
    public string BlockId { get; }
    public int PageNumber { get; }
    public int ReadingOrder { get; }
    public string BlockType { get; }
    public string Text { get; }
    public double Confidence { get; }
    public IReadOnlyList<string> ComponentIds { get; }
    public string? SourceLayoutType { get; }
    public bool IsAtomic { get; }
    public IReadOnlyList<NormalizedTableRow> TableRows { get; }
}

/// <summary>
/// A logical table row retained through normalization for row-aware chunking
/// and precise source spans.
/// </summary>
public sealed class NormalizedTableRow
{
    public NormalizedTableRow(
        int rowIndex,
        int pageNumber,
        string text,
        IEnumerable<string> componentIds)
    {
        if (rowIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(rowIndex));
        if (pageNumber <= 0)
            throw new ArgumentOutOfRangeException(nameof(pageNumber));

        RowIndex = rowIndex;
        PageNumber = pageNumber;
        Text = ChunkingContractGuards.Required(text, nameof(text));
        ComponentIds = ChunkingContractGuards.RequiredList(componentIds, nameof(componentIds));
    }

    public int RowIndex { get; }
    public int PageNumber { get; }
    public string Text { get; }
    public IReadOnlyList<string> ComponentIds { get; }
}
