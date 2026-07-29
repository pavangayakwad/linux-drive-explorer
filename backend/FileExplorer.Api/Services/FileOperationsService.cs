using FileExplorer.Api.Data;
using FileExplorer.Api.Models.Entities;
using FileExplorer.Api.Services.Jobs;

namespace FileExplorer.Api.Services;

public class FileOperationsService(AppDbContext db, IJobQueue queue, IPathResolver pathResolver) : IFileOperationsService
{
    public async Task<Guid> CreateJobAsync(
        FileOperationType type,
        IReadOnlyList<string> sources,
        string? destination,
        int userId,
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

        var job = new FileOperationJob
        {
            Type = type,
            DestinationPath = destination,
            CreatedByUserId = userId,
        };
        job.SetSourcePaths(sources);

        db.FileOperationJobs.Add(job);
        await db.SaveChangesAsync(ct);
        await queue.EnqueueAsync(job.Id, ct);

        return job.Id;
    }
}
