namespace CorpMindAI.Infrastructure.Settings;

public sealed class QdrantSettings
{
    public string Endpoint { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string CollectionName { get; set; } = "corp_mind_ai_child_chunks_v1";
    public int VectorSize { get; set; } = 1536;
    public string Distance { get; set; } = "Cosine";
    public int TimeoutSeconds { get; set; } = 60;

    public void Validate()
    {
        if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out _))
            throw new InvalidOperationException("Qdrant:Endpoint must be an absolute URI.");
        if (string.IsNullOrWhiteSpace(ApiKey))
            throw new InvalidOperationException("Qdrant:ApiKey is required.");
        if (string.IsNullOrWhiteSpace(CollectionName) || CollectionName.Length > 80)
            throw new InvalidOperationException("Qdrant:CollectionName is required and must be at most 80 characters.");
        if (VectorSize != 1536)
            throw new InvalidOperationException("Qdrant:VectorSize must be 1536 for text-embedding-3-small.");
        if (!string.Equals(Distance, "Cosine", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Qdrant:Distance must be Cosine for this index.");
        if (TimeoutSeconds is < 1 or > 300)
            throw new InvalidOperationException("Qdrant:TimeoutSeconds must be between 1 and 300.");
    }
}
