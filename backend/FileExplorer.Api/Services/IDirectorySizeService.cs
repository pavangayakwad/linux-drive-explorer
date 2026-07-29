using FileExplorer.Api.Models.Dtos;

namespace FileExplorer.Api.Services;

public interface IDirectorySizeService
{
    /// <summary>Starts (or reuses a recent cached result for) a background size calculation for a directory.</summary>
    DirectorySizeJobDto Start(string virtualPath);

    DirectorySizeJobDto? Get(Guid jobId);

    bool Cancel(Guid jobId);
}
