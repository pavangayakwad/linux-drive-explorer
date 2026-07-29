using FileExplorer.Api.Models.Dtos;

namespace FileExplorer.Api.Services;

public interface IDriveInfoProvider
{
    IReadOnlyList<DriveSummaryDto> GetDrives();
}
