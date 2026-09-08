using System;
using System.Text.Json;
using CorpMindAI.Application.DTOs.Common;
using CorpMindAI.Application.DTOs.Document;
using CorpMindAI.Application.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;

namespace CorpMindAI.Application.Usecase.Document.Query
{
    public record GetOcrStatusQuery(int DocumentId, int UserId, int DepartmentId = 0)
        : IRequest<ServiceResult<OcrStatusResponseDto>>;

    public class GetOcrStatusQueryHandler
        : IRequestHandler<GetOcrStatusQuery, ServiceResult<OcrStatusResponseDto>>
    {
        private readonly IDocumentRepository _documentRepo;
        private readonly IUserRepository _userRepo;
        private readonly ILogger<GetOcrStatusQueryHandler> _logger;

        public GetOcrStatusQueryHandler(
            IDocumentRepository documentRepo,
            IUserRepository userRepo,
            ILogger<GetOcrStatusQueryHandler> logger)
        {
            _documentRepo = documentRepo;
            _userRepo = userRepo;
            _logger = logger;
        }

        public GetOcrStatusQueryHandler(
            IDocumentRepository documentRepo,
            ILogger<GetOcrStatusQueryHandler> logger)
            : this(documentRepo, null!, logger)
        {
        }

        public async Task<ServiceResult<OcrStatusResponseDto>> Handle(
            GetOcrStatusQuery query,
            CancellationToken cancellationToken)
        {
            _logger.LogDebug(
                "Get OCR status for document {DocumentId} by user {UserId}",
                query.DocumentId,
                query.UserId);

            var document = await _documentRepo.GetByIdWithOcrResultAsync(
                query.DocumentId,
                cancellationToken);

            if (document is null)
                return ServiceResult<OcrStatusResponseDto>.Fail(
                    $"Document {query.DocumentId} was not found.");

            if (query.DepartmentId > 0 && document.DepartmentId != query.DepartmentId)
                return ServiceResult<OcrStatusResponseDto>.Fail(
                    "Document does not belong to the requested department.");

            if (query.DepartmentId == 0 && _userRepo is null)
            {
                if (document.UploadedById != query.UserId)
                    return ServiceResult<OcrStatusResponseDto>.Fail(
                        "You do not have permission to view this document's OCR status.");
            }
            else
            {
                var requestor = await _userRepo.GetUserById(query.UserId);
                var canReadDepartmentDocument = requestor is not null &&
                    string.Equals(requestor.Status, "active", StringComparison.OrdinalIgnoreCase) &&
                    requestor.UserRoles.Any(ur =>
                        ur.DepartmentId == document.DepartmentId &&
                        (ur.Role.RoleName == "knowledge_contributor" ||
                         ur.Role.RoleName == "knowledge_manager"));

                if (!canReadDepartmentDocument)
                    return ServiceResult<OcrStatusResponseDto>.Fail(
                        "You do not have permission to view this document's OCR status.");
            }

            var response = new OcrStatusResponseDto
            {
                DocumentId = document.Id,
                OcrStatus = document.OcrStatus,
            };

            if (document.OcrResult is not null)
            {
                response.ErrorMessage = document.OcrResult.ErrorMessage;

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
