namespace FileExplorer.Api.Models.Entities;

public enum FileOperationType
{
    Copy,
    Move,
    Delete,
    PurgeTrash,
    Zip,
}

public enum FileOperationStatus
{
    Queued,
    Running,
    Completed,
    Failed,
    Cancelled,
}
