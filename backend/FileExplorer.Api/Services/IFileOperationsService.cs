using FileExplorer.Api.Models.Entities;

namespace FileExplorer.Api.Services;

public interface IFileOperationsService
{
    /// <summary>Validates the request, creates a queued job row, enqueues it, and returns the new job id.</summary>
    Task<Guid> CreateJobAsync(
        FileOperationType type,
        IReadOnlyList<string> sources,
        string? destination,
        int userId,
        CancellationToken ct = default);
}
