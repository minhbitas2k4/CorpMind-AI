using CorpMindAI.Application.Chunking.Models;

namespace CorpMindAI.Application.Interfaces;

public interface IChunkingResultValidator
{
    ChunkingValidationResult Validate(
        ChunkingResult? result,
        ChunkingSourceInventory sourceInventory,
        double minimumSourceCoverage);

    ChunkingValidationResult Validate(
        ChunkingResult? result,
        ChunkingSourceInventory sourceInventory,
        ChunkingOptions options,
        ITokenCounter tokenCounter) =>
        Validate(result, sourceInventory, options.MinimumSourceCoverage);
}
