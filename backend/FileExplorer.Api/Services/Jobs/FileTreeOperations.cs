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

    /// <summary>How long a single chunk read/write is allowed to make no progress before it's treated as a
    /// stalled/disconnected device rather than just a slow one. 1 MiB chunks completing in under a minute holds
    /// even for very slow media (the incident this guards against averaged ~2 MB/s over 8 hours); past that,
    /// the device is presumed gone.</summary>
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Bounds a single async I/O call so it can be abandoned instead of waited on forever. A blocked
    /// kernel-level read/write against an unresponsive or disconnected device (network share timeout, a USB
    /// drive that stopped answering) cannot be forcibly interrupted from managed code - CancellationToken only
    /// stops an operation that hasn't started yet. Task.WaitAsync sidesteps that: it returns the instant the
    /// token fires or the timeout elapses, regardless of whether the underlying operation ever completes, which
    /// is what lets Cancel (and this timeout) actually take effect on a hung copy/move/delete/zip instead of
    /// leaving the job - and the Cancel button - stuck indefinitely.</summary>
    internal static async Task<int> GuardAsync(ValueTask<int> operation, string path, CancellationToken ct)
    {
        try
        {
            return await operation.AsTask().WaitAsync(StallTimeout, ct);
        }
        catch (TimeoutException)
        {
            throw StallException(path);
        }
    }

    internal static async Task GuardAsync(ValueTask operation, string path, CancellationToken ct)
    {
        try
        {
            await operation.AsTask().WaitAsync(StallTimeout, ct);
        }
        catch (TimeoutException)
        {
            throw StallException(path);
        }
    }

    private static IOException StallException(string path) =>
        new($"'{path}' stopped responding (no progress for {(int)StallTimeout.TotalSeconds}s) - the device may be disconnected or unresponsive.");

    /// <summary>Fire-and-forget best-effort close: if a read/write against this stream was abandoned via
    /// GuardAsync above, it may genuinely still be blocked in the kernel. Awaiting Dispose here could hang or
    /// throw while racing that in-flight call and would mask the real cancellation/stall error, so this
    /// deliberately isn't awaited by the caller - it either closes immediately (the common case) or finishes
    /// quietly on its own later.</summary>
    internal static async Task DisposeQuietlyAsync(Stream stream)
    {
        try
        {
            await stream.DisposeAsync();
        }
        catch
        {
            // Best-effort only - see summary above.
        }
    }

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
        var source = new FileStream(sourcePhysical, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize, FileOptions.SequentialScan | FileOptions.Asynchronous);
        var dest = new FileStream(destPhysical, FileMode.CreateNew, FileAccess.Write, FileShare.None, CopyBufferSize, FileOptions.Asynchronous);

        try
        {
            var buffer = new byte[CopyBufferSize];
            int read;
            while ((read = await GuardAsync(source.ReadAsync(buffer.AsMemory(0, buffer.Length), ct), sourcePhysical, ct)) > 0)
            {
                await GuardAsync(dest.WriteAsync(buffer.AsMemory(0, read), ct), destPhysical, ct);
                await onBytesCopied(read);
            }
        }
        finally
        {
            _ = DisposeQuietlyAsync(source);
            _ = DisposeQuietlyAsync(dest);
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

    /// <summary>Determines whether two paths live under the same mounted filesystem, by matching each against
    /// /proc/mounts and comparing the longest matching mount point. This exists so the fast-rename attempt in
    /// FileOperationWorker can be skipped entirely for a cross-device move: Directory.Move fails immediately on
    /// a cross-device EXDEV, but File.Move on Linux silently falls back to a full blocking byte-for-byte copy
    /// when crossing devices - one that reports no progress, can't be cancelled, and isn't stall-guarded, unlike
    /// CopyRecursiveAsync above. A move between two different mounted drives (e.g. two separate /mnt volumes)
    /// would otherwise sit at 0 processed items/bytes for the entire transfer even though the data is genuinely
    /// moving, because the whole copy happens inside that one opaque File.Move call.
    ///
    /// Degrades to true when mount info isn't available (Windows dev, no /proc/mounts) - the previous behavior
    /// of just attempting the OS rename and falling back to copy on any failure.</summary>
    public static bool AreOnSameFileSystem(string pathA, string pathB)
    {
        var mountPoints = GetMountPoints();
        if (mountPoints.Count == 0)
        {
            return true;
        }

        return FindMountPoint(pathA, mountPoints) == FindMountPoint(pathB, mountPoints);
    }

    private static string? FindMountPoint(string path, IReadOnlyList<string> mountPoints)
    {
        var normalized = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string? best = null;
        foreach (var mountPoint in mountPoints)
        {
            var isMatch = normalized.Equals(mountPoint, StringComparison.Ordinal)
                || normalized.StartsWith(mountPoint + "/", StringComparison.Ordinal)
                || mountPoint == "/";
            if (!isMatch || (best is not null && mountPoint.Length <= best.Length))
            {
                continue;
            }
            best = mountPoint;
        }
        return best;
    }

    private static IReadOnlyList<string> GetMountPoints()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/proc/mounts"))
        {
            return [];
        }

        try
        {
            return File.ReadLines("/proc/mounts")
                .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .Where(fields => fields.Length >= 2)
                .Select(fields =>
                {
                    var mountPoint = UnescapeMountField(fields[1]).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    return mountPoint.Length == 0 ? "/" : mountPoint;
                })
                .ToList();
        }
        catch (IOException)
        {
            return [];
        }
    }

    private static string UnescapeMountField(string field) =>
        field.Replace("\\040", " ").Replace("\\011", "\t").Replace("\\012", "\n").Replace("\\134", "\\");
}
