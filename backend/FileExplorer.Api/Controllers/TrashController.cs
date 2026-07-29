using System.Security.Claims;
using FileExplorer.Api.Data;
using FileExplorer.Api.Models.Dtos;
using FileExplorer.Api.Models.Entities;
using FileExplorer.Api.Services;
using FileExplorer.Api.Services.Jobs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FileExplorer.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/trash")]
public class TrashController(AppDbContext db, ITrashService trashService, IJobQueue queue) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<TrashItemDto>>> List(CancellationToken ct)
    {
        // SQLite/EF Core can't translate ORDER BY over DateTimeOffset columns, so sort client-side.
        var items = await db.TrashItems.ToListAsync(ct);
        var ordered = items.OrderByDescending(t => t.DeletedAt).Select(TrashItemDto.FromEntity).ToList();
        return Ok(ordered);
    }

    [HttpPost("{id:guid}/restore")]
    public async Task<IActionResult> Restore(Guid id, CancellationToken ct)
    {
        var item = await db.TrashItems.FindAsync([id], ct);
        if (item is null)
        {
            return NotFound();
        }

        try
        {
            trashService.Restore(item);
        }
        catch (FileNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (IOException ex)
        {
            return Conflict(new { message = ex.Message });
        }

        db.TrashItems.Remove(item);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpPost("check-space")]
    public ActionResult<object> CheckSpace(CheckTrashSpaceRequest request)
    {
        var hasSpace = trashService.HasSpaceForAll(request.Sources);
        return Ok(new { hasSpace });
    }

    [HttpPost("purge")]
    public async Task<IActionResult> Purge(PurgeTrashRequest request, CancellationToken ct)
    {
        var ids = request.Ids is { Count: > 0 }
            ? request.Ids
            : await db.TrashItems.Select(t => t.Id).ToListAsync(ct);

        if (ids.Count == 0)
        {
            return BadRequest(new { message = "There is nothing in the trash to purge." });
        }

        var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var job = new FileOperationJob { Type = FileOperationType.PurgeTrash, CreatedByUserId = userId };
        job.SetSourcePaths(ids.Select(id => id.ToString()).ToList());

        db.FileOperationJobs.Add(job);
        await db.SaveChangesAsync(ct);
        await queue.EnqueueAsync(job.Id, ct);

        return Accepted(new { jobId = job.Id });
    }
}
