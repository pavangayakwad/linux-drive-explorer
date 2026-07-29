namespace FileExplorer.Api.Services;

public interface IMountLocator
{
    /// <summary>Physical root directory of the nearest mounted filesystem containing the given physical path.</summary>
    string FindMountRoot(string physicalPath);
}
