using System.Security.Claims;
using CorpMindAI.Application.Usecase.Search.Query;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CorpMindAI.Api.Controllers.Search;

[Route("api/departments/{department_id:int}/search")]
[ApiController]
[Authorize(Policy = "CanContributeKnowledge")]
public sealed class SemanticSearchController : ControllerBase
{
    private readonly IMediator _mediator;

    public SemanticSearchController(IMediator mediator) => _mediator = mediator;

    [HttpPost("semantic")]
    public async Task<IActionResult> Search(
        [FromRoute] int department_id,
        [FromBody] SemanticSearchRequest request,
        CancellationToken cancellationToken)
    {
        var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User.FindFirstValue("sub");
        if (!int.TryParse(userIdClaim, out var userId))
            return Unauthorized(new { message = "Không thể xác định danh tính người dùng từ token." });

        var result = await _mediator.Send(
            new SemanticSearchQuery(userId, department_id, request.Query, request.Limit),
            cancellationToken);
        if (!result.Success)
        {
            if (result.Message.Contains("permission", StringComparison.OrdinalIgnoreCase))
                return Forbid();
            return BadRequest(new { message = result.Message });
        }

        return Ok(new
        {
            message = "Semantic search completed.",
            data = result.Data
        });
    }

    [HttpPost("answer")]
    public async Task<IActionResult> Answer(
        [FromRoute] int department_id,
        [FromBody] RagAnswerRequest request,
        CancellationToken cancellationToken)
    {
        var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User.FindFirstValue("sub");
        if (!int.TryParse(userIdClaim, out var userId))
            return Unauthorized(new { message = "KhÃ´ng thá»ƒ xÃ¡c Ä‘á»‹nh danh tÃ­nh ngÆ°á»i dÃ¹ng tá»« token." });

        var result = await _mediator.Send(
            new RagAnswerQuery(userId, department_id, request.Query, request.Limit),
            cancellationToken);
        if (!result.Success)
        {
            if (result.Message.Contains("permission", StringComparison.OrdinalIgnoreCase))
                return Forbid();
            return BadRequest(new { message = result.Message });
        }

        return Ok(new
        {
            message = "RAG answer generated.",
            data = result.Data
        });
    }
}

public sealed record SemanticSearchRequest(string Query, int Limit = 10);

public sealed record RagAnswerRequest(string Query, int Limit = 10);
