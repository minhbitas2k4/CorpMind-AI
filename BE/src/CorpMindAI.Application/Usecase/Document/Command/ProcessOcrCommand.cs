using CorpMindAI.Application.DTOs.Common;
using CorpMindAI.Application.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;

namespace CorpMindAI.Application.Usecase.Document.Command
{
    // MediatR Command để yêu cầu OCR một document.
    // Command này chỉ validate + enqueue Hangfire job, KHÔNG chạy OCR đồng bộ.
    // Kết quả OCR sẽ được xử lý bởi OcrProcessingJob trong background worker.
    public record ProcessOcrCommand(int DocumentId, int UserId)
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
        private readonly IJobScheduler _jobScheduler;
        private readonly IUnitOfWork _unitOfWork;
        private readonly ILogger<ProcessOcrCommandHandler> _logger;

        public ProcessOcrCommandHandler(
            IDocumentRepository documentRepo,
            IJobScheduler jobScheduler,
            IUnitOfWork unitOfWork,
            ILogger<ProcessOcrCommandHandler> logger)
        {
            _documentRepo = documentRepo;
            _jobScheduler = jobScheduler;
            _unitOfWork = unitOfWork;
            _logger = logger;
        }

        public async Task<ServiceResult<OcrEnqueuedResultDto>> Handle(
            ProcessOcrCommand command,
            CancellationToken cancellationToken)
        {
            _logger.LogInformation(
                "Yêu cầu OCR cho document {DocumentId} bởi user {UserId}",
                command.DocumentId,
                command.UserId);

            // Validate: document có tồn tại không
            var document = await _documentRepo.GetByIdAsync(command.DocumentId, cancellationToken);
            if (document is null)
                return ServiceResult<OcrEnqueuedResultDto>.Fail(
                    $"Không tìm thấy document với id {command.DocumentId}.");

            // Validate: người dùng có quyền không (chỉ người upload mới được yêu cầu OCR)
            if (document.UploadedById != command.UserId)
                return ServiceResult<OcrEnqueuedResultDto>.Fail(
                    "Bạn không có quyền thực hiện OCR cho document này.");

            // Validate: chỉ hỗ trợ file PDF
            if (!document.FileType?.StartsWith("application/pdf") ?? true)
                return ServiceResult<OcrEnqueuedResultDto>.Fail(
                    "Chỉ hỗ trợ OCR cho file PDF.");

            // Validate: document đang được xử lý thì không enqueue lại
            if (document.OcrStatus == "processing" || document.OcrStatus == "queued")
                return ServiceResult<OcrEnqueuedResultDto>.Fail(
                    "Document đang được xử lý OCR, vui lòng thử lại sau.");

            // Nếu đã có kết quả OCR — trả về kết quả hiện tại (idempotent)
            var existingResult = await _documentRepo.GetOcrResultByDocumentIdAsync(
                command.DocumentId, cancellationToken);
            if (existingResult is not null && document.OcrStatus == "completed")
            {
                _logger.LogInformation(
                    "Document {DocumentId} đã có kết quả OCR, bỏ qua enqueue",
                    command.DocumentId);
                return ServiceResult<OcrEnqueuedResultDto>.Fail(
                    "Document đã có kết quả OCR. Không cần xử lý lại.");
            }

            // Cập nhật trạng thái document sang "queued"
            document.OcrStatus = "queued";
            document.UpdatedAt = DateTime.UtcNow;
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            var jobId = _jobScheduler.EnqueueFireAndForget<IOcrProcessingJob>(
                job => job.Execute(command.DocumentId, command.UserId));

            _logger.LogInformation(
                "OCR job đã được enqueue cho document {DocumentId}, Hangfire JobId: {JobId}",
                command.DocumentId,
                jobId);

            return ServiceResult<OcrEnqueuedResultDto>.Ok(
                new OcrEnqueuedResultDto
                {
                    JobId = jobId,
                    DocumentId = command.DocumentId,
                    OcrStatus = "queued",
                },
                "OCR job đã được xếp hàng xử lý.");
        }
    }


    public interface IOcrProcessingJob
    {
        // Thực thi OCR job trong background worker.
        // Hangfire sẽ serialize method call này.
        Task Execute(int documentId, int userId);
    }
}
