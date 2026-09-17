namespace CorpMindAI.Application.Chunking.Models;

public sealed class NormalizationNotice
{
    public NormalizationNotice(
        string componentId,
        int pageNumber,
        string reason,
        string text)
    {
        if (pageNumber <= 0)
            throw new ArgumentOutOfRangeException(nameof(pageNumber));

        ComponentId = ChunkingContractGuards.Required(componentId, nameof(componentId));
        PageNumber = pageNumber;
        Reason = ChunkingContractGuards.Required(reason, nameof(reason));
        Text = text ?? string.Empty;
    }

    public string ComponentId { get; }
    public int PageNumber { get; }
    public string Reason { get; }
    public string Text { get; }
}
