namespace CorpMindAI.Infrastructure.Settings;

public sealed class OpenAiEmbeddingSettings
{
    public string Endpoint { get; set; } = "https://api.openai.com/v1/";
    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = "text-embedding-3-small";
    public int Dimensions { get; set; } = 1536;
    public int BatchSize { get; set; } = 64;
    public int TimeoutSeconds { get; set; } = 90;

    public void Validate()
    {
        if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out _))
            throw new InvalidOperationException("OpenAI Embeddings:Endpoint must be an absolute URI.");
        if (string.IsNullOrWhiteSpace(ApiKey))
            throw new InvalidOperationException("OpenAI Embeddings:ApiKey is required.");
        if (!string.Equals(Model, "text-embedding-3-small", StringComparison.Ordinal))
            throw new InvalidOperationException("OpenAI Embeddings:Model must be text-embedding-3-small for this index.");
        if (Dimensions != 1536)
            throw new InvalidOperationException("OpenAI Embeddings:Dimensions must be 1536 for this index.");
        if (BatchSize is < 1 or > 128)
            throw new InvalidOperationException("OpenAI Embeddings:BatchSize must be between 1 and 128.");
        if (TimeoutSeconds is < 1 or > 300)
            throw new InvalidOperationException("OpenAI Embeddings:TimeoutSeconds must be between 1 and 300.");
    }
}
