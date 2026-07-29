namespace FileExplorer.Api.Services;

/// <summary>
/// Translates between the "virtual" POSIX-style paths the client works with (rooted at "/")
/// and the real physical paths on disk (rooted at FileSystemOptions.RootPath).
/// </summary>
public interface IPathResolver
{
    /// <summary>
    /// Resolves a client-supplied virtual path to a physical path, throwing
    /// <see cref="UnauthorizedAccessException"/> if it would escape the configured root.
    /// </summary>
    string ToPhysicalPath(string virtualPath);

    string ToVirtualPath(string physicalPath);

    string RootPath { get; }
}
