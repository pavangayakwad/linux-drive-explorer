using FileExplorer.Api.Options;
using Microsoft.Extensions.Options;

namespace FileExplorer.Api.Services;

public class PathResolver : IPathResolver
{
    public string RootPath { get; }

    public PathResolver(IOptions<FileSystemOptions> options)
    {
        RootPath = Path.GetFullPath(options.Value.RootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Directory.Exists(RootPath))
        {
            Directory.CreateDirectory(RootPath);
        }
    }

    public string ToPhysicalPath(string virtualPath)
    {
        var normalized = (virtualPath ?? "/").Replace('\\', '/').TrimStart('/');
        var combined = normalized.Length == 0 ? RootPath : Path.Combine(RootPath, normalized);
        var full = Path.GetFullPath(combined);

        if (!IsWithinRoot(full))
        {
            throw new UnauthorizedAccessException($"Path '{virtualPath}' resolves outside the allowed root.");
        }

        return full;
    }

    public string ToVirtualPath(string physicalPath)
    {
        var full = Path.GetFullPath(physicalPath);

        if (!IsWithinRoot(full))
        {
            throw new UnauthorizedAccessException($"Physical path '{physicalPath}' is outside the allowed root.");
        }

        if (full.Equals(RootPath, StringComparison.OrdinalIgnoreCase))
        {
            return "/";
        }

        var relative = full[RootPath.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return "/" + relative.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
    }

    private bool IsWithinRoot(string fullPath)
    {
        return fullPath.Equals(RootPath, StringComparison.OrdinalIgnoreCase)
            || fullPath.StartsWith(RootPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
