using System.Text.Json;
using System.Diagnostics;
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
                    "[Hangfire Job] Source file is unavailable for document {DocumentId}",
                    command.DocumentId);

                await PersistFailureAsync(command.DocumentId, "File does not exist on the server.", cancellationToken);
                await UpdateDocumentStatusAsync(document, "failed", "failed", cancellationToken);
                return ServiceResult<OcrResponseDto>.Fail("File không tồn tại trên server.");
            }

            try
            {
                // Cập nhật trạng thái sang "processing"
                await UpdateDocumentStatusAsync(document, "processing", "processing", cancellationToken);

                _logger.LogInformation(
                    "[Hangfire Job] Gọi OCR service cho document {DocumentId}",
                    command.DocumentId);

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

                    await PersistFailureAsync(
                        command.DocumentId,
                        ocrResult.ErrorMessage ?? "Unknown OCR service failure.",
                        cancellationToken);
                    await UpdateDocumentStatusAsync(document, "failed", "failed", cancellationToken);
                    return ServiceResult<OcrResponseDto>.Fail(
                        $"OCR thất bại: {ocrResult.ErrorMessage}");
                }

                // OCR thành công — lưu kết quả vào DB
                var entity = await _documentRepo.GetOcrResultByDocumentIdAsync(
                    command.DocumentId, cancellationToken);
                if (entity is null)
                {
                    entity = new Domain.Entities.OcrResult
                    {
                        DocumentId = command.DocumentId,
                        CreatedAt = DateTime.UtcNow,
                    };
                    await _documentRepo.AddOcrResultAsync(entity, cancellationToken);
                }
                entity.TotalPages = ocrResult.TotalPages;
                entity.PageAverageConfidence = ocrResult.PageAverageConfidence;
                entity.OverallLevel = ocrResult.OverallLevel;
                entity.ComponentsJson = ocrResult.ComponentsJson;
                entity.ValidationErrorsJson = ocrResult.ValidationErrorsJson;
                entity.SchemaVersion = ocrResult.SchemaVersion;
                entity.StructuredDocumentJson = ocrResult.StructuredDocumentJson;
                entity.ReconstructionStatus = "not_started";
                entity.ReconstructedStorageKey = null;
                entity.ReconstructedAt = null;
                entity.ErrorMessage = null;
                entity.Status = "completed";
                entity.UpdatedAt = DateTime.UtcNow;

                await TransferReconstructionAsync(command.DocumentId, ocrResult, entity, cancellationToken);

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

                await PersistFailureAsync(command.DocumentId, ex.Message, cancellationToken);
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
            var components = OcrContractDeserializer.DeserializeComponents(entity.ComponentsJson);
            var validationErrors = OcrContractDeserializer.DeserializeValidationErrors(
                entity.ValidationErrorsJson);

            return new OcrResponseDto
            {
                DocumentId = documentId,
                Status = entity.Status,
                TotalPages = entity.TotalPages,
                PageAverageConfidence = entity.PageAverageConfidence,
                OverallLevel = entity.OverallLevel,
                Components = components,
                ValidationErrors = validationErrors,
                StructuredDocument = DeserializeStructuredDocument(entity.StructuredDocumentJson),
                ReconstructedStorageKey = entity.ReconstructedStorageKey,
                ReconstructionStatus = entity.ReconstructionStatus,
                ReconstructedAt = entity.ReconstructedAt,
            };
        }

        private async Task PersistFailureAsync(
            int documentId,
            string errorMessage,
            CancellationToken cancellationToken)
        {
            var entity = await _documentRepo.GetOcrResultByDocumentIdAsync(documentId, cancellationToken);
            if (entity is null)
            {
                entity = new Domain.Entities.OcrResult
                {
                    DocumentId = documentId,
                    CreatedAt = DateTime.UtcNow,
                };
                await _documentRepo.AddOcrResultAsync(entity, cancellationToken);
            }
            entity.TotalPages = 0;
            entity.PageAverageConfidence = 0;
            entity.OverallLevel = "failed";
            entity.ComponentsJson = "[]";
            entity.ValidationErrorsJson = "[]";
            entity.SchemaVersion = null;
            entity.StructuredDocumentJson = null;
            entity.ReconstructedStorageKey = null;
            entity.ReconstructionStatus = null;
            entity.ReconstructedAt = null;
            entity.ErrorMessage = errorMessage;
            entity.Status = "failed";
            entity.UpdatedAt = DateTime.UtcNow;
        }

        private async Task TransferReconstructionAsync(
            int documentId,
            OcrServiceResponse ocrResult,
            Domain.Entities.OcrResult entity,
            CancellationToken cancellationToken)
        {
            var artifact = ocrResult.ReconstructionArtifact;
            if (artifact is null || string.IsNullOrWhiteSpace(artifact.ArtifactId))
            {
                entity.ReconstructionStatus = "failed";
                _logger.LogWarning(
                    "OCR completed for document {DocumentId}, but no reconstruction artifact was available.",
                    documentId);
                return;
            }
            if (artifact.DocumentId != documentId.ToString())
            {
                entity.ReconstructionStatus = "failed";
                _logger.LogWarning(
                    "Reconstruction artifact document mismatch for document {DocumentId}.", documentId);
                return;
            }

            var transferRoot = Path.Combine(Path.GetTempPath(), "CorpMindAI", "ocr-artifact-transfer");
            Directory.CreateDirectory(transferRoot);
            CleanupAbandonedTransfers(transferRoot);
            var temporaryPath = Path.Combine(transferRoot, $"{Guid.NewGuid():N}.pdf");
            var stored = false;
            var transferStarted = Stopwatch.StartNew();
            try
            {
                await using (var temporaryOutput = new FileStream(
                    temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None,
                    bufferSize: 81920, useAsync: true))
                {
                    await _ocrService.DownloadReconstructionAsync(
                        documentId, artifact.ArtifactId, temporaryOutput, cancellationToken);
                }

                await using var artifactStream = new FileStream(
                    temporaryPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                    bufferSize: 81920, useAsync: true);
                entity.ReconstructedStorageKey = await _fileStorage.UploadReconstructionAsync(
                    artifactStream, documentId, cancellationToken);
                entity.ReconstructionStatus = "completed";
                entity.ReconstructedAt = DateTime.UtcNow;
                stored = true;
                _logger.LogInformation(
                    "Reconstruction artifact transferred and stored for document {DocumentId} " +
                    "at storage key {StorageKey} in {ElapsedMilliseconds} ms.",
                    documentId, entity.ReconstructedStorageKey, transferStarted.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                entity.ReconstructionStatus = "failed";
                entity.ReconstructedStorageKey = null;
                entity.ReconstructedAt = null;
                _logger.LogError(ex,
                    "OCR succeeded but reconstruction transfer/storage failed for document {DocumentId}.",
                    documentId);
            }
            finally
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Could not clean local reconstruction transfer file for document {DocumentId}.",
                        documentId);
                }
            }

            if (!stored)
                return;

            try
            {
                await _ocrService.CleanupReconstructionAsync(
                    documentId, artifact.ArtifactId, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Reconstruction was durably stored, but Python cleanup failed for document {DocumentId}.",
                    documentId);
            }
        }

        private void CleanupAbandonedTransfers(string transferRoot)
        {
            var resolvedRoot = Path.GetFullPath(transferRoot);
            var threshold = DateTime.UtcNow.AddDays(-1);
            foreach (var path in Directory.EnumerateFiles(resolvedRoot, "*.pdf", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var resolvedPath = Path.GetFullPath(path);
                    if (!resolvedPath.StartsWith(
                            resolvedRoot + Path.DirectorySeparatorChar,
                            StringComparison.OrdinalIgnoreCase) ||
                        File.GetLastWriteTimeUtc(resolvedPath) >= threshold)
                        continue;
                    File.Delete(resolvedPath);
                    _logger.LogInformation("Removed abandoned CorpMindAI OCR transfer file.");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not remove an abandoned CorpMindAI OCR transfer file.");
                }
            }
        }

        private static StructuredDocumentDto? DeserializeStructuredDocument(string? json)
        {
            return string.IsNullOrWhiteSpace(json)
                ? null
                : JsonSerializer.Deserialize<StructuredDocumentDto>(json);
        }
    }
}
