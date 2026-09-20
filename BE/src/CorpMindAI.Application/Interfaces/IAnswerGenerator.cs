namespace CorpMindAI.Application.Interfaces;

public interface IAnswerGenerator
{
    Task<GeneratedAnswer> GenerateAsync(
        string query,
        IReadOnlyList<AnswerContext> context,
        CancellationToken cancellationToken = default);
}

public sealed record AnswerContext(
    string ChildChunkId,
    int DocumentId,
    double Score,
    IReadOnlyList<string> SectionPath,
    string Content,
    int? PageFrom,
    int? PageTo,
    IReadOnlyList<string> ComponentIds);

public sealed record GeneratedAnswer(
    string Answer,
    bool HasAnswer,
    IReadOnlyList<string> CitationChunkIds);

public sealed class AnswerGenerationException : Exception
{
    public AnswerGenerationException(string message)
        : base(message)
    {
    }

    public AnswerGenerationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
