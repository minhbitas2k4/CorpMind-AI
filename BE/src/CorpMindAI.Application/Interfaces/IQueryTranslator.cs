namespace CorpMindAI.Application.Interfaces;

public interface IQueryTranslator
{
    Task<string> TranslateToAlternateLanguageAsync(
        string query,
        CancellationToken cancellationToken = default);
}
