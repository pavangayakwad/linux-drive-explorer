using FileExplorer.Api.Data;
using FileExplorer.Api.Models.Entities;
using FileExplorer.Api.Options;
using FileExplorer.Api.Services.Jobs;
using Microsoft.Extensions.Options;

namespace FileExplorer.Api.Services;

public class FileOperationsService(
    AppDbContext db,
    IJobQueue queue,
    IPathResolver pathResolver,
    IMountLocator mountLocator,
    IOptions<FileSystemOptions> fsOptions) : IFileOperationsService
{
    // Extra headroom on top of the archive's estimated (uncompressed) size, for the zip's own central directory
    // and to leave the staging volume from filling up to zero.
    private const long ZipSafetyMarginBytes = 64L * 1024 * 1024;

    public async Task<Guid> CreateJobAsync(
        FileOperationType type,
        IReadOnlyList<string> sources,
        string? destination,
        int userId,
        bool permanent = false,
        CancellationToken ct = default)
    {
        if (type == FileOperationType.PurgeTrash)
        {
            throw new ArgumentException("Purge jobs must be created through the trash endpoints.");
        }

        if (sources.Count == 0)
        {
            throw new ArgumentException("At least one source path is required.");
        }

        foreach (var source in sources)
        {
            pathResolver.ToPhysicalPath(source); // throws UnauthorizedAccessException if outside the sandbox
        }

        if (type is FileOperationType.Copy or FileOperationType.Move)
        {
            if (string.IsNullOrWhiteSpace(destination))
            {
                throw new ArgumentException("A destination folder is required for copy/move operations.");
            }
            pathResolver.ToPhysicalPath(destination);
        }

        if (type == FileOperationType.Zip)
        {
            if (!string.IsNullOrWhiteSpace(destination))
            {
                throw new ArgumentException("Zip jobs do not take a destination.");
            }
            EnsureSpaceForZip(sources);
        }

        var job = new FileOperationJob
        {
            Type = type,
            DestinationPath = destination,
            CreatedByUserId = userId,
            Permanent = permanent && type == FileOperationType.Delete,
        };
        job.SetSourcePaths(sources);

        db.FileOperationJobs.Add(job);
        await db.SaveChangesAsync(ct);
        await queue.EnqueueAsync(job.Id, ct);

        return job.Id;
    }

    private void EnsureSpaceForZip(IReadOnlyList<string> sources)
    {
        var stagingDir = ZipStaging.GetStagingDirectory(pathResolver, fsOptions.Value);
        Directory.CreateDirectory(stagingDir);

        long totalBytes = 0;
        foreach (var source in sources)
        {
            var physical = pathResolver.ToPhysicalPath(source);
            totalBytes += FileTreeOperations.Measure(physical).Bytes;
        }

        var mountRoot = mountLocator.FindMountRoot(stagingDir);
        long availableBytes;
        try
        {
            availableBytes = new DriveInfo(mountRoot).AvailableFreeSpace;
        }
        catch (Exception)
        {
            // Can't determine free space - don't block the zip on a guess.
            return;
        }

        if (availableBytes < totalBytes + ZipSafetyMarginBytes)
        {
            throw new InsufficientSpaceException(totalBytes, availableBytes);
        }
    }
}
