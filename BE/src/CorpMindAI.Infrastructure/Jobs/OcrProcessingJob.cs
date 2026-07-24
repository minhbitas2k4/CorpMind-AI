using CorpMindAI.Application.Usecase.Document.Command;
using MediatR;
using Microsoft.Extensions.Logging;

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

        public async Task Execute(int documentId, int userId)
        {
            _logger.LogInformation(
                "[Hangfire] Bắt đầu xử lý OCR job cho document {DocumentId}, user {UserId}",
                documentId,
                userId);

            try
            {
                // Gửi ProcessOcrJobCommand qua MediatR
                // MediatR sẽ resolve ProcessOcrJobCommandHandler + tất cả dependencies
                var result = await _mediator.Send(
                    new ProcessOcrJobCommand(documentId, userId));

                if (!result.Success)
                {
                    _logger.LogWarning(
                        "[Hangfire] OCR job thất bại cho document {DocumentId}: {Message}",
                        documentId,
                        result.Message);

                    // Throw exception để Hangfire đánh dấu job là Failed và có thể retry
                    throw new InvalidOperationException(
                        $"OCR job failed for document {documentId}: {result.Message}");
                }

                _logger.LogInformation(
                    "[Hangfire] OCR job hoàn thành thành công cho document {DocumentId}",
                    documentId);
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

                // Wrap exception để Hangfire có thông tin rõ ràng hơn
                throw new InvalidOperationException(
                    $"OCR job error for document {documentId}: {ex.Message}", ex);
            }
        }
    }
}
