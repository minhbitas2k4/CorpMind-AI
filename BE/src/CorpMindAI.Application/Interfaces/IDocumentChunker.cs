using CorpMindAI.Application.Chunking.Models;

namespace CorpMindAI.Application.Interfaces;

public interface IDocumentChunker
{
    ChunkingResult Chunk(NormalizedDocument document, ChunkingOptions options);
}
