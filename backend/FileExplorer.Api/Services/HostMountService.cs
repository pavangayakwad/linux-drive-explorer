using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using FileExplorer.Api.Models.Dtos;

namespace FileExplorer.Api.Services;

/// <summary>Mounts/unmounts removable block devices on the *host*, not just inside this container's own
/// mount namespace. The api container runs with `privileged: true` and `pid: host` (see
/// docker-compose.yml) specifically so `nsenter --target 1 ...` can execute mount/umount inside PID 1's
/// (the host init's) namespaces - a plain `mount` here would only affect this container's private view
/// and would never be visible on the real host.</summary>
public partial class HostMountService(
    IDriveInfoProvider driveInfoProvider,
    IPathResolver pathResolver,
    ILogger<HostMountService> logger) : IHostMountService
{
    private const string HostMountRoot = "/mnt";
    private static readonly TimeSpan MountVisibilityTimeout = TimeSpan.FromSeconds(3);

    [GeneratedRegex(@"[^A-Za-z0-9_-]+")]
    private static partial Regex UnsafeNameCharsRegex();

    // Serializes mount/unmount calls process-wide so two concurrent requests can't race to create the
    // same /mnt/<name> folder or interleave nsenter invocations.
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task<DriveSummaryDto> MountAsync(string device, CancellationToken cancellationToken)
    {
        RequireLinux();

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Never trust the client-supplied device string directly - re-derive the set of legitimately
            // mountable candidates server-side and require an exact match against it.
            var candidate = driveInfoProvider.GetUnmountedRemovableDevices()
                .FirstOrDefault(d => d.Device.Equals(device, StringComparison.OrdinalIgnoreCase));
            if (candidate is null)
            {
                throw new MountOperationException($"'{device}' is not a currently unmounted removable device.");
            }

            var folderName = BuildMountFolderName(candidate);
            var hostMountPath = $"{HostMountRoot}/{folderName}";

            await RunHostCommandAsync(cancellationToken, "mkdir", "-p", hostMountPath).ConfigureAwait(false);
            await RunHostCommandAsync(cancellationToken, "mount", candidate.Device, hostMountPath).ConfigureAwait(false);

            var physicalMountPath = Path.Combine(pathResolver.RootPath, "mnt", folderName);
            var drive = await WaitForDriveAsync(physicalMountPath, cancellationToken).ConfigureAwait(false);
            if (drive is null)
            {
                throw new MountOperationException(
                    $"Mounted '{candidate.Device}' at {hostMountPath}, but it hasn't appeared in the app's view yet - try refreshing.");
            }

            return drive;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task UnmountAsync(string virtualMountPath, CancellationToken cancellationToken)
    {
        RequireLinux();

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string physicalPath;
            try
            {
                physicalPath = pathResolver.ToPhysicalPath(virtualMountPath);
            }
            catch (UnauthorizedAccessException)
            {
                throw new MountOperationException($"'{virtualMountPath}' is not a valid path.");
            }

            // Mount/unmount is restricted to /mnt in both directions - this is the one place a client
            // request could otherwise be pointed at unmounting the app's own root, /home, a Docker data
            // directory, etc.
            var hostMntRoot = Path.Combine(pathResolver.RootPath, "mnt") + Path.DirectorySeparatorChar;
            if (!physicalPath.StartsWith(hostMntRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new MountOperationException("Only drives mounted under /mnt by this app can be unmounted.");
            }

            var drive = driveInfoProvider.GetDrives()
                .FirstOrDefault(d => d.MountPath.Equals(virtualMountPath, StringComparison.OrdinalIgnoreCase));
            if (drive is null || !drive.IsRemovable)
            {
                throw new MountOperationException($"'{virtualMountPath}' is not a currently mounted removable drive.");
            }

            var folderName = physicalPath[hostMntRoot.Length..];
            var hostMountPath = $"{HostMountRoot}/{folderName}";

            await RunHostCommandAsync(cancellationToken, "umount", hostMountPath).ConfigureAwait(false);

            try
            {
                await RunHostCommandAsync(cancellationToken, "rmdir", hostMountPath).ConfigureAwait(false);
            }
            catch (MountOperationException ex)
            {
                // Best-effort - e.g. a stray lost+found left the directory non-empty. The unmount itself
                // already succeeded, so this isn't worth failing the request over.
                logger.LogInformation("Left mount point directory {Path} in place after unmount: {Message}", hostMountPath, ex.Message);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    private string BuildMountFolderName(UnmountedDeviceDto candidate)
    {
        var deviceBaseName = Path.GetFileName(candidate.Device);
        var sanitizedLabel = string.IsNullOrWhiteSpace(candidate.Label)
            ? string.Empty
            : UnsafeNameCharsRegex().Replace(candidate.Label, "-").Trim('-');
        var baseName = sanitizedLabel.Length > 0 ? sanitizedLabel : deviceBaseName;

        var mntRoot = Path.Combine(pathResolver.RootPath, "mnt");
        var name = baseName;
        var suffix = 1;
        while (Directory.Exists(Path.Combine(mntRoot, name)))
        {
            suffix++;
            name = $"{baseName}-{suffix}";
        }

        return name;
    }

    private async Task<DriveSummaryDto?> WaitForDriveAsync(string physicalMountPath, CancellationToken cancellationToken)
    {
        string virtualPath;
        try
        {
            virtualPath = pathResolver.ToVirtualPath(physicalMountPath);
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        var deadline = DateTimeOffset.UtcNow + MountVisibilityTimeout;
        while (true)
        {
            var drive = driveInfoProvider.GetDrives()
                .FirstOrDefault(d => d.MountPath.Equals(virtualPath, StringComparison.OrdinalIgnoreCase));
            if (drive is not null)
            {
                return drive;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                return null;
            }

            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void RequireLinux()
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new MountOperationException(
                "Mounting/unmounting is only supported when the API runs on Linux with the required container privileges.");
        }
    }

    private static async Task RunHostCommandAsync(CancellationToken cancellationToken, string exe, params string[] args)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "nsenter",
                ArgumentList = { "--target", "1", "--mount", "--uts", "--ipc", "--net", "--pid", "--", exe },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };
        foreach (var arg in args)
        {
            process.StartInfo.ArgumentList.Add(arg);
        }

        try
        {
            process.Start();
        }
        catch (Win32Exception)
        {
            throw new MountOperationException(
                "nsenter is not available - this requires the api container to run with 'privileged: true' and 'pid: host' (see docker-compose.yml).");
        }

        // Drain both streams concurrently with waiting for exit, mirroring DirectorySizeService - a
        // chatty stderr (e.g. mount warnings) can otherwise fill the pipe buffer and deadlock the child.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        await stdoutTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            throw new MountOperationException(
                string.IsNullOrWhiteSpace(stderr) ? $"'{exe}' failed with exit code {process.ExitCode}." : stderr.Trim());
        }
    }
}
