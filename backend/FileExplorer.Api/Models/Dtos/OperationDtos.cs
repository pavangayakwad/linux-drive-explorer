using FileExplorer.Api.Models.Entities;

namespace FileExplorer.Api.Models.Dtos;

public record CreateOperationRequest(FileOperationType Type, IReadOnlyList<string> Sources, string? Destination, bool Permanent = false);

public record JobDto(
    Guid Id,
    FileOperationType Type,
    IReadOnlyList<string> Sources,
    string? Destination,
    FileOperationStatus Status,
    int TotalItems,
    int ProcessedItems,
    long TotalBytes,
    long ProcessedBytes,
    string? CurrentItem,
    string? ErrorMessage,
    IReadOnlyList<string> PermanentlyDeletedNames,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt)
{
    public static JobDto FromEntity(FileOperationJob job) => new(
        job.Id,
        job.Type,
        job.GetSourcePaths(),
        job.DestinationPath,
        job.Status,
        job.TotalItems,
        job.ProcessedItems,
        job.TotalBytes,
        job.ProcessedBytes,
        job.CurrentItem,
        job.ErrorMessage,
        job.GetPermanentlyDeletedNames(),
        job.CreatedAt,
        job.StartedAt,
        job.CompletedAt);
}

public record TrashItemDto(
    Guid Id,
    string OriginalPath,
    string Name,
    bool IsDirectory,
    long? SizeBytes,
    DateTimeOffset DeletedAt)
{
    public static TrashItemDto FromEntity(TrashItem item) => new(
        item.Id,
        item.OriginalPath,
        item.Name,
        item.IsDirectory,
        item.SizeBytes,
        item.DeletedAt);
}

public record PurgeTrashRequest(IReadOnlyList<Guid>? Ids);

public record CheckTrashSpaceRequest(IReadOnlyList<string> Sources);
