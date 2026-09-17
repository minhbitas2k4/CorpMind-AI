using CorpMindAI.Application.Chunking.Models;

namespace CorpMindAI.Application.Interfaces;

public interface IParentChunkBuilder
{
    IReadOnlyList<ParentChunkResult> Build(
        SectionedDocument document,
        ChunkingOptions options,
        ITokenCounter tokenCounter);
}
