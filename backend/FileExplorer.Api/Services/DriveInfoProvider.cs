using System.Text.RegularExpressions;
using FileExplorer.Api.Models.Dtos;

namespace FileExplorer.Api.Services;

public partial class DriveInfoProvider(IPathResolver pathResolver) : IDriveInfoProvider
{
    private static readonly HashSet<string> PseudoFileSystems = new(StringComparer.OrdinalIgnoreCase)
    {
        "proc", "sysfs", "devtmpfs", "tmpfs", "cgroup", "cgroup2", "mqueue", "debugfs", "tracefs",
        "fusectl", "configfs", "binfmt_misc", "overlay", "squashfs", "devpts", "autofs", "none",
        "nsfs", "bpf", "pstore", "securityfs", "hugetlbfs", "rpc_pipefs", "efivarfs", "ramfs",
    };

    private static readonly string[] PseudoMountPrefixes = ["/proc", "/sys", "/dev", "/run"];

    /// <summary>Substrings of mount paths that are host/container implementation detail, not a volume a user would
    /// ever want to browse (EFI system partitions, Docker's internal overlay2 storage, etc.).</summary>
    private static readonly string[] NoiseMountPathSubstrings = ["/boot/efi", "/var/lib/docker"];

    /// <summary>Exact mount paths that are noise rather than substrings, since substring-matching "/boot" would also
    /// hide unrelated real mounts that merely contain that text (e.g. "/mnt/reboot-backup").</summary>
    private static readonly HashSet<string> NoiseMountPaths = new(StringComparer.OrdinalIgnoreCase) { "/boot" };

    private static readonly string[] RemovableMountPrefixes = ["/mnt/", "/media/"];

    [GeneratedRegex(@"^(sd[a-z]+|vd[a-z]+|xvd[a-z]+|nvme\d+n\d+|mmcblk\d+)")]
    private static partial Regex BaseBlockDeviceRegex();

    public IReadOnlyList<DriveSummaryDto> GetDrives()
    {
        var results = new Dictionary<string, DriveSummaryDto>(StringComparer.OrdinalIgnoreCase);
        var mounts = ReadMounts();
        var mountDevices = mounts.ToDictionary(kv => kv.Key, kv => kv.Value.Device, StringComparer.OrdinalIgnoreCase);

        // Prefer the raw kernel mount table over DriveInfo.GetDrives(): with the host bind-mounted
        // in via `rslave` propagation (see docker-compose.yml), a USB drive mounted on the host after
        // this process started shows up here immediately, including ones nested several levels under
        // /mnt or /media. Fall back to DriveInfo.GetDrives() only where /proc/mounts isn't available
        // (e.g. running the API on Windows during local development).
        IEnumerable<(string MountPath, string FsType)> candidates = mounts.Count > 0
            ? mounts.Select(kv => (kv.Key, kv.Value.FsType))
            : DriveInfo.GetDrives()
                .Where(d => d.IsReady)
                .Select(d => (d.RootDirectory.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), d.DriveFormat));

        foreach (var (rawMountPath, fsType) in candidates)
        {
            var mountFullPath = rawMountPath.Length == 0 ? "/" : rawMountPath;

            if (PseudoFileSystems.Contains(fsType))
            {
                continue;
            }

            if (PseudoMountPrefixes.Any(prefix => mountFullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (NoiseMountPathSubstrings.Any(substring => mountFullPath.Contains(substring, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            string virtualPath;
            try
            {
                virtualPath = pathResolver.ToVirtualPath(mountFullPath);
            }
            catch (UnauthorizedAccessException)
            {
                // Mount point is outside the configured root - not reachable through the API, skip it.
                continue;
            }

            // Compared against the virtual (root-relative) path, not the raw physical mount path: when the
            // configured root is a bind-mounted prefix like /host_root, the physical path is /host_root/boot,
            // which would never equal the exact string "/boot".
            if (NoiseMountPaths.Contains(virtualPath))
            {
                continue;
            }

            DriveInfo drive;
            try
            {
                drive = new DriveInfo(mountFullPath);
                if (!drive.IsReady)
                {
                    continue;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            {
                continue;
            }

            // drive.Name/VolumeLabel are physical paths (e.g. "/host_root/mnt/box-office-I") that mean nothing to the
            // client and, on Linux, are frequently identical to each other - showing "X (X)". The virtual path is
            // the one identity the client actually knows about, so use it as the display name unless there's a
            // genuinely distinct volume label to call out.
            var hasDistinctLabel = !string.IsNullOrWhiteSpace(drive.VolumeLabel)
                && !drive.VolumeLabel.Equals(mountFullPath, StringComparison.OrdinalIgnoreCase);
            var name = hasDistinctLabel ? $"{drive.VolumeLabel} ({virtualPath})" : virtualPath;

            try
            {
                results[mountFullPath] = new DriveSummaryDto(
                    Name: name,
                    MountPath: virtualPath,
                    DriveFormat: drive.DriveFormat,
                    TotalBytes: drive.TotalSize,
                    FreeBytes: drive.AvailableFreeSpace,
                    IsRemovable: IsRemovable(mountFullPath, mountDevices));
            }
            catch (IOException)
            {
                // Drive briefly unavailable (e.g. unmounted between IsReady check and stat) - skip it.
            }
        }

        return results.Values
            .OrderBy(d => d.MountPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>A drive is removable if its underlying block device reports so via sysfs, or - when that can't be
    /// determined (non-Linux, network share, sysfs unreadable) - if it's mounted under a conventional removable-media
    /// path such as /mnt or /media.</summary>
    private static bool IsRemovable(string mountFullPath, IReadOnlyDictionary<string, string> mountDevices)
    {
        if (mountDevices.TryGetValue(mountFullPath, out var device))
        {
            var sysfsResult = TryReadSysfsRemovable(device);
            if (sysfsResult.HasValue)
            {
                return sysfsResult.Value;
            }
        }

        return RemovableMountPrefixes.Any(prefix => (mountFullPath + "/").StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private static bool? TryReadSysfsRemovable(string device)
    {
        if (!device.StartsWith("/dev/", StringComparison.Ordinal))
        {
            // Not a local block device (e.g. an NFS/CIFS export) - sysfs has nothing to say about it.
            return null;
        }

        var deviceName = device["/dev/".Length..];
        var match = BaseBlockDeviceRegex().Match(deviceName);
        var baseDevice = match.Success ? match.Value : deviceName;

        var removableFile = Path.Combine("/sys/block", baseDevice, "removable");
        try
        {
            if (!File.Exists(removableFile))
            {
                return null;
            }

            return File.ReadAllText(removableFile).Trim() == "1";
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private readonly record struct MountEntry(string Device, string FsType);

    /// <summary>Reads the live kernel mount table from /proc/mounts, keyed by mount point. When a path is
    /// mounted over more than once, the last entry in the file is the currently active one, so later lines
    /// naturally overwrite earlier ones here.</summary>
    private static Dictionary<string, MountEntry> ReadMounts()
    {
        var map = new Dictionary<string, MountEntry>(StringComparer.OrdinalIgnoreCase);

        if (!OperatingSystem.IsLinux() || !File.Exists("/proc/mounts"))
        {
            return map;
        }

        try
        {
            foreach (var line in File.ReadLines("/proc/mounts"))
            {
                var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length < 3)
                {
                    continue;
                }

                var device = fields[0];
                var mountPoint = UnescapeMountField(fields[1]).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (mountPoint.Length == 0)
                {
                    mountPoint = "/";
                }

                map[mountPoint] = new MountEntry(device, fields[2]);
            }
        }
        catch (IOException)
        {
            // Best-effort - fall back to the mount-path heuristic for every drive.
        }

        return map;
    }

    private static string UnescapeMountField(string field) =>
        field.Replace("\\040", " ").Replace("\\011", "\t").Replace("\\012", "\n").Replace("\\134", "\\");
}
