namespace CorpMindAI.Application.Settings;

public sealed class RagOptions
{
    public int MaxContextChunks { get; set; } = 5;

    public int MaxContextCharacters { get; set; } = 24_000;

    public double MinimumRelevantScore { get; set; } = 0.25;

    public void Validate()
    {
        if (MaxContextChunks is < 1 or > 20)
            throw new InvalidOperationException("Rag:MaxContextChunks must be between 1 and 20.");

        if (MaxContextCharacters is < 1_000 or > 100_000)
            throw new InvalidOperationException("Rag:MaxContextCharacters must be between 1,000 and 100,000.");

        if (MinimumRelevantScore is < 0 or > 1)
            throw new InvalidOperationException("Rag:MinimumRelevantScore must be between 0 and 1.");

    }
}
