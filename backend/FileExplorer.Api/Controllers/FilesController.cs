using FileExplorer.Api.Models.Dtos;
using FileExplorer.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;

namespace FileExplorer.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/files")]
public class FilesController(IFileSystemService fileSystemService, IPathResolver pathResolver, IDirectorySizeService directorySizeService) : ControllerBase
{
    private static readonly FileExtensionContentTypeProvider ContentTypeProvider = new();

    private static readonly HashSet<string> PreviewableTextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".log", ".json", ".xml", ".csv", ".yml", ".yaml", ".ts", ".js",
        ".css", ".html", ".sh", ".cs", ".cshtml", ".sql", ".ini", ".conf", ".gitignore",
    };

    [HttpGet("list")]
    public ActionResult<DirectoryListingDto> List([FromQuery] string path = "/")
    {
        try
        {
            return Ok(fileSystemService.ListDirectory(path));
        }
        catch (DirectoryNotFoundException)
        {
            return NotFound(new { message = $"Directory '{path}' was not found." });
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { message = ex.Message });
        }
    }

    [HttpPost("entries")]
    public ActionResult<FileEntryDto> CreateEntry(CreateEntryRequest request)
    {
        try
        {
            return Ok(fileSystemService.CreateEntry(request.ParentPath, request.Name, request.IsDirectory));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (DirectoryNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (IOException ex)
        {
            return Conflict(new { message = ex.Message });
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { message = ex.Message });
        }
    }

    [HttpPut("rename")]
    public ActionResult<FileEntryDto> Rename(RenameRequest request)
    {
        try
        {
            return Ok(fileSystemService.Rename(request.Path, request.NewName));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (FileNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (IOException ex)
        {
            return Conflict(new { message = ex.Message });
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { message = ex.Message });
        }
    }

    [HttpPost("size")]
    public ActionResult<DirectorySizeJobDto> StartSizeCalculation([FromQuery] string path)
    {
        try
        {
            return Ok(directorySizeService.Start(path));
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { message = ex.Message });
        }
    }

    [HttpGet("size/{jobId:guid}")]
    public ActionResult<DirectorySizeJobDto> GetSizeCalculation(Guid jobId)
    {
        var job = directorySizeService.Get(jobId);
        return job is null ? NotFound() : Ok(job);
    }

    [HttpPost("size/{jobId:guid}/cancel")]
    public IActionResult CancelSizeCalculation(Guid jobId) =>
        directorySizeService.Cancel(jobId) ? NoContent() : NotFound();

    [HttpGet("preview")]
    public IActionResult Preview([FromQuery] string path)
    {
        string physical;
        try
        {
            physical = pathResolver.ToPhysicalPath(path);
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { message = ex.Message });
        }

        if (!System.IO.File.Exists(physical))
        {
            return NotFound(new { message = $"'{path}' was not found." });
        }

        var extension = Path.GetExtension(physical);
        var contentType = ResolvePreviewContentType(physical, extension);
        if (contentType is null)
        {
            return StatusCode(StatusCodes.Status415UnsupportedMediaType, new { message = "Preview is not supported for this file type." });
        }

        var stream = System.IO.File.OpenRead(physical);
        return File(stream, contentType, enableRangeProcessing: true);
    }

    [HttpGet("download")]
    public IActionResult Download([FromQuery] string path)
    {
        string physical;
        try
        {
            physical = pathResolver.ToPhysicalPath(path);
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { message = ex.Message });
        }

        if (!System.IO.File.Exists(physical))
        {
            return NotFound(new { message = $"'{path}' was not found." });
        }

        if (!ContentTypeProvider.TryGetContentType(physical, out var contentType))
        {
            contentType = "application/octet-stream";
        }

        var stream = System.IO.File.OpenRead(physical);
        return File(stream, contentType, Path.GetFileName(physical), enableRangeProcessing: true);
    }

    private static string? ResolvePreviewContentType(string physicalPath, string extension)
    {
        if (PreviewableTextExtensions.Contains(extension))
        {
            return "text/plain; charset=utf-8";
        }

        if (extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            return "application/pdf";
        }

        if (ContentTypeProvider.TryGetContentType(physicalPath, out var contentType) &&
            (contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ||
             contentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) ||
             contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)))
        {
            return contentType;
        }

        return null;
    }
}
