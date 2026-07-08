using CorpMindAI.Application.DTOs.Document;
using CorpMindAI.Application.Interfaces;
using CorpMindAI.Application.Settings;
using CorpMindAI.Domain.Entities;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CorpMindAI.Application.Usecase.Document.Command
{
    /// - Chỉ phụ thuộc vào IFileStorage và IUnitOfWork (interface)
    /// - Partial success: mỗi file xử lý độc lập, lỗi 1 file không hủy toàn bộ request.
    /// - Streaming: dùng OpenReadStream() — không đọc toàn bộ file vào RAM.
    /// - Lưu batch: AddRangeAsync + SaveChanges một lần sau khi tất cả file thành công.
    /// - Không hard-code giới hạn — đọc từ UploadSettings.
    public class UploadDocumentCommandHandler : IRequestHandler<UploadDocumentCommand, UploadDocumentResponseDto>
    {
        private readonly IFileStorage _fileStorage;
        private readonly IUnitOfWork _unitOfWork;
        private readonly UploadSettings _uploadSettings;
        private readonly ILogger<UploadDocumentCommandHandler> _logger;

        public UploadDocumentCommandHandler(
            IFileStorage fileStorage,
            IUnitOfWork unitOfWork,
            IOptions<UploadSettings> uploadSettings,
            ILogger<UploadDocumentCommandHandler> logger)
        {
            _fileStorage = fileStorage;
            _unitOfWork = unitOfWork;
            _uploadSettings = uploadSettings.Value;
            _logger = logger;
        }

        public async Task<UploadDocumentResponseDto> Handle(
            UploadDocumentCommand command,
            CancellationToken cancellationToken)
        {
            _logger.LogInformation(
                "Bắt đầu upload {FileCount} file(s) cho department {DepartmentId} bởi user {UserId}",
                command.Files.Count,
                command.DepartmentId,
                command.UploadedByUserId);

            var response = new UploadDocumentResponseDto();
            var documentsToSave = new List<Domain.Entities.Document>();

            foreach (var file in command.Files)
            {
                try
                {
                    var validationError = ValidateFile(file);
                    if (validationError is not null)
                    {
                        _logger.LogWarning(
                            "Validation thất bại cho file '{FileName}': {Reason}",
                            file.FileName,
                            validationError);

                        response.FailedFiles.Add(new FailedFileDto
                        {
                            FileName = file.FileName,
                            Reason = validationError
                        });
                        continue;
                    }

                    // Upload file lên storage (streaming — không đọc vào RAM)
                    string storageKey;
                    await using (var stream = file.OpenReadStream())
                    {
                        storageKey = await _fileStorage.UploadAsync(
                            stream,
                            file.FileName,
                            file.ContentType,
                            cancellationToken);
                    }

                    _logger.LogInformation(
                        "Upload storage thành công: '{OriginalName}' → '{StorageKey}'",
                        file.FileName,
                        storageKey);

                    // Tạo document entity (chưa lưu DB)
                    var document = new Domain.Entities.Document
                    {
                        OriginalFileName = file.FileName,
                        Title = Path.GetFileNameWithoutExtension(file.FileName),
                        StorageKey = storageKey,
                        FileType = file.ContentType,
                        FileSize = file.Length,
                        UploadedById = command.UploadedByUserId,
                        DepartmentId = command.DepartmentId,
                        Status = "uploaded",
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    };

                    documentsToSave.Add(document);
                }
                catch (Exception ex)
                {
                    // Bắt mọi lỗi không mong đợi — không expose chi tiết lên response
                    _logger.LogError(ex,
                        "Lỗi không mong đợi khi upload file '{FileName}': {ErrorMessage}",
                        file.FileName,
                        ex.Message);

                    response.FailedFiles.Add(new FailedFileDto
                    {
                        FileName = file.FileName,
                        Reason = "Lỗi hệ thống trong quá trình xử lý file. Vui lòng thử lại."
                    });
                }
            }

            // Lưu tất cả document thành công vào DB trong một lần SaveChanges
            if (documentsToSave.Count > 0)
            {
                try
                {
                    await _unitOfWork.DocumentRepo.AddRangeAsync(documentsToSave, cancellationToken);
                    await _unitOfWork.SaveChangesAsync(cancellationToken);

                    _logger.LogInformation(
                        "Lưu DB thành công: {Count} document(s) cho department {DepartmentId}",
                        documentsToSave.Count,
                        command.DepartmentId);

                    response.SuccessfulFiles = documentsToSave.Select(d => new DocumentUploadResultDto
                    {
                        Id = d.Id,
                        OriginalFileName = d.OriginalFileName,
                        FileSize = d.FileSize ?? 0,
                        UploadedAt = d.CreatedAt,
                        Status = d.Status
                    }).ToList();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Lỗi khi lưu {Count} document(s) vào DB sau khi đã upload storage.",
                        documentsToSave.Count);

                    // Rollback Storage (Compensating Transaction)
                    foreach (var document in documentsToSave)
                    {
                        try
                        {
                            await _fileStorage.DeleteAsync(
                                document.StorageKey,
                                cancellationToken);

                            _logger.LogInformation(
                                "Đã rollback storage thành công: {StorageKey}",
                                document.StorageKey);
                        }
                        catch (Exception cleanupEx)
                        {
                            // Không throw tiếp, chỉ log
                            _logger.LogError(cleanupEx,
                                "Rollback storage thất bại: {StorageKey}",
                                document.StorageKey);
                        }
                    }

                    // Đưa tất cả vào danh sách thất bại
                    response.FailedFiles.AddRange(documentsToSave.Select(d => new FailedFileDto
                    {
                        FileName = d.OriginalFileName,
                        Reason = "Lỗi khi lưu metadata vào cơ sở dữ liệu. File đã được rollback khỏi storage."
                    }));
                }
            }

            response.SuccessCount = response.SuccessfulFiles.Count;
            response.FailedCount = response.FailedFiles.Count;

            _logger.LogInformation(
                "Hoàn thành upload request: {SuccessCount} thành công, {FailedCount} thất bại",
                response.SuccessCount,
                response.FailedCount);

            return response;
        }

        private string? ValidateFile(IFormFile file)
        {
            if (file.Length == 0)
                return "File rỗng, không thể upload.";

            if (file.Length > _uploadSettings.MaxFileSizeBytes)
            {
                var maxMb = _uploadSettings.MaxFileSizeBytes / (1024 * 1024);
                return $"File vượt quá kích thước tối đa {maxMb}MB.";
            }

            var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
            if (!_uploadSettings.AllowedExtensions.Contains(extension))
                return $"Định dạng '{extension}' không được chấp nhận. Chỉ chấp nhận: {string.Join(", ", _uploadSettings.AllowedExtensions)}.";

            if (!_uploadSettings.AllowedContentTypes.Contains(file.ContentType.ToLowerInvariant()))
                return $"Content-Type '{file.ContentType}' không hợp lệ. Chỉ chấp nhận PDF.";

            return null; 
        }
    }
}
