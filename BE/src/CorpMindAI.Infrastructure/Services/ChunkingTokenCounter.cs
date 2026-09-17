using CorpMindAI.Application.Interfaces;
using SharpToken;

namespace CorpMindAI.Infrastructure.Services;

// Counts tokens with the configured chunk-boundary encoding without
// depending on an embedding provider or model name.
public sealed class ChunkingTokenCounter : ITokenCounter
{
    public const string EncodingName = "cl100k_base";

    private static readonly GptEncoding Encoding = GptEncoding.GetEncoding(EncodingName);

    public int Count(string text) => string.IsNullOrWhiteSpace(text)
        ? 0
        : Encoding.CountTokens(text);
}
