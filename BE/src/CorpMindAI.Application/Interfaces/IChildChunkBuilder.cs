using CorpMindAI.Application.Chunking.Models;

namespace CorpMindAI.Application.Interfaces;

public interface IChildChunkBuilder
{
    IReadOnlyList<ChildChunkResult> Build(
        IEnumerable<ParentChunkResult> parents,
        string? documentTitle,
        ChunkingOptions options,
        ITokenCounter tokenCounter);
}
