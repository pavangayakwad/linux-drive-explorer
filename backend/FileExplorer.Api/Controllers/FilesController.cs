using FileExplorer.Api.Data;
using FileExplorer.Api.Filters;
using FileExplorer.Api.Models.Dtos;
using FileExplorer.Api.Models.Entities;
using FileExplorer.Api.Options;
using FileExplorer.Api.Services;
using FileExplorer.Api.Services.Jobs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace FileExplorer.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/files")]
public class FilesController(
    IFileSystemService fileSystemService,
    IPathResolver pathResolver,
    IDirectorySizeService directorySizeService,
    AppDbContext db,
    IOptions<FileSystemOptions> fsOptions) : ControllerBase
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

    [HttpGet("download-zip/{jobId:guid}")]
    public async Task<IActionResult> DownloadZip(Guid jobId, CancellationToken ct)
    {
        var job = await db.FileOperationJobs.FindAsync([jobId], ct);
        if (job is null || job.Type != FileOperationType.Zip)
        {
            return NotFound();
        }

        if (job.Status != FileOperationStatus.Completed)
        {
            return Conflict(new { message = "This archive isn't ready yet." });
        }

        var zipPhysicalPath = ZipStaging.GetZipPhysicalPath(pathResolver, fsOptions.Value, jobId);
        if (!System.IO.File.Exists(zipPhysicalPath))
        {
            return NotFound(new { message = "This archive is no longer available - it may have already been downloaded, or the server was restarted before it could be." });
        }

        var sources = job.GetSourcePaths();
        var fileName = sources.Count == 1
            ? $"{Path.GetFileName(sources[0].TrimEnd('/'))}.zip"
            : "Archive.zip";

        var stream = System.IO.File.OpenRead(zipPhysicalPath);
        Response.OnCompleted(() =>
        {
            TryDeleteFile(zipPhysicalPath);
            return Task.CompletedTask;
        });

        return File(stream, "application/zip", fileName);
    }

    /// <summary>
    /// Streams an uploaded file straight from the multipart request body to its destination on disk - deliberately
    /// bypassing [FromForm]/IFormFile model binding, which would otherwise buffer the whole file to a temp location
    /// before this action ever saw it. Expects 'destinationPath' and 'relativePath' form fields ahead of the file
    /// field (the client sends them in that order) plus the file itself under any field name.
    /// </summary>
    [HttpPost("upload")]
    [DisableRequestSizeLimit]
    [DisableFormValueModelBinding]
    public async Task<ActionResult<FileEntryDto>> Upload(CancellationToken ct)
    {
        var contentType = Request.ContentType;
        if (contentType is null || !contentType.Contains("multipart/", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new { message = "Expected a multipart/form-data request." });
        }

        var boundary = HeaderUtilities.RemoveQuotes(MediaTypeHeaderValue.Parse(contentType).Boundary).Value;
        if (string.IsNullOrEmpty(boundary))
        {
            return BadRequest(new { message = "Missing multipart boundary." });
        }

        var reader = new MultipartReader(boundary, Request.Body);
        string? destinationPath = null;
        string? relativePath = null;
        string? writtenPhysicalPath = null;

        try
        {
            MultipartSection? section;
            while ((section = await reader.ReadNextSectionAsync(ct)) is not null)
            {
                if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition))
                {
                    continue;
                }

                if (IsFileSection(disposition))
                {
                    return await SaveUploadedFileAsync(section, disposition, destinationPath, relativePath, path => writtenPhysicalPath = path, ct);
                }

                if (IsFormSection(disposition))
                {
                    var value = await ReadSectionAsStringAsync(section, ct);
                    switch (disposition.Name.Value)
                    {
                        case "destinationPath":
                            destinationPath = value;
                            break;
                        case "relativePath":
                            relativePath = value;
                            break;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            if (writtenPhysicalPath is not null)
            {
                TryDeleteFile(writtenPhysicalPath);
            }
            throw;
        }

        return BadRequest(new { message = "No file was included in the request." });
    }

    private async Task<ActionResult<FileEntryDto>> SaveUploadedFileAsync(
        MultipartSection section,
        ContentDispositionHeaderValue disposition,
        string? destinationPath,
        string? relativePath,
        Action<string> onPhysicalPathKnown,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(destinationPath))
        {
            return BadRequest(new { message = "'destinationPath' must be sent before the file." });
        }

        string parentPhysical;
        try
        {
            parentPhysical = pathResolver.ToPhysicalPath(destinationPath);
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { message = ex.Message });
        }

        if (!Directory.Exists(parentPhysical))
        {
            return NotFound(new { message = $"'{destinationPath}' was not found." });
        }

        string safeRelative;
        try
        {
            var fallbackName = disposition.FileName.Value ?? disposition.FileNameStar.Value ?? "upload";
            safeRelative = SanitizeRelativePath(relativePath, fallbackName);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }

        var isNested = safeRelative.Contains('/');
        var targetPhysical = Path.Combine(parentPhysical, safeRelative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(targetPhysical)!);

        // Nested files (folder uploads) trust that the client already resolved their top-level folder name to
        // something unique before uploading any files into it - renaming individual nested files here would let
        // concurrent requests for the same new folder race to different names. Flat/top-level files get their
        // own conflict check since nothing pre-negotiates those.
        if (!isNested)
        {
            targetPhysical = FileTreeOperations.GetUniqueDestination(parentPhysical, safeRelative);
        }

        onPhysicalPathKnown(targetPhysical);

        await using (var destStream = new FileStream(targetPhysical, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, useAsync: true))
        {
            await section.Body.CopyToAsync(destStream, ct);
        }

        return Ok(fileSystemService.DescribeEntry(pathResolver.ToVirtualPath(targetPhysical)));
    }

    private static bool IsFileSection(ContentDispositionHeaderValue disposition) =>
        disposition.DispositionType.Equals("form-data") &&
        (!StringSegment.IsNullOrEmpty(disposition.FileName) || !StringSegment.IsNullOrEmpty(disposition.FileNameStar));

    private static bool IsFormSection(ContentDispositionHeaderValue disposition) =>
        disposition.DispositionType.Equals("form-data") &&
        StringSegment.IsNullOrEmpty(disposition.FileName) &&
        StringSegment.IsNullOrEmpty(disposition.FileNameStar);

    private static async Task<string> ReadSectionAsStringAsync(MultipartSection section, CancellationToken ct)
    {
        using var reader = new StreamReader(section.Body);
        return await reader.ReadToEndAsync(ct);
    }

    /// <summary>Splits a client-supplied relative path into segments, dropping '.'/'..'/empty segments and any
    /// characters that aren't valid in a file name, so a malicious client can't traverse outside the destination
    /// folder or write to an unintended location.</summary>
    private static string SanitizeRelativePath(string? relativePath, string fallbackFileName)
    {
        var candidate = string.IsNullOrWhiteSpace(relativePath) ? fallbackFileName : relativePath;
        var invalidChars = Path.GetInvalidFileNameChars();
        var segments = candidate
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(segment => segment != "." && segment != "..")
            .Select(segment => new string(segment.Where(c => Array.IndexOf(invalidChars, c) < 0).ToArray()))
            .Where(segment => segment.Length > 0)
            .ToArray();

        if (segments.Length == 0)
        {
            throw new ArgumentException("The uploaded file has no valid name.");
        }

        return string.Join('/', segments);
    }

    private static void TryDeleteFile(string physicalPath)
    {
        try
        {
            if (System.IO.File.Exists(physicalPath))
            {
                System.IO.File.Delete(physicalPath);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup only.
        }
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
