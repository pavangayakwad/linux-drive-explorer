namespace FileExplorer.Api.Services.Jobs;

/// <summary>Low-level, path-resolver-agnostic filesystem walking helpers shared by the job worker and trash service.</summary>
public static class FileTreeOperations
{
    public static (int Items, long Bytes) Measure(string physicalPath)
    {
        if (File.Exists(physicalPath))
        {
            return (1, SafeLength(physicalPath));
        }

        if (!Directory.Exists(physicalPath))
        {
            return (0, 0);
        }

        var items = 1; // the folder itself
        long bytes = 0;

        foreach (var entry in SafeEnumerateRecursive(physicalPath))
        {
            items++;
            if (!Directory.Exists(entry))
            {
                bytes += SafeLength(entry);
            }
        }

        return (items, bytes);
    }

    public static string GetUniqueDestination(string destinationDir, string name)
    {
        var candidate = Path.Combine(destinationDir, name);
        if (!File.Exists(candidate) && !Directory.Exists(candidate))
        {
            return candidate;
        }

        var extension = Path.GetExtension(name);
        var baseName = Path.GetFileNameWithoutExtension(name);
        for (var i = 2; ; i++)
        {
            var attempt = Path.Combine(destinationDir, $"{baseName} ({i}){extension}");
            if (!File.Exists(attempt) && !Directory.Exists(attempt))
            {
                return attempt;
            }
        }
    }

    private const int CopyBufferSize = 1024 * 1024; // 1 MiB

    /// <summary>Copies a file or directory tree, invoking onBytesCopied after every chunk written (so progress for a
    /// single large file is visible mid-copy, not just once the whole file lands) and onItemCompleted once per
    /// file/folder finished.</summary>
    public static void CopyRecursive(string sourcePhysical, string destPhysical, Action<long> onBytesCopied, Action onItemCompleted, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (File.Exists(sourcePhysical))
        {
            CopyFileWithProgress(sourcePhysical, destPhysical, onBytesCopied, ct);
            onItemCompleted();
            return;
        }

        Directory.CreateDirectory(destPhysical);
        onItemCompleted();

        foreach (var entry in Directory.EnumerateFileSystemEntries(sourcePhysical))
        {
            ct.ThrowIfCancellationRequested();
            var destEntry = Path.Combine(destPhysical, Path.GetFileName(entry));
            if (Directory.Exists(entry))
            {
                CopyRecursive(entry, destEntry, onBytesCopied, onItemCompleted, ct);
            }
            else
            {
                CopyFileWithProgress(entry, destEntry, onBytesCopied, ct);
                onItemCompleted();
            }
        }
    }

    private static void CopyFileWithProgress(string sourcePhysical, string destPhysical, Action<long> onBytesCopied, CancellationToken ct)
    {
        using var source = new FileStream(sourcePhysical, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize, FileOptions.SequentialScan);
        using var dest = new FileStream(destPhysical, FileMode.CreateNew, FileAccess.Write, FileShare.None, CopyBufferSize);

        var buffer = new byte[CopyBufferSize];
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            dest.Write(buffer, 0, read);
            onBytesCopied(read);
        }
    }

    /// <summary>Permanently deletes a file or directory tree, invoking onItemDeleted() after each item.</summary>
    public static void DeleteRecursive(string physicalPath, Action onItemDeleted, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (File.Exists(physicalPath))
        {
            File.Delete(physicalPath);
            onItemDeleted();
            return;
        }

        if (!Directory.Exists(physicalPath))
        {
            return;
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(physicalPath).ToList())
        {
            ct.ThrowIfCancellationRequested();
            if (Directory.Exists(entry))
            {
                DeleteRecursive(entry, onItemDeleted, ct);
            }
            else
            {
                File.Delete(entry);
                onItemDeleted();
            }
        }

        Directory.Delete(physicalPath);
        onItemDeleted();
    }

    private static IEnumerable<string> SafeEnumerateRecursive(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
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

            foreach (var entry in entries)
            {
                yield return entry;
                if (Directory.Exists(entry))
                {
                    stack.Push(entry);
                }
            }
        }
    }

    private static long SafeLength(string filePath)
    {
        try
        {
            return new FileInfo(filePath).Length;
        }
        catch (IOException)
        {
            return 0;
        }
    }
}
