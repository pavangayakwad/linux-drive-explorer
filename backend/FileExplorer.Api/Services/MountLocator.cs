namespace FileExplorer.Api.Services;

public class MountLocator(IPathResolver pathResolver) : IMountLocator
{
    public string FindMountRoot(string physicalPath)
    {
        var best = pathResolver.RootPath;

        foreach (var drive in DriveInfo.GetDrives())
        {
            if (!drive.IsReady)
            {
                continue;
            }

            var mount = drive.RootDirectory.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var matches = physicalPath.Equals(mount, StringComparison.OrdinalIgnoreCase)
                || physicalPath.StartsWith(mount + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

            if (matches && mount.Length > best.Length)
            {
                best = mount;
            }
        }

        return best;
    }
}
