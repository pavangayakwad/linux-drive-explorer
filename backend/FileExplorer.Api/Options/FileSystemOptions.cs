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

    /// <summary>
    /// Name of the hidden directory (at the top of the virtual root) used to stage zip archives while a
    /// multi-file/folder download job builds them.
    /// </summary>
    public string ZipDirectoryName { get; set; } = ".filexplorer-zips";
}
