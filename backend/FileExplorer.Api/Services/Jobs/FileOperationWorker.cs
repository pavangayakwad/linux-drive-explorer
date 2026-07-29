using FileExplorer.Api.Data;
using FileExplorer.Api.Hubs;
using FileExplorer.Api.Models.Dtos;
using FileExplorer.Api.Models.Entities;
using FileExplorer.Api.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace FileExplorer.Api.Services.Jobs;

public class FileOperationWorker(
    IServiceScopeFactory scopeFactory,
    IJobQueue queue,
    JobCancellationRegistry cancellationRegistry,
    IHubContext<TasksHub> hub,
    ILogger<FileOperationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RequeueInterruptedJobsAsync(stoppingToken);

        await Parallel.ForEachAsync(
            queue.DequeueAllAsync(stoppingToken),
            new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = stoppingToken },
            async (jobId, ct) => await ProcessJobAsync(jobId, ct));
    }

    private async Task RequeueInterruptedJobsAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var stale = await db.FileOperationJobs
            .Where(j => j.Status == FileOperationStatus.Queued || j.Status == FileOperationStatus.Running)
            .ToListAsync(ct);

        foreach (var job in stale)
        {
            if (job.Status == FileOperationStatus.Running)
            {
                job.Status = FileOperationStatus.Failed;
                job.ErrorMessage = "Interrupted by a server restart.";
                job.CompletedAt = DateTimeOffset.UtcNow;
            }
            else
            {
                await queue.EnqueueAsync(job.Id, ct);
            }
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task ProcessJobAsync(Guid jobId, CancellationToken outerCt)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var pathResolver = scope.ServiceProvider.GetRequiredService<IPathResolver>();
        var trashService = scope.ServiceProvider.GetRequiredService<ITrashService>();

        var job = await db.FileOperationJobs.FindAsync([jobId], outerCt);
        if (job is null)
        {
            return;
        }

        using var cts = cancellationRegistry.Register(jobId, outerCt);
        var ct = cts.Token;

        job.Status = FileOperationStatus.Running;
        job.StartedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(outerCt);
        await BroadcastAsync(job, outerCt);

        try
        {
            switch (job.Type)
            {
                case FileOperationType.Copy:
                    ExecuteCopyOrMove(job, db, pathResolver, move: false, ct);
                    break;
                case FileOperationType.Move:
                    ExecuteCopyOrMove(job, db, pathResolver, move: true, ct);
                    break;
                case FileOperationType.Delete:
                    ExecuteDelete(job, db, trashService, ct);
                    break;
                case FileOperationType.PurgeTrash:
                    ExecutePurge(job, db, trashService, ct);
                    break;
            }

            job.Status = FileOperationStatus.Completed;
        }
        catch (OperationCanceledException)
        {
            job.Status = FileOperationStatus.Cancelled;
        }
        catch (Exception ex)
        {
            job.Status = FileOperationStatus.Failed;
            job.ErrorMessage = ex.Message;
            logger.LogError(ex, "File operation job {JobId} ({Type}) failed", jobId, job.Type);
        }
        finally
        {
            cancellationRegistry.Unregister(jobId);
            job.CurrentItem = null;
            job.CompletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(outerCt);
            await BroadcastAsync(job, outerCt);
        }
    }

    private void ExecuteCopyOrMove(FileOperationJob job, AppDbContext db, IPathResolver pathResolver, bool move, CancellationToken ct)
    {
        var destinationPhysical = pathResolver.ToPhysicalPath(job.DestinationPath!);
        Directory.CreateDirectory(destinationPhysical);

        var plan = new List<(string PhysicalSource, string PhysicalDestination)>();
        long totalBytes = 0;
        var totalItems = 0;

        foreach (var virtualSource in job.GetSourcePaths())
        {
            var physicalSource = pathResolver.ToPhysicalPath(virtualSource);
            var (items, bytes) = FileTreeOperations.Measure(physicalSource);
            totalItems += items;
            totalBytes += bytes;

            var name = Path.GetFileName(physicalSource.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var uniqueDestination = FileTreeOperations.GetUniqueDestination(destinationPhysical, name);
            plan.Add((physicalSource, uniqueDestination));
        }

        job.TotalItems = totalItems;
        job.TotalBytes = totalBytes;
        var reportEvery = Math.Max(1, totalItems / 50);
        var lastReported = 0;
        var lastReportedAt = DateTime.UtcNow;
        var reportInterval = TimeSpan.FromMilliseconds(500);

        // Item-count throttling alone leaves a single large file's progress bar frozen at 0% for its whole
        // copy (ProcessedItems only increments once the file finishes) - a time-based fallback keeps
        // mid-file byte progress flowing to the UI without flooding SignalR on every chunk.
        void Report(bool force)
        {
            var now = DateTime.UtcNow;
            var itemThresholdMet = job.ProcessedItems - lastReported >= reportEvery;
            if (!force && !itemThresholdMet && now - lastReportedAt < reportInterval)
            {
                return;
            }
            lastReported = job.ProcessedItems;
            lastReportedAt = now;
            db.SaveChangesAsync(ct).GetAwaiter().GetResult();
            BroadcastAsync(job, ct).GetAwaiter().GetResult();
        }

        Report(force: true);

        foreach (var (physicalSource, physicalDestination) in plan)
        {
            job.CurrentItem = pathResolver.ToVirtualPath(physicalSource);
            FileTreeOperations.CopyRecursive(
                physicalSource,
                physicalDestination,
                bytes =>
                {
                    job.ProcessedBytes += bytes;
                    Report(force: false);
                },
                () =>
                {
                    job.ProcessedItems++;
                    Report(force: false);
                },
                ct);
        }

        if (move)
        {
            foreach (var (physicalSource, _) in plan)
            {
                FileTreeOperations.DeleteRecursive(physicalSource, () => { }, ct);
            }
        }

        Report(force: true);
    }

    private void ExecuteDelete(FileOperationJob job, AppDbContext db, ITrashService trashService, CancellationToken ct)
    {
        var sources = job.GetSourcePaths();
        job.TotalItems = sources.Count;
        db.SaveChangesAsync(ct).GetAwaiter().GetResult();
        BroadcastAsync(job, ct).GetAwaiter().GetResult();

        foreach (var virtualSource in sources)
        {
            ct.ThrowIfCancellationRequested();
            job.CurrentItem = virtualSource;
            var outcome = trashService.MoveToTrash(virtualSource, job.CreatedByUserId);
            if (outcome.PermanentlyDeleted)
            {
                job.AddPermanentlyDeletedName(outcome.Name);
            }
            else
            {
                db.TrashItems.Add(outcome.Item!);
            }
            job.ProcessedItems++;
            db.SaveChangesAsync(ct).GetAwaiter().GetResult();
            BroadcastAsync(job, ct).GetAwaiter().GetResult();
        }
    }

    private void ExecutePurge(FileOperationJob job, AppDbContext db, ITrashService trashService, CancellationToken ct)
    {
        var ids = job.GetSourcePaths().Select(Guid.Parse).ToHashSet();
        var items = db.TrashItems.Where(t => ids.Contains(t.Id)).ToList();
        job.TotalItems = items.Count;
        db.SaveChangesAsync(ct).GetAwaiter().GetResult();
        BroadcastAsync(job, ct).GetAwaiter().GetResult();

        var trashRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            job.CurrentItem = item.Name;

            var physical = trashService.ToPhysicalTrashPath(item);
            FileTreeOperations.DeleteRecursive(physical, () => { }, ct);

            var wrapperDir = Path.GetDirectoryName(physical);
            if (wrapperDir is not null)
            {
                var trashRoot = Path.GetDirectoryName(wrapperDir);
                if (trashRoot is not null)
                {
                    trashRoots.Add(trashRoot);
                }
                if (Directory.Exists(wrapperDir) && !Directory.EnumerateFileSystemEntries(wrapperDir).Any())
                {
                    Directory.Delete(wrapperDir);
                }
            }

            db.TrashItems.Remove(item);
            job.ProcessedItems++;
            db.SaveChangesAsync(ct).GetAwaiter().GetResult();
            BroadcastAsync(job, ct).GetAwaiter().GetResult();
        }

        // Once every trashed item under a mount's trash root has been purged, remove the (now empty)
        // .filexplorer-trash directory itself so no app-created folder lingers on disk.
        foreach (var trashRoot in trashRoots)
        {
            if (Directory.Exists(trashRoot) && !Directory.EnumerateFileSystemEntries(trashRoot).Any())
            {
                Directory.Delete(trashRoot);
            }
        }
    }

    private Task BroadcastAsync(FileOperationJob job, CancellationToken ct) =>
        hub.Clients.All.SendAsync("jobUpdated", JobDto.FromEntity(job), ct);
}
