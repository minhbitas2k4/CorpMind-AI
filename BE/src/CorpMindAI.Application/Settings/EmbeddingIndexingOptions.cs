namespace CorpMindAI.Application.Settings;

public sealed class EmbeddingIndexingOptions
{
    public int BatchSize { get; set; } = 64;
    public int DispatchBatchSize { get; set; } = 5;

    public void Validate()
    {
        if (BatchSize is < 1 or > 256)
            throw new InvalidOperationException("EmbeddingIndexing:BatchSize must be between 1 and 256.");
        if (DispatchBatchSize is < 1 or > 50)
            throw new InvalidOperationException("EmbeddingIndexing:DispatchBatchSize must be between 1 and 50.");
    }
}
