namespace CorpMindAI.Application.Chunking.Models;

public sealed class ChunkingOptions
{
    public const string SupportedTokenizerEncoding = "cl100k_base";

    public int ParentTargetTokens { get; init; } = 1200;
    public int ParentMaxTokens { get; init; } = 2000;
    public int ParentMinTokens { get; init; } = 200;
    public int ChildTargetTokens { get; init; } = 350;
    public int ChildMaxTokens { get; init; } = 500;
    public int ChildOverlapTokens { get; init; } = 60;
    public double MinimumSourceCoverage { get; init; } = 1.0;
    public string TokenizerEncoding { get; init; } = SupportedTokenizerEncoding;
    public string ChunkerVersion { get; init; } = "6.0";

    public void Validate()
    {
        ValidatePositive(ParentTargetTokens, nameof(ParentTargetTokens));
        ValidatePositive(ParentMaxTokens, nameof(ParentMaxTokens));
        ValidatePositive(ParentMinTokens, nameof(ParentMinTokens));
        ValidatePositive(ChildTargetTokens, nameof(ChildTargetTokens));
        ValidatePositive(ChildMaxTokens, nameof(ChildMaxTokens));
        ValidateNonNegative(ChildOverlapTokens, nameof(ChildOverlapTokens));

        if (double.IsNaN(MinimumSourceCoverage) ||
            double.IsInfinity(MinimumSourceCoverage) ||
            MinimumSourceCoverage is <= 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MinimumSourceCoverage),
                "MinimumSourceCoverage must be greater than zero and no greater than one.");
        }

        if (ParentMinTokens > ParentTargetTokens || ParentTargetTokens > ParentMaxTokens)
            throw new ArgumentException(
                "ParentMinTokens must be less than or equal to ParentTargetTokens, which must be less than or equal to ParentMaxTokens.");

        if (ChildTargetTokens > ChildMaxTokens)
            throw new ArgumentException(
                "ChildTargetTokens must be less than or equal to ChildMaxTokens.");

        if (ChildOverlapTokens >= ChildTargetTokens)
            throw new ArgumentException(
                "ChildOverlapTokens must be less than ChildTargetTokens.");

        if (!string.Equals(TokenizerEncoding, SupportedTokenizerEncoding, StringComparison.Ordinal))
            throw new ArgumentException(
                $"TokenizerEncoding must be '{SupportedTokenizerEncoding}' for this chunker version.",
                nameof(TokenizerEncoding));

        ChunkingContractGuards.Required(ChunkerVersion, nameof(ChunkerVersion));
    }

    private static void ValidatePositive(int value, string parameterName)
    {
        if (value <= 0)
            throw new ArgumentOutOfRangeException(parameterName, "Value must be greater than zero.");
    }

    private static void ValidateNonNegative(int value, string parameterName)
    {
        if (value < 0)
            throw new ArgumentOutOfRangeException(parameterName, "Value must not be negative.");
    }
}
