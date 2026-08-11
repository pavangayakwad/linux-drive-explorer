using System.IO.Compression;
using FileExplorer.Api.Data;
using FileExplorer.Api.Hubs;
using FileExplorer.Api.Models.Dtos;
using FileExplorer.Api.Models.Entities;
using FileExplorer.Api.Options;
using FileExplorer.Api.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

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
        var fsOptions = scope.ServiceProvider.GetRequiredService<IOptions<FileSystemOptions>>().Value;

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
                    await ExecuteCopyOrMoveAsync(job, db, pathResolver, move: false, ct);
                    break;
                case FileOperationType.Move:
                    await ExecuteCopyOrMoveAsync(job, db, pathResolver, move: true, ct);
                    break;
                case FileOperationType.Delete:
                    await ExecuteDeleteAsync(job, db, trashService, ct);
                    break;
                case FileOperationType.PurgeTrash:
                    await ExecutePurgeAsync(job, db, trashService, ct);
                    break;
                case FileOperationType.Zip:
                    await ExecuteZipAsync(job, db, pathResolver, fsOptions, ct);
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

    private async Task ExecuteCopyOrMoveAsync(FileOperationJob job, AppDbContext db, IPathResolver pathResolver, bool move, CancellationToken ct)
    {
        var destinationPhysical = pathResolver.ToPhysicalPath(job.DestinationPath!);
        Directory.CreateDirectory(destinationPhysical);

        var plan = new List<(string PhysicalSource, string PhysicalDestination, int ItemCount, long ByteCount)>();
        long totalBytes = 0;
        var totalItems = 0;

        // Directory-tree scanning (Measure/GetUniqueDestination) is offloaded to a pool thread via Task.Run
        // rather than run inline, so a slow/network source mount only occupies a worker for the scan itself
        // instead of blocking whichever thread happened to pick up this continuation. .WaitAsync(ct) on top
        // means Cancel is honored immediately even if the scan itself is stuck mid-call on an unresponsive
        // mount - the caller stops waiting right away instead of leaving the Cancel button unresponsive; the
        // orphaned Task.Run finishes quietly in the background whenever (if ever) the mount answers.
        foreach (var virtualSource in job.GetSourcePaths())
        {
            var physicalSource = pathResolver.ToPhysicalPath(virtualSource);
            var (items, bytes) = await Task.Run(() => FileTreeOperations.Measure(physicalSource), ct).WaitAsync(ct);
            totalItems += items;
            totalBytes += bytes;

            var name = Path.GetFileName(physicalSource.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var uniqueDestination = await Task.Run(() => FileTreeOperations.GetUniqueDestination(destinationPhysical, name), ct).WaitAsync(ct);
            plan.Add((physicalSource, uniqueDestination, items, bytes));
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
        async Task Report(bool force)
        {
            var now = DateTime.UtcNow;
            var itemThresholdMet = job.ProcessedItems - lastReported >= reportEvery;
            if (!force && !itemThresholdMet && now - lastReportedAt < reportInterval)
            {
                return;
            }
            lastReported = job.ProcessedItems;
            lastReportedAt = now;
            await db.SaveChangesAsync(ct);
            await BroadcastAsync(job, ct);
        }

        await Report(force: true);

        foreach (var (physicalSource, physicalDestination, itemCount, byteCount) in plan)
        {
            job.CurrentItem = pathResolver.ToVirtualPath(physicalSource);

            // A move within the same filesystem/mount is just a directory-entry rename - the OS repoints
            // it in O(1) regardless of how large the tree is, with no data actually touched (this is what
            // makes "mv" on Linux instant). Try that first and only fall back to the byte-for-byte copy +
            // delete below when it's not possible, e.g. a genuine cross-device move (EXDEV) between two
            // different mounted drives.
            if (move && await TryFastRenameAsync(physicalSource, physicalDestination, ct))
            {
                job.ProcessedItems += itemCount;
                job.ProcessedBytes += byteCount;
                await Report(force: false);
                continue;
            }

            await FileTreeOperations.CopyRecursiveAsync(
                physicalSource,
                physicalDestination,
                async bytes =>
                {
                    job.ProcessedBytes += bytes;
                    await Report(force: false);
                },
                async () =>
                {
                    job.ProcessedItems++;
                    await Report(force: false);
                },
                ct);

            // Delete each source immediately once its own copy lands, rather than in a
            // second pass after everything is copied - otherwise a cancellation or crash
            // between the two passes leaves already-copied items duplicated at both ends
            // instead of moved.
            if (move)
            {
                await Task.Run(() => FileTreeOperations.DeleteRecursive(physicalSource, () => { }, ct), ct).WaitAsync(ct);
            }
        }

        await Report(force: true);
    }

    /// <summary>Attempts an in-place rename (File.Move/Directory.Move) instead of copy+delete. Never throws -
    /// returns false on any failure (e.g. source and destination live on different filesystems) so the caller
    /// falls back to the slow path.</summary>
    private static async Task<bool> TryFastRenameAsync(string physicalSource, string physicalDestination, CancellationToken ct)
    {
        try
        {
            return await Task.Run(() =>
            {
                if (File.Exists(physicalSource))
                {
                    File.Move(physicalSource, physicalDestination);
                    return true;
                }
                if (Directory.Exists(physicalSource))
                {
                    Directory.Move(physicalSource, physicalDestination);
                    return true;
                }
                return false;
            }, ct).WaitAsync(ct);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async Task ExecuteDeleteAsync(FileOperationJob job, AppDbContext db, ITrashService trashService, CancellationToken ct)
    {
        var sources = job.GetSourcePaths();
        job.TotalItems = sources.Count;
        await db.SaveChangesAsync(ct);
        await BroadcastAsync(job, ct);

        foreach (var virtualSource in sources)
        {
            ct.ThrowIfCancellationRequested();
            job.CurrentItem = virtualSource;
            var outcome = await Task.Run(
                () => job.Permanent
                    ? trashService.DeleteForever(virtualSource)
                    : trashService.MoveToTrash(virtualSource, job.CreatedByUserId),
                ct).WaitAsync(ct);
            if (outcome.PermanentlyDeleted)
            {
                job.AddPermanentlyDeletedName(outcome.Name);
            }
            else
            {
                db.TrashItems.Add(outcome.Item!);
            }
            job.ProcessedItems++;
            await db.SaveChangesAsync(ct);
            await BroadcastAsync(job, ct);
        }
    }

    private async Task ExecutePurgeAsync(FileOperationJob job, AppDbContext db, ITrashService trashService, CancellationToken ct)
    {
        var ids = job.GetSourcePaths().Select(Guid.Parse).ToHashSet();
        var items = db.TrashItems.Where(t => ids.Contains(t.Id)).ToList();
        job.TotalItems = items.Count;
        await db.SaveChangesAsync(ct);
        await BroadcastAsync(job, ct);

        var trashRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            job.CurrentItem = item.Name;

            var physical = trashService.ToPhysicalTrashPath(item);
            await Task.Run(() =>
            {
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
            }, ct).WaitAsync(ct);

            db.TrashItems.Remove(item);
            job.ProcessedItems++;
            await db.SaveChangesAsync(ct);
            await BroadcastAsync(job, ct);
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

    private async Task ExecuteZipAsync(FileOperationJob job, AppDbContext db, IPathResolver pathResolver, FileSystemOptions fsOptions, CancellationToken ct)
    {
        var stagingDir = ZipStaging.GetStagingDirectory(pathResolver, fsOptions);
        Directory.CreateDirectory(stagingDir);
        var zipPhysicalPath = ZipStaging.GetZipPhysicalPath(pathResolver, fsOptions, job.Id);

        var plan = new List<(string PhysicalPath, string EntryRootName)>();
        long totalBytes = 0;
        var totalItems = 0;

        foreach (var virtualSource in job.GetSourcePaths())
        {
            var physicalSource = pathResolver.ToPhysicalPath(virtualSource);
            var (items, bytes) = await Task.Run(() => FileTreeOperations.Measure(physicalSource), ct).WaitAsync(ct);
            totalItems += items;
            totalBytes += bytes;

            var name = Path.GetFileName(physicalSource.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            plan.Add((physicalSource, name));
        }

        job.TotalItems = totalItems;
        job.TotalBytes = totalBytes;
        var reportEvery = Math.Max(1, totalItems / 50);
        var lastReported = 0;
        var lastReportedAt = DateTime.UtcNow;
        var reportInterval = TimeSpan.FromMilliseconds(500);

        async Task Report(bool force)
        {
            var now = DateTime.UtcNow;
            var itemThresholdMet = job.ProcessedItems - lastReported >= reportEvery;
            if (!force && !itemThresholdMet && now - lastReportedAt < reportInterval)
            {
                return;
            }
            lastReported = job.ProcessedItems;
            lastReportedAt = now;
            await db.SaveChangesAsync(ct);
            await BroadcastAsync(job, ct);
        }

        await Report(force: true);

        try
        {
            await using (var zipFileStream = new FileStream(zipPhysicalPath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous))
            using (var archive = new ZipArchive(zipFileStream, ZipArchiveMode.Create))
            {
                foreach (var (physicalPath, entryRootName) in plan)
                {
                    job.CurrentItem = pathResolver.ToVirtualPath(physicalPath);
                    await AddToArchiveAsync(archive, physicalPath, entryRootName, job, Report, ct);
                }
            }
            await Report(force: true);
        }
        catch
        {
            TryDeleteFile(zipPhysicalPath);
            throw;
        }
    }

    private static async Task AddToArchiveAsync(ZipArchive archive, string physicalPath, string entryName, FileOperationJob job, Func<bool, Task> report, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (Directory.Exists(physicalPath))
        {
            job.ProcessedItems++;
            await report(false);
            foreach (var child in Directory.EnumerateFileSystemEntries(physicalPath))
            {
                await AddToArchiveAsync(archive, child, $"{entryName}/{Path.GetFileName(child)}", job, report, ct);
            }
            return;
        }

        var entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
        entry.LastWriteTime = File.GetLastWriteTimeUtc(physicalPath);

        var entryStream = entry.Open();
        var source = new FileStream(physicalPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan | FileOptions.Asynchronous);
        try
        {
            var buffer = new byte[1024 * 1024];
            int read;
            // Stall-guarded the same way as the plain copy path: a source file on an unresponsive/disconnected
            // mount can't be allowed to block Cancel forever, and disposing entryStream/source normally on that
            // path could itself hang mid-flush - see FileTreeOperations.GuardAsync/DisposeQuietlyAsync.
            while ((read = await FileTreeOperations.GuardAsync(source.ReadAsync(buffer.AsMemory(0, buffer.Length), ct), physicalPath, ct)) > 0)
            {
                await FileTreeOperations.GuardAsync(entryStream.WriteAsync(buffer.AsMemory(0, read), ct), physicalPath, ct);
                job.ProcessedBytes += read;
                await report(false);
            }
        }
        finally
        {
            _ = FileTreeOperations.DisposeQuietlyAsync(source);
            _ = FileTreeOperations.DisposeQuietlyAsync(entryStream);
        }

        job.ProcessedItems++;
        await report(false);
    }

    private static void TryDeleteFile(string physicalPath)
    {
        try
        {
            if (File.Exists(physicalPath))
            {
                File.Delete(physicalPath);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup only.
        }
    }

    private Task BroadcastAsync(FileOperationJob job, CancellationToken ct) =>
        hub.Clients.All.SendAsync("jobUpdated", JobDto.FromEntity(job), ct);
}
