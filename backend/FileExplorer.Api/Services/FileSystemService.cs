using FileExplorer.Api.Models.Dtos;
using FileExplorer.Api.Options;
using Microsoft.Extensions.Options;

namespace FileExplorer.Api.Services;

public class FileSystemService(IPathResolver pathResolver, IOptions<FileSystemOptions> fsOptions) : IFileSystemService
{
    private readonly string _trashDirName = fsOptions.Value.TrashDirectoryName;

    public DirectoryListingDto ListDirectory(string virtualPath)
    {
        var physicalPath = pathResolver.ToPhysicalPath(virtualPath);

        if (!Directory.Exists(physicalPath))
        {
            throw new DirectoryNotFoundException($"Directory '{virtualPath}' does not exist.");
        }

        var entries = new List<FileEntryDto>();

        foreach (var entryPath in Directory.EnumerateFileSystemEntries(physicalPath))
        {
            var name = Path.GetFileName(entryPath);
            if (name.Equals(_trashDirName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            FileEntryDto? dto = TryDescribeEntry(entryPath, name);
            if (dto is not null)
            {
                entries.Add(dto);
            }
        }

        var currentVirtual = pathResolver.ToVirtualPath(physicalPath);
        string? parentVirtual = null;

        if (currentVirtual != "/")
        {
            var parentPhysical = Directory.GetParent(physicalPath)?.FullName;
            parentVirtual = parentPhysical is not null && parentPhysical.Length >= pathResolver.RootPath.Length
                ? pathResolver.ToVirtualPath(parentPhysical)
                : "/";
        }

        return new DirectoryListingDto(currentVirtual, parentVirtual, entries);
    }

    public IReadOnlyList<FileEntryDto> Search(string rootVirtualPath, string query, int maxResults = 200)
    {
        var rootPhysical = pathResolver.ToPhysicalPath(rootVirtualPath);
        if (!Directory.Exists(rootPhysical))
        {
            throw new DirectoryNotFoundException($"Directory '{rootVirtualPath}' does not exist.");
        }

        var results = new List<FileEntryDto>();
        var stack = new Stack<string>();
        stack.Push(rootPhysical);

        while (stack.Count > 0 && results.Count < maxResults)
        {
            var dir = stack.Pop();
            List<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(dir).ToList();
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var entryPath in entries)
            {
                var name = Path.GetFileName(entryPath);
                if (name.Equals(_trashDirName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (name.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    var dto = TryDescribeEntry(entryPath, name);
                    if (dto is not null)
                    {
                        results.Add(dto);
                        if (results.Count >= maxResults)
                        {
                            break;
                        }
                    }
                }

                if (Directory.Exists(entryPath))
                {
                    stack.Push(entryPath);
                }
            }
        }

        return results;
    }

    public FileEntryDto CreateEntry(string parentVirtualPath, string name, bool isDirectory)
    {
        ValidateEntryName(name);

        var parentPhysical = pathResolver.ToPhysicalPath(parentVirtualPath);
        if (!Directory.Exists(parentPhysical))
        {
            throw new DirectoryNotFoundException($"Directory '{parentVirtualPath}' does not exist.");
        }

        var physicalPath = Path.Combine(parentPhysical, name);
        if (Directory.Exists(physicalPath) || File.Exists(physicalPath))
        {
            throw new IOException($"'{name}' already exists.");
        }

        if (isDirectory)
        {
            Directory.CreateDirectory(physicalPath);
        }
        else
        {
            using (File.Create(physicalPath))
            {
            }
        }

        return DescribeEntry(physicalPath, name);
    }

    public FileEntryDto Rename(string virtualPath, string newName)
    {
        ValidateEntryName(newName);

        var physicalPath = pathResolver.ToPhysicalPath(virtualPath);
        var isDirectory = Directory.Exists(physicalPath);
        if (!isDirectory && !File.Exists(physicalPath))
        {
            throw new FileNotFoundException($"'{virtualPath}' does not exist.");
        }

        var parentPhysical = Path.GetDirectoryName(physicalPath) ?? pathResolver.RootPath;
        var newPhysicalPath = Path.Combine(parentPhysical, newName);

        if (!newPhysicalPath.Equals(physicalPath, StringComparison.OrdinalIgnoreCase) &&
            (Directory.Exists(newPhysicalPath) || File.Exists(newPhysicalPath)))
        {
            throw new IOException($"'{newName}' already exists.");
        }

        if (isDirectory)
        {
            Directory.Move(physicalPath, newPhysicalPath);
        }
        else
        {
            File.Move(physicalPath, newPhysicalPath);
        }

        return DescribeEntry(newPhysicalPath, newName);
    }

    private static void ValidateEntryName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) ||
            name is "." or ".." ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException($"'{name}' is not a valid file or folder name.");
        }
    }

    private FileEntryDto? TryDescribeEntry(string physicalPath, string name)
    {
        try
        {
            return DescribeEntry(physicalPath, name);
        }
        catch (IOException)
        {
            // Broken symlinks / entries that vanish mid-enumeration - skip rather than fail the whole listing.
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private FileEntryDto DescribeEntry(string physicalPath, string name)
    {
        var isDirectory = Directory.Exists(physicalPath);
        var info = isDirectory ? (FileSystemInfo)new DirectoryInfo(physicalPath) : new FileInfo(physicalPath);
        var extension = isDirectory ? null : Path.GetExtension(name);
        var isHidden = name.StartsWith('.') || info.Attributes.HasFlag(FileAttributes.Hidden);

        return new FileEntryDto(
            Name: name,
            Path: pathResolver.ToVirtualPath(physicalPath),
            IsDirectory: isDirectory,
            SizeBytes: isDirectory ? null : ((FileInfo)info).Length,
            CreatedAt: info.CreationTimeUtc,
            ModifiedAt: info.LastWriteTimeUtc,
            Extension: extension,
            TypeLabel: FileTypeLabeler.Resolve(isDirectory, extension),
            UnixPermissions: UnixPermissionFormatter.TryFormat(physicalPath),
            IsHidden: isHidden);
    }
}
