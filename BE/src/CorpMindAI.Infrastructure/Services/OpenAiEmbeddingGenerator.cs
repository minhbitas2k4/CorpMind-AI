using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CorpMindAI.Application.Interfaces;
using CorpMindAI.Infrastructure.Settings;
using Microsoft.Extensions.Options;

namespace CorpMindAI.Infrastructure.Services;

public sealed class OpenAiEmbeddingGenerator : IEmbeddingGenerator
{
    private const int MaxAttempts = 3;
    private const int MaxRetryAfterSeconds = 300;
    private static readonly HashSet<string> NonRetryableQuotaCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "credit_balance_exhausted",
        "insufficient_quota",
        "organization_spend_limit_exceeded",
        "project_spend_limit_exceeded",
        "organization_usage_limit_exceeded"
    };
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient;
    private readonly OpenAiEmbeddingSettings _settings;

    public OpenAiEmbeddingGenerator(HttpClient httpClient, IOptions<OpenAiEmbeddingSettings> options)
    {
        _httpClient = httpClient;
        _settings = options.Value;
        _settings.Validate();
    }

    public string Model => _settings.Model;

    public int Dimensions => _settings.Dimensions;

    public async Task<IReadOnlyList<EmbeddingVector>> GenerateAsync(
        IReadOnlyList<string> inputs,
        CancellationToken cancellationToken = default)
    {
        if (inputs.Count == 0)
            return Array.Empty<EmbeddingVector>();
        if (inputs.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Embedding inputs cannot be empty.", nameof(inputs));
        if (inputs.Count > _settings.BatchSize)
            throw new ArgumentException($"Embedding batch cannot exceed {_settings.BatchSize} inputs.", nameof(inputs));

        var payload = new
        {
            model = _settings.Model,
            input = inputs,
            encoding_format = "float",
            dimensions = _settings.Dimensions
        };

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            HttpResponseMessage response;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, "embeddings")
                {
                    Content = JsonContent.Create(payload, options: JsonOptions)
                };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.ApiKey);
                response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
            }
            catch (HttpRequestException) when (attempt < MaxAttempts)
            {
                await Task.Delay(GetRetryDelay(null, attempt), cancellationToken);
                continue;
            }

            using (response)
            {
                if (response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadFromJsonAsync<EmbeddingResponse>(JsonOptions, cancellationToken)
                        ?? throw new InvalidOperationException("OpenAI returned an empty embedding response.");
                    if (body.Data is null || body.Data.Count != inputs.Count)
                        throw new InvalidOperationException("OpenAI returned a different number of embeddings than inputs.");

                    return body.Data
                        .OrderBy(item => item.Index)
                        .Select(item =>
                        {
                            if (item.Embedding is null || item.Embedding.Length != _settings.Dimensions)
                                throw new InvalidOperationException("OpenAI returned an embedding with an unexpected dimension.");
                            return new EmbeddingVector(item.Embedding.Select(Convert.ToSingle).ToArray());
                        })
                        .ToArray();
                }

                var errorCode = await ReadErrorCodeAsync(response, cancellationToken);
                var retryable = IsRetryable(response.StatusCode, errorCode);
                if (!retryable || attempt == MaxAttempts)
                {
                    var suffix = string.IsNullOrWhiteSpace(errorCode) ? string.Empty : $" ({errorCode})";
                    throw new HttpRequestException(
                        $"OpenAI embeddings request failed with HTTP {(int)response.StatusCode}{suffix}.");
                }

                await Task.Delay(GetRetryDelay(response, attempt), cancellationToken);
            }
        }

        throw new InvalidOperationException("OpenAI embedding request did not complete.");
    }

    private static bool IsRetryable(HttpStatusCode statusCode, string? errorCode) =>
        (int)statusCode >= 500 ||
        (statusCode == (HttpStatusCode)429 && !NonRetryableQuotaCodes.Contains(errorCode ?? string.Empty));

    private static TimeSpan GetRetryDelay(HttpResponseMessage? response, int attempt)
    {
        var retryAfter = response?.Headers.RetryAfter?.Delta;
        if (retryAfter is not null)
        {
            var seconds = Math.Clamp(retryAfter.Value.TotalSeconds, 1, MaxRetryAfterSeconds);
            return TimeSpan.FromSeconds(seconds + Random.Shared.NextDouble());
        }

        return TimeSpan.FromMilliseconds(500 * Math.Pow(2, attempt - 1) + Random.Shared.Next(0, 250));
    }

    private static async Task<string?> ReadErrorCodeAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(body))
            return null;

        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement
                .GetProperty("error")
                .GetProperty("code")
                .GetString();
        }
        catch (JsonException)
        {
            return null;
        }
        catch (KeyNotFoundException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private sealed record EmbeddingResponse(IReadOnlyList<EmbeddingItem>? Data);
    private sealed record EmbeddingItem(int Index, float[]? Embedding);
}
