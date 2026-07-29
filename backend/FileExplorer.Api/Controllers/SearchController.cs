using FileExplorer.Api.Models.Dtos;
using FileExplorer.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FileExplorer.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/search")]
public class SearchController(IFileSystemService fileSystemService) : ControllerBase
{
    [HttpGet]
    public ActionResult<IReadOnlyList<FileEntryDto>> Search([FromQuery] string path, [FromQuery] string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return BadRequest(new { message = "A search query is required." });
        }

        try
        {
            return Ok(fileSystemService.Search(path, query));
        }
        catch (DirectoryNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { message = ex.Message });
        }
    }
}
