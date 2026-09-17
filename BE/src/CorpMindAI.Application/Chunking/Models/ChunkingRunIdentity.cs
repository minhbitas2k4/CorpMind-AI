using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CorpMindAI.Application.Chunking.Models;

public static class ChunkingRunIdentity
{
    public static string SerializeConfiguration(ChunkingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        return JsonSerializer.Serialize(options);
    }

    public static string CreateId(
        int documentId,
        string sourceContentHash,
        string sourceSchemaVersion,
        string chunkerVersion,
        string configurationJson)
    {
        ChunkingContractGuards.ValidateDocumentId(documentId);
        var input = string.Join(
            "|",
            documentId.ToString(CultureInfo.InvariantCulture),
            ChunkingContractGuards.Required(sourceContentHash, nameof(sourceContentHash)),
            ChunkingContractGuards.Required(sourceSchemaVersion, nameof(sourceSchemaVersion)),
            ChunkingContractGuards.Required(chunkerVersion, nameof(chunkerVersion)),
            ChunkingContractGuards.Required(configurationJson, nameof(configurationJson)));
        return "run-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)))
            .ToLowerInvariant();
    }
}
