using CorpMindAI.Application.DTOs.Common;
using CorpMindAI.Application.Usecase.Document.Command;
using CorpMindAI.Application.Usecase.Document.Query;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using CorpMindAI.Application.Settings;
using System.Security.Claims;

namespace CorpMindAI.Api.Controllers.Document
{
    [Route("api/departments/{department_id:int}/documents")]
    [ApiController]
    [Authorize]
    public class DocumentController : ControllerBase
    {
        private readonly IMediator _mediator;
        private readonly UploadSettings _uploadSettings;

        public DocumentController(
            IMediator mediator,
            IOptions<UploadSettings> uploadSettings)
        {
            _mediator = mediator;
            _uploadSettings = uploadSettings.Value;
        }

        // Authorization: Chỉ knowledge_contributor, knowledge_manager của phòng ban có department_id tương ứng mới được phép.
        [HttpPost("upload")]
        [Authorize(Policy = "CanContributeAndManagerKnowledge")]
        [RequestSizeLimit(209_715_200)]
        [Consumes("multipart/form-data")]
        public async Task<IActionResult> UploadDocuments(
            [FromRoute] int department_id,
            [FromForm] IFormFileCollection files,
            CancellationToken cancellationToken)
        {
            if (files == null || files.Count == 0)
                return BadRequest(new { message = "Không có file nào được gửi lên." });

            if (files.Count > _uploadSettings.MaxFilesPerRequest)
                return BadRequest(new
                {
                    message = $"Chỉ được phép upload tối đa {_uploadSettings.MaxFilesPerRequest} file mỗi lần."
                });

            var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier)
                              ?? User.FindFirstValue("sub");

            if (userIdClaim is null || !int.TryParse(userIdClaim, out int currentUserId))
                return Unauthorized(new { message = "Không thể xác định danh tính người dùng từ token." });
            var command = new UploadDocumentCommand(
                Files: files,
                DepartmentId: department_id,
                UploadedByUserId: currentUserId);

            var result = await _mediator.Send(command, cancellationToken);
            // - Tất cả thất bại → 400 Bad Request
            // - Một phần thành công → 207 Multi-Status
            // - Tất cả thành công → 200 OK
            if (result.SuccessCount == 0)
                return BadRequest(result);

            if (result.FailedCount > 0)
                return StatusCode(207, result);

            return Ok(result);
        }

        
        // Trả về 202 Accepted với thông tin job đã được enqueue.
        [HttpPost("{document_id:int}/ocr")]
        [Authorize(Policy = "CanContributeAndManagerKnowledge")]
        public async Task<IActionResult> ProcessOcr(
            [FromRoute] int department_id,
            [FromRoute] int document_id,
            CancellationToken cancellationToken)
        {
            var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier)
                              ?? User.FindFirstValue("sub");

            if (userIdClaim is null || !int.TryParse(userIdClaim, out int currentUserId))
                return Unauthorized(new { message = "Không thể xác định danh tính người dùng từ token." });

            // Gửi ProcessOcrCommand — handler sẽ validate + enqueue Hangfire job
            var command = new ProcessOcrCommand(document_id, currentUserId, department_id);
            var result = await _mediator.Send(command, cancellationToken);

            if (!result.Success)
            {
                if (result.Message.Contains("permission", StringComparison.OrdinalIgnoreCase) ||
                    result.Message.Contains("department", StringComparison.OrdinalIgnoreCase))
                    return Forbid();

                return BadRequest(new { message = result.Message });
            }

            return Accepted(new
            {
                message = result.Message,
                data = result.Data,
            });
        }

        // Kiểm tra trạng thái OCR hiện tại của document.
        [HttpGet("{document_id:int}/ocr-status")]
        [Authorize(Policy = "CanContributeAndManagerKnowledge")]
        public async Task<IActionResult> GetOcrStatus(
            [FromRoute] int department_id,
            [FromRoute] int document_id,
            CancellationToken cancellationToken)
        {
            var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier)
                              ?? User.FindFirstValue("sub");

            if (userIdClaim is null || !int.TryParse(userIdClaim, out int currentUserId))
                return Unauthorized(new { message = "Không thể xác định danh tính người dùng từ token." });

            var query = new GetOcrStatusQuery(document_id, currentUserId, department_id);
            var result = await _mediator.Send(query, cancellationToken);

            if (!result.Success)
                return NotFound(new { message = result.Message });

            return Ok(new
            {
                message = "Lấy trạng thái OCR thành công.",
                data = result.Data,
            });
        }
    }
}
