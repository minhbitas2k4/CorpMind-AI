namespace CorpMindAI.Application.Chunking.Models;

public sealed class ChunkSourceSpan
{
    public ChunkSourceSpan(
        int start,
        int length,
        int pageNumber,
        IEnumerable<string> componentIds)
    {
        if (start < 0)
            throw new ArgumentOutOfRangeException(nameof(start));
        if (length <= 0)
            throw new ArgumentOutOfRangeException(nameof(length));
        if (pageNumber <= 0)
            throw new ArgumentOutOfRangeException(nameof(pageNumber));

        Start = start;
        Length = length;
        PageNumber = pageNumber;
        ComponentIds = ChunkingContractGuards.RequiredList(componentIds, nameof(componentIds));
    }

    public int Start { get; }
    public int Length { get; }
    public int End => checked(Start + Length);
    public int PageNumber { get; }
    public IReadOnlyList<string> ComponentIds { get; }
}
