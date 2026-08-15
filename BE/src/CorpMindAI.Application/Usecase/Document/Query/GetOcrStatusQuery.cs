using System.Text.Json;
using CorpMindAI.Application.DTOs.Common;
using CorpMindAI.Application.DTOs.Document;
using CorpMindAI.Application.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;

namespace CorpMindAI.Application.Usecase.Document.Query
{
    // Query kiểm tra trạng thái OCR hiện tại của document.
    // Client gọi endpoint này để polling sau khi OCR job đã được enqueue.
    public record GetOcrStatusQuery(int DocumentId, int UserId)
        : IRequest<ServiceResult<OcrStatusResponseDto>>;

    public class GetOcrStatusQueryHandler
        : IRequestHandler<GetOcrStatusQuery, ServiceResult<OcrStatusResponseDto>>
    {
        private readonly IDocumentRepository _documentRepo;
        private readonly ILogger<GetOcrStatusQueryHandler> _logger;

        public GetOcrStatusQueryHandler(
            IDocumentRepository documentRepo,
            ILogger<GetOcrStatusQueryHandler> logger)
        {
            _documentRepo = documentRepo;
            _logger = logger;
        }

        public async Task<ServiceResult<OcrStatusResponseDto>> Handle(
            GetOcrStatusQuery query,
            CancellationToken cancellationToken)
        {
            _logger.LogDebug(
                "Kiểm tra trạng thái OCR cho document {DocumentId} bởi user {UserId}",
                query.DocumentId,
                query.UserId);

            // Lấy document kèm thông tin OCR (nếu có)
            var document = await _documentRepo.GetByIdWithOcrResultAsync(
                query.DocumentId, cancellationToken);

            if (document is null)
                return ServiceResult<OcrStatusResponseDto>.Fail(
                    $"Không tìm thấy document với id {query.DocumentId}.");

            // Kiểm tra quyền — chỉ người upload mới được xem trạng thái OCR
            if (document.UploadedById != query.UserId)
                return ServiceResult<OcrStatusResponseDto>.Fail(
                    "Bạn không có quyền xem trạng thái OCR của document này.");

            var response = new OcrStatusResponseDto
            {
                DocumentId = document.Id,
                OcrStatus = document.OcrStatus,
            };

            // Nếu đã có kết quả OCR — populate Result và ErrorMessage
            if (document.OcrResult is not null)
            {
                response.ErrorMessage = document.OcrResult.ErrorMessage;

                // Chỉ trả về kết quả chi tiết khi OCR hoàn thành
                if (document.OcrStatus == "completed")
                {
                    var components = OcrContractDeserializer.DeserializeComponents(
                        document.OcrResult.ComponentsJson);
                    var validationErrors = OcrContractDeserializer.DeserializeValidationErrors(
                        document.OcrResult.ValidationErrorsJson);

                    response.Result = new OcrResponseDto
                    {
                        DocumentId = document.Id,
                        Status = document.OcrResult.Status,
                        TotalPages = document.OcrResult.TotalPages,
                        PageAverageConfidence = document.OcrResult.PageAverageConfidence,
                        OverallLevel = document.OcrResult.OverallLevel,
                        Components = components,
                        ValidationErrors = validationErrors,
                        StructuredDocument = string.IsNullOrWhiteSpace(document.OcrResult.StructuredDocumentJson)
                            ? null
                            : JsonSerializer.Deserialize<StructuredDocumentDto>(
                                document.OcrResult.StructuredDocumentJson),
                        ReconstructedStorageKey = document.OcrResult.ReconstructedStorageKey,
                        ReconstructionStatus = document.OcrResult.ReconstructionStatus,
                        ReconstructedAt = document.OcrResult.ReconstructedAt,
                    };
                }
            }

            return ServiceResult<OcrStatusResponseDto>.Ok(response);
        }
    }
}
