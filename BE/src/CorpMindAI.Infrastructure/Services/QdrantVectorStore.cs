using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CorpMindAI.Application.Interfaces;
using CorpMindAI.Infrastructure.Settings;
using Microsoft.Extensions.Options;

namespace CorpMindAI.Infrastructure.Services;

public sealed class QdrantVectorStore : IVectorStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient;
    private readonly QdrantSettings _settings;

    public QdrantVectorStore(HttpClient httpClient, IOptions<QdrantSettings> options)
    {
        _httpClient = httpClient;
        _settings = options.Value;
        _settings.Validate();
    }

    public async Task EnsureCollectionAsync(CancellationToken cancellationToken = default)
    {
        var collectionPath = $"collections/{Uri.EscapeDataString(_settings.CollectionName)}";
        var collectionExists = false;
        using (var get = await SendAsync(HttpMethod.Get, collectionPath, null, cancellationToken))
        {
            if (get.IsSuccessStatusCode)
                collectionExists = true;
            else if (get.StatusCode != HttpStatusCode.NotFound)
                await ThrowProviderErrorAsync(get, "Qdrant collection lookup");
        }

        if (!collectionExists)
        {
            var createPayload = new
            {
                vectors = new
                {
                    size = _settings.VectorSize,
                    distance = _settings.Distance
                }
            };
            using var create = await SendAsync(HttpMethod.Put, collectionPath, createPayload, cancellationToken);
            if (!create.IsSuccessStatusCode && create.StatusCode != HttpStatusCode.Conflict)
                await ThrowProviderErrorAsync(create, "Qdrant collection creation");
        }

        await CreatePayloadIndexAsync(collectionPath, "department_id", "integer", cancellationToken);
        await CreatePayloadIndexAsync(collectionPath, "document_id", "integer", cancellationToken);
        await CreatePayloadIndexAsync(collectionPath, "chunking_run_id", "keyword", cancellationToken);
        await CreatePayloadIndexAsync(collectionPath, "is_active", "bool", cancellationToken);
    }

    public async Task UpsertAsync(
        IReadOnlyList<VectorPoint> points,
        CancellationToken cancellationToken = default)
    {
        if (points.Count == 0)
            return;
        if (points.Any(point => point.Vector.Count != _settings.VectorSize))
            throw new ArgumentException("Every vector must match the configured Qdrant vector size.", nameof(points));

        var payload = new
        {
            points = points.Select(point => new
            {
                id = CreatePointId(point.ChildChunkId),
                vector = point.Vector,
                payload = new
                {
                    child_chunk_id = point.ChildChunkId,
                    document_id = point.DocumentId,
                    department_id = point.DepartmentId,
                    chunking_run_id = point.ChunkingRunId,
                    source_content_hash = point.SourceContentHash,
                    source_schema_version = point.SourceSchemaVersion,
                    chunker_version = point.ChunkerVersion,
                    embedding_version = point.EmbeddingVersion,
                    is_active = false
                }
            }).ToArray()
        };
        var path = $"collections/{Uri.EscapeDataString(_settings.CollectionName)}/points?wait=true";
        using var response = await SendAsync(HttpMethod.Put, path, payload, cancellationToken);
        if (!response.IsSuccessStatusCode)
            await ThrowProviderErrorAsync(response, "Qdrant upsert");
    }

    public async Task ActivateRunAsync(
        string chunkingRunId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(chunkingRunId);
        var payload = new
        {
            payload = new { is_active = true },
            filter = new
            {
                must = new[] { Match("chunking_run_id", chunkingRunId) }
            }
        };
        var path = $"collections/{Uri.EscapeDataString(_settings.CollectionName)}/points/payload?wait=true";
        using var response = await SendAsync(HttpMethod.Post, path, payload, cancellationToken);
        if (!response.IsSuccessStatusCode)
            await ThrowProviderErrorAsync(response, "Qdrant activation");
    }

    public async Task DeleteOtherRunsAsync(
        int documentId,
        string activeChunkingRunId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(activeChunkingRunId);
        var payload = new
        {
            filter = new
            {
                must = new[] { Match("document_id", documentId) },
                must_not = new[] { Match("chunking_run_id", activeChunkingRunId) }
            }
        };
        var path = $"collections/{Uri.EscapeDataString(_settings.CollectionName)}/points/delete?wait=true";
        using var response = await SendAsync(HttpMethod.Post, path, payload, cancellationToken);
        if (!response.IsSuccessStatusCode)
            await ThrowProviderErrorAsync(response, "Qdrant cleanup");
    }

    public async Task<IReadOnlyList<VectorSearchHit>> SearchAsync(
        IReadOnlyList<float> queryVector,
        IReadOnlyCollection<int> departmentIds,
        int limit,
        double? minimumScore = null,
        CancellationToken cancellationToken = default)
    {
        if (queryVector.Count != _settings.VectorSize)
            throw new ArgumentException("The query vector has an unexpected dimension.", nameof(queryVector));
        if (departmentIds.Count == 0)
            throw new ArgumentException("At least one department is required for vector search.", nameof(departmentIds));

        var must = new List<object> { Match("is_active", true) };
        if (departmentIds.Count == 1)
            must.Add(Match("department_id", departmentIds.Single()));

        var filter = new Dictionary<string, object> { ["must"] = must };
        if (departmentIds.Count > 1)
            filter["should"] = departmentIds.Select(id => Match("department_id", id)).ToArray();

        var payload = new Dictionary<string, object?>
        {
            ["vector"] = queryVector,
            ["limit"] = limit,
            ["with_payload"] = true,
            ["filter"] = filter
        };
        if (minimumScore is not null)
            payload["score_threshold"] = minimumScore.Value;
        var path = $"collections/{Uri.EscapeDataString(_settings.CollectionName)}/points/search";
        using var response = await SendAsync(HttpMethod.Post, path, payload, cancellationToken);
        if (!response.IsSuccessStatusCode)
            await ThrowProviderErrorAsync(response, "Qdrant search");

        var body = await response.Content.ReadFromJsonAsync<QdrantResponse<List<QdrantSearchResult>>>(JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("Qdrant returned an empty search response.");
        return (body.Result ?? new List<QdrantSearchResult>())
            .Select(result =>
            {
                var payloadMap = result.Payload ?? new Dictionary<string, JsonElement>();
                return new VectorSearchHit(
                    ReadString(payloadMap, "child_chunk_id"),
                    ReadInt32(payloadMap, "document_id"),
                    ReadInt32(payloadMap, "department_id"),
                    ReadString(payloadMap, "chunking_run_id"),
                    result.Score);
            })
            .Where(hit => !string.IsNullOrWhiteSpace(hit.ChildChunkId))
            .ToArray();
    }

    private async Task CreatePayloadIndexAsync(
        string collectionPath,
        string fieldName,
        string fieldSchema,
        CancellationToken cancellationToken)
    {
        var payload = new { field_name = fieldName, field_schema = fieldSchema };
        using var response = await SendAsync(HttpMethod.Put, $"{collectionPath}/index", payload, cancellationToken);
        if (!response.IsSuccessStatusCode)
            await ThrowProviderErrorAsync(response, $"Qdrant payload index '{fieldName}' creation");
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        object? body,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(method, path);
                request.Headers.TryAddWithoutValidation("api-key", _settings.ApiKey);
                if (body is not null)
                    request.Content = JsonContent.Create(body, options: JsonOptions);
                var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                var retryable = response.StatusCode == (HttpStatusCode)429 || (int)response.StatusCode >= 500;
                if (!retryable || attempt == 3)
                    return response;

                response.Dispose();
            }
            catch (HttpRequestException) when (attempt < 3)
            {
                // Retry the bounded transient network failure below.
            }

            await Task.Delay(
                TimeSpan.FromMilliseconds(250 * Math.Pow(2, attempt - 1) + Random.Shared.Next(0, 100)),
                cancellationToken);
        }

        throw new InvalidOperationException("Qdrant request did not complete.");
    }

    private static object Match(string key, object value) =>
        new { key, match = new { value } };

    private static string ReadString(IReadOnlyDictionary<string, JsonElement> payload, string key) =>
        payload.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static int ReadInt32(IReadOnlyDictionary<string, JsonElement> payload, string key) =>
        payload.TryGetValue(key, out var value) && value.TryGetInt32(out var result) ? result : 0;

    private static string CreatePointId(string childChunkId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(childChunkId));
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes[..16]).ToString();
    }

    private static Task ThrowProviderErrorAsync(HttpResponseMessage response, string operation)
    {
        var status = (int)response.StatusCode;
        return Task.FromException(new HttpRequestException($"{operation} failed with HTTP {status}."));
    }

    private sealed record QdrantResponse<T>(string? Status, T? Result);
    private sealed record QdrantSearchResult(
        string? Id,
        double Score,
        Dictionary<string, JsonElement>? Payload);
}
