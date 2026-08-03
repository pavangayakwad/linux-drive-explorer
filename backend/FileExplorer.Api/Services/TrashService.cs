using FileExplorer.Api.Models.Entities;
using FileExplorer.Api.Options;
using FileExplorer.Api.Services.Jobs;
using Microsoft.Extensions.Options;

namespace FileExplorer.Api.Services;

public class TrashService(IPathResolver pathResolver, IMountLocator mountLocator, IOptions<FileSystemOptions> fsOptions) : ITrashService
{
    private readonly string _trashDirName = fsOptions.Value.TrashDirectoryName;

    // Extra headroom required on top of the item's own size before we'll attempt a trash move -
    // the filesystem needs a little room for the trash item's own directory entry/metadata.
    private const long SafetyMarginBytes = 1024 * 1024;

    public TrashOutcome MoveToTrash(string virtualOriginalPath, int userId)
    {
        var physicalSource = pathResolver.ToPhysicalPath(virtualOriginalPath);
        var isDirectory = Directory.Exists(physicalSource);
        if (!isDirectory && !File.Exists(physicalSource))
        {
            throw new FileNotFoundException($"'{virtualOriginalPath}' does not exist.");
        }

        var name = Path.GetFileName(physicalSource.TrimEnd(Path.DirectorySeparatorChar));
        var (_, sizeBytes) = FileTreeOperations.Measure(physicalSource);
        var mountRoot = mountLocator.FindMountRoot(physicalSource);

        if (!HasRoomForTrash(mountRoot, sizeBytes))
        {
            FileTreeOperations.DeleteRecursive(physicalSource, () => { }, CancellationToken.None);
            return new TrashOutcome { Name = name, PermanentlyDeleted = true };
        }

        var trashRoot = Path.Combine(mountRoot, _trashDirName);
        Directory.CreateDirectory(trashRoot);
        var itemTrashDir = Path.Combine(trashRoot, Guid.NewGuid().ToString("N"));
        var destPhysical = Path.Combine(itemTrashDir, name);

        try
        {
            Directory.CreateDirectory(itemTrashDir);
            if (isDirectory)
            {
                Directory.Move(physicalSource, destPhysical);
            }
            else
            {
                File.Move(physicalSource, destPhysical);
            }
        }
        catch (IOException ex) when (IsOutOfDiskSpace(ex))
        {
            // Race: free space checked out above but the volume filled up before/during the move
            // (e.g. another job writing concurrently) - fall back to a permanent delete rather
            // than leaving the item stuck half-moved or failing the whole delete job.
            TryDeleteEmptyDirectory(itemTrashDir);
            FileTreeOperations.DeleteRecursive(physicalSource, () => { }, CancellationToken.None);
            return new TrashOutcome { Name = name, PermanentlyDeleted = true };
        }

        return new TrashOutcome
        {
            Name = name,
            Item = new TrashItem
            {
                OriginalPath = virtualOriginalPath,
                TrashPath = pathResolver.ToVirtualPath(destPhysical),
                Name = name,
                IsDirectory = isDirectory,
                SizeBytes = sizeBytes,
                DeletedByUserId = userId,
            },
        };
    }

    public TrashOutcome DeleteForever(string virtualOriginalPath)
    {
        var physicalSource = pathResolver.ToPhysicalPath(virtualOriginalPath);
        var isDirectory = Directory.Exists(physicalSource);
        if (!isDirectory && !File.Exists(physicalSource))
        {
            throw new FileNotFoundException($"'{virtualOriginalPath}' does not exist.");
        }

        var name = Path.GetFileName(physicalSource.TrimEnd(Path.DirectorySeparatorChar));
        FileTreeOperations.DeleteRecursive(physicalSource, () => { }, CancellationToken.None);
        return new TrashOutcome { Name = name, PermanentlyDeleted = true };
    }

    public bool HasSpaceForAll(IReadOnlyList<string> virtualPaths)
    {
        var bytesByMount = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        foreach (var virtualPath in virtualPaths)
        {
            var physicalSource = pathResolver.ToPhysicalPath(virtualPath);
            if (!Directory.Exists(physicalSource) && !File.Exists(physicalSource))
            {
                continue;
            }

            var (_, sizeBytes) = FileTreeOperations.Measure(physicalSource);
            var mountRoot = mountLocator.FindMountRoot(physicalSource);
            bytesByMount[mountRoot] = bytesByMount.GetValueOrDefault(mountRoot) + sizeBytes;
        }

        return bytesByMount.All(pair => HasRoomForTrash(pair.Key, pair.Value));
    }

    private static bool HasRoomForTrash(string mountRoot, long sizeBytes)
    {
        try
        {
            return new DriveInfo(mountRoot).AvailableFreeSpace >= sizeBytes + SafetyMarginBytes;
        }
        catch (Exception)
        {
            // Can't determine free space - don't block the delete on a guess, the IOException
            // fallback in MoveToTrash still catches a genuine out-of-space failure.
            return true;
        }
    }

    private static bool IsOutOfDiskSpace(IOException ex) => ex.Message.Contains("space", StringComparison.OrdinalIgnoreCase);

    private static void TryDeleteEmptyDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
            {
                Directory.Delete(path);
            }
        }
        catch
        {
            // best-effort cleanup
        }
    }

    public void Restore(TrashItem item)
    {
        var physicalTrash = pathResolver.ToPhysicalPath(item.TrashPath);
        if (!Directory.Exists(physicalTrash) && !File.Exists(physicalTrash))
        {
            throw new FileNotFoundException("This item is no longer in the trash.");
        }

        var physicalOriginal = pathResolver.ToPhysicalPath(item.OriginalPath);
        var originalParent = Path.GetDirectoryName(physicalOriginal) ?? pathResolver.RootPath;
        Directory.CreateDirectory(originalParent);

        var destination = FileTreeOperations.GetUniqueDestination(originalParent, item.Name);

        if (item.IsDirectory)
        {
            Directory.Move(physicalTrash, destination);
        }
        else
        {
            File.Move(physicalTrash, destination);
        }

        // Clean up the now-empty per-item trash folder (guid dir that wrapped the item).
        var itemTrashDir = Path.GetDirectoryName(physicalTrash);
        if (itemTrashDir is not null && Directory.Exists(itemTrashDir) && !Directory.EnumerateFileSystemEntries(itemTrashDir).Any())
        {
            Directory.Delete(itemTrashDir);
        }
    }

    public string ToPhysicalTrashPath(TrashItem item) => pathResolver.ToPhysicalPath(item.TrashPath);
}
