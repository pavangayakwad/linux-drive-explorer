namespace FileExplorer.Api.Models.Entities;

public class TrashItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string OriginalPath { get; set; } = string.Empty;
    public string TrashPath { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsDirectory { get; set; }
    public long? SizeBytes { get; set; }
    public DateTimeOffset DeletedAt { get; set; } = DateTimeOffset.UtcNow;
    public int DeletedByUserId { get; set; }
}
