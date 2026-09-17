namespace CorpMindAI.Application.Chunking.Models;

internal static class ChunkingContractGuards
{
    public static string Required(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Value is required.", parameterName);

        return value.Trim();
    }

    public static IReadOnlyList<string> RequiredList(
        IEnumerable<string>? values,
        string parameterName)
    {
        var result = (values ?? throw new ArgumentNullException(parameterName))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .ToArray();

        if (result.Length == 0)
            throw new ArgumentException("At least one value is required.", parameterName);

        return result;
    }

    public static IReadOnlyList<string> OptionalList(IEnumerable<string>? values) =>
        (values ?? Enumerable.Empty<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .ToArray();

    public static void ValidateDocumentId(int documentId, string parameterName = "documentId")
    {
        if (documentId <= 0)
            throw new ArgumentOutOfRangeException(parameterName, "DocumentId must be greater than zero.");
    }

    public static void ValidatePageRange(int pageFrom, int pageTo)
    {
        if (pageFrom <= 0)
            throw new ArgumentOutOfRangeException(nameof(pageFrom), "PageFrom must be greater than zero.");

        if (pageTo < pageFrom)
            throw new ArgumentOutOfRangeException(nameof(pageTo), "PageTo must be greater than or equal to PageFrom.");
    }

    public static void ValidateConfidence(double confidence, string parameterName = "confidence")
    {
        if (confidence is < 0 or > 1)
            throw new ArgumentOutOfRangeException(parameterName, "Confidence must be between zero and one.");
    }
}
