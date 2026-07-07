using CorpMindAI.Application.Usecase.GetUser.Query;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace CorpMindAI.Api.Controllers.Auth
{
    [Route("api/[controller]")]
    [ApiController]
    public class UserController : ControllerBase
    {
        private readonly IMediator _mediator;

        public UserController(IMediator mediator)
        {
            _mediator = mediator;
        }

        [HttpGet("{department_id}")]
        [Authorize(Policy = "CanContributeKnowledge")]
        public async Task<IActionResult> GetUserById(int department_id, [FromQuery] int id)
        {
            var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value 
                              ?? User.FindFirst("sub")?.Value;

            if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int requestorUserId))
            {
                return Unauthorized(new { message = "Invalid requestor credentials." });
            }

            var result = await _mediator.Send(new GetUserByIdQuery(id, requestorUserId));

            if (!result.Success)
            {
                if (result.Message.Contains("Access denied"))
                {
                    return StatusCode(StatusCodes.Status403Forbidden, result);
                }
                return NotFound(result);
            }

            return Ok(result);
        }
    }
}
