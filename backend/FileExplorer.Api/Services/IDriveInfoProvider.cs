using FileExplorer.Api.Models.Dtos;

namespace FileExplorer.Api.Services;

public interface IDriveInfoProvider
{
    IReadOnlyList<DriveSummaryDto> GetDrives();

    /// <summary>Same data as <see cref="GetDrives"/>, but bounded: if a mount is saturated enough that
    /// stat'ing its free space blocks past a short deadline, returns the last successful snapshot instead
    /// of hanging the caller indefinitely. Use this from request-handling code (e.g. the polled drives
    /// endpoint); use <see cref="GetDrives"/> directly only for rare, user-triggered lookups where blocking
    /// briefly is acceptable.</summary>
    Task<IReadOnlyList<DriveSummaryDto>> GetDrivesAsync(CancellationToken ct);

    /// <summary>Removable block devices/partitions (e.g. a plugged-in USB drive) that the OS hasn't
    /// mounted anywhere yet, so they wouldn't otherwise appear in <see cref="GetDrives"/>.</summary>
    IReadOnlyList<UnmountedDeviceDto> GetUnmountedRemovableDevices();
}
