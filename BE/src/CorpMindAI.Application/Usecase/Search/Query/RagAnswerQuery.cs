using CorpMindAI.Application.DTOs.Common;
using CorpMindAI.Application.Interfaces;
using CorpMindAI.Application.Settings;
using MediatR;
using Microsoft.Extensions.Logging;

namespace CorpMindAI.Application.Usecase.Search.Query;

public sealed record RagAnswerQuery(
    int UserId,
    int DepartmentId,
    string Query,
    int Limit = 10)
    : IRequest<ServiceResult<RagAnswerResponseDto>>;

public sealed record RagCitationDto(
    string ChildChunkId,
    int DocumentId,
    int? PageFrom,
    int? PageTo,
    IReadOnlyList<string> SectionPath,
    IReadOnlyList<string> ComponentIds,
    double Score);

public sealed record RagAnswerResponseDto(
    string Answer,
    bool HasAnswer,
    IReadOnlyList<RagCitationDto> Citations,
    int RetrievedCount);

public sealed class RagAnswerQueryHandler
    : IRequestHandler<RagAnswerQuery, ServiceResult<RagAnswerResponseDto>>
{
    private const int MaxQueryLength = 8_000;
    private const int MaxLimit = 50;
    private const string EnglishNoAnswerMessage =
        "I couldn't find enough information in the authorized documents to answer this question.";
    private const string VietnameseNoAnswerMessage =
        "Tôi không tìm thấy đủ thông tin trong các tài liệu được phép truy cập để trả lời câu hỏi này.";

    private readonly ISender _sender;
    private readonly IAnswerGenerator _answerGenerator;
    private readonly RagOptions _options;
    private readonly ILogger<RagAnswerQueryHandler> _logger;

    public RagAnswerQueryHandler(
        ISender sender,
        IAnswerGenerator answerGenerator,
        RagOptions options,
        ILogger<RagAnswerQueryHandler> logger)
    {
        _sender = sender;
        _answerGenerator = answerGenerator;
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        _logger = logger;
    }

    public async Task<ServiceResult<RagAnswerResponseDto>> Handle(
        RagAnswerQuery request,
        CancellationToken cancellationToken)
    {
        if (request.UserId <= 0 || request.DepartmentId <= 0)
            return ServiceResult<RagAnswerResponseDto>.Fail("User and department are required.");
        if (string.IsNullOrWhiteSpace(request.Query) || request.Query.Length > MaxQueryLength)
            return ServiceResult<RagAnswerResponseDto>.Fail("Query is required and must be at most 8,000 characters.");
        if (request.Limit is < 1 or > MaxLimit)
            return ServiceResult<RagAnswerResponseDto>.Fail($"Limit must be between 1 and {MaxLimit}.");

        var search = await _sender.Send(
            new SemanticSearchQuery(request.UserId, request.DepartmentId, request.Query, request.Limit),
            cancellationToken);
        if (!search.Success)
            return ServiceResult<RagAnswerResponseDto>.Fail(search.Message);

        var retrieved = search.Data ?? Array.Empty<SemanticSearchResultDto>();
        var context = BuildContext(retrieved);
        if (context.Count == 0)
        {
            _logger.LogInformation(
                "RAG returned no answer because no relevant context was found for DepartmentId {DepartmentId}.",
                request.DepartmentId);
            return ServiceResult<RagAnswerResponseDto>.Ok(
                new RagAnswerResponseDto(
                    GetNoAnswerMessage(request.Query),
                    false,
                    Array.Empty<RagCitationDto>(),
                    retrieved.Count));
        }

        GeneratedAnswer generated;
        try
        {
            generated = await _answerGenerator.GenerateAsync(
                request.Query.Trim(),
                context,
                cancellationToken);
        }
        catch (AnswerGenerationException exception)
        {
            _logger.LogError(
                exception,
                "RAG answer generation failed for DepartmentId {DepartmentId}.",
                request.DepartmentId);
            return ServiceResult<RagAnswerResponseDto>.Fail(
                "Answer generation is temporarily unavailable. Please try again.");
        }

        var citations = MapCitations(generated.CitationChunkIds, context);
        if (!generated.HasAnswer || citations.Count == 0)
        {
            return ServiceResult<RagAnswerResponseDto>.Ok(
                new RagAnswerResponseDto(
                    string.IsNullOrWhiteSpace(generated.Answer)
                        ? GetNoAnswerMessage(request.Query)
                        : generated.Answer.Trim(),
                    false,
                    Array.Empty<RagCitationDto>(),
                    retrieved.Count));
        }

        return ServiceResult<RagAnswerResponseDto>.Ok(
            new RagAnswerResponseDto(generated.Answer.Trim(), true, citations, retrieved.Count));
    }

    private static string GetNoAnswerMessage(string query) =>
        query.Any(character => "ăâđêôơưĂÂĐÊÔƠƯ".Contains(character))
            ? VietnameseNoAnswerMessage
            : EnglishNoAnswerMessage;

    private IReadOnlyList<AnswerContext> BuildContext(
        IReadOnlyList<SemanticSearchResultDto> results)
    {
        var context = new List<AnswerContext>(_options.MaxContextChunks);
        var characterCount = 0;

        foreach (var result in results
                     .Where(result => result.Score >= _options.MinimumRelevantScore)
                     .Take(_options.MaxContextChunks))
        {
            var content = result.Content.Trim();
            if (content.Length == 0)
                continue;

            var remaining = _options.MaxContextCharacters - characterCount;
            if (remaining <= 0)
                break;
            if (content.Length > remaining)
                content = content[..remaining];

            context.Add(new AnswerContext(
                result.ChildChunkId,
                result.DocumentId,
                result.Score,
                result.SectionPath,
                content,
                result.PageFrom,
                result.PageTo,
                result.ComponentIds));
            characterCount += content.Length;
        }

        return context;
    }

    private static IReadOnlyList<RagCitationDto> MapCitations(
        IReadOnlyList<string> citationChunkIds,
        IReadOnlyList<AnswerContext> context)
    {
        var byId = context.ToDictionary(item => item.ChildChunkId, StringComparer.Ordinal);
        return citationChunkIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .Select(id => byId.TryGetValue(id, out var item) ? item : null)
            .Where(item => item is not null)
            .Select(item => new RagCitationDto(
                item!.ChildChunkId,
                item.DocumentId,
                item.PageFrom,
                item.PageTo,
                item.SectionPath,
                item.ComponentIds,
                item.Score))
            .ToArray();
    }
}
