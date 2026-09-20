namespace CorpMindAI.Infrastructure.Settings;

public sealed class OpenAiAnswerSettings
{
    public string Endpoint { get; set; } = "https://api.openai.com/v1/";

    public string ApiKey { get; set; } = string.Empty;

    public string Model { get; set; } = "gpt-4.1-mini";

    public int MaxOutputTokens { get; set; } = 700;

    public int TimeoutSeconds { get; set; } = 60;

    public void Validate()
    {
        if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out _))
            throw new InvalidOperationException("OpenAI Answer:Endpoint must be an absolute URI.");
        if (string.IsNullOrWhiteSpace(ApiKey))
            throw new InvalidOperationException("OpenAI Answer:ApiKey is required.");
        if (!string.Equals(Model, "gpt-4.1-mini", StringComparison.Ordinal))
            throw new InvalidOperationException("OpenAI Answer:Model must be gpt-4.1-mini for this RAG implementation.");
        if (MaxOutputTokens is < 64 or > 4_096)
            throw new InvalidOperationException("OpenAI Answer:MaxOutputTokens must be between 64 and 4,096.");
        if (TimeoutSeconds is < 1 or > 300)
            throw new InvalidOperationException("OpenAI Answer:TimeoutSeconds must be between 1 and 300.");
    }
}
