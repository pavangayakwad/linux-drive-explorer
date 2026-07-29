using System.IO;

namespace FileExplorer.Api.Services;

public static class UnixPermissionFormatter
{
    /// <summary>
    /// Returns a "rwxr-xr-x" style string for the given path, or null on platforms
    /// (e.g. Windows dev machines) that don't support POSIX file modes.
    /// </summary>
    public static string? TryFormat(string physicalPath)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return null;
        }

        try
        {
            var mode = File.GetUnixFileMode(physicalPath);
            return Format(mode);
        }
        catch
        {
            return null;
        }
    }

    public static string Format(UnixFileMode mode)
    {
        Span<char> chars = stackalloc char[9];
        chars[0] = mode.HasFlag(UnixFileMode.UserRead) ? 'r' : '-';
        chars[1] = mode.HasFlag(UnixFileMode.UserWrite) ? 'w' : '-';
        chars[2] = mode.HasFlag(UnixFileMode.UserExecute) ? 'x' : '-';
        chars[3] = mode.HasFlag(UnixFileMode.GroupRead) ? 'r' : '-';
        chars[4] = mode.HasFlag(UnixFileMode.GroupWrite) ? 'w' : '-';
        chars[5] = mode.HasFlag(UnixFileMode.GroupExecute) ? 'x' : '-';
        chars[6] = mode.HasFlag(UnixFileMode.OtherRead) ? 'r' : '-';
        chars[7] = mode.HasFlag(UnixFileMode.OtherWrite) ? 'w' : '-';
        chars[8] = mode.HasFlag(UnixFileMode.OtherExecute) ? 'x' : '-';
        return new string(chars);
    }
}
