namespace FileExplorer.Api.Models.Dtos;

public record FileEntryDto(
    string Name,
    string Path,
    bool IsDirectory,
    long? SizeBytes,
    DateTimeOffset CreatedAt,
    DateTimeOffset ModifiedAt,
    string? Extension,
    string TypeLabel,
    string? UnixPermissions,
    bool IsHidden);

public record DirectoryListingDto(
    string Path,
    string? ParentPath,
    IReadOnlyList<FileEntryDto> Entries);

public record DriveSummaryDto(
    string Name,
    string MountPath,
    string DriveFormat,
    long TotalBytes,
    long FreeBytes,
    bool IsRemovable);

public record CreateEntryRequest(string ParentPath, string Name, bool IsDirectory);

public record RenameRequest(string Path, string NewName);

public enum DirectorySizeStatus { Running, Completed, Cancelled, Failed }

public record DirectorySizeJobDto(Guid JobId, string Path, DirectorySizeStatus Status, long Bytes);
