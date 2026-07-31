using FileExplorer.Api.Data;
using FileExplorer.Api.Models.Dtos;
using FileExplorer.Api.Models.Entities;
using FileExplorer.Api.Options;
using FileExplorer.Api.Services;
using FileExplorer.Api.Services.Jobs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FileExplorer.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/tasks")]
public class TasksController(
    AppDbContext db,
    JobCancellationRegistry cancellationRegistry,
    IPathResolver pathResolver,
    IOptions<FileSystemOptions> fsOptions) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<JobDto>>> List(CancellationToken ct)
    {
        // SQLite/EF Core can't translate ORDER BY over DateTimeOffset columns, so sort client-side.
        var jobs = await db.FileOperationJobs.ToListAsync(ct);
        var ordered = jobs.OrderByDescending(j => j.CreatedAt).Take(200).Select(JobDto.FromEntity).ToList();

        return Ok(ordered);
    }

    [HttpPost("{id:guid}/cancel")]
    public IActionResult Cancel(Guid id)
    {
        return cancellationRegistry.TryCancel(id)
            ? NoContent()
            : NotFound(new { message = "That job isn't currently running." });
    }

    [HttpDelete("finished")]
    public async Task<IActionResult> ClearFinished(CancellationToken ct)
    {
        var finishedStatuses = new[] { FileOperationStatus.Completed, FileOperationStatus.Cancelled, FileOperationStatus.Failed };
        var finished = await db.FileOperationJobs.Where(j => finishedStatuses.Contains(j.Status)).ToListAsync(ct);

        foreach (var job in finished.Where(j => j.Type == FileOperationType.Zip))
        {
            TryDeleteFile(ZipStaging.GetZipPhysicalPath(pathResolver, fsOptions.Value, job.Id));
        }

        db.FileOperationJobs.RemoveRange(finished);
        await db.SaveChangesAsync(ct);

        return NoContent();
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
}
