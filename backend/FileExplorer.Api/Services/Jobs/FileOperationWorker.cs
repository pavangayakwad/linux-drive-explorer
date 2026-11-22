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

        var sources = job.GetSourcePaths();

        // Sources that still have to be transferred byte for byte: every source of a copy job, plus the
        // sources of a move that the OS wouldn't just rename (a genuine cross-device move, EXDEV).
        var copyPlan = new List<(string PhysicalSource, string PhysicalDestination)>();

        var lastReported = 0;
        var lastReportedAt = DateTime.UtcNow;
        var reportInterval = TimeSpan.FromMilliseconds(500);

        // Item-count throttling alone leaves a single large file's progress bar frozen at 0% for its whole
        // copy (ProcessedItems only increments once the file finishes) - a time-based fallback keeps
        // mid-file byte progress flowing to the UI without flooding SignalR on every chunk.
        // reportEvery is read from the totals as they currently stand rather than computed once up front,
        // because a move job discovers its work source by source instead of in one up-front scan.
        async Task Report(bool force)
        {
            var now = DateTime.UtcNow;
            var reportEvery = Math.Max(1, job.TotalItems / 50);
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

        if (move)
        {
            // A move inside one filesystem is a single directory-entry rename per top-level item - O(1) no
            // matter how deep the tree under it is - so there is nothing to measure before it happens.
            // Scanning every source tree up front just to have numbers to display meant such a move spent
            // its entire runtime inside the scan with the Tasks bar pinned at "0/0 items, 0%", and then
            // snapped straight to 100%: the scan was the only slow part of the job, and it reported no
            // progress at all (and did no work either). A move now counts one unit of work per file/folder
            // the user asked to move, renames them one at a time, and broadcasts after each one, so the bar
            // tracks what has actually been relocated from the first item onwards.
            job.TotalItems = sources.Count;
            await Report(force: true);

            foreach (var virtualSource in sources)
            {
                ct.ThrowIfCancellationRequested();

                var physicalSource = pathResolver.ToPhysicalPath(virtualSource);
                job.CurrentItem = pathResolver.ToVirtualPath(physicalSource);

                // Directory-tree work (GetUniqueDestination stats candidate names) is offloaded to a pool
                // thread via Task.Run rather than run inline, so a slow/network source mount only occupies a
                // worker for the call itself. .WaitAsync(ct) on top means Cancel is honored immediately even
                // if the call is stuck mid-flight on an unresponsive mount - the caller stops waiting right
                // away instead of leaving the Cancel button unresponsive, and the orphaned Task.Run finishes
                // quietly in the background whenever (if ever) the mount answers.
                var uniqueDestination = await Task.Run(() => FileTreeOperations.GetUniqueDestination(destinationPhysical, ItemName(physicalSource)), ct).WaitAsync(ct);

                // The OS repoints the entry in O(1) regardless of how large the tree is, with no data
                // actually touched (this is what makes "mv" on Linux instant). Only fall back to the
                // byte-for-byte copy + delete below when it isn't possible.
                if (await TryFastRenameAsync(physicalSource, uniqueDestination, ct))
                {
                    job.ProcessedItems++;
                    // Not forced: a "move 10,000 selected files on the same disk" job renames them faster
                    // than a save-and-broadcast each, and the ~2%-of-items threshold already keeps the bar live.
                    await Report(force: false);
                    continue;
                }

                copyPlan.Add((physicalSource, uniqueDestination));
            }

            // What the OS wouldn't rename has to be copied, and a byte-driven bar needs the sizes, so those
            // sources are measured here - after the renames have already put real movement on screen. Their
            // items are deliberately not added to TotalItems: for a move, one item only counts as done once
            // it has been copied *and* removed from where it was, which is when that move is actually over.
            foreach (var (physicalSource, _) in copyPlan)
            {
                ct.ThrowIfCancellationRequested();
                job.CurrentItem = pathResolver.ToVirtualPath(physicalSource);

                var (_, bytes) = await Task.Run(() => FileTreeOperations.Measure(physicalSource), ct).WaitAsync(ct);
                job.TotalBytes += bytes;
                await Report(force: true);
            }
        }
        else
        {
            // Copy: there is no shortcut, so every source has to be transferred and its size is worth
            // knowing before the transfer starts - the byte totals are what make a long copy's bar move.
            var totalBytes = 0L;
            var totalItems = 0;

            foreach (var virtualSource in sources)
            {
                ct.ThrowIfCancellationRequested();

                var physicalSource = pathResolver.ToPhysicalPath(virtualSource);
                var (items, bytes) = await Task.Run(() => FileTreeOperations.Measure(physicalSource), ct).WaitAsync(ct);
                totalItems += items;
                totalBytes += bytes;

                var uniqueDestination = await Task.Run(() => FileTreeOperations.GetUniqueDestination(destinationPhysical, ItemName(physicalSource)), ct).WaitAsync(ct);
                copyPlan.Add((physicalSource, uniqueDestination));
            }

            job.TotalItems = totalItems;
            job.TotalBytes = totalBytes;
            await Report(force: true);
        }

        foreach (var (physicalSource, physicalDestination) in copyPlan)
        {
            job.CurrentItem = pathResolver.ToVirtualPath(physicalSource);
            await Report(force: true);

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
                    // Only a copy job counts the individual files and folders inside a tree. A move job
                    // reports per top-level item, which is not done until the item has left its original
                    // location too.
                    if (!move)
                    {
                        job.ProcessedItems++;
                    }
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

                // The item only counts as moved once it is off the source as well as on the destination.
                job.ProcessedItems++;
                await Report(force: true);
            }
        }

        await Report(force: true);
    }

    private static string ItemName(string physicalPath) =>
        Path.GetFileName(physicalPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    /// <summary>Attempts an in-place rename (File.Move/Directory.Move) instead of copy+delete. Never throws -
    /// returns false on any failure (e.g. source and destination live on different filesystems) so the caller
    /// falls back to the slow path.</summary>
    private static async Task<bool> TryFastRenameAsync(string physicalSource, string physicalDestination, CancellationToken ct)
    {
        // Checked up front rather than just letting File.Move fail: Directory.Move fails immediately on a
        // cross-device EXDEV, but File.Move on Linux silently absorbs it by doing a full blocking
        // byte-for-byte copy internally with no progress callback, no cancellation, and no stall detection -
        // see FileTreeOperations.AreOnSameFileSystem for the incident this avoids.
        if (!FileTreeOperations.AreOnSameFileSystem(physicalSource, physicalDestination))
        {
            return false;
        }

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
