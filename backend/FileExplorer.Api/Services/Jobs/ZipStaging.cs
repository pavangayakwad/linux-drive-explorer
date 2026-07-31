using FileExplorer.Api.Options;

namespace FileExplorer.Api.Services.Jobs;

/// <summary>
/// Computes the (deterministic, never persisted) physical location of a Zip job's staged archive - the job's own
/// id is enough to find it again, so nothing needs a database column for it.
/// </summary>
public static class ZipStaging
{
    public static string GetStagingDirectory(IPathResolver pathResolver, FileSystemOptions options) =>
        Path.Combine(pathResolver.RootPath, options.ZipDirectoryName);

    public static string GetZipPhysicalPath(IPathResolver pathResolver, FileSystemOptions options, Guid jobId) =>
        Path.Combine(GetStagingDirectory(pathResolver, options), $"{jobId:N}.zip");
}
