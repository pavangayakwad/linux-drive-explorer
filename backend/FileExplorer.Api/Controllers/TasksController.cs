using FileExplorer.Api.Data;
using FileExplorer.Api.Models.Dtos;
using FileExplorer.Api.Models.Entities;
using FileExplorer.Api.Services.Jobs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FileExplorer.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/tasks")]
public class TasksController(AppDbContext db, JobCancellationRegistry cancellationRegistry) : ControllerBase
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
        db.FileOperationJobs.RemoveRange(finished);
        await db.SaveChangesAsync(ct);

        return NoContent();
    }
}
