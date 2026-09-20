namespace CorpMindAI.Application.Settings;

public sealed class SemanticSearchOptions
{
    public int CandidateLimit { get; set; } = 20;

    public double? MinimumScore { get; set; }

    public bool EnableTranslationFallback { get; set; } = true;

    public double TranslationFallbackScoreThreshold { get; set; } = 0.25;

    public void Validate()
    {
        if (CandidateLimit is < 1 or > 100)
            throw new InvalidOperationException("SemanticSearch:CandidateLimit must be between 1 and 100.");

        if (MinimumScore is < 0 or > 1)
            throw new InvalidOperationException("SemanticSearch:MinimumScore must be between 0 and 1 when configured.");

        if (TranslationFallbackScoreThreshold is < 0 or > 1)
        {
            throw new InvalidOperationException(
                "SemanticSearch:TranslationFallbackScoreThreshold must be between 0 and 1.");
        }
    }
}
