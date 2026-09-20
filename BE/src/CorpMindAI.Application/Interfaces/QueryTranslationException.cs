namespace CorpMindAI.Application.Interfaces;

public sealed class QueryTranslationException : Exception
{
    public QueryTranslationException(string message)
        : base(message)
    {
    }

    public QueryTranslationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
