using CorpMindAI.Application.DTOs.Common;
using CorpMindAI.Application.Interfaces;
using CorpMindAI.Application.Services;
using CorpMindAI.Application.Settings;
using MediatR;
using Microsoft.Extensions.Logging;

namespace CorpMindAI.Application.Usecase.Search.Query;

public sealed record SemanticSearchQuery(
    int UserId,
    int DepartmentId,
    string Query,
    int Limit = 10)
    : IRequest<ServiceResult<IReadOnlyList<SemanticSearchResultDto>>>;

public sealed record SemanticSearchResultDto(
    string ChildChunkId,
    int DocumentId,
    double Score,
    IReadOnlyList<string> SectionPath,
    string Content,
    int? PageFrom,
    int? PageTo,
    IReadOnlyList<string> ComponentIds);

public sealed class SemanticSearchQueryHandler
    : IRequestHandler<SemanticSearchQuery, ServiceResult<IReadOnlyList<SemanticSearchResultDto>>>
{
    private const int MaxQueryLength = 8_000;
    private const int MaxLimit = 50;
    private readonly IUserRepository _users;
    private readonly IEmbeddingGenerator _embeddings;
    private readonly IVectorStore _vectors;
    private readonly IEmbeddingIndexSource _source;
    private readonly IQueryTranslator _queryTranslator;
    private readonly SemanticSearchOptions _options;
    private readonly ILogger<SemanticSearchQueryHandler> _logger;

    public SemanticSearchQueryHandler(
        IUserRepository users,
        IEmbeddingGenerator embeddings,
        IVectorStore vectors,
        IEmbeddingIndexSource source,
        IQueryTranslator queryTranslator,
        SemanticSearchOptions options,
        ILogger<SemanticSearchQueryHandler> logger)
    {
        _users = users;
        _embeddings = embeddings;
        _vectors = vectors;
        _source = source;
        _queryTranslator = queryTranslator;
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        _logger = logger;
    }

    public async Task<ServiceResult<IReadOnlyList<SemanticSearchResultDto>>> Handle(
        SemanticSearchQuery request,
        CancellationToken cancellationToken)
    {
        if (request.UserId <= 0 || request.DepartmentId <= 0)
            return ServiceResult<IReadOnlyList<SemanticSearchResultDto>>.Fail("User and department are required.");
        if (string.IsNullOrWhiteSpace(request.Query) || request.Query.Length > MaxQueryLength)
            return ServiceResult<IReadOnlyList<SemanticSearchResultDto>>.Fail("Search query is required and must be at most 8,000 characters.");
        if (request.Limit is < 1 or > MaxLimit)
            return ServiceResult<IReadOnlyList<SemanticSearchResultDto>>.Fail($"Limit must be between 1 and {MaxLimit}.");

        var user = await _users.GetUserById(request.UserId);
        var canReadDepartment = user is not null &&
            string.Equals(user.Status, "active", StringComparison.OrdinalIgnoreCase) &&
            user.UserRoles.Any(role =>
                role.DepartmentId == request.DepartmentId &&
                role.Role is not null &&
                (role.Role.RoleName is "knowledge_contributor" or "knowledge_manager" or "system_admin"));
        if (!canReadDepartment)
            return ServiceResult<IReadOnlyList<SemanticSearchResultDto>>.Fail("You do not have permission to search this department.");

        await _vectors.EnsureCollectionAsync(cancellationToken);
        var candidateLimit = Math.Max(request.Limit, _options.CandidateLimit);
        var normalizedQuery = request.Query.Trim();
        var hits = await SearchAsync(
            normalizedQuery,
            request.DepartmentId,
            candidateLimit,
            cancellationToken);

        if (ShouldUseTranslationFallback(hits))
        {
            try
            {
                var translatedQuery = await _queryTranslator.TranslateToAlternateLanguageAsync(
                    normalizedQuery,
                    cancellationToken);
                if (IsUsableTranslation(normalizedQuery, translatedQuery))
                {
                    var translatedHits = await SearchAsync(
                        translatedQuery.Trim(),
                        request.DepartmentId,
                        candidateLimit,
                        cancellationToken);
                    hits = MergeHits(hits, translatedHits);
                    _logger.LogInformation(
                        "Semantic search used translation fallback for DepartmentId {DepartmentId}.",
                        request.DepartmentId);
                }
            }
            catch (QueryTranslationException exception)
            {
                _logger.LogWarning(
                    exception,
                    "Semantic search translation fallback failed for DepartmentId {DepartmentId}; original-query results will be used.",
                    request.DepartmentId);
            }
        }

        if (hits.Count == 0)
            return ServiceResult<IReadOnlyList<SemanticSearchResultDto>>.Ok(Array.Empty<SemanticSearchResultDto>());

        var chunks = await _source.GetChunksByIdsAsync(
            hits.Select(hit => hit.ChildChunkId).Distinct(StringComparer.Ordinal).ToArray(),
            request.DepartmentId,
            cancellationToken);
        var results = hits
            .Where(hit => chunks.ContainsKey(hit.ChildChunkId))
            .Where(hit => EmbeddingChunkSearchability.IsSearchable(
                chunks[hit.ChildChunkId].RawContent,
                chunks[hit.ChildChunkId].SectionPath))
            .Select(hit =>
            {
                var chunk = chunks[hit.ChildChunkId];
                return new SemanticSearchResultDto(
                    chunk.Id,
                    chunk.DocumentId,
                    hit.Score,
                    chunk.SectionPath,
                    chunk.RawContent,
                    chunk.PageFrom,
                    chunk.PageTo,
                    chunk.ComponentIds);
            })
            .Take(request.Limit)
            .ToArray();

        _logger.LogInformation(
            "Semantic search returned {ResultCount} result(s) for DepartmentId {DepartmentId}.",
            results.Length,
            request.DepartmentId);
        return ServiceResult<IReadOnlyList<SemanticSearchResultDto>>.Ok(results);
    }

    private async Task<IReadOnlyList<VectorSearchHit>> SearchAsync(
        string query,
        int departmentId,
        int candidateLimit,
        CancellationToken cancellationToken)
    {
        var embedding = (await _embeddings.GenerateAsync(new[] { query }, cancellationToken))[0];
        return await _vectors.SearchAsync(
            embedding.Values,
            new[] { departmentId },
            candidateLimit,
            _options.MinimumScore,
            cancellationToken);
    }

    private bool ShouldUseTranslationFallback(IReadOnlyList<VectorSearchHit> hits) =>
        _options.EnableTranslationFallback &&
        (hits.Count == 0 || hits.Max(hit => hit.Score) < _options.TranslationFallbackScoreThreshold);

    private static bool IsUsableTranslation(string originalQuery, string translatedQuery) =>
        !string.IsNullOrWhiteSpace(translatedQuery) &&
        translatedQuery.Length <= MaxQueryLength &&
        !string.Equals(originalQuery, translatedQuery.Trim(), StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<VectorSearchHit> MergeHits(
        IReadOnlyList<VectorSearchHit> originalHits,
        IReadOnlyList<VectorSearchHit> translatedHits) =>
        originalHits
            .Concat(translatedHits)
            .GroupBy(hit => hit.ChildChunkId, StringComparer.Ordinal)
            .Select(group => group
                .OrderByDescending(hit => hit.Score)
                .First())
            .OrderByDescending(hit => hit.Score)
            .ThenBy(hit => hit.ChildChunkId, StringComparer.Ordinal)
            .ToArray();
}
