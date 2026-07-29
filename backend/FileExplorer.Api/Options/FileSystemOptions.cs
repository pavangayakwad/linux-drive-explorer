namespace FileExplorer.Api.Options;

public class FileSystemOptions
{
    public const string SectionName = "FileSystem";

    /// <summary>
    /// The physical directory that acts as virtual "/" for every path the client sends/receives.
    /// In Docker this is the mount point of the bind-mounted host filesystem (e.g. /host_root).
    /// </summary>
    public string RootPath { get; set; } = "/";

    /// <summary>
    /// Name of the hidden trash directory created at the top of each mounted drive.
    /// </summary>
    public string TrashDirectoryName { get; set; } = ".filexplorer-trash";
}
