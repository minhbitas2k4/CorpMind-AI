namespace CorpMindAI.Application.Interfaces;

public interface IEmbeddingGenerator
{
    string Model { get; }

    int Dimensions { get; }

    Task<IReadOnlyList<EmbeddingVector>> GenerateAsync(
        IReadOnlyList<string> inputs,
        CancellationToken cancellationToken = default);
}

public sealed record EmbeddingVector(IReadOnlyList<float> Values);
