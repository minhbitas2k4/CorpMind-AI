using System;
using CorpMindAI.Application.DTOs.Common;
using CorpMindAI.Application.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;

namespace CorpMindAI.Application.Usecase.Document.Command
{
    public record ProcessOcrCommand(int DocumentId, int UserId, int DepartmentId = 0)
        : IRequest<ServiceResult<OcrEnqueuedResultDto>>;

    public class OcrEnqueuedResultDto
    {
        public string JobId { get; set; } = string.Empty;
        public int DocumentId { get; set; }
        public string OcrStatus { get; set; } = string.Empty;
    }

    public class ProcessOcrCommandHandler
        : IRequestHandler<ProcessOcrCommand, ServiceResult<OcrEnqueuedResultDto>>
    {
        private readonly IDocumentRepository _documentRepo;
        private readonly IUserRepository _userRepo;
        private readonly IJobScheduler _jobScheduler;
        private readonly IUnitOfWork _unitOfWork;
        private readonly ILogger<ProcessOcrCommandHandler> _logger;

        public ProcessOcrCommandHandler(
            IDocumentRepository documentRepo,
            IUserRepository userRepo,
            IJobScheduler jobScheduler,
            IUnitOfWork unitOfWork,
            ILogger<ProcessOcrCommandHandler> logger)
        {
            _documentRepo = documentRepo;
            _userRepo = userRepo;
            _jobScheduler = jobScheduler;
            _unitOfWork = unitOfWork;
            _logger = logger;
        }

        public async Task<ServiceResult<OcrEnqueuedResultDto>> Handle(
            ProcessOcrCommand command,
            CancellationToken cancellationToken)
        {
            _logger.LogInformation(
                "Request OCR for document {DocumentId} by user {UserId}",
                command.DocumentId,
                command.UserId);

            var document = await _documentRepo.GetByIdAsync(command.DocumentId, cancellationToken);
            if (document is null)
                return ServiceResult<OcrEnqueuedResultDto>.Fail(
                    $"Document {command.DocumentId} was not found.");

            if (document.DepartmentId != command.DepartmentId)
                return ServiceResult<OcrEnqueuedResultDto>.Fail(
                    "Document does not belong to the requested department.");

            var requestor = await _userRepo.GetUserById(command.UserId);
            var canProcessDepartmentDocument = requestor is not null &&
                string.Equals(requestor.Status, "active", StringComparison.OrdinalIgnoreCase) &&
                requestor.UserRoles.Any(ur =>
                    ur.DepartmentId == document.DepartmentId &&
                    (ur.Role.RoleName == "knowledge_contributor" ||
                     ur.Role.RoleName == "knowledge_manager"));

            if (!canProcessDepartmentDocument)
                return ServiceResult<OcrEnqueuedResultDto>.Fail(
                    "You do not have permission to process OCR for this document.");

            if (!document.FileType?.StartsWith("application/pdf", StringComparison.OrdinalIgnoreCase) ?? true)
                return ServiceResult<OcrEnqueuedResultDto>.Fail(
                    "OCR is currently supported only for PDF files.");

            if (document.OcrStatus == "processing" || document.OcrStatus == "queued")
                return ServiceResult<OcrEnqueuedResultDto>.Fail(
                    "Document OCR is already being processed.");

            var existingResult = await _documentRepo.GetOcrResultByDocumentIdAsync(
                command.DocumentId,
                cancellationToken);
            if (existingResult is not null && document.OcrStatus == "completed")
                return ServiceResult<OcrEnqueuedResultDto>.Fail(
                    "Document already has a completed OCR result.");

            document.OcrStatus = "queued";
            document.UpdatedAt = DateTime.UtcNow;
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            var jobId = _jobScheduler.EnqueueFireAndForget<IOcrProcessingJob>(
                job => job.Execute(command.DocumentId, command.UserId));

            return ServiceResult<OcrEnqueuedResultDto>.Ok(
                new OcrEnqueuedResultDto
                {
                    JobId = jobId,
                    DocumentId = command.DocumentId,
                    OcrStatus = "queued",
                },
                "OCR job queued successfully.");
        }
    }

    public interface IOcrProcessingJob
    {
        Task Execute(int documentId, int userId);
    }
}
