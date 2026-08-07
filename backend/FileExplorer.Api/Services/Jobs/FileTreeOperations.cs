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
    /// file/folder finished.
    ///
    /// Uses real async file I/O (FileOptions.Asynchronous + ReadAsync/WriteAsync) rather than blocking Read/Write,
    /// so a slow/saturated disk only borrows a ThreadPool worker for the duration of each chunk's syscall instead of
    /// pinning one thread for the copy's entire multi-hour lifetime - see the 8-hour-move near-freeze writeup.</summary>
    public static async Task CopyRecursiveAsync(string sourcePhysical, string destPhysical, Func<long, Task> onBytesCopied, Func<Task> onItemCompleted, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (File.Exists(sourcePhysical))
        {
            await CopyFileWithProgressAsync(sourcePhysical, destPhysical, onBytesCopied, ct);
            await onItemCompleted();
            return;
        }

        Directory.CreateDirectory(destPhysical);
        await onItemCompleted();

        foreach (var entry in Directory.EnumerateFileSystemEntries(sourcePhysical))
        {
            ct.ThrowIfCancellationRequested();
            var destEntry = Path.Combine(destPhysical, Path.GetFileName(entry));
            if (Directory.Exists(entry))
            {
                await CopyRecursiveAsync(entry, destEntry, onBytesCopied, onItemCompleted, ct);
            }
            else
            {
                await CopyFileWithProgressAsync(entry, destEntry, onBytesCopied, ct);
                await onItemCompleted();
            }
        }
    }

    private static async Task CopyFileWithProgressAsync(string sourcePhysical, string destPhysical, Func<long, Task> onBytesCopied, CancellationToken ct)
    {
        await using var source = new FileStream(sourcePhysical, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize, FileOptions.SequentialScan | FileOptions.Asynchronous);
        await using var dest = new FileStream(destPhysical, FileMode.CreateNew, FileAccess.Write, FileShare.None, CopyBufferSize, FileOptions.Asynchronous);

        var buffer = new byte[CopyBufferSize];
        int read;
        while ((read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
        {
            await dest.WriteAsync(buffer.AsMemory(0, read), ct);
            await onBytesCopied(read);
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
