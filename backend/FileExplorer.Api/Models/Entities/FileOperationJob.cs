using System.Text.Json;

namespace FileExplorer.Api.Models.Entities;

public class FileOperationJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public FileOperationType Type { get; set; }
    public string SourcePathsJson { get; set; } = "[]";
    public string? DestinationPath { get; set; }
    public FileOperationStatus Status { get; set; } = FileOperationStatus.Queued;

    public int TotalItems { get; set; }
    public int ProcessedItems { get; set; }
    public long TotalBytes { get; set; }
    public long ProcessedBytes { get; set; }
    public string? CurrentItem { get; set; }
    public string? ErrorMessage { get; set; }

    /// <summary>Names of items that couldn't be moved to Trash for lack of disk space and were deleted permanently instead.</summary>
    public string PermanentlyDeletedNamesJson { get; set; } = "[]";

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public int CreatedByUserId { get; set; }

    public List<string> GetSourcePaths() => JsonSerializer.Deserialize<List<string>>(SourcePathsJson) ?? [];

    public void SetSourcePaths(IReadOnlyList<string> paths) => SourcePathsJson = JsonSerializer.Serialize(paths);

    public List<string> GetPermanentlyDeletedNames() => JsonSerializer.Deserialize<List<string>>(PermanentlyDeletedNamesJson) ?? [];

    public void AddPermanentlyDeletedName(string name)
    {
        var names = GetPermanentlyDeletedNames();
        names.Add(name);
        PermanentlyDeletedNamesJson = JsonSerializer.Serialize(names);
    }
}
