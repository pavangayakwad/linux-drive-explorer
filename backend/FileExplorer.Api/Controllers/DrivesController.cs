using FileExplorer.Api.Models.Dtos;
using FileExplorer.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FileExplorer.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/drives")]
public class DrivesController(IDriveInfoProvider driveInfoProvider, IHostMountService hostMountService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<DriveSummaryDto>>> Get(CancellationToken cancellationToken) =>
        Ok(await driveInfoProvider.GetDrivesAsync(cancellationToken));

    [HttpGet("unmounted")]
    public ActionResult<IReadOnlyList<UnmountedDeviceDto>> GetUnmounted() => Ok(driveInfoProvider.GetUnmountedRemovableDevices());

    [HttpPost("mount")]
    public async Task<ActionResult<DriveSummaryDto>> Mount(MountDeviceRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await hostMountService.MountAsync(request.Device, cancellationToken));
        }
        catch (MountOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("unmount")]
    public async Task<IActionResult> Unmount(UnmountDriveRequest request, CancellationToken cancellationToken)
    {
        try
        {
            await hostMountService.UnmountAsync(request.MountPath, cancellationToken);
            return NoContent();
        }
        catch (MountOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }
}
