namespace CorpMindAI.Infrastructure.Settings;

public sealed class OpenAiQueryTranslationSettings
{
    public string Endpoint { get; set; } = "https://api.openai.com/v1/";

    public string ApiKey { get; set; } = string.Empty;

    public string Model { get; set; } = "gpt-4.1-mini";

    public int MaxOutputTokens { get; set; } = 512;

    public int TimeoutSeconds { get; set; } = 30;

    public void Validate()
    {
        if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out _))
            throw new InvalidOperationException("OpenAI Translation:Endpoint must be an absolute URI.");
        if (string.IsNullOrWhiteSpace(ApiKey))
            throw new InvalidOperationException("OpenAI Translation:ApiKey is required.");
        if (string.IsNullOrWhiteSpace(Model))
            throw new InvalidOperationException("OpenAI Translation:Model is required.");
        if (MaxOutputTokens is < 64 or > 4096)
            throw new InvalidOperationException("OpenAI Translation:MaxOutputTokens must be between 64 and 4096.");
        if (TimeoutSeconds is < 1 or > 300)
            throw new InvalidOperationException("OpenAI Translation:TimeoutSeconds must be between 1 and 300.");
    }
}
