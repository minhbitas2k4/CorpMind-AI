namespace CorpMindAI.Application.Chunking.Models;

public sealed record ChunkingValidationError(
    string Code,
    string Message,
    int? DocumentId = null,
    string? ParentChunkId = null,
    string? ChildChunkId = null);

public sealed class ChunkingValidationResult
{
    public ChunkingValidationResult(
        IEnumerable<ChunkingValidationError>? errors = null,
        IEnumerable<string>? warnings = null,
        double? sourceCoverage = null,
        double? sourceFidelity = null,
        double? parentCoverage = null,
        double? childCoverage = null)
    {
        Errors = (errors ?? Array.Empty<ChunkingValidationError>()).ToArray();
        Warnings = (warnings ?? Array.Empty<string>()).ToArray();
        SourceCoverage = sourceCoverage;
        SourceFidelity = sourceFidelity;
        ParentCoverage = parentCoverage ?? sourceCoverage;
        ChildCoverage = childCoverage;
    }

    public bool IsValid => Errors.Count == 0;
    public IReadOnlyList<ChunkingValidationError> Errors { get; }
    public IReadOnlyList<string> Warnings { get; }

    public double? SourceFidelity { get; }

    public double? ParentCoverage { get; }

    public double? ChildCoverage { get; }

    public double? SourceToParentCoverage => ParentCoverage;

    public double? ParentToChildCoverage => ChildCoverage;

    public double? PdfToStructuredFidelity => SourceFidelity;

    public double? SourceCoverage { get; }
}
