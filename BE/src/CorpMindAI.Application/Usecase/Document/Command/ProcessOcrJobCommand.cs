using System.Text.Json;
using CorpMindAI.Application.DTOs.Common;
using CorpMindAI.Application.DTOs.Document;
using CorpMindAI.Application.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;

namespace CorpMindAI.Application.Usecase.Document.Command
{
    // MediatR Command xử lý OCR thực sự — được gọi bởi Hangfire background job.
    public record ProcessOcrJobCommand(int DocumentId, int UserId)
        : IRequest<ServiceResult<OcrResponseDto>>;

    public class ProcessOcrJobCommandHandler
        : IRequestHandler<ProcessOcrJobCommand, ServiceResult<OcrResponseDto>>
    {
        private readonly IDocumentRepository _documentRepo;
        private readonly IFileStorage _fileStorage;
        private readonly IOcrService _ocrService;
        private readonly IUnitOfWork _unitOfWork;
        private readonly ILogger<ProcessOcrJobCommandHandler> _logger;

        public ProcessOcrJobCommandHandler(
            IDocumentRepository documentRepo,
            IFileStorage fileStorage,
            IOcrService ocrService,
            IUnitOfWork unitOfWork,
            ILogger<ProcessOcrJobCommandHandler> logger)
        {
            _documentRepo = documentRepo;
            _fileStorage = fileStorage;
            _ocrService = ocrService;
            _unitOfWork = unitOfWork;
            _logger = logger;
        }

        public async Task<ServiceResult<OcrResponseDto>> Handle(
            ProcessOcrJobCommand command,
            CancellationToken cancellationToken)
        {
            _logger.LogInformation(
                "[Hangfire Job] Bắt đầu OCR cho document {DocumentId} bởi user {UserId}",
                command.DocumentId,
                command.UserId);

            // Lấy document từ DB (AsNoTracking trong repo, nên cần attach lại để cập nhật)
            var document = await _documentRepo.GetByIdAsync(command.DocumentId, cancellationToken);
            if (document is null)
            {
                _logger.LogWarning(
                    "[Hangfire Job] Document {DocumentId} không tồn tại — bỏ qua job",
                    command.DocumentId);
                return ServiceResult<OcrResponseDto>.Fail(
                    $"Không tìm thấy document với id {command.DocumentId}.");
            }

            // Kiểm tra file type — chỉ hỗ trợ PDF
            if (!document.FileType?.StartsWith("application/pdf") ?? true)
            {
                _logger.LogWarning(
                    "[Hangfire Job] Document {DocumentId} không phải PDF — bỏ qua",
                    command.DocumentId);
                return ServiceResult<OcrResponseDto>.Fail("Chỉ hỗ trợ OCR cho file PDF.");
            }

            // Kiểm tra file có tồn tại trên disk không
            var physicalPath = _fileStorage.GetPhysicalPath(document.StorageKey);
            if (!File.Exists(physicalPath))
            {
                _logger.LogError(
                    "[Hangfire Job] File không tồn tại: {PhysicalPath} cho document {DocumentId}",
                    physicalPath,
                    command.DocumentId);

                await UpdateDocumentStatusAsync(document, "failed", "failed", cancellationToken);
                return ServiceResult<OcrResponseDto>.Fail("File không tồn tại trên server.");
            }

            try
            {
                // Cập nhật trạng thái sang "processing"
                await UpdateDocumentStatusAsync(document, "processing", "processing", cancellationToken);

                _logger.LogInformation(
                    "[Hangfire Job] Gọi OCR service cho document {DocumentId}, file: {Path}",
                    command.DocumentId,
                    physicalPath);

                // Gọi Python OCR service qua HTTP
                var ocrResult = await _ocrService.ProcessOcrAsync(
                    command.DocumentId,
                    physicalPath,
                    cancellationToken);

                // OCR thất bại — cập nhật trạng thái failed
                if (!ocrResult.Success)
                {
                    _logger.LogWarning(
                        "[Hangfire Job] OCR thất bại cho document {DocumentId}: {Error}",
                        command.DocumentId,
                        ocrResult.ErrorMessage);

                    await UpdateDocumentStatusAsync(document, "failed", "failed", cancellationToken);
                    return ServiceResult<OcrResponseDto>.Fail(
                        $"OCR thất bại: {ocrResult.ErrorMessage}");
                }

                // OCR thành công — lưu kết quả vào DB
                var entity = new Domain.Entities.OcrResult
                {
                    DocumentId = command.DocumentId,
                    TotalPages = ocrResult.TotalPages,
                    PageAverageConfidence = ocrResult.PageAverageConfidence,
                    OverallLevel = ocrResult.OverallLevel,
                    ComponentsJson = ocrResult.ComponentsJson,
                    ValidationErrorsJson = ocrResult.ValidationErrorsJson,
                    Status = "completed",
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                };

                await _documentRepo.AddOcrResultAsync(entity, cancellationToken);

                // Cập nhật document status sang completed
                await UpdateDocumentStatusAsync(document, "completed", "completed", cancellationToken);

                _logger.LogInformation(
                    "[Hangfire Job] OCR hoàn thành cho document {DocumentId}: {Pages} trang, confidence={Confidence:F4}",
                    command.DocumentId,
                    ocrResult.TotalPages,
                    ocrResult.PageAverageConfidence);

                var responseDto = MapEntityToResponseDto(command.DocumentId, entity);
                return ServiceResult<OcrResponseDto>.Ok(responseDto, "OCR hoàn thành.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "[Hangfire Job] Lỗi khi xử lý OCR cho document {DocumentId}",
                    command.DocumentId);

                await UpdateDocumentStatusAsync(document, "failed", "failed", cancellationToken);
                return ServiceResult<OcrResponseDto>.Fail(
                    $"Lỗi hệ thống khi xử lý OCR: {ex.Message}");
            }
        }

        // Cập nhật trạng thái OCR và status của document.
        private async Task UpdateDocumentStatusAsync(
            Domain.Entities.Document document,
            string ocrStatus,
            string status,
            CancellationToken cancellationToken)
        {
            document.OcrStatus = ocrStatus;
            document.Status = status;
            document.UpdatedAt = DateTime.UtcNow;
            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }

        // Map entity OcrResult sang OcrResponseDto.
        // Deserialize JSON fields (components, validation_errors) sang strongly-typed DTOs.
        private static OcrResponseDto MapEntityToResponseDto(
            int documentId,
            Domain.Entities.OcrResult entity)
        {
            var components = JsonSerializer.Deserialize<List<OcrComponentDto>>(
                entity.ComponentsJson) ?? new();
            var validationErrors = JsonSerializer.Deserialize<List<OcrValidationErrorDto>>(
                entity.ValidationErrorsJson) ?? new();

            return new OcrResponseDto
            {
                DocumentId = documentId,
                Status = entity.Status,
                TotalPages = entity.TotalPages,
                PageAverageConfidence = entity.PageAverageConfidence,
                OverallLevel = entity.OverallLevel,
                Components = components,
                ValidationErrors = validationErrors,
            };
        }
    }
}
