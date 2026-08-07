using FileExplorer.Api.Models.Dtos;

namespace FileExplorer.Api.Services;

public interface IHostMountService
{
    /// <summary>Mounts a currently-unmounted removable device (as reported by
    /// <see cref="IDriveInfoProvider.GetUnmountedRemovableDevices"/>) under the host's /mnt, and returns
    /// the resulting drive once it's visible through the container's own filesystem view.</summary>
    Task<DriveSummaryDto> MountAsync(string device, CancellationToken cancellationToken);

    /// <summary>Unmounts a drive previously mounted under /mnt by <see cref="MountAsync"/>. Refuses to
    /// touch anything outside /mnt or anything not currently reported as a removable mount.</summary>
    Task UnmountAsync(string virtualMountPath, CancellationToken cancellationToken);
}
