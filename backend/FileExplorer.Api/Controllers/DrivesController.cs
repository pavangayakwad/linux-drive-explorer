using FileExplorer.Api.Models.Dtos;
using FileExplorer.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FileExplorer.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/drives")]
public class DrivesController(IDriveInfoProvider driveInfoProvider) : ControllerBase
{
    [HttpGet]
    public ActionResult<IReadOnlyList<DriveSummaryDto>> Get() => Ok(driveInfoProvider.GetDrives());
}
