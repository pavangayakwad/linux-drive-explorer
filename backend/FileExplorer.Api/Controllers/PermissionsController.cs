using System.Text.RegularExpressions;
using FileExplorer.Api.Models.Dtos;
using FileExplorer.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FileExplorer.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/permissions")]
public partial class PermissionsController(IPermissionsService permissionsService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PermissionsDto>> Get([FromQuery] string path, CancellationToken ct)
    {
        try
        {
            return Ok(await permissionsService.GetAsync(path, ct));
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { message = ex.Message });
        }
        catch (IOException ex)
        {
            return NotFound(new { message = ex.Message });
        }
    }

    [HttpPut]
    public async Task<IActionResult> Update(UpdatePermissionsRequest request, CancellationToken ct)
    {
        if (request.OctalMode is not null && !OctalModeRegex().IsMatch(request.OctalMode))
        {
            return BadRequest(new { message = "Mode must be an octal number like 755." });
        }

        try
        {
            await permissionsService.UpdateAsync(request.Path, request.OctalMode, request.Owner, request.Group, ct);
            return NoContent();
        }
        catch (PlatformNotSupportedException ex)
        {
            return StatusCode(StatusCodes.Status501NotImplemented, new { message = ex.Message });
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { message = ex.Message });
        }
        catch (IOException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpGet("principals")]
    public async Task<ActionResult<PrincipalsResponse>> Principals(CancellationToken ct) =>
        Ok(await permissionsService.ListPrincipalsAsync(ct));

    [GeneratedRegex("^[0-7]{3,4}$")]
    private static partial Regex OctalModeRegex();
}
