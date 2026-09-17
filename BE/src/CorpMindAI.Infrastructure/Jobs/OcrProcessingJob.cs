using CorpMindAI.Application.Usecase.Document.Command;
using CorpMindAI.Application.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;
using Hangfire;
using System.Diagnostics;

namespace CorpMindAI.Infrastructure.Jobs
{
    public class OcrProcessingJob : IOcrProcessingJob
    {
        private readonly IMediator _mediator;
        private readonly ILogger<OcrProcessingJob> _logger;

        public OcrProcessingJob(
            IMediator mediator,
            ILogger<OcrProcessingJob> logger)
        {
            _mediator = mediator;
            _logger = logger;
        }

        [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
        public async Task Execute(int documentId, int userId)
        {
            var started = Stopwatch.StartNew();
            _logger.LogInformation(
                "[Hangfire] Bắt đầu xử lý OCR job cho document {DocumentId}, user {UserId}",
                documentId,
                userId);

            try
            {
                var result = await _mediator.Send(
                    new ProcessOcrJobCommand(documentId, userId));

                if (!result.Success)
                {
                    _logger.LogWarning(
                        "[Hangfire] OCR job thất bại cho document {DocumentId}: {Message}",
                        documentId,
                        result.Message);

                    throw new InvalidOperationException(
                        $"OCR job failed for document {documentId}: {result.Message}");
                }

                _logger.LogInformation(
                    "[Hangfire] OCR completion and its durable chunking request were committed for DocumentId {DocumentId}.",
                    documentId);

                _logger.LogInformation(
                    "[Hangfire] OCR job hoàn thành cho document {DocumentId}: " +
                    "pages={PageCount}, duration_ms={DurationMilliseconds}",
                    documentId, result.Data?.TotalPages, started.ElapsedMilliseconds);
            }
            catch (InvalidOperationException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "[Hangfire] Lỗi không mong đợi trong OCR job cho document {DocumentId}",
                    documentId);

                throw new InvalidOperationException(
                    $"OCR job error for document {documentId}: {ex.Message}", ex);
            }
        }
    }
}
