using System.Security.Claims;
using FileExplorer.Api.Models.Dtos;
using FileExplorer.Api.Models.Entities;
using FileExplorer.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FileExplorer.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/operations")]
public class OperationsController(IFileOperationsService operationsService) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Create(CreateOperationRequest request, CancellationToken ct)
    {
        if (request.Type == FileOperationType.PurgeTrash)
        {
            return BadRequest(new { message = "Use the /api/trash endpoints to purge items." });
        }

        try
        {
            var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var jobId = await operationsService.CreateJobAsync(request.Type, request.Sources, request.Destination, userId, ct);
            return Accepted(new { jobId });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { message = ex.Message });
        }
        catch (InsufficientSpaceException ex)
        {
            return Conflict(new { code = "insufficient_space", message = ex.Message, requiredBytes = ex.RequiredBytes, availableBytes = ex.AvailableBytes });
        }
    }
}
