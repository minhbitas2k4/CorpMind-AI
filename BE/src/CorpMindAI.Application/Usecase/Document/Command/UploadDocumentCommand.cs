using CorpMindAI.Application.DTOs.Document;
using MediatR;
using Microsoft.AspNetCore.Http;

namespace CorpMindAI.Application.Usecase.Document.Command
{
    // Files được truyền qua IFormFileCollection để hỗ trợ streaming — không đọc vào RAM.
    public record UploadDocumentCommand(
        IFormFileCollection Files,
        int DepartmentId,
        int UploadedByUserId
    ) : IRequest<UploadDocumentResponseDto>;
}
