using System.Diagnostics;
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

    /// <summary>Carved out of the "/run" pseudo-mount filter above: udisks2-based distros (Fedora, Arch, ...)
    /// auto-mount removable media at /run/media/$USER/&lt;label&gt; by default, which would otherwise be
    /// indistinguishable from /run's usual noise (tmpfs runtime state, /run/lock, etc.). Debian/Ubuntu's
    /// equivalent convention, /media/$USER/&lt;label&gt;, doesn't need a carve-out since it never matches the
    /// "/run" prefix in the first place.</summary>
    private const string RunMediaPrefix = "/run/media/";

    /// <summary>Substrings of mount paths that are host/container implementation detail, not a volume a user would
    /// ever want to browse (EFI system partitions, Docker's internal overlay2 storage, etc.).</summary>
    private static readonly string[] NoiseMountPathSubstrings = ["/boot/efi", "/var/lib/docker"];

    /// <summary>Exact mount paths that are noise rather than substrings, since substring-matching "/boot" would also
    /// hide unrelated real mounts that merely contain that text (e.g. "/mnt/reboot-backup").</summary>
    private static readonly HashSet<string> NoiseMountPaths = new(StringComparer.OrdinalIgnoreCase) { "/boot" };

    private static readonly string[] RemovableMountPrefixes = ["/mnt/", "/media/", RunMediaPrefix];

    /// <summary>Block devices that report as "removable" in sysfs but aren't real user-facing media
    /// (loopback mounts, RAM disks, device-mapper targets) - never worth offering as mountable.</summary>
    private static readonly string[] ExcludedBlockDevicePrefixes = ["loop", "ram", "dm-", "zram"];

    [GeneratedRegex(@"^(sd[a-z]+|vd[a-z]+|xvd[a-z]+|nvme\d+n\d+|mmcblk\d+)")]
    private static partial Regex BaseBlockDeviceRegex();

    private static readonly TimeSpan DrivesAsyncTimeout = TimeSpan.FromSeconds(5);
    private IReadOnlyList<DriveSummaryDto> lastKnownDrives = [];

    public async Task<IReadOnlyList<DriveSummaryDto>> GetDrivesAsync(CancellationToken ct)
    {
        try
        {
            // GetDrives() itself blocks on statvfs(2) per mount, which can stall for a long time against a
            // mount saturated by a concurrent large copy/move. Task.Run keeps that block off of whichever
            // thread is awaiting this call, and WaitAsync bounds how long the caller (an HTTP request, polled
            // every 60s by the frontend) waits for it - past the deadline, fall back to the last good snapshot
            // rather than hang the request/connection. The orphaned Task.Run continues in the background since
            // statvfs isn't cancellable, but it no longer blocks request handling. See the 8-hour-move
            // near-freeze writeup for the incident this addresses.
            var drives = await Task.Run(GetDrives, ct).WaitAsync(DrivesAsyncTimeout, ct);
            lastKnownDrives = drives;
            return drives;
        }
        catch (TimeoutException)
        {
            return lastKnownDrives;
        }
    }

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

            var isRunMedia = mountFullPath.StartsWith(RunMediaPrefix, StringComparison.OrdinalIgnoreCase);
            if (!isRunMedia && PseudoMountPrefixes.Any(prefix => mountFullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
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

    public IReadOnlyList<UnmountedDeviceDto> GetUnmountedRemovableDevices()
    {
        const string sysBlockPath = "/sys/block";
        if (!OperatingSystem.IsLinux() || !Directory.Exists(sysBlockPath))
        {
            return [];
        }

        var mountedDevices = new HashSet<string>(
            ReadMounts().Values.Select(entry => entry.Device),
            StringComparer.OrdinalIgnoreCase);

        var results = new List<UnmountedDeviceDto>();

        foreach (var diskDir in Directory.GetDirectories(sysBlockPath))
        {
            var diskName = Path.GetFileName(diskDir)!;
            if (ExcludedBlockDevicePrefixes.Any(prefix => diskName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (!IsSysfsRemovableDisk(diskDir))
            {
                continue;
            }

            // Real partitions are marked in sysfs with a "partition" file (containing the partition
            // number) inside their subdirectory - a naming-agnostic way to tell them apart from other
            // sysfs entries (e.g. "queue", "holders") that also live under the disk directory.
            var partitionDirs = Directory.GetDirectories(diskDir)
                .Where(dir => File.Exists(Path.Combine(dir, "partition")))
                .ToList();

            // Some USB sticks carry a filesystem directly on the whole-disk device with no partition
            // table at all - fall back to the disk itself as the single mountable candidate.
            List<(string Name, string SizePath)> candidates = partitionDirs.Count > 0
                ? partitionDirs.Select(dir => (Path.GetFileName(dir)!, Path.Combine(dir, "size"))).ToList()
                : [(diskName, Path.Combine(diskDir, "size"))];

            foreach (var (name, sizePath) in candidates)
            {
                var devicePath = $"/dev/{name}";
                if (mountedDevices.Contains(devicePath))
                {
                    continue;
                }

                var sizeBytes = TryReadSectorSizeBytes(sizePath);
                var (label, fsType) = ReadBlkidInfo(devicePath);
                results.Add(new UnmountedDeviceDto(devicePath, label, fsType, sizeBytes));
            }
        }

        return results;
    }

    private static bool IsSysfsRemovableDisk(string diskDir)
    {
        try
        {
            return File.ReadAllText(Path.Combine(diskDir, "removable")).Trim() == "1";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>sysfs reports block device sizes in 512-byte sectors regardless of the device's actual
    /// physical sector size - see Linux's Documentation/block/capability.rst.</summary>
    private static long? TryReadSectorSizeBytes(string sizePath)
    {
        try
        {
            return long.TryParse(File.ReadAllText(sizePath).Trim(), out var sectors) ? sectors * 512L : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Best-effort filesystem label/type lookup - `blkid` may be absent, may need privileges
    /// this process doesn't have, or may simply find no signature on unformatted media, all of which
    /// should degrade to null fields rather than fail the whole device listing.</summary>
    private static (string? Label, string? FsType) ReadBlkidInfo(string devicePath)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "blkid",
                    ArgumentList = { "-o", "export", devicePath },
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                },
            };
            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();

            string? label = null;
            string? fsType = null;
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split('=', 2);
                if (parts.Length != 2)
                {
                    continue;
                }

                if (parts[0] == "LABEL")
                {
                    label = parts[1].Trim();
                }
                else if (parts[0] == "TYPE")
                {
                    fsType = parts[1].Trim();
                }
            }

            return (label, fsType);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            return (null, null);
        }
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
