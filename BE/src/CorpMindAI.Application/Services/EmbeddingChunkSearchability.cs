namespace CorpMindAI.Application.Services;

public static class EmbeddingChunkSearchability
{
    public static bool IsSearchable(
        string? rawContent,
        IReadOnlyCollection<string>? sectionPath)
    {
        var content = rawContent?.Trim();
        if (string.IsNullOrWhiteSpace(content))
            return false;

        return sectionPath is null || !sectionPath.Any(path =>
            string.Equals(path?.Trim(), content, StringComparison.OrdinalIgnoreCase));
    }
}
